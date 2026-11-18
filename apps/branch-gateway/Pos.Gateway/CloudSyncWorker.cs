using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Sync;

namespace Pos.Gateway;

public sealed record SyncOptions(int BatchSize, TimeSpan MaxBackoff, TimeSpan RequestTimeout, TimeSpan IdlePoll)
{
    public static SyncOptions Default { get; } = new(SyncProtocol.MaxBatchEvents, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
}

/// <summary>A sync attempt that did not complete; <see cref="BlockedSequence"/> is set when one event blocks progress.</summary>
internal sealed class SyncFailureException : Exception
{
    public SyncFailureException(string message, long? blockedSequence = null) : base(message) => BlockedSequence = blockedSequence;

    public SyncFailureException() : this("Échec de synchronisation.") { }

    public SyncFailureException(string message, Exception innerException) : base(message, innerException) { }

    public long? BlockedSequence { get; }
}

/// <summary>
/// Pushes the outbox to the cloud: ordered, resumable, bounded batches; at-least-once delivery.
///
/// - The checkpoint (<c>sync_state.acked_sequence</c>) only advances to what the cloud reports as durably
///   committed. A lost acknowledgment means the same events are sent again; the cloud recognizes them as
///   duplicates. The gateway never deletes or skips an event: a rejected event blocks synchronization
///   (visible in « Pos.Gateway status » and the logs) until an operator resolves it.
/// - Failures back off exponentially (1 s, 2 s, 4 s … up to <see cref="SyncOptions.MaxBackoff"/>, ±20 % jitter).
/// - Registration with a one-time code is done here, by the gateway's own account (DPAPI-protected key).
/// - Local operations never wait for the cloud: an internet outage does not affect the branch.
/// - Recovery safety (Phase 1.5): nothing is appended or sent while the gateway is not in 'normal' state; once per
///   start it checks whether the cloud holds MORE events than this database (restored copy) and, if so, blocks
///   shared writes ('cloud_ahead'); a credential revoked by the cloud (gateway replaced) fences this gateway.
/// </summary>
public sealed partial class CloudSyncWorker(GatewayStore store, SyncOptions options, ILogger<CloudSyncWorker> logger) : BackgroundService
{
    private const int MaxBatchBytes = 1_500_000;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CloudClient _cloud = new(store, options.RequestTimeout);
    private bool _cloudChecked;

    /// <summary>Wakes the worker (an event was appended).</summary>
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var more = false;
            try
            {
                if (!store.GetRecoveryState().IsNormal)
                {
                    await IdleAsync(stoppingToken); // recovery: the operator reconciles explicitly (« reconcile »)
                    continue;
                }

                var link = store.GetCloudLink();
                if (link is null)
                {
                    store.PublishCatalogIfChanged();
                    await IdleAsync(stoppingToken);
                    continue;
                }

                if (!link.IsLinked)
                {
                    await _cloud.RegisterAsync(link, stoppingToken);
                    store.RecordSyncSuccess();
                    failures = 0;
                    continue;
                }

                if (!_cloudChecked && await CloudIsAheadAsync(link, stoppingToken))
                {
                    continue;
                }

                _cloudChecked = true;
                store.PublishCatalogIfChanged();
                more = await PushAsync(link, stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (CloudRevokedException ex)
            {
                // Another gateway now serves this branch: stop accepting shared writes here (no split brain).
                store.SetRecoveryState(RecoveryStatus.Fenced, ex.Message);
                store.RecordSyncFailure("Le cloud refuse les identifiants de cette passerelle : révoquée (remplacée ?). Modifications partagées suspendues.", null);
                LogFenced(logger);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Never let a sync problem stop the gateway: the branch keeps working on the LAN.
                failures++;
                var message = ex switch
                {
                    SyncFailureException => ex.Message,
                    TaskCanceledException => "Cloud injoignable : délai dépassé.",
                    HttpRequestException or IOException or AuthenticationException => $"Cloud injoignable : {ex.GetBaseException().Message}",
                    JsonException => "Réponse du cloud illisible.",
                    _ => $"Erreur interne de synchronisation : {ex.GetType().Name}: {ex.Message}",
                };
                if (ex is not (SyncFailureException or TaskCanceledException or HttpRequestException or IOException or AuthenticationException or JsonException))
                {
                    LogUnexpected(logger, ex);
                }

                store.RecordSyncFailure(message, (ex as SyncFailureException)?.BlockedSequence);
                LogFailure(logger, failures, message);
            }

            if (failures > 0)
            {
                await Task.Delay(Backoff(failures), stoppingToken);
            }
            else if (!more)
            {
                await IdleAsync(stoppingToken);
            }
        }
    }

    internal TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(options.MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 16)));
        return TimeSpan.FromSeconds(seconds * (0.8 + (0.4 * Random.Shared.NextDouble())));
    }

    private async Task IdleAsync(CancellationToken ct)
    {
        try
        {
            await _signal.WaitAsync(options.IdlePoll, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>A database restored (or copied) from an older state is behind the cloud: block writes before anything diverges.</summary>
    private async Task<bool> CloudIsAheadAsync(CloudLink link, CancellationToken ct)
    {
        var cloud = await _cloud.GetAsync<SyncCloudState>(link, "/v1/sync/state", ct);
        var local = store.GetSyncStatus().LastSequence;
        if (cloud.LastSequence <= local)
        {
            return false;
        }

        store.SetRecoveryState(RecoveryStatus.CloudAhead,
            $"Le cloud a {cloud.LastSequence} événements pour cette succursale, cette base seulement {local} (restaurée ou copiée ?). " +
            "Arrêtez la passerelle puis exécutez « Pos.Gateway reconcile ».");
        LogCloudAhead(logger, cloud.LastSequence, local);
        return true;
    }

    /// <summary>Sends the next bounded batch. Returns true when more events may be waiting.</summary>
    private async Task<bool> PushAsync(CloudLink link, CancellationToken ct)
    {
        var status = store.GetSyncStatus();
        var events = store.PendingEvents(status.AckedSequence, options.BatchSize, MaxBatchBytes);
        if (events.Count == 0)
        {
            return false;
        }

        var body = Encoding.UTF8.GetBytes($"{{\"events\":[{string.Join(',', events.Select(e => e.Payload))}]}}");
        store.MarkAttempt(events[0].Sequence, events[^1].Sequence);
        using var response = await _cloud.SendSignedAsync(link, HttpMethod.Post, "/v1/sync/events", body, ct);
        await CloudClient.ThrowIfRefusedAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await CloudClient.ReadErrorAsync(response, ct);
            throw new SyncFailureException($"Le cloud a répondu {(int)response.StatusCode} ({error}).");
        }

        var result = await response.Content.ReadFromJsonAsync<SyncBatchResult>(SyncProtocol.Json, ct)
            ?? throw new SyncFailureException("Réponse du cloud vide.");
        var localLast = store.GetSyncStatus().LastSequence;
        if (result.LastAcceptedSequence > localLast)
        {
            store.SetRecoveryState(RecoveryStatus.CloudAhead,
                $"Le cloud a déjà {result.LastAcceptedSequence} événements, la passerelle seulement {localLast}. Exécutez « Pos.Gateway reconcile ».");
            throw new SyncFailureException(
                $"Le cloud a déjà {result.LastAcceptedSequence} événements pour cette succursale, la passerelle seulement {localLast} (passerelle restaurée ou remplacée ?). Réconciliation manuelle requise.",
                localLast + 1);
        }

        if (result.LastAcceptedSequence > status.AckedSequence)
        {
            store.AckThrough(result.LastAcceptedSequence);
        }

        var accepted = result.Outcomes.Count(o => o.Status == SyncOutcomeStatus.Accepted);
        var duplicates = result.Outcomes.Count - accepted;
        LogPushed(logger, events[0].Sequence, events[^1].Sequence, accepted, duplicates, result.LastAcceptedSequence);

        if (result.Rejected is { } rejected)
        {
            if (rejected.Code == SyncRejectionCodes.SequenceGap && rejected.ExpectedSequence is { } expected && expected - 1 < status.AckedSequence)
            {
                store.Rewind(expected - 1); // the cloud lost acknowledged events: resend them
                return true;
            }

            throw new SyncFailureException($"Synchronisation bloquée à la séquence {rejected.Sequence} : {rejected.Code} — {rejected.Message}", rejected.Sequence);
        }

        store.RecordSyncSuccess();
        return events.Count == options.BatchSize || events[^1].Sequence < localLast;
    }

    public override void Dispose()
    {
        base.Dispose();
        _cloud.Dispose();
        _signal.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloud sync: sent {FirstSequence}..{LastSequence}, {Accepted} accepted, {Duplicates} already known (replay), cloud checkpoint {CloudSequence}")]
    private static partial void LogPushed(ILogger logger, long firstSequence, long lastSequence, int accepted, int duplicates, long cloudSequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected cloud sync error")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloud sync attempt failed ({Failures} in a row): {Reason}. Events stay in the outbox; retrying with backoff.")]
    private static partial void LogFailure(ILogger logger, int failures, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The cloud revoked this gateway's credential (replaced?): shared writes are suspended on this gateway")]
    private static partial void LogFenced(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The cloud holds {CloudSequence} events for this branch but this database only {LocalSequence}: shared writes blocked until « reconcile »")]
    private static partial void LogCloudAhead(ILogger logger, long cloudSequence, long localSequence);
}

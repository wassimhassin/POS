using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
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
/// </summary>
public sealed partial class CloudSyncWorker(GatewayStore store, SyncOptions options, ILogger<CloudSyncWorker> logger) : BackgroundService
{
    private const int MaxBatchBytes = 1_500_000;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private HttpClient? _http;
    private string? _httpFor;
    private ECDsa? _key;
    private string? _keyFor;

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
                store.PublishCatalogIfChanged();
                var link = store.GetCloudLink();
                if (link is null)
                {
                    await IdleAsync(stoppingToken);
                    continue;
                }

                if (!link.IsLinked)
                {
                    await RegisterAsync(link, stoppingToken);
                    failures = 0;
                    continue;
                }

                more = await PushAsync(link, stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
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
        using var response = await SendSignedAsync(link, "/v1/sync/events", body, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SyncFailureException("Le cloud refuse les identifiants de cette passerelle (révoquée ou inconnue). Les événements restent en attente.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, ct);
            throw new SyncFailureException($"Le cloud a répondu {(int)response.StatusCode} ({error}).");
        }

        var result = await response.Content.ReadFromJsonAsync<SyncBatchResult>(SyncProtocol.Json, ct)
            ?? throw new SyncFailureException("Réponse du cloud vide.");
        var localLast = store.GetSyncStatus().LastSequence;
        if (result.LastAcceptedSequence > localLast)
        {
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

    private async Task RegisterAsync(CloudLink link, CancellationToken ct)
    {
        var identity = store.GetIdentity() ?? throw new SyncFailureException("Passerelle non initialisée.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignData(SyncProtocol.RegisterPayload(link.PendingCode!, identity.BranchId, publicKey), HashAlgorithmName.SHA256);
        var request = new RegisterGatewayRequest(link.PendingCode!, identity.BranchId, Convert.ToBase64String(publicKey), Convert.ToBase64String(signature));

        using var response = await Http(link).PostAsJsonAsync("v1/gateways/register", request, SyncProtocol.Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SyncFailureException($"Enregistrement auprès du cloud refusé ({(int)response.StatusCode} {await ReadErrorAsync(response, ct)}). " +
                "Générez un nouveau code avec « Pos.Cloud gateway-code » puis « Pos.Gateway cloud-link ».");
        }

        var registration = await response.Content.ReadFromJsonAsync<RegisterGatewayResponse>(SyncProtocol.Json, ct)
            ?? throw new SyncFailureException("Réponse d'enregistrement vide.");
        if (registration.BranchId != identity.BranchId)
        {
            throw new SyncFailureException($"Le cloud a associé cette passerelle à la succursale {registration.BranchId}, attendu {identity.BranchId}.");
        }

        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            store.CompleteCloudLink(registration, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        store.RecordSyncSuccess();
    }

    private async Task<HttpResponseMessage> SendSignedAsync(CloudLink link, string path, byte[] body, CancellationToken ct)
    {
        if (_key is null || _keyFor != link.GatewayId)
        {
            _key?.Dispose();
            _key = GatewayStore.LoadCloudKey(link);
            _keyFor = link.GatewayId;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = _key.SignData(SyncProtocol.RequestPayload(link.GatewayId!, "POST", path, timestamp, SyncProtocol.Sha256Hex(body)), HashAlgorithmName.SHA256);
        using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(SyncProtocol.GatewayScheme,
            SyncProtocol.AuthorizationParameter(link.GatewayId!, timestamp, signature));
        return await Http(link).SendAsync(request, ct);
    }

    /// <summary>HTTPS only. With a pin (development/test certificates) the cloud certificate must match it exactly; otherwise normal CA validation.</summary>
    private HttpClient Http(CloudLink link)
    {
        var key = $"{link.CloudUrl}|{link.CloudPin}";
        if (_http is not null && _httpFor == key)
        {
            return _http;
        }

        _http?.Dispose();
        var pin = link.CloudPin is null ? null : Convert.FromHexString(link.CloudPin);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = pin is null
                    ? null
                    : (_, certificate, _, _) => certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin),
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(link.CloudUrl), Timeout = options.RequestTimeout };
        _httpFor = key;
        return _http;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<CloudError>(SyncProtocol.Json, ct);
            return error is null ? "?" : $"{error.Code}: {error.Message}";
        }
        catch (JsonException)
        {
            return "réponse illisible";
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _http?.Dispose();
        _key?.Dispose();
        _signal.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloud sync: sent {FirstSequence}..{LastSequence}, {Accepted} accepted, {Duplicates} already known (replay), cloud checkpoint {CloudSequence}")]
    private static partial void LogPushed(ILogger logger, long firstSequence, long lastSequence, int accepted, int duplicates, long cloudSequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected cloud sync error")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloud sync attempt failed ({Failures} in a row): {Reason}. Events stay in the outbox; retrying with backoff.")]
    private static partial void LogFailure(ILogger logger, int failures, string reason);
}

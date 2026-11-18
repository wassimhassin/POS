using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pos.Core.Printing;
using Pos.Sync;

namespace Pos.Gateway;

public sealed record ReconcileReport(
    bool DryRun,
    string StateBefore,
    string StateAfter,
    long LocalSequenceBefore,
    long AckedBefore,
    long CloudSequence,
    int VerifiedUnacknowledged,
    int Imported,
    int ImportedSubmissions,
    int UncertainPrintJobs,
    long LocalOnlyEvents,
    IReadOnlyList<string> Quarantined,
    IReadOnlyList<string> Notes);

/// <summary>
/// Compares this (restored) database with the events the cloud durably confirmed for the branch, then:
///  1. local events the cloud also holds but never acknowledged (lost acks) are verified by content hash and acknowledged;
///  2. a local event whose content differs from the cloud's at the same sequence is QUARANTINED (blocking) — never merged;
///  3. events the cloud holds beyond the local end are imported in order with their original ids (orders, operations,
///     print intents as 'uncertain' print jobs), each in a savepoint; one that cannot be applied is quarantined and the
///     import stops there (later events depend on it).
/// Nothing absent from both the backup and the cloud is (or can be) recovered; the report says so. Dry-run rolls back.
/// </summary>
public static class GatewayReconciler
{
    public static async Task<ReconcileReport> RunAsync(GatewayStore store, PrintQueue printQueue, CloudClient cloud, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cloud);
        var stateBefore = store.GetRecoveryState();
        var link = store.GetCloudLink() ?? throw new Pos.Core.PosValidationException("not_linked",
            "Passerelle non liée au cloud : rien à réconcilier (« recovery-verify --without-cloud » après contrôle).");
        if (!link.IsLinked)
        {
            if (dryRun)
            {
                throw new Pos.Core.PosValidationException("not_registered", "Liaison au cloud en attente : exécutez « reconcile » sans --dry-run pour enregistrer la passerelle.");
            }

            await cloud.RegisterAsync(link, ct);
            link = store.GetCloudLink()!;
        }

        var cloudState = await cloud.GetAsync<SyncCloudState>(link, "/v1/sync/state", ct);
        var status = store.GetSyncStatus();
        var (local, acked) = (status.LastSequence, status.AckedSequence);
        var notes = new List<string>();
        var quarantined = new List<string>();
        var verified = 0;
        var imported = new List<SyncEvent>();

        using var connection = store.Database.OpenConnection();
        using var tx = connection.BeginTransaction();
        var now = store.Now();

        // 1-2. Overlap: local events after the local checkpoint that the cloud also holds.
        var overlapEnd = Math.Min(local, cloudState.LastSequence);
        var conflicts = 0;
        await foreach (var remote in ReadAsync(cloud, link, acked, overlapEnd, ct))
        {
            var payload = LocalPayload(connection, tx, remote.Sequence);
            var localEvent = JsonSerializer.Deserialize<SyncEvent>(payload, SyncProtocol.Json)!;
            if (SyncProtocol.EventHash(localEvent) == SyncProtocol.EventHash(remote))
            {
                verified++;
                continue;
            }

            conflicts++;
            var detail = $"séquence {remote.Sequence} : contenu local ({localEvent.Type}, {localEvent.EventId}) différent de l'événement confirmé par le cloud ({remote.Type}, {remote.EventId})";
            GatewayStore.AddQuarantine(connection, tx, now, "sequence_conflict", true, remote.Sequence, localEvent.EventId, detail, payload);
            quarantined.Add(detail);
        }

        // 3. Import what the cloud confirmed beyond the local end (only if the shared history is consistent).
        var importedSubmissions = 0;
        var uncertain = 0;
        if (conflicts == 0 && cloudState.LastSequence > local)
        {
            var stop = false;
            await foreach (var remote in ReadAsync(cloud, link, local, cloudState.LastSequence, ct))
            {
                tx.Save("evt");
                var eventNotes = new List<string>();
                string? error;
                try
                {
                    error = GatewayStore.ApplyConfirmedEvent(connection, tx, remote, printQueue, now, eventNotes);
                }
                catch (SqliteException ex)
                {
                    error = $"erreur SQLite {ex.SqliteErrorCode} : {ex.Message}";
                }

                if (error is not null)
                {
                    tx.Rollback("evt");
                    var detail = $"séquence {remote.Sequence} ({remote.Type}, {remote.EventId}) non importable : {error}";
                    GatewayStore.AddQuarantine(connection, tx, now, "import_failed", true, remote.Sequence, remote.EventId, detail, JsonSerializer.Serialize(remote, SyncProtocol.Json));
                    quarantined.Add(detail);
                    stop = true;
                    break;
                }

                tx.Release("evt");
                imported.Add(remote);
                foreach (var note in eventNotes)
                {
                    GatewayStore.AddQuarantine(connection, tx, now, "note", false, remote.Sequence, remote.EventId, note, null);
                    notes.Add(note);
                }

                if (remote.Type == SyncEventTypes.Submit)
                {
                    importedSubmissions++;
                    uncertain += remote.PrintStations?.Count ?? 0;
                }
            }

            if (!stop && imported.LastOrDefault(e => e.Type == SyncEventTypes.CatalogPublished) is { Catalog: { } catalog } last)
            {
                // The branch catalog is local authority: the restored one stays in force (it is re-published after
                // verification). Its prices may predate the confirmed one: flagged for the operator.
                var hash = SyncProtocol.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(catalog, SyncProtocol.Json));
                GatewayStore.Execute(connection, tx, $"UPDATE sync_state SET catalog_hash = '{hash}' WHERE id = 1;");
                var detail = $"catalogue publié à la séquence {last.Sequence} ({catalog.Count} produits) : le catalogue restauré peut être plus ancien — vérifiez les prix avant de reprendre.";
                GatewayStore.AddQuarantine(connection, tx, now, "catalog_after_backup", false, last.Sequence, last.EventId, detail, JsonSerializer.Serialize(catalog, SyncProtocol.Json));
                notes.Add(detail);
            }
        }

        var newLocal = local + imported.Count;
        var blocking = quarantined.Count > 0;
        if (!blocking)
        {
            var newAcked = Math.Max(acked, Math.Min(newLocal, cloudState.LastSequence));
            using var update = GatewayStore.Command(connection, tx, """
                UPDATE sync_outbox SET acked_at = COALESCE(acked_at, $now) WHERE sequence <= $acked;
                UPDATE sync_state SET acked_sequence = $acked, last_error = NULL, blocked_sequence = NULL, consecutive_failures = 0 WHERE id = 1;
                """);
            update.Parameters.AddWithValue("$acked", newAcked);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
        }

        var after = blocking ? RecoveryStatus.CloudAhead : stateBefore.IsNormal && imported.Count == 0 && verified == 0 ? RecoveryStatus.Normal : RecoveryStatus.Reconciled;
        if (dryRun)
        {
            tx.Rollback();
        }
        else
        {
            if (after != stateBefore.Status || after == RecoveryStatus.Reconciled)
            {
                store.SetRecoveryState(connection, tx, after, blocking
                    ? $"Réconciliation bloquée : {quarantined.Count} différence(s) en quarantaine (« Pos.Gateway quarantine »)."
                    : $"Réconcilié avec le cloud (séquence {cloudState.LastSequence}) : {imported.Count} événement(s) importé(s), {verified} vérifié(s). Contrôlez puis « recovery-verify ».");
            }

            tx.Commit();
            GatewayStore.Audit(store.DataDir, "reconcile", new { local, acked, cloud = cloudState.LastSequence, verified, imported = imported.Count, quarantined, notes, after });
        }

        return new ReconcileReport(dryRun, stateBefore.Status, dryRun ? stateBefore.Status : after, local, acked, cloudState.LastSequence, verified, imported.Count,
            importedSubmissions, uncertain, Math.Max(0, newLocal - cloudState.LastSequence), quarantined, notes);
    }

    private static string LocalPayload(SqliteConnection connection, SqliteTransaction tx, long sequence)
    {
        using var query = GatewayStore.Command(connection, tx, "SELECT payload FROM sync_outbox WHERE sequence = $s;");
        query.Parameters.AddWithValue("$s", sequence);
        return (string)query.ExecuteScalar()!;
    }

    /// <summary>Cloud events in (after, until], in pages, contiguous (a hole is a protocol error, not silently skipped).</summary>
    private static async IAsyncEnumerable<SyncEvent> ReadAsync(CloudClient cloud, CloudLink link, long after, long until,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var next = after;
        while (next < until)
        {
            var page = await cloud.GetAsync<SyncEventPage>(link, string.Create(CultureInfo.InvariantCulture, $"/v1/sync/events?after={next}&limit={SyncProtocol.MaxBatchEvents}"), ct);
            if (page.Events.Count == 0)
            {
                throw new SyncFailureException($"Le cloud n'a renvoyé aucun événement après {next} (attendu jusqu'à {until}).");
            }

            foreach (var e in page.Events)
            {
                if (e.Sequence != next + 1)
                {
                    throw new SyncFailureException($"Événements du cloud non contigus : {e.Sequence} reçu, {next + 1} attendu.");
                }

                if (e.Sequence > until)
                {
                    yield break;
                }

                next = e.Sequence;
                yield return e;
            }
        }
    }
}

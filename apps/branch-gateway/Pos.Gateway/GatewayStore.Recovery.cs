using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pos.Core.Gateway;
using Pos.Core.Printing;
using Pos.Sync;

namespace Pos.Gateway;

public static class RecoveryStatus
{
    public const string Normal = "normal";
    public const string Restored = "restored";
    public const string CloudAhead = "cloud_ahead";
    public const string Reconciled = "reconciled";
    public const string Fenced = "fenced";
}

public sealed record RecoveryState(string Status, string? Reason, string? Since, string? BackupId)
{
    public bool IsNormal => Status == RecoveryStatus.Normal;
}

public sealed record QuarantineItem(long Id, string Kind, bool Blocking, long? Sequence, string? EventId, string Detail, string CreatedAt, string? ResolvedAt, string? Resolution);

/// <summary>
/// Recovery state (gates shared writes), quarantine of unresolved differences, an append-only audit trail, and the
/// import of cloud-confirmed events into a restored database. Nothing here invents an operation: only events the
/// cloud durably holds (full order snapshots) are imported, with their original ids and sequences.
/// </summary>
public sealed partial class GatewayStore
{
    /// <summary>Owner of operations whose terminal is not in this (restored) database: enrolled after the backup.</summary>
    public const string UnknownTerminalName = "Terminal inconnu (restauration)";

    public const string RecoveryInProgressCode = "recovery_in_progress";

    public RecoveryState GetRecoveryState()
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, "SELECT status, reason, since, backup_id FROM recovery_state WHERE id = 1;");
        using var reader = query.ExecuteReader();
        reader.Read();
        return new RecoveryState(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public void SetRecoveryState(string status, string reason, string? backupId = null)
    {
        using var connection = Database.OpenConnection();
        SetRecoveryState(connection, null, status, reason, backupId);
    }

    internal void SetRecoveryState(SqliteConnection connection, SqliteTransaction? tx, string status, string reason, string? backupId = null)
    {
        using var update = Command(connection, tx, "UPDATE recovery_state SET status = $s, reason = $r, since = $now, backup_id = COALESCE($b, backup_id) WHERE id = 1;");
        update.Parameters.AddWithValue("$s", status);
        update.Parameters.AddWithValue("$r", reason);
        update.Parameters.AddWithValue("$now", Now());
        update.Parameters.AddWithValue("$b", (object?)backupId ?? DBNull.Value);
        update.ExecuteNonQuery();
        Audit(DataDir, "recovery_state", new { status, reason, backupId });
        LogRecoveryState(_logger, status, reason);
    }

    /// <summary>Inside the writer transaction: shared writes (and burning unknown operation ids) wait for a verified recovery.</summary>
    internal static void RequireNormal(SqliteConnection connection, SqliteTransaction tx)
    {
        using var query = Command(connection, tx, "SELECT status, reason FROM recovery_state WHERE id = 1;");
        using var reader = query.ExecuteReader();
        if (reader.Read() && reader.GetString(0) != RecoveryStatus.Normal)
        {
            // 423: definitive "not applied" for the terminal (a 5xx would mean "outcome unknown, retry").
            throw new GatewayException(423, RecoveryInProgressCode,
                $"Passerelle en récupération ({reader.GetString(0)}) : les modifications partagées sont suspendues jusqu'à la vérification par un administrateur. " +
                (reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
    }

    internal static void AddQuarantine(SqliteConnection connection, SqliteTransaction? tx, string now, string kind, bool blocking, long? sequence, string? eventId, string detail, string? payload)
    {
        using var insert = Command(connection, tx, """
            INSERT INTO recovery_quarantine (kind, blocking, sequence, event_id, detail, payload, created_at) VALUES ($k, $b, $s, $e, $d, $p, $now);
            """);
        insert.Parameters.AddWithValue("$k", kind);
        insert.Parameters.AddWithValue("$b", blocking ? 1 : 0);
        insert.Parameters.AddWithValue("$s", (object?)sequence ?? DBNull.Value);
        insert.Parameters.AddWithValue("$e", (object?)eventId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$d", detail);
        insert.Parameters.AddWithValue("$p", (object?)payload ?? DBNull.Value);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
    }

    public IReadOnlyList<QuarantineItem> ListQuarantine(bool unresolvedOnly = false)
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, $"""
            SELECT id, kind, blocking, sequence, event_id, detail, created_at, resolved_at, resolution FROM recovery_quarantine
            {(unresolvedOnly ? "WHERE resolved_at IS NULL" : "")} ORDER BY id;
            """);
        using var reader = query.ExecuteReader();
        var list = new List<QuarantineItem>();
        while (reader.Read())
        {
            list.Add(new QuarantineItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) == 1, reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list;
    }

    /// <summary>Records an operator decision on a quarantined difference. The item is kept (audit), only marked resolved.</summary>
    public bool ResolveQuarantine(long id, string resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution) || resolution.Length > 500)
        {
            throw new Pos.Core.PosValidationException("invalid_resolution", "Décrivez la décision (1 à 500 caractères).");
        }

        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, "UPDATE recovery_quarantine SET resolved_at = $now, resolution = $r WHERE id = $id AND resolved_at IS NULL;");
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$r", resolution.Trim());
        update.Parameters.AddWithValue("$now", Now());
        var done = update.ExecuteNonQuery() == 1;
        if (done)
        {
            Audit(DataDir, "quarantine_resolved", new { id, resolution });
        }

        return done;
    }

    /// <summary>
    /// Ends a recovery: only from 'restored' (no cloud link), 'reconciled' or 'fenced' (after re-linking), with an intact
    /// database and no unresolved blocking quarantine item. Shared writes resume afterwards.
    /// </summary>
    public RecoveryState VerifyRecovery(bool withoutCloud)
    {
        var state = GetRecoveryState();
        if (state.IsNormal)
        {
            return state;
        }

        using (var connection = Database.OpenConnection())
        {
            var check = Convert.ToString(Scalar(connection, null, "PRAGMA integrity_check;"), CultureInfo.InvariantCulture);
            if (check != "ok")
            {
                throw new Pos.Core.PosValidationException("integrity_failed", $"Base de données endommagée : {check}");
            }
        }

        var blocking = ListQuarantine(unresolvedOnly: true).Where(q => q.Blocking).ToList();
        if (blocking.Count > 0)
        {
            throw new Pos.Core.PosValidationException("quarantine_unresolved",
                $"{blocking.Count} différence(s) bloquante(s) en quarantaine (voir « Pos.Gateway quarantine »). Résolvez-les avant de reprendre le service.");
        }

        var linked = GetCloudLink() is { IsLinked: true };
        var allowed = state.Status switch
        {
            RecoveryStatus.Reconciled => true,
            RecoveryStatus.Restored or RecoveryStatus.CloudAhead => withoutCloud && !linked,
            _ => false,
        };
        if (!allowed)
        {
            throw new Pos.Core.PosValidationException("reconcile_first", state.Status switch
            {
                RecoveryStatus.Fenced => "Passerelle révoquée par le cloud (remplacée ?). Ne reprenez le service qu'après avoir lié CETTE passerelle (« cloud-relink ») puis « reconcile ».",
                _ when linked => "Passerelle liée au cloud : exécutez d'abord « Pos.Gateway reconcile » (comparaison avec les événements confirmés par le cloud).",
                _ => "Aucun cloud lié : confirmez explicitement avec --without-cloud (les opérations postérieures à la sauvegarde ne peuvent pas être récupérées).",
            });
        }

        SetRecoveryState(RecoveryStatus.Normal, $"Récupération vérifiée (depuis « {state.Status} »){(withoutCloud ? ", sans cloud" : "")}.");
        Audit(DataDir, "recovery_verified", new { from = state.Status, withoutCloud, quarantine = ListQuarantine().Count });
        return GetRecoveryState();
    }

    /// <summary>
    /// After a restore, queued/printing/failed jobs may already have been printed by the replaced gateway: they become
    /// 'uncertain' (an operator decides; nothing is reprinted automatically).
    /// </summary>
    internal static int MarkPrintJobsUncertain(SqliteConnection connection, SqliteTransaction tx, string now, string reason)
    {
        using var events = Command(connection, tx, """
            INSERT INTO print_job_events (job_id, at, event, detail)
            SELECT id, $now, 'recovered_uncertain', $reason FROM print_jobs WHERE status IN ('queued', 'printing', 'failed');
            """);
        events.Parameters.AddWithValue("$now", now);
        events.Parameters.AddWithValue("$reason", reason);
        events.ExecuteNonQuery();
        using var update = Command(connection, tx, """
            UPDATE print_jobs SET status = 'uncertain', next_attempt_at = NULL, updated_at = $now, last_error = $reason
            WHERE status IN ('queued', 'printing', 'failed');
            """);
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$reason", reason);
        return update.ExecuteNonQuery();
    }

    /// <summary>
    /// Imports one event the cloud confirmed (sequence = local last + 1) with its ORIGINAL event/operation id. Returns
    /// null on success, or why it cannot be applied (the caller rolls back and quarantines; nothing is guessed).
    /// </summary>
    internal static string? ApplyConfirmedEvent(SqliteConnection connection, SqliteTransaction tx, SyncEvent e, PrintQueue printQueue, string now, List<string> notes)
    {
        var last = Convert.ToInt64(Scalar(connection, tx, "SELECT COALESCE(MAX(sequence), 0) FROM sync_outbox;"), CultureInfo.InvariantCulture);
        if (e.Sequence != last + 1)
        {
            return $"séquence {e.Sequence} non contiguë (dernière locale {last})";
        }

        using (var known = Command(connection, tx, "SELECT (SELECT COUNT(*) FROM sync_outbox WHERE event_id = $id) + (SELECT COUNT(*) FROM operations WHERE operation_id = $id);"))
        {
            known.Parameters.AddWithValue("$id", e.EventId);
            if (Convert.ToInt64(known.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return $"l'identifiant {e.EventId} existe déjà localement avec un autre rôle";
            }
        }

        long? position = null;
        var intents = new List<PrintIntent>();
        if (e.Type != SyncEventTypes.CatalogPublished)
        {
            if (e.Order is not { } order)
            {
                return "événement de commande sans instantané";
            }

            var terminal = EnsureTerminal(connection, tx, e.TerminalId, now, notes);
            if (SyncEventTypes.OrderEvents.Contains(e.Type) && e.Type != SyncEventTypes.OrderImported)
            {
                // request_hash NULL marks an imported operation: a terminal retrying the same id gets the stored result.
                using var insert = Command(connection, tx, """
                    INSERT INTO operations (operation_id, terminal_id, type, status, at) VALUES ($op, $terminal, $type, 'applied', $at) RETURNING position;
                    """);
                insert.Parameters.AddWithValue("$op", e.EventId);
                insert.Parameters.AddWithValue("$terminal", terminal);
                insert.Parameters.AddWithValue("$type", e.Type);
                insert.Parameters.AddWithValue("$at", e.OccurredAt);
                position = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (UpsertOrder(connection, tx, order, terminal) is { } error)
            {
                return error;
            }

            if (e.Type == SyncEventTypes.Submit)
            {
                foreach (var station in e.PrintStations ?? [])
                {
                    if (CreateUncertainIntent(connection, tx, order, station, e.EventId, printQueue, now) is { } intent)
                    {
                        intents.Add(intent);
                    }
                    else
                    {
                        notes.Add($"commande n°{order.Number} : poste {station} absent localement, aucun ticket recréé");
                    }
                }
            }

            if (position is { } p)
            {
                var current = OrderService.LoadOrder(connection, tx, order.Id)!;
                using var update = Command(connection, tx, "UPDATE operations SET order_id = $order, result_version = $v, response_json = $json WHERE position = $p;");
                update.Parameters.AddWithValue("$order", order.Id);
                update.Parameters.AddWithValue("$v", current.Version);
                update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new OperationResult(e.EventId, p, false, current, intents, []), GatewayProtocol.Json));
                update.Parameters.AddWithValue("$p", p);
                update.ExecuteNonQuery();
            }
        }

        using var outbox = Command(connection, tx, """
            INSERT INTO sync_outbox (sequence, event_id, type, operation_position, payload, created_at, acked_at) VALUES ($s, $id, $type, $pos, $payload, $at, $now);
            """);
        outbox.Parameters.AddWithValue("$s", e.Sequence);
        outbox.Parameters.AddWithValue("$id", e.EventId);
        outbox.Parameters.AddWithValue("$type", e.Type);
        outbox.Parameters.AddWithValue("$pos", (object?)position ?? DBNull.Value);
        outbox.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(e, SyncProtocol.Json));
        outbox.Parameters.AddWithValue("$at", e.OccurredAt);
        outbox.Parameters.AddWithValue("$now", now);
        outbox.ExecuteNonQuery();
        return null;
    }

    private static string EnsureTerminal(SqliteConnection connection, SqliteTransaction tx, string? terminalId, string now, List<string> notes)
    {
        var id = terminalId ?? "unknown-terminal";
        using var exists = Command(connection, tx, "SELECT COUNT(*) FROM terminals WHERE id = $id;");
        exists.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            // Enrolled after the backup: its public key is lost, so it is recorded as revoked (it must be re-paired).
            using var insert = Command(connection, tx, """
                INSERT INTO terminals (id, branch_id, name, public_key, status, enrolled_at, revoked_at)
                VALUES ($id, (SELECT branch_id FROM gateway_identity WHERE id = 1), $name, X'', 'revoked', $now, $now);
                """);
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$name", UnknownTerminalName);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
            notes.Add($"terminal {id} inconnu de la sauvegarde : enregistré comme révoqué, à réappairer");
        }

        return id;
    }

    private static string? UpsertOrder(SqliteConnection connection, SqliteTransaction tx, SyncOrder order, string terminal)
    {
        long? existingVersion = null;
        using (var find = Command(connection, tx, "SELECT version FROM shared_orders WHERE id = $id;"))
        {
            find.Parameters.AddWithValue("$id", order.Id);
            if (find.ExecuteScalar() is long v)
            {
                existingVersion = v;
            }
        }

        using (var number = Command(connection, tx, "SELECT COUNT(*) FROM shared_orders WHERE number = $n AND id <> $id;"))
        {
            number.Parameters.AddWithValue("$n", order.Number);
            number.Parameters.AddWithValue("$id", order.Id);
            if (Convert.ToInt64(number.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return $"le n° de commande {order.Number} est déjà utilisé localement par une autre commande";
            }
        }

        if (existingVersion is { } existing && existing >= order.Version)
        {
            return $"commande n°{order.Number} : version locale {existing} ≥ version confirmée {order.Version}";
        }

        var submitted = order.Status == "submitted";
        using (var upsert = Command(connection, tx, existingVersion is null
            ? """
              INSERT INTO shared_orders (id, branch_id, number, label, status, version, currency, created_by, created_at, updated_by, updated_at, submitted_by, submitted_at)
              VALUES ($id, (SELECT branch_id FROM gateway_identity WHERE id = 1), $number, $label, $status, $version, $currency, $terminal, $created, $terminal, $updated, $submittedBy, $submittedAt);
              """
            : """
              UPDATE shared_orders SET label = $label, status = $status, version = $version, updated_by = $terminal, updated_at = $updated,
                  submitted_by = COALESCE(submitted_by, $submittedBy), submitted_at = COALESCE(submitted_at, $submittedAt) WHERE id = $id;
              """))
        {
            upsert.Parameters.AddWithValue("$id", order.Id);
            upsert.Parameters.AddWithValue("$number", order.Number);
            upsert.Parameters.AddWithValue("$label", (object?)order.Label ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$status", order.Status);
            upsert.Parameters.AddWithValue("$version", order.Version);
            upsert.Parameters.AddWithValue("$currency", order.Currency);
            upsert.Parameters.AddWithValue("$terminal", terminal);
            upsert.Parameters.AddWithValue("$created", order.CreatedAt);
            upsert.Parameters.AddWithValue("$updated", order.UpdatedAt);
            upsert.Parameters.AddWithValue("$submittedBy", submitted ? terminal : DBNull.Value);
            upsert.Parameters.AddWithValue("$submittedAt", (object?)order.SubmittedAt ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        using (var delete = Command(connection, tx, "DELETE FROM shared_order_lines WHERE order_id = $id;"))
        {
            delete.Parameters.AddWithValue("$id", order.Id);
            delete.ExecuteNonQuery();
        }

        var lineNo = 0;
        foreach (var line in order.Lines)
        {
            using var insert = Command(connection, tx, """
                INSERT INTO shared_order_lines (order_id, product_id, line_no, product_name, unit_price_minor, quantity) VALUES ($o, $p, $n, $name, $price, $q);
                """);
            insert.Parameters.AddWithValue("$o", order.Id);
            insert.Parameters.AddWithValue("$p", line.ProductId);
            insert.Parameters.AddWithValue("$n", ++lineNo);
            insert.Parameters.AddWithValue("$name", line.Name);
            insert.Parameters.AddWithValue("$price", line.UnitPriceMinor);
            insert.Parameters.AddWithValue("$q", line.Quantity);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint: product absent from the restored catalog
            {
                return $"commande n°{order.Number} : produit {line.ProductId} absent du catalogue local";
            }
        }

        return null;
    }

    private static PrintIntent? CreateUncertainIntent(SqliteConnection connection, SqliteTransaction tx, SyncOrder order, string stationId, string operationId, PrintQueue printQueue, string now)
    {
        string name;
        int width;
        using (var query = Command(connection, tx, "SELECT name, paper_width_mm FROM print_destinations WHERE id = $id AND kind = 'station';"))
        {
            query.Parameters.AddWithValue("$id", stationId);
            using var reader = query.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            (name, width) = (reader.GetString(0), reader.GetInt32(1));
        }

        using (var exists = Command(connection, tx, "SELECT print_job_id FROM print_intents WHERE order_id = $o AND station_id = $s;"))
        {
            exists.Parameters.AddWithValue("$o", order.Id);
            exists.Parameters.AddWithValue("$s", stationId);
            if (exists.ExecuteScalar() is string existingJob)
            {
                return new PrintIntent(stationId, existingJob); // one intent per (order, station), never two
            }
        }

        var lines = order.Lines.Where(l => l.StationId == stationId).ToList();
        var number = order.Number.ToString("D3", CultureInfo.InvariantCulture);
        var source = new TicketSource(TicketLayout.StationKind, number, DateTimeOffset.Parse(order.SubmittedAt ?? order.UpdatedAt, CultureInfo.InvariantCulture), name,
            [.. lines.Select(l => new TicketItem(l.Name, l.Quantity, l.UnitPriceMinor, l.LineTotalMinor))], lines.Sum(l => l.Quantity), lines.Sum(l => l.LineTotalMinor), order.Currency);
        var jobId = printQueue.Enqueue(connection, tx, new NewPrintJob(TicketLayout.StationKind, stationId, order.Id, number, source, width, 0, null));
        const string Reason = "Récupération : ce ticket a pu être imprimé par la passerelle remplacée. Vérifiez avant toute réimpression.";
        using (var update = Command(connection, tx, "UPDATE print_jobs SET status = 'uncertain', next_attempt_at = NULL, last_error = $r, updated_at = $now WHERE id = $id;"))
        {
            update.Parameters.AddWithValue("$id", jobId);
            update.Parameters.AddWithValue("$r", Reason);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
        }

        using (var log = Command(connection, tx, "INSERT INTO print_job_events (job_id, at, event, detail) VALUES ($id, $now, 'recovered_uncertain', $r);"))
        {
            log.Parameters.AddWithValue("$id", jobId);
            log.Parameters.AddWithValue("$now", now);
            log.Parameters.AddWithValue("$r", Reason);
            log.ExecuteNonQuery();
        }

        using (var intent = Command(connection, tx, "INSERT INTO print_intents (order_id, station_id, print_job_id, operation_id, created_at) VALUES ($o, $s, $j, $op, $now);"))
        {
            intent.Parameters.AddWithValue("$o", order.Id);
            intent.Parameters.AddWithValue("$s", stationId);
            intent.Parameters.AddWithValue("$j", jobId);
            intent.Parameters.AddWithValue("$op", operationId);
            intent.Parameters.AddWithValue("$now", now);
            intent.ExecuteNonQuery();
        }

        return new PrintIntent(stationId, jobId);
    }

    /// <summary>
    /// Replacement hardware (the DPAPI-protected key of the old gateway cannot be used here) or a revoked credential:
    /// forget the current credential (kept in the audit) and register again with a new one-time code. With a code
    /// issued with --replace, the cloud revokes the previous gateway of the branch in the same step.
    /// </summary>
    public void RelinkCloud(string url, string code, string? pin)
    {
        var previous = GetCloudLink();
        using (var connection = Database.OpenConnection())
        {
            Execute(connection, null, "UPDATE cloud_link SET gateway_id = NULL, private_key = NULL, linked_at = NULL WHERE id = 1;");
        }

        RequestCloudLink(url, code, pin);
        Audit(DataDir, "cloud_relink", new { previousGatewayId = previous?.GatewayId, url });
    }

    /// <summary>Append-only JSON lines in {data}\audit\recovery.jsonl: outside the database, so it survives restores.</summary>
    public static void Audit(string dataDir, string action, object details)
    {
        var dir = Path.Combine(dataDir, "audit");
        Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            action,
            user = Environment.UserName,
            machine = Environment.MachineName,
            details,
        }, GatewayProtocol.Json);
        File.AppendAllText(Path.Combine(dir, "recovery.jsonl"), line + Environment.NewLine);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery state is now {RecoveryStatus}: {Reason}")]
    private static partial void LogRecoveryState(ILogger logger, string recoveryStatus, string reason);
}

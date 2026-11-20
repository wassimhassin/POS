using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pos.Core;
using Pos.Core.Gateway;
using Pos.Sync;

namespace Pos.Gateway;

public sealed record CloudLink(
    string CloudUrl,
    string? CloudPin,
    string? PendingCode,
    string? GatewayId,
    string? OrganizationId,
    string? OrganizationName,
    string? BusinessId,
    string? BusinessName,
    byte[]? ProtectedPrivateKey,
    string RequestedAt,
    string? LinkedAt,
    byte[]? PendingProtectedKey = null)
{
    public bool IsLinked => GatewayId is not null;
}

public sealed record SyncStatus(
    long AckedSequence,
    long LastSequence,
    string? LastSuccessAt,
    string? LastAttemptAt,
    int ConsecutiveFailures,
    string? LastError,
    long? BlockedSequence)
{
    public long Pending => LastSequence - AckedSequence;
}

public sealed record OutboxEntry(long Sequence, string Payload);

/// <summary>Cloud synchronization storage: outbox, cloud link and checkpoint (same SQLite file, same single writer).</summary>
public sealed partial class GatewayStore
{
    private static readonly byte[] CloudKeyEntropy = "PosSaaS.Gateway.Cloud.v1"u8.ToArray();

    /// <summary>
    /// Appends an immutable event to the outbox INSIDE the caller's transaction, so a change and its outbox entry
    /// commit (or roll back) together. The sequence is MAX+1 under the SQLite write lock: contiguous, no gaps.
    /// </summary>
    internal static long AppendOutbox(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventId,
        string type,
        long? operationPosition,
        string? terminalId,
        SyncOrder? order,
        IReadOnlyList<string>? printStations,
        IReadOnlyList<SyncProduct>? catalog,
        string now)
    {
        var sequence = Convert.ToInt64(Scalar(connection, tx, "SELECT COALESCE(MAX(sequence), 0) + 1 FROM sync_outbox;"), CultureInfo.InvariantCulture);
        var e = new SyncEvent(sequence, eventId, type, SyncProtocol.SchemaVersion, now, terminalId, order, printStations, catalog);
        using var insert = Command(connection, tx, """
            INSERT INTO sync_outbox (sequence, event_id, type, operation_position, payload, created_at)
            VALUES ($seq, $id, $type, $position, $payload, $now);
            """);
        insert.Parameters.AddWithValue("$seq", sequence);
        insert.Parameters.AddWithValue("$id", eventId);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$position", (object?)operationPosition ?? DBNull.Value);
        insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(e, SyncProtocol.Json));
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
        return sequence;
    }

    internal static SyncOrder ToSyncOrder(SharedOrder o) => new(
        o.Id, o.Number, o.Label, o.Status, o.Version, o.Currency,
        [.. o.Lines.Select(l => new SyncOrderLine(l.ProductId, l.Name, l.UnitPriceMinor, l.Quantity, l.LineTotalMinor, l.StationId))],
        o.ItemCount, o.TotalMinor, o.CreatedByName, o.UpdatedByName, o.SubmittedByName, o.CreatedAt, o.UpdatedAt, o.SubmittedAt);

    /// <summary>Migration 2: orders created before the outbox existed are exported once, as snapshots, so the cloud is complete.</summary>
    private void BackfillOutbox(SqliteConnection connection, SqliteTransaction tx)
    {
        var ids = new List<string>();
        using (var query = Command(connection, tx, "SELECT id FROM shared_orders ORDER BY number;"))
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        var now = Now();
        foreach (var id in ids)
        {
            var order = OrderService.LoadOrder(connection, tx, id)!;
            AppendOutbox(connection, tx, Guid.NewGuid().ToString("D"), SyncEventTypes.OrderImported, null, null, ToSyncOrder(order), null, null, now);
        }

        if (ids.Count > 0)
        {
            _logger.LogInformation("Outbox backfilled with {OrderCount} existing orders", ids.Count);
        }
    }

    /// <summary>
    /// Publishes the local catalog to the outbox when it changed (one-way, for cloud reporting). Called at
    /// startup and by the sync loop. Returns true if an event was appended.
    /// </summary>
    public bool PublishCatalogIfChanged()
    {
        using var connection = Database.OpenConnection();
        using var tx = connection.BeginTransaction();
        var products = new List<SyncProduct>();
        using (var query = Command(connection, tx, "SELECT id, name, category, price_minor, currency, station_id, is_active FROM products ORDER BY sort_order, id;"))
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                products.Add(new SyncProduct(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6) == 1));
            }
        }

        var hash = SyncProtocol.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(products, SyncProtocol.Json));
        if (Scalar(connection, tx, "SELECT catalog_hash FROM sync_state WHERE id = 1;") as string == hash)
        {
            return false;
        }

        var sequence = AppendOutbox(connection, tx, Guid.NewGuid().ToString("D"), SyncEventTypes.CatalogPublished, null, null, null, null, products, Now());
        using (var update = Command(connection, tx, "UPDATE sync_state SET catalog_hash = $hash WHERE id = 1;"))
        {
            update.Parameters.AddWithValue("$hash", hash);
            update.ExecuteNonQuery();
        }

        tx.Commit();
        _logger.LogInformation("Catalog snapshot ({ProductCount} products) queued for the cloud at sequence {Sequence}", products.Count, sequence);
        return true;
    }

    public CloudLink? GetCloudLink()
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, """
            SELECT cloud_url, cloud_pin, pending_code, gateway_id, organization_id, organization_name, business_id, business_name,
                   private_key, requested_at, linked_at, pending_private_key
            FROM cloud_link WHERE id = 1;
            """);
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new CloudLink(reader.GetString(0), Text(1), Text(2), Text(3), Text(4), Text(5), Text(6), Text(7),
            reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8), reader.GetString(9), Text(10),
            reader.IsDBNull(11) ? null : (byte[])reader.GetValue(11));
    }

    /// <summary>
    /// Records the cloud address and a one-time provisioning code (administrator, CLI). The RUNNING gateway
    /// performs the registration, so its private key is created and DPAPI-protected by the gateway's own account.
    /// </summary>
    public void RequestCloudLink(string url, string code, string? pin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/"
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            throw new PosValidationException("invalid_url", "L'adresse du cloud doit être https://hôte[:port] (HTTP non chiffré interdit).");
        }

        if (SyncProtocol.NormalizeCode(code).Length != 20)
        {
            throw new PosValidationException("invalid_code", "Code de provisionnement invalide.");
        }

        var normalizedPin = pin is null ? null : GatewayProtocol.NormalizeFingerprint(pin);
        if (pin is not null && normalizedPin is not { Length: 64 })
        {
            throw new PosValidationException("invalid_pin", "Empreinte SHA-256 invalide (64 caractères hexadécimaux).");
        }

        if (GetCloudLink() is { IsLinked: true } existing)
        {
            throw new PosValidationException("already_linked", $"Passerelle déjà liée au cloud (passerelle {existing.GatewayId}).");
        }

        using var connection = Database.OpenConnection();
        using var upsert = Command(connection, null, """
            INSERT INTO cloud_link (id, cloud_url, cloud_pin, pending_code, requested_at) VALUES (1, $url, $pin, $code, $now)
            ON CONFLICT (id) DO UPDATE SET cloud_url = $url, cloud_pin = $pin, pending_code = $code, pending_private_key = NULL, requested_at = $now;
            UPDATE sync_state SET last_error = NULL, consecutive_failures = 0 WHERE id = 1;
            """);
        upsert.Parameters.AddWithValue("$url", uri.GetLeftPart(UriPartial.Authority) + "/");
        upsert.Parameters.AddWithValue("$pin", (object?)normalizedPin ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$code", SyncProtocol.NormalizeCode(code));
        upsert.Parameters.AddWithValue("$now", Now());
        upsert.ExecuteNonQuery();
        _logger.LogInformation("Cloud link requested to {CloudUrl}", uri);
    }

    internal void CompleteCloudLink(RegisterGatewayResponse registration, byte[] privateKeyPkcs8)
    {
        var protectedKey = ProtectedData.Protect(privateKeyPkcs8, CloudKeyEntropy, DataProtectionScope.CurrentUser);
        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, """
            UPDATE cloud_link SET pending_code = NULL, pending_private_key = NULL, gateway_id = $gateway, organization_id = $org, organization_name = $orgName,
                business_id = $business, business_name = $businessName, private_key = $key, linked_at = $now
            WHERE id = 1;
            """);
        update.Parameters.AddWithValue("$gateway", registration.GatewayId);
        update.Parameters.AddWithValue("$org", registration.OrganizationId);
        update.Parameters.AddWithValue("$orgName", registration.OrganizationName);
        update.Parameters.AddWithValue("$business", registration.BusinessId);
        update.Parameters.AddWithValue("$businessName", registration.BusinessName);
        update.Parameters.AddWithValue("$key", protectedKey);
        update.Parameters.AddWithValue("$now", Now());
        update.ExecuteNonQuery();
        _logger.LogInformation("Linked to the cloud as gateway {GatewayId}: organization {OrganizationId}, business {BusinessId}, branch {BranchId}",
            registration.GatewayId, registration.OrganizationId, registration.BusinessId, registration.BranchId);
    }

    /// <summary>
    /// The key this gateway registers with for the current provisioning code: created and stored (DPAPI) before the
    /// first attempt, then reused by every retry, so a lost reply never turns into a second identity.
    /// </summary>
    internal ECDsa PendingCloudKey(CloudLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.PendingProtectedKey is { } stored)
        {
            return ImportProtected(stored);
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            using var connection = Database.OpenConnection();
            using var update = Command(connection, null,
                "UPDATE cloud_link SET pending_private_key = $key WHERE id = 1 AND pending_code = $code AND pending_private_key IS NULL;");
            update.Parameters.AddWithValue("$key", ProtectedData.Protect(pkcs8, CloudKeyEntropy, DataProtectionScope.CurrentUser));
            update.Parameters.AddWithValue("$code", link.PendingCode);
            if (update.ExecuteNonQuery() != 1)
            {
                key.Dispose();
                throw new SyncFailureException("Demande de liaison modifiée pendant l'enregistrement ; nouvel essai.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        return key;
    }

    internal static ECDsa LoadCloudKey(CloudLink link) => ImportProtected(link.ProtectedPrivateKey!);

    private static ECDsa ImportProtected(byte[] protectedKey)
    {
        var pkcs8 = ProtectedData.Unprotect(protectedKey, CloudKeyEntropy, DataProtectionScope.CurrentUser);
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public SyncStatus GetSyncStatus()
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, """
            SELECT s.acked_sequence, (SELECT COALESCE(MAX(sequence), 0) FROM sync_outbox), s.last_success_at, s.last_attempt_at,
                   s.consecutive_failures, s.last_error, s.blocked_sequence
            FROM sync_state s WHERE s.id = 1;
            """);
        using var reader = query.ExecuteReader();
        reader.Read();
        return new SyncStatus(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    /// <summary>Next events after <paramref name="afterSequence"/>, bounded by count and by size (at least one).</summary>
    internal List<OutboxEntry> PendingEvents(long afterSequence, int maxEvents, int maxBytes)
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, "SELECT sequence, payload FROM sync_outbox WHERE sequence > $after ORDER BY sequence LIMIT $limit;");
        query.Parameters.AddWithValue("$after", afterSequence);
        query.Parameters.AddWithValue("$limit", maxEvents);
        using var reader = query.ExecuteReader();
        var list = new List<OutboxEntry>();
        var bytes = 0;
        while (reader.Read())
        {
            var payload = reader.GetString(1);
            bytes += payload.Length;
            if (list.Count > 0 && bytes > maxBytes)
            {
                break;
            }

            list.Add(new OutboxEntry(reader.GetInt64(0), payload));
        }

        return list;
    }

    internal void MarkAttempt(long fromSequence, long toSequence)
    {
        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, """
            UPDATE sync_outbox SET attempts = attempts + 1, last_attempt_at = $now WHERE sequence BETWEEN $from AND $to;
            UPDATE sync_state SET last_attempt_at = $now WHERE id = 1;
            """);
        update.Parameters.AddWithValue("$from", fromSequence);
        update.Parameters.AddWithValue("$to", toSequence);
        update.Parameters.AddWithValue("$now", Now());
        update.ExecuteNonQuery();
    }

    /// <summary>Advances the acknowledged checkpoint (never backwards here; see <see cref="Rewind"/>).</summary>
    internal void AckThrough(long sequence)
    {
        using var connection = Database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var update = Command(connection, tx, """
            UPDATE sync_outbox SET acked_at = $now WHERE sequence <= $seq AND acked_at IS NULL;
            UPDATE sync_state SET acked_sequence = MAX(acked_sequence, $seq) WHERE id = 1;
            """);
        update.Parameters.AddWithValue("$seq", sequence);
        update.Parameters.AddWithValue("$now", Now());
        update.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>The cloud lost events it had acknowledged (e.g. restored from a backup): resend them, nothing is lost locally.</summary>
    internal void Rewind(long sequence)
    {
        using var connection = Database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var update = Command(connection, tx, """
            UPDATE sync_outbox SET acked_at = NULL WHERE sequence > $seq;
            UPDATE sync_state SET acked_sequence = $seq WHERE id = 1;
            """);
        update.Parameters.AddWithValue("$seq", sequence);
        update.ExecuteNonQuery();
        tx.Commit();
        _logger.LogWarning("Cloud checkpoint is behind the gateway: rewinding to sequence {Sequence} and resending", sequence);
    }

    internal void RecordSyncSuccess()
    {
        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, """
            UPDATE sync_state SET last_success_at = $now, consecutive_failures = 0, last_error = NULL, blocked_sequence = NULL WHERE id = 1;
            """);
        update.Parameters.AddWithValue("$now", Now());
        update.ExecuteNonQuery();
    }

    internal void RecordSyncFailure(string error, long? blockedSequence)
    {
        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, """
            UPDATE sync_state SET consecutive_failures = consecutive_failures + 1, last_error = $error, blocked_sequence = $blocked WHERE id = 1;
            """);
        update.Parameters.AddWithValue("$error", error.Length > 500 ? error[..500] : error);
        update.Parameters.AddWithValue("$blocked", (object?)blockedSequence ?? DBNull.Value);
        update.ExecuteNonQuery();
    }
}

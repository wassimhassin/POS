using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Sync;

/// <summary>
/// Gateway → cloud synchronization protocol (v1).
///
/// The branch gateway is the authoritative writer of operational orders. Every committed change is
/// appended, in the same SQLite transaction, to a durable outbox as an immutable <see cref="SyncEvent"/>
/// with a contiguous per-branch <see cref="SyncEvent.Sequence"/> (1, 2, 3…). The gateway pushes bounded
/// batches; the cloud accepts them strictly in order, deduplicates by (branch, sequence) and
/// (branch, event id), and answers with the highest contiguous sequence it has durably committed.
/// Delivery is at-least-once; processing is idempotent. Tenant, business and branch are never taken
/// from the request: the cloud derives them from the authenticated gateway credential.
/// </summary>
public static class SyncProtocol
{
    public const int SchemaVersion = 1;
    public const int MaxBatchEvents = 100;
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int MaxOrderLines = 500;
    public const int MaxCatalogProducts = 5000;
    public const string GatewayScheme = "PosGateway";
    public const string ReportScheme = "Bearer";
    public static readonly TimeSpan SignatureValidity = TimeSpan.FromMinutes(5);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
    };

    /// <summary>What a gateway signs to register its public key with a one-time provisioning code.</summary>
    public static byte[] RegisterPayload(string code, string branchId, byte[] publicKey) =>
        Encoding.UTF8.GetBytes($"pos-cloud-register-v1\n{NormalizeCode(code)}\n{branchId}\n{Sha256Hex(publicKey)}");

    /// <summary>What a gateway signs for every request (domain-separated, bound to method, path, time and body).</summary>
    public static byte[] RequestPayload(string gatewayId, string method, string pathAndQuery, long unixSeconds, string bodySha256) =>
        Encoding.UTF8.GetBytes($"pos-cloud-request-v1\n{gatewayId}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{unixSeconds}\n{bodySha256}");

    /// <summary>Authorization header value: <c>PosGateway {gatewayId}:{unixSeconds}:{base64 signature}</c>.</summary>
    public static string AuthorizationParameter(string gatewayId, long unixSeconds, byte[] signature) =>
        $"{gatewayId}:{unixSeconds}:{Convert.ToBase64String(signature)}";

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    public static string NormalizeCode(string code) =>
        new([.. code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    public static bool IsId(string? value) => Guid.TryParseExact(value, "D", out _);

    /// <summary>Canonical hash of an event: identical events always hash identically (used to detect conflicting replays).</summary>
    public static string EventHash(SyncEvent e) => Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(e, Json));
}

public static class SyncEventTypes
{
    public const string CreateOrder = "create_order";
    public const string AddItem = "add_item";
    public const string SetQuantity = "set_quantity";
    public const string Submit = "submit";

    /// <summary>Snapshot of an order that existed before the outbox was introduced (migration backfill).</summary>
    public const string OrderImported = "order_imported";

    /// <summary>One-way (gateway → cloud) snapshot of the branch's local catalog, for reporting only.</summary>
    public const string CatalogPublished = "catalog_published";

    public static readonly IReadOnlySet<string> OrderEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        CreateOrder, AddItem, SetQuantity, Submit, OrderImported,
    };

    public static bool IsKnown(string type) => OrderEvents.Contains(type) || type == CatalogPublished;
}

public sealed record SyncOrderLine(string ProductId, string Name, long UnitPriceMinor, int Quantity, long LineTotalMinor, string? StationId);

/// <summary>Full state of an order after the operation (state transfer: the cloud never recomputes branch state).</summary>
public sealed record SyncOrder(
    string Id,
    int Number,
    string? Label,
    string Status,
    long Version,
    string Currency,
    IReadOnlyList<SyncOrderLine> Lines,
    int ItemCount,
    long TotalMinor,
    string CreatedByName,
    string UpdatedByName,
    string? SubmittedByName,
    string CreatedAt,
    string UpdatedAt,
    string? SubmittedAt);

public sealed record SyncProduct(string Id, string Name, string Category, long PriceMinor, string Currency, string? StationId, bool IsActive);

public sealed record SyncEvent(
    long Sequence,
    string EventId,
    string Type,
    int SchemaVersion,
    string OccurredAt,
    string? TerminalId,
    SyncOrder? Order,
    IReadOnlyList<string>? PrintStations,
    IReadOnlyList<SyncProduct>? Catalog);

public sealed record SyncBatch(IReadOnlyList<SyncEvent> Events);

public static class SyncOutcomeStatus
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
}

public sealed record SyncEventOutcome(long Sequence, string EventId, string Status);

/// <summary>Why the cloud stopped processing a batch at <see cref="Sequence"/>. Nothing after it was accepted.</summary>
public sealed record SyncRejection(long Sequence, string Code, string Message, long? ExpectedSequence);

/// <summary>
/// <see cref="LastAcceptedSequence"/> is the highest contiguous sequence durably committed in the cloud for
/// this branch (it can only be acknowledged after the PostgreSQL commit).
/// </summary>
public sealed record SyncBatchResult(long LastAcceptedSequence, IReadOnlyList<SyncEventOutcome> Outcomes, SyncRejection? Rejected);

public static class SyncRejectionCodes
{
    /// <summary>The batch does not start right after the cloud's last accepted sequence; resend from <c>ExpectedSequence</c>.</summary>
    public const string SequenceGap = "sequence_gap";

    /// <summary>The cloud already holds a DIFFERENT event at this sequence or with this id: needs operator reconciliation.</summary>
    public const string SequenceConflict = "sequence_conflict";

    /// <summary>The event is invalid (schema, values). Not skipped: synchronization stops here until resolved.</summary>
    public const string EventRejected = "event_rejected";
}

public sealed record RegisterGatewayRequest(string Code, string BranchId, string PublicKey, string Signature);

public sealed record RegisterGatewayResponse(
    string GatewayId,
    string OrganizationId,
    string OrganizationName,
    string BusinessId,
    string BusinessName,
    string BranchId,
    string BranchName);

public sealed record CloudError(string Code, string Message);

/// <summary>Authenticated gateway errors that the gateway acts on (not only displays).</summary>
public static class CloudErrorCodes
{
    /// <summary>The credential was revoked (e.g. replaced by another gateway): the gateway must stop serving writes.</summary>
    public const string GatewayRevoked = "gateway_revoked";

    /// <summary>The branch already has an active gateway; issue a code with --replace to replace it explicitly.</summary>
    public const string GatewayAlreadyActive = "gateway_already_active";
}

// --- Recovery (Phase 1.5): a gateway reads back ITS OWN branch's confirmed events ---

/// <summary>The cloud checkpoint for the authenticated gateway's branch.</summary>
public sealed record SyncCloudState(long LastSequence);

/// <summary>Confirmed events after a sequence, in order (GET /v1/sync/events?after=N&amp;limit=M).</summary>
public sealed record SyncEventPage(long LastSequence, IReadOnlyList<SyncEvent> Events);

// --- Reporting (read-only) ---

public sealed record BranchReport(
    string Id,
    string Name,
    string BusinessId,
    int OrderCount,
    int SubmittedCount,
    long SubmittedTotalMinor,
    long LastSequence,
    string? LastSyncAt);

public sealed record BusinessReport(string Id, string Name, string OrganizationId, IReadOnlyList<BranchReport> Branches);

public sealed record OrganizationReport(string Id, string Name, IReadOnlyList<BusinessReport> Businesses);

public sealed record ReportOrder(string BranchId, long LastSequence, SyncOrder Order);

public sealed record ReportCatalog(string BranchId, long LastSequence, IReadOnlyList<SyncProduct> Products);

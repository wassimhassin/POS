using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Core.Gateway;

/// <summary>
/// Wire contract between POS terminals and the branch LAN gateway (shared by both sides).
/// All IDs are GUIDs ("D" format). Every mutation carries a client-generated operation ID that is the
/// idempotency key: replaying it returns the original result and never re-applies the change.
/// </summary>
public static class GatewayProtocol
{
    public const string ApiVersion = "v1";
    public const string HubPath = "/v1/hub";
    public const string OrderChangedMethod = "orderChanged";
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxLabelLength = 60;
    public const int MaxQuantity = 999;

    /// <summary>Strict options: unknown members, missing required members and loose number formats are rejected.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
    };

    /// <summary>What a terminal signs to obtain a session token (domain-separated, bound to branch and gateway certificate).</summary>
    public static byte[] AuthPayload(string branchId, string terminalId, string nonce, string certificateSha256) =>
        Encoding.UTF8.GetBytes($"pos-gateway-auth-v1\n{branchId}\n{terminalId}\n{nonce}\n{certificateSha256}");

    /// <summary>Proof of possession of the private key at enrollment.</summary>
    public static byte[] EnrollPayload(string terminalId, string code, string certificateSha256) =>
        Encoding.UTF8.GetBytes($"pos-gateway-enroll-v1\n{terminalId}\n{NormalizeCode(code)}\n{certificateSha256}");

    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    /// <summary>Accepts "AB:CD…", "ab cd…" or "ABCD…"; returns 64 uppercase hex chars or null.</summary>
    public static string? NormalizeFingerprint(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var hex = new string(value.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    public static string NormalizeCode(string code) =>
        new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static bool IsId(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);
}

public static class SharedOrderStatus
{
    public const string Open = "open";
    public const string Submitted = "submitted";
}

public sealed record EnrollRequest(string Code, string TerminalId, string PublicKey, string Signature);

public sealed record EnrollResponse(string BranchId, string BranchName, string TerminalId, string TerminalName);

public sealed record ChallengeRequest(string TerminalId);

public sealed record ChallengeResponse(string Nonce);

public sealed record TokenRequest(string TerminalId, string Nonce, string Signature);

public sealed record TokenResponse(string Token, DateTimeOffset ExpiresAt, string BranchId, string BranchName, string TerminalName);

public sealed record SharedOrderLine(string ProductId, string Name, long UnitPriceMinor, int Quantity, long LineTotalMinor, string? StationId);

public sealed record SharedOrder(
    string Id,
    int Number,
    string? Label,
    string Status,
    long Version,
    IReadOnlyList<SharedOrderLine> Lines,
    int ItemCount,
    long TotalMinor,
    string Currency,
    string CreatedByName,
    string UpdatedByName,
    string CreatedAt,
    string UpdatedAt,
    string? SubmittedAt,
    string? SubmittedByName);

/// <summary>Full state at log position <see cref="Position"/> (recovery after connect/reconnect).</summary>
public sealed record OrdersSnapshot(long Position, IReadOnlyList<SharedOrder> Orders);

public sealed record CreateOrderRequest(string OperationId, string? Label);

/// <summary>Additive and commutative: never needs a version (two terminals adding items both win).</summary>
public sealed record AddItemRequest(string OperationId, string ProductId, int Quantity);

/// <summary>Overwrites a quantity: rejected with <c>version_conflict</c> if the order changed since <see cref="ExpectedVersion"/>.</summary>
public sealed record SetQuantityRequest(string OperationId, string ProductId, int Quantity, long ExpectedVersion);

/// <summary>Submits exactly the version the operator saw.</summary>
public sealed record SubmitRequest(string OperationId, long ExpectedVersion);

public sealed record PrintIntent(string StationId, string PrintJobId);

public sealed record OperationResult(
    string OperationId,
    long Position,
    bool Replayed,
    SharedOrder Order,
    IReadOnlyList<PrintIntent> PrintIntents,
    IReadOnlyList<string> UnroutedItems);

public static class OperationStatus
{
    public const string Applied = "applied";
    /// <summary>Never applied; the ID is burned so a late duplicate can never apply it.</summary>
    public const string Abandoned = "abandoned";
}

public sealed record OperationResolution(string OperationId, string Status, OperationResult? Result);

/// <summary>Error body for every non-2xx response. <see cref="Current"/> is set on version conflicts.</summary>
public sealed record GatewayError(string Code, string Message, SharedOrder? Current = null);

public sealed record OrderChangedEvent(long Position, SharedOrder Order);

public sealed record TerminalInfo(string TerminalId, string TerminalName, string BranchId, string BranchName);

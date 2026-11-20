using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Sync;

namespace Pos.Cloud;

/// <summary>
/// Gateway authentication: every request carries
/// <c>Authorization: PosGateway {gatewayId}:{unixSeconds}:{signature}</c>, an ECDSA P-256 signature (with the
/// gateway's private key, which never leaves the gateway) over method, path, timestamp and body hash. The
/// cloud stores only the public key. Requests older or newer than 5 minutes are refused. A replay inside that
/// window can only resend an identical batch, which sync processing treats as a duplicate.
/// </summary>
public sealed partial class GatewaySignatureHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    CloudDb db,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var prefix = SyncProtocol.GatewayScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var parts = header[prefix.Length..].Split(':');
        var signature = new byte[64];
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "D", out var gatewayId) || !long.TryParse(parts[1], out var unixSeconds)
            || !Convert.TryFromBase64String(parts[2], signature, out var signatureLength) || signatureLength != 64)
        {
            return Fail("malformed");
        }

        if (Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - unixSeconds) > SyncProtocol.SignatureValidity.TotalSeconds)
        {
            return Fail("stale");
        }

        // Before the tenant is known: a narrow lookup of this one credential (row-level security hides every table row).
        var credential = await db.GatewayCredentialAsync(gatewayId, Context.RequestAborted);
        if (credential is null)
        {
            return Fail("unknown gateway");
        }

        Request.EnableBuffering(SyncProtocol.MaxBodyBytes);
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, Context.RequestAborted);
        Request.Body.Position = 0;

        var payload = SyncProtocol.RequestPayload(parts[0], Request.Method, $"{Request.Path}{Request.QueryString}", unixSeconds,
            SyncProtocol.Sha256Hex(body.GetBuffer().AsSpan(0, (int)body.Length)));
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(credential.PublicKey, out _);
        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256))
        {
            return Fail("bad signature");
        }

        if (credential.Revoked)
        {
            // Told only to the holder of the private key (signature verified above): the gateway fences itself.
            Context.Items[RevokedMarker] = true;
            return Fail("revoked gateway");
        }

        var principal = new GatewayPrincipal(gatewayId, credential.OrganizationId, credential.BusinessId, credential.BranchId);
        Context.Items[typeof(GatewayPrincipal)] = principal;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, gatewayId.ToString())], SyncProtocol.GatewayScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SyncProtocol.GatewayScheme));
    }

    internal static readonly object RevokedMarker = new();

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => CloudApi.WriteUnauthorized(Context);

    private AuthenticateResult Fail(string reason)
    {
        LogRefused(Logger, reason, Context.Connection.RemoteIpAddress?.ToString());
        return AuthenticateResult.Fail(reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway authentication refused ({Reason}) from {RemoteAddress}")]
    private static partial void LogRefused(ILogger logger, string reason, string? remoteAddress);
}

/// <summary>A read-only reporting credential and the scope it covers.</summary>
public sealed record ReportPrincipal(Guid KeyId, Guid OrganizationId, Guid? BusinessId, Guid? BranchId)
{
    public bool CoversOrganization(Guid organizationId) => OrganizationId == organizationId && BusinessId is null;

    public bool CoversBusiness(Business business) =>
        OrganizationId == business.OrganizationId && BranchId is null && (BusinessId is null || BusinessId == business.Id);

    public bool CoversBranch(Branch branch) =>
        OrganizationId == branch.OrganizationId && (BusinessId is null || BusinessId == branch.BusinessId) && (BranchId is null || BranchId == branch.Id);
}

/// <summary>
/// Reporting keys: <c>Authorization: Bearer posrk.{keyId}.{secret}</c>. Only SHA-256(secret) is stored
/// (the secret is 256 random bits, so a slow hash adds nothing); comparison is constant-time.
/// </summary>
public sealed partial class ReportKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    CloudDb db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string Prefix = "posrk";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var parts = header[7..].Split('.');
        if (parts.Length != 3 || parts[0] != Prefix || !Guid.TryParseExact(parts[1], "N", out var keyId) || parts[2].Length != 64)
        {
            return Fail("malformed");
        }

        // Pre-tenant lookup of this one key, answered only for the matching secret (256-bit; compared as its SHA-256).
        var key = await db.ReportKeyAsync(keyId, SyncProtocol.Sha256Hex(Encoding.ASCII.GetBytes(parts[2])), Context.RequestAborted);
        if (key is null)
        {
            return Fail("invalid key");
        }

        Context.Items[typeof(ReportPrincipal)] = new ReportPrincipal(keyId, key.OrganizationId, key.BusinessId, key.BranchId);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, keyId.ToString())], SyncProtocol.ReportScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SyncProtocol.ReportScheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => CloudApi.WriteUnauthorized(Context);

    /// <summary>Creates a key; returns the full token, shown once.</summary>
    public static (ReportKey Key, string Token) Create(Guid organizationId, Guid? businessId, Guid? branchId, string name, DateTimeOffset now)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var key = new ReportKey
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BusinessId = businessId,
            BranchId = branchId,
            Name = name,
            SecretSha256 = SyncProtocol.Sha256Hex(Encoding.ASCII.GetBytes(secret)),
            CreatedAt = now,
        };
        return (key, $"{Prefix}.{key.Id:N}.{secret}");
    }

    private AuthenticateResult Fail(string reason)
    {
        LogRefused(Logger, reason, Context.Connection.RemoteIpAddress?.ToString());
        return AuthenticateResult.Fail(reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Report key refused ({Reason}) from {RemoteAddress}")]
    private static partial void LogRefused(ILogger logger, string reason, string? remoteAddress);
}

/// <summary>
/// Gateway registration with a one-time provisioning code + proof of possession of its private key.
/// A code is consumed by exactly one key. Presenting it again with that same key (the reply was lost: timeout,
/// dropped connection) returns the same registration instead of an error, so the gateway can retry safely;
/// any other key still gets invalid_code. Concurrent attempts are serialized by the code row lock.
/// </summary>
public sealed partial class GatewayRegistration(CloudDb db, TimeProvider time, ILogger<GatewayRegistration> logger)
{
    /// <summary>How long after first use the same key may replay a code (a gateway retries within minutes; offline gateways for days).</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromDays(7);

    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    private const int CodeLength = 20;

    public static string NewCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return string.Join('-', chars.Chunk(5).Select(c => new string(c)));
    }

    public static string HashCode(string code) => SyncProtocol.Sha256Hex(Encoding.UTF8.GetBytes(SyncProtocol.NormalizeCode(code)));

    public async Task<RegisterGatewayResponse> RegisterAsync(RegisterGatewayRequest request, CancellationToken ct)
    {
        if (SyncProtocol.NormalizeCode(request.Code).Length != CodeLength || !Guid.TryParseExact(request.BranchId, "D", out var branchId))
        {
            throw Invalid();
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.PublicKey);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || !key.VerifyData(SyncProtocol.RegisterPayload(request.Code, request.BranchId, publicKey), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256))
            {
                throw Invalid();
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw Invalid();
        }

        var now = time.GetUtcNow();
        var hash = HashCode(request.Code);
        // The code alone says which organization it belongs to; everything after runs inside that tenant only.
        var organizationId = await db.ProvisioningCodeOrganizationAsync(hash, ct) ?? throw Invalid();
        await using var tx = await db.BeginTenantAsync(organizationId, ct);
        var code = await db.ProvisioningCodes.FromSql($"SELECT * FROM provisioning_codes WHERE code_hash = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        var fingerprint = SyncProtocol.Sha256Hex(publicKey);
        if (code is { UsedAt: { } usedAt, UsedByGatewayId: { } usedBy })
        {
            // Idempotent retry: only the very key that consumed the code (possession just proven above), for the same
            // branch, while that credential is still active and within the replay window. Nothing is written.
            var previous = await db.GatewayCredentials.AsNoTracking().SingleOrDefaultAsync(g => g.Id == usedBy, ct);
            if (previous is null || previous.RevokedAt is not null || previous.BranchId != branchId || now - usedAt > ReplayWindow
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(previous.PublicKeySha256), Encoding.ASCII.GetBytes(fingerprint)))
            {
                throw Invalid();
            }

            var replayed = await ResponseAsync(previous.Id, code, ct);
            await tx.CommitAsync(ct);
            LogReplayed(logger, previous.Id, branchId);
            return replayed;
        }

        if (code is null || code.UsedAt is not null || code.ExpiresAt <= now)
        {
            throw Invalid();
        }

        if (code.BranchId != branchId)
        {
            // The gateway's own branch id (from its local identity) must be the one the code was issued for.
            throw new CloudException(409, "branch_mismatch", "Ce code a été émis pour une autre succursale que celle de cette passerelle.");
        }

        // One active gateway per branch: a second registration is refused unless the code was issued as an
        // explicit replacement, which revokes the previous credential in the same transaction (audited in the log).
        var active = await db.GatewayCredentials
            .FromSql($"SELECT * FROM gateway_credentials WHERE branch_id = {branchId} AND revoked_at IS NULL FOR UPDATE").ToListAsync(ct);
        if (active.Count > 0 && !code.ReplacesActive)
        {
            throw new CloudException(409, CloudErrorCodes.GatewayAlreadyActive,
                "Cette succursale a déjà une passerelle active. Émettez un code de remplacement (« gateway-code --replace ») après avoir vérifié que l'ancienne est hors service.");
        }

        foreach (var previous in active)
        {
            previous.RevokedAt = now;
            LogReplaced(logger, previous.Id, branchId);
            Identity.AuthAudit.Add(db, time, PlatformEvents.GatewayReplaced, null, null, organizationId: code.OrganizationId, details: $"gateway={previous.Id:D};branch={branchId:D}");
        }

        // Keys are unique across ALL tenants: asked through a yes/no lookup, never by reading other tenants' rows.
        if (await db.GatewayKeyRegisteredAsync(fingerprint, ct))
        {
            throw new CloudException(409, "key_already_registered", "Cette clé est déjà enregistrée.");
        }

        var credential = new GatewayCredential
        {
            Id = Guid.NewGuid(),
            OrganizationId = code.OrganizationId,
            BusinessId = code.BusinessId,
            BranchId = code.BranchId,
            PublicKey = publicKey,
            PublicKeySha256 = fingerprint,
            RegisteredAt = now,
        };
        db.GatewayCredentials.Add(credential);
        code.UsedAt = now;
        code.UsedByGatewayId = credential.Id;
        Identity.AuthAudit.Add(db, time, PlatformEvents.GatewayRegistered, null, null, organizationId: code.OrganizationId,
            details: $"gateway={credential.Id:D};branch={branchId:D};replaced={active.Count}");
        await db.SaveChangesAsync(ct);
        var response = await ResponseAsync(credential.Id, code, ct);
        await tx.CommitAsync(ct);
        LogRegistered(logger, credential.Id, branchId, code.BusinessId, code.OrganizationId);
        return response;
    }

    private async Task<RegisterGatewayResponse> ResponseAsync(Guid gatewayId, ProvisioningCode code, CancellationToken ct)
    {
        var names = await (from b in db.Branches
                           join bu in db.Businesses on b.BusinessId equals bu.Id
                           join o in db.Organizations on bu.OrganizationId equals o.Id
                           where b.Id == code.BranchId
                           select new { Branch = b.Name, Business = bu.Name, Organization = o.Name }).SingleAsync(ct);
        return new RegisterGatewayResponse(gatewayId.ToString("D"), code.OrganizationId.ToString("D"), names.Organization,
            code.BusinessId.ToString("D"), names.Business, code.BranchId.ToString("D"), names.Branch);
    }

    private static CloudException Invalid() => new(401, "invalid_code", "Code de provisionnement invalide, expiré ou déjà utilisé, ou preuve de clé invalide.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway {GatewayId} of branch {BranchId} revoked: replaced by a newly registered gateway")]
    private static partial void LogReplaced(ILogger logger, Guid gatewayId, Guid branchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway {GatewayId} of branch {BranchId} repeated its registration (same key): previous reply returned")]
    private static partial void LogReplayed(ILogger logger, Guid gatewayId, Guid branchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway {GatewayId} registered for branch {BranchId} (business {BusinessId}, organization {OrganizationId})")]
    private static partial void LogRegistered(ILogger logger, Guid gatewayId, Guid branchId, Guid businessId, Guid organizationId);
}

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pos.Core.Gateway;

namespace Pos.Gateway;

public sealed record TerminalSession(string TerminalId, string TerminalName, string BranchId, DateTimeOffset ExpiresAt);

/// <summary>
/// Terminal authentication without any shared secret:
///  - enrollment: a one-time pairing code (generated on the gateway by an administrator) + the terminal's
///    ECDSA P-256 public key + a signature proving possession of the private key;
///  - login: challenge/response — the terminal signs a single-use nonce bound to the branch and to the
///    gateway's certificate fingerprint, and receives a random bearer token (1 h, memory only).
/// Tokens are stored as SHA-256 hashes and die with the process: after a gateway restart every terminal
/// re-authenticates. Revocation is checked on every request.
/// </summary>
public sealed class TerminalAuth(GatewayStore store, GatewayCertificate certificate, TimeProvider time, ILogger<TerminalAuth> logger)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(60);
    private const int MaxPendingChallenges = 10_000;

    private readonly ConcurrentDictionary<string, (string TerminalId, DateTimeOffset ExpiresAt)> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);

    public EnrollResponse Enroll(EnrollRequest request)
    {
        var identity = store.GetIdentity() ?? throw new InvalidOperationException("Gateway not initialized.");
        if (!GatewayProtocol.IsId(request.TerminalId) || request.Code.Length > 64)
        {
            throw Unauthorized();
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.PublicKey);
            using var key = ImportP256(publicKey);
            var signature = Convert.FromBase64String(request.Signature);
            if (!key.VerifyData(GatewayProtocol.EnrollPayload(request.TerminalId, request.Code, certificate.Fingerprint), signature, HashAlgorithmName.SHA256))
            {
                throw Unauthorized();
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new GatewayException(400, "invalid_key", "Clé publique ou signature invalide.");
        }

        var terminal = store.EnrollTerminal(request.Code, request.TerminalId, publicKey, identity.BranchId);
        if (terminal is null)
        {
            logger.LogWarning("Enrollment rejected for {TerminalId}: invalid, expired or used code", request.TerminalId);
            throw Unauthorized();
        }

        return new EnrollResponse(identity.BranchId, identity.BranchName, terminal.Id, terminal.Name);
    }

    public ChallengeResponse Challenge(ChallengeRequest request)
    {
        if (!GatewayProtocol.IsId(request.TerminalId))
        {
            throw Unauthorized();
        }

        Sweep();
        if (_challenges.Count >= MaxPendingChallenges)
        {
            throw new GatewayException(503, "busy", "Trop de demandes d'authentification en cours.");
        }

        // A nonce is returned even for unknown terminals: no enumeration of enrolled IDs.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _challenges[nonce] = (request.TerminalId, time.GetUtcNow().Add(ChallengeLifetime));
        return new ChallengeResponse(nonce);
    }

    public TokenResponse IssueToken(TokenRequest request)
    {
        // Single use: removed before verification, whatever the outcome.
        if (!_challenges.TryRemove(request.Nonce, out var challenge)
            || challenge.TerminalId != request.TerminalId
            || challenge.ExpiresAt < time.GetUtcNow())
        {
            throw Unauthorized();
        }

        var identity = store.GetIdentity()!;
        var terminal = store.GetTerminal(request.TerminalId);
        if (terminal is null || terminal.Status != "active" || terminal.BranchId != identity.BranchId)
        {
            logger.LogWarning("Authentication refused for {TerminalId}: unknown, revoked or foreign terminal", request.TerminalId);
            throw Unauthorized();
        }

        try
        {
            using var key = ImportP256(terminal.PublicKey);
            var payload = GatewayProtocol.AuthPayload(identity.BranchId, terminal.Id, request.Nonce, certificate.Fingerprint);
            if (!key.VerifyData(payload, Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256))
            {
                logger.LogWarning("Authentication refused for {TerminalId}: bad signature", request.TerminalId);
                throw Unauthorized();
            }
        }
        catch (FormatException)
        {
            throw Unauthorized();
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = time.GetUtcNow().Add(TokenLifetime);
        _sessions[HashToken(token)] = new TerminalSession(terminal.Id, terminal.Name, identity.BranchId, expiresAt);
        store.TouchTerminal(terminal.Id);
        logger.LogInformation("Terminal authenticated {TerminalId} {TerminalName}", terminal.Id, terminal.Name);
        return new TokenResponse(token, expiresAt, identity.BranchId, identity.BranchName, terminal.Name);
    }

    /// <summary>Valid, unexpired token of a still-active terminal, or null.</summary>
    public TerminalSession? Validate(string token)
    {
        var hash = HashToken(token);
        if (!_sessions.TryGetValue(hash, out var session))
        {
            return null;
        }

        if (session.ExpiresAt < time.GetUtcNow() || !IsActive(session.TerminalId))
        {
            _sessions.TryRemove(hash, out _);
            return null;
        }

        return session;
    }

    public bool IsActive(string terminalId) => store.GetTerminal(terminalId)?.Status == "active";

    public static GatewayException Unauthorized() => new(401, "unauthorized", "Authentification refusée.");

    private static ECDsa ImportP256(byte[] subjectPublicKeyInfo)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            key.Dispose();
            throw new CryptographicException("Only ECDSA P-256 keys are accepted.");
        }

        return key;
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (nonce, challenge) in _challenges)
        {
            if (challenge.ExpiresAt < now)
            {
                _challenges.TryRemove(nonce, out _);
            }
        }

        foreach (var (hash, session) in _sessions)
        {
            if (session.ExpiresAt < now)
            {
                _sessions.TryRemove(hash, out _);
            }
        }
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>ASP.NET Core scheme: "Authorization: Bearer &lt;token&gt;" only (never a query string, so tokens do not end up in logs).</summary>
public sealed class TerminalTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    TerminalAuth auth) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "TerminalToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 200)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var session = auth.Validate(header["Bearer ".Length..]);
        if (session is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired token."));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.TerminalId),
            new Claim(ClaimTypes.Name, session.TerminalName),
            new Claim("branch", session.BranchId),
        ], SchemeName));
        Context.Items[typeof(TerminalSession)] = session;
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Response.WriteAsJsonAsync(new GatewayError("unauthorized", "Authentification requise."), GatewayProtocol.Json);
    }
}

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Pos.Cloud.Identity;

/// <summary>What an authenticated session may do with respect to the second factor.</summary>
public static class MfaStates
{
    /// <summary>Fully signed in (second factor done, or not required for this user).</summary>
    public const string Ok = "ok";

    /// <summary>Password verified; only the second-factor step (and logout) is allowed.</summary>
    public const string Challenge = "challenge";

    /// <summary>Privileged user without MFA: only enrolment, identity and session endpoints are allowed.</summary>
    public const string Enroll = "enroll";
}

/// <summary>The authenticated cloud user of this request (set by <see cref="SessionHandler"/>).</summary>
public sealed record UserPrincipal(Guid UserId, Guid SessionId, string MfaState);

/// <summary>Server-side sessions behind the <c>__Host-pos_session</c> cookie.</summary>
public sealed class SessionService(CloudDb db, TimeProvider time)
{
    public const string Scheme = "Session";
    public const string CookieName = "__Host-pos_session";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RotationInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RotationGrace = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_touchInterval = TimeSpan.FromMinutes(1);

    /// <summary>Creates a session, sets its cookie, and returns it. The token exists only in the cookie.</summary>
    public async Task<UserSession> CreateAsync(CloudUser user, string kind, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(http);
        var now = time.GetUtcNow();
        var token = AuthSecrets.NewToken();
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = AuthSecrets.Hash(token),
            Kind = kind,
            SecurityStamp = user.SecurityStamp ?? "",
            CreatedAt = now,
            RotatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + (kind == SessionKinds.MfaPending ? PendingLifetime : AbsoluteLifetime),
            IpAddress = Truncate(http.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 200),
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);
        SetCookie(http, token, session.ExpiresAt);
        return session;
    }

    /// <summary>
    /// The live session for a cookie token, or null. Expired, revoked, idle and stamp-mismatched sessions are refused
    /// (and revoked). A full session is re-keyed every hour; the replaced token stays valid for one minute.
    /// </summary>
    public async Task<(UserSession Session, CloudUser User)?> ValidateAsync(string token, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (token.Length is < 40 or > 64)
        {
            return null;
        }

        var hash = AuthSecrets.Hash(token);
        var session = await db.UserSessions.SingleOrDefaultAsync(s => s.TokenHash == hash || s.PreviousTokenHash == hash, ct);
        if (session is null || session.RevokedAt is not null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var viaPrevious = !session.TokenHash.AsSpan().SequenceEqual(hash);
        if (viaPrevious && now - session.RotatedAt > RotationGrace)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId, ct);
        string? invalid = user is null ? "user_deleted"
            : now >= session.ExpiresAt ? "expired"
            : now - session.LastSeenAt >= IdleTimeout ? "idle"
            : !string.Equals(session.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal) ? "security_stamp"
            : null;
        if (invalid is not null)
        {
            session.RevokedAt = now;
            session.RevokeReason = invalid;
            await db.SaveChangesAsync(ct);
            return null;
        }

        var changed = false;
        if (!viaPrevious && session.Kind == SessionKinds.Full && now - session.RotatedAt >= RotationInterval)
        {
            var fresh = AuthSecrets.NewToken();
            session.PreviousTokenHash = session.TokenHash;
            session.TokenHash = AuthSecrets.Hash(fresh);
            session.RotatedAt = now;
            SetCookie(http, fresh, session.ExpiresAt);
            changed = true;
        }

        if (now - session.LastSeenAt >= s_touchInterval)
        {
            session.LastSeenAt = now;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        return (session, user!);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid sessionId, string reason, CancellationToken ct)
    {
        var count = await db.UserSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, time.GetUtcNow()).SetProperty(s => s.RevokeReason, reason), ct);
        return count > 0;
    }

    /// <summary>Revokes every live session of the user, except <paramref name="keep"/>. Returns how many.</summary>
    public Task<int> RevokeAllAsync(Guid userId, Guid? keep, string reason, CancellationToken ct) =>
        db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (keep == null || s.Id != keep))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, time.GetUtcNow()).SetProperty(s => s.RevokeReason, reason), ct);

    public static void SetCookie(HttpContext http, string token, DateTimeOffset expires)
    {
        ArgumentNullException.ThrowIfNull(http);
        // __Host-: Secure, Path=/ and no Domain are enforced by browsers. Strict: never sent on cross-site requests.
        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
            IsEssential = true,
        });
    }

    public static void ClearCookie(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(CookieName, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Authenticates cloud users from the session cookie. Gateways and report keys never go through here.</summary>
public sealed partial class SessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    SessionService sessions,
    CloudDb db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string MfaStateClaim = "pos_mfa_state";
    public const string SessionIdClaim = "pos_sid";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionService.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var result = await sessions.ValidateAsync(token, Context, Context.RequestAborted);
        if (result is not { } valid)
        {
            SessionService.ClearCookie(Context);
            LogRefused(Logger, Context.Connection.RemoteIpAddress?.ToString());
            return AuthenticateResult.Fail("invalid session");
        }

        var state = valid.Session.Kind == SessionKinds.MfaPending ? MfaStates.Challenge : await FullStateAsync(valid.Session.Id, valid.User.Id);
        Context.Items[typeof(UserPrincipal)] = new UserPrincipal(valid.User.Id, valid.Session.Id, state);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, valid.User.Id.ToString("D")),
                new Claim(SessionIdClaim, valid.Session.Id.ToString("D")),
                new Claim(MfaStateClaim, state),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>A privileged user (any live membership) without enabled MFA may only enrol.</summary>
    private async Task<string> FullStateAsync(Guid sessionId, Guid userId)
    {
        var ct = Context.RequestAborted;
        var privileged = (await db.SessionMembershipsAsync(sessionId, userId, ct)).Count > 0;
        if (!privileged)
        {
            return MfaStates.Ok;
        }

        return await db.UserMfa.AnyAsync(m => m.UserId == userId && m.EnabledAt != null, ct) ? MfaStates.Ok : MfaStates.Enroll;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => CloudApi.WriteUnauthorized(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var state = (Context.Items[typeof(UserPrincipal)] as UserPrincipal)?.MfaState;
        return state is MfaStates.Challenge or MfaStates.Enroll
            ? CloudApi.WriteErrorAsync(Context, 403, AuthErrors.MfaRequired,
                state == MfaStates.Challenge ? "Deuxième facteur requis pour terminer la connexion." : "Activez l'authentification à deux facteurs pour continuer.")
            : CloudApi.WriteErrorAsync(Context, 403, "forbidden", "Accès refusé.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session cookie refused from {RemoteAddress}")]
    private static partial void LogRefused(ILogger logger, string? remoteAddress);
}

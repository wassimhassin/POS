using Microsoft.AspNetCore.Authorization;
using Pos.Sync;

namespace Pos.Cloud.Identity;

public sealed record RegisterRequest(string Email, string Password);

public sealed record EmailRequest(string Email);

public sealed record TokenRequest(string Token);

public sealed record LoginRequest(string Email, string Password);

public sealed record MfaChallengeRequest(string? Code = null, string? RecoveryCode = null);

public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

public sealed record PasswordResetRequest(string Token, string NewPassword);

public sealed record CodeRequest(string Code);

public sealed record MfaDisableRequest(string Password, string Code);

/// <summary>Cloud user authentication endpoints (/v1/auth, /v1/me). Gateway and report-key endpoints are unaffected.</summary>
public static class AuthApi
{
    /// <summary>Fully signed in (second factor done or not required).</summary>
    public const string UserPolicy = "user";

    /// <summary>Signed in, or a privileged user who must still enrol MFA (identity, sessions and MFA setup only).</summary>
    public const string EnrollingPolicy = "user-enrolling";

    /// <summary>Password verified, second factor pending.</summary>
    public const string ChallengePolicy = "user-mfa-challenge";

    /// <summary>Any live session (logout).</summary>
    public const string AnySessionPolicy = "user-any-session";

    public const string AuthRateLimit = "auth";
    public const string MailRateLimit = "auth-mail";

    public static void AddPolicies(AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .AddPolicy(UserPolicy, p => p.AddAuthenticationSchemes(SessionService.Scheme).RequireClaim(SessionHandler.MfaStateClaim, MfaStates.Ok))
            .AddPolicy(EnrollingPolicy, p => p.AddAuthenticationSchemes(SessionService.Scheme).RequireClaim(SessionHandler.MfaStateClaim, MfaStates.Ok, MfaStates.Enroll))
            .AddPolicy(ChallengePolicy, p => p.AddAuthenticationSchemes(SessionService.Scheme).RequireClaim(SessionHandler.MfaStateClaim, MfaStates.Challenge))
            .AddPolicy(AnySessionPolicy, p => p.AddAuthenticationSchemes(SessionService.Scheme).RequireClaim(SessionHandler.MfaStateClaim, MfaStates.Ok, MfaStates.Enroll, MfaStates.Challenge));
    }

    public static void Map(RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        var auth = v1.MapGroup("/auth").WithTags("Authentication").ProducesErrors(403);

        auth.MapPost("/register", (RegisterRequest r, AuthService s, HttpContext h, CancellationToken ct) => Accepted(s.RegisterAsync(r.Email, r.Password, h, ct)))
            .AllowAnonymous().RequireRateLimiting(MailRateLimit)
            .WithName("Register").WithSummary("Create an account (no tenant access). Same answer whether or not the address already has an account.")
            .Produces<AuthStatus>(StatusCodes.Status202Accepted);

        auth.MapPost("/verify-email", (TokenRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.VerifyEmailAsync(r.Token, h, ct))
            .AllowAnonymous().RequireRateLimiting(AuthRateLimit)
            .WithName("VerifyEmail").WithSummary("Confirm the address with the emailed single-use token.")
            .Produces<AuthStatus>();

        auth.MapPost("/resend-verification", (EmailRequest r, AuthService s, CancellationToken ct) => Accepted(s.ResendVerificationAsync(r.Email, ct)))
            .AllowAnonymous().RequireRateLimiting(MailRateLimit)
            .WithName("ResendVerification").WithSummary("Send a new verification link if the address has an unverified account (always 202).")
            .Produces<AuthStatus>(StatusCodes.Status202Accepted);

        auth.MapPost("/login", (LoginRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.LoginAsync(r.Email, r.Password, h, ct))
            .AllowAnonymous().RequireRateLimiting(AuthRateLimit)
            .WithName("Login").WithSummary("Password step. Returns signed_in, or mfa_required with a short second-factor session.")
            .Produces<LoginResult>();

        auth.MapPost("/login/mfa", (MfaChallengeRequest r, AuthService s, HttpContext h, CancellationToken ct) =>
                s.CompleteMfaAsync(User(h), r.Code, r.RecoveryCode, h, ct))
            .RequireAuthorization(ChallengePolicy).RequireRateLimiting(AuthRateLimit)
            .WithName("CompleteMfaLogin").WithSummary("Second step: a TOTP code or one recovery code. Starts a new full session.")
            .Produces<LoginResult>();

        auth.MapPost("/logout", async (AuthService s, HttpContext h, CancellationToken ct) =>
            {
                await s.LogoutAsync(User(h), h, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AnySessionPolicy)
            .WithName("Logout").WithSummary("Revoke the current session.")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapPost("/password/forgot", (EmailRequest r, AuthService s, HttpContext h, CancellationToken ct) => Accepted(s.ForgotPasswordAsync(r.Email, h, ct)))
            .AllowAnonymous().RequireRateLimiting(MailRateLimit)
            .WithName("ForgotPassword").WithSummary("Email a reset link if the address has a verified account (always 202).")
            .Produces<AuthStatus>(StatusCodes.Status202Accepted);

        auth.MapPost("/password/reset", (PasswordResetRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.ResetPasswordAsync(r.Token, r.NewPassword, h, ct))
            .AllowAnonymous().RequireRateLimiting(AuthRateLimit)
            .WithName("ResetPassword").WithSummary("Set a new password with the emailed token; ends every session.")
            .Produces<AuthStatus>();

        auth.MapPost("/onboarding/accept", (TokenRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.AcceptOwnerInviteAsync(User(h), r.Token, h, ct))
            .RequireAuthorization(UserPolicy).RequireRateLimiting(AuthRateLimit)
            .WithName("AcceptOwnerInvite").WithSummary("Accept an owner invitation (verified, matching address and enabled MFA required).")
            .Produces<OnboardingResult>();

        var me = v1.MapGroup("/me").WithTags("Current user").ProducesErrors(403);

        me.MapGet("", (AuthService s, HttpContext h, CancellationToken ct) => s.MeAsync(User(h), ct))
            .RequireAuthorization(EnrollingPolicy)
            .WithName("GetCurrentUser").WithSummary("The signed-in user, MFA state and organization memberships.")
            .Produces<MeResponse>();

        me.MapGet("/sessions", (AuthService s, HttpContext h, CancellationToken ct) => s.SessionsAsync(User(h), ct))
            .RequireAuthorization(EnrollingPolicy)
            .WithName("ListSessions").WithSummary("Live sessions of the signed-in user.")
            .Produces<IReadOnlyList<SessionInfo>>();

        me.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, AuthService s, HttpContext h, CancellationToken ct) =>
                await s.RevokeSessionAsync(User(h), sessionId, h, ct) ? Results.NoContent() : Results.NotFound(new CloudError("not_found", "Ressource introuvable.")))
            .RequireAuthorization(EnrollingPolicy)
            .WithName("RevokeSession").WithSummary("Revoke one of the user's sessions (another user's session is 404).")
            .Produces(StatusCodes.Status204NoContent).ProducesErrors(404);

        me.MapPost("/sessions/revoke-others", (AuthService s, HttpContext h, CancellationToken ct) => s.RevokeOtherSessionsAsync(User(h), h, ct))
            .RequireAuthorization(EnrollingPolicy)
            .WithName("RevokeOtherSessions").WithSummary("Revoke every session except the current one.")
            .Produces<AuthStatus>();

        me.MapPost("/password", (PasswordChangeRequest r, AuthService s, HttpContext h, CancellationToken ct) =>
                s.ChangePasswordAsync(User(h), r.CurrentPassword, r.NewPassword, h, ct))
            .RequireAuthorization(UserPolicy).RequireRateLimiting(AuthRateLimit)
            .WithName("ChangePassword").WithSummary("Change the password; other sessions end and this one is re-keyed.")
            .Produces<AuthStatus>();

        me.MapPost("/mfa/totp", (AuthService s, HttpContext h, CancellationToken ct) => s.StartTotpSetupAsync(User(h), h, ct))
            .RequireAuthorization(EnrollingPolicy)
            .WithName("StartTotpSetup").WithSummary("New authenticator key (not active until confirmed).")
            .Produces<TotpSetup>().ProducesErrors(409);

        me.MapPost("/mfa/totp/confirm", (CodeRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.ConfirmTotpAsync(User(h), r.Code, h, ct))
            .RequireAuthorization(EnrollingPolicy).RequireRateLimiting(AuthRateLimit)
            .WithName("ConfirmTotp").WithSummary("Activate TOTP with a first code; returns recovery codes once.")
            .Produces<RecoveryCodes>().ProducesErrors(409);

        me.MapPost("/mfa/recovery-codes", (CodeRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.RegenerateRecoveryCodesAsync(User(h), r.Code, h, ct))
            .RequireAuthorization(UserPolicy).RequireRateLimiting(AuthRateLimit)
            .WithName("RegenerateRecoveryCodes").WithSummary("Replace all recovery codes (current TOTP code required).")
            .Produces<RecoveryCodes>().ProducesErrors(409);

        me.MapPost("/mfa/disable", (MfaDisableRequest r, AuthService s, HttpContext h, CancellationToken ct) => s.DisableMfaAsync(User(h), r.Password, r.Code, h, ct))
            .RequireAuthorization(UserPolicy).RequireRateLimiting(AuthRateLimit)
            .WithName("DisableMfa").WithSummary("Turn MFA off (password + code; refused for organization owners).")
            .Produces<AuthStatus>().ProducesErrors(409);
    }

    /// <summary>
    /// CSRF defence in depth for cookie-authenticated endpoints (SameSite=Strict is the first layer): a browser
    /// request from another origin is refused. Non-browser clients send no Origin and are unaffected.
    /// </summary>
    public static async Task GuardAsync(HttpContext http, Func<Task> next, AuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(settings);
        var path = http.Request.Path;
        if (path.StartsWithSegments("/v1/auth", StringComparison.Ordinal) || path.StartsWithSegments("/v1/me", StringComparison.Ordinal))
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            {
                var origin = http.Request.Headers.Origin.ToString();
                var fetchSite = http.Request.Headers["Sec-Fetch-Site"].ToString();
                if ((origin.Length > 0 && !string.Equals(origin, settings.PublicOrigin, StringComparison.OrdinalIgnoreCase))
                    || fetchSite == "cross-site")
                {
                    await CloudApi.WriteErrorAsync(http, 403, AuthErrors.OriginMismatch, "Requête refusée : origine non autorisée.");
                    return;
                }
            }
        }

        await next();
    }

    private static UserPrincipal User(HttpContext http) =>
        http.Items[typeof(UserPrincipal)] as UserPrincipal ?? throw new CloudException(401, "unauthorized", "Authentification requise ou refusée.");

    private static async Task<IResult> Accepted<T>(Task<T> work) => Results.Json(await work, statusCode: StatusCodes.Status202Accepted);
}

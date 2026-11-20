using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Pos.Cloud.Identity;

/// <summary>Settings of the user-authentication module (validated at startup by <see cref="CloudSettings"/>).</summary>
public sealed record AuthSettings(string PublicOrigin, MailSettings Mail, SecretKeyRing Keys);

/// <summary>Error codes of the authentication API (in addition to the generic envelope codes).</summary>
public static class AuthErrors
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string EmailNotVerified = "email_not_verified";
    public const string MfaRequired = "mfa_required";
    public const string InvalidCode = "invalid_code";
    public const string InvalidToken = "invalid_token";
    public const string WeakPassword = "weak_password";
    public const string InvalidEmail = "invalid_email";
    public const string MfaAlreadyEnabled = "mfa_already_enabled";
    public const string MfaNotEnabled = "mfa_not_enabled";
    public const string MfaRequiredForRole = "mfa_required_for_role";
    public const string OriginMismatch = "origin_mismatch";
}

/// <summary>Audit event names (stable: used by future security reports).</summary>
public static class AuthEvents
{
    public const string Registered = "user_registered";
    public const string RegisterExisting = "register_existing_address";
    public const string EmailVerified = "email_verified";
    public const string LoginSucceeded = "login_succeeded";
    public const string LoginFailed = "login_failed";
    public const string LoginUnverified = "login_email_not_verified";
    public const string LockedOut = "account_locked_out";
    public const string MfaChallengeSucceeded = "mfa_challenge_succeeded";
    public const string MfaChallengeFailed = "mfa_challenge_failed";
    public const string RecoveryCodeUsed = "recovery_code_used";
    public const string MfaSetupStarted = "mfa_setup_started";
    public const string MfaEnabled = "mfa_enabled";
    public const string MfaDisabled = "mfa_disabled";
    public const string RecoveryCodesRegenerated = "recovery_codes_regenerated";
    public const string PasswordChanged = "password_changed";
    public const string PasswordResetRequested = "password_reset_requested";
    public const string PasswordReset = "password_reset";
    public const string Logout = "logout";
    public const string SessionRevoked = "session_revoked";
    public const string OtherSessionsRevoked = "other_sessions_revoked";
    public const string OwnerInvited = "owner_invited";
    public const string OwnerInviteAccepted = "owner_invite_accepted";
}

public sealed record AuthStatus(string Status);

public sealed record LoginResult(string Status, int? RecoveryCodesRemaining = null);

public sealed record TotpSetup(string SharedKey, string OtpAuthUri);

public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record MembershipInfo(string OrganizationId, string Role);

public sealed record MeResponse(string Id, string Email, bool EmailVerified, bool MfaEnabled, bool MfaRequired, int RecoveryCodesRemaining, IReadOnlyList<MembershipInfo> Memberships);

public sealed record SessionInfo(string Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, string? IpAddress, string? UserAgent, bool Current);

public sealed record OnboardingResult(string OrganizationId, string Role);

/// <summary>
/// Registration, verification, login (+ TOTP / recovery codes), password change/reset, MFA management and owner
/// onboarding. Anti-enumeration: responses about an address never reveal whether it has an account; unknown
/// addresses cost a password hash like known ones.
/// </summary>
public sealed partial class AuthService(
    CloudDb db,
    UserManager<CloudUser> users,
    SessionService sessions,
    IAuthMailer mailer,
    AuthSettings settings,
    SecretKeyRing keys,
    TimeProvider time,
    ILogger<AuthService> logger)
{
    public const int MaxPasswordLength = 128;
    public const int RecoveryCodeCount = 10;
    private const int MaxMailsPerWindow = 3;
    private static readonly TimeSpan s_mailWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_verifyLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan s_resetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);

    // A real PBKDF2 hash of a random value: unknown addresses are checked against it, so they cost the same time.
    private static readonly Lazy<string> s_dummyHash = new(() => new PasswordHasher<CloudUser>().HashPassword(new CloudUser(), AuthSecrets.NewToken()));

    // ---------------------------------------------------------------- registration and verification

    public async Task<AuthStatus> RegisterAsync(string email, string password, HttpContext http, CancellationToken ct)
    {
        var address = ValidEmail(email);
        await ValidatePasswordAsync(password, address);
        var existing = await users.FindByEmailAsync(address);
        if (existing is not null)
        {
            _ = users.PasswordHasher.HashPassword(existing, password); // same cost as a creation (timing)
            await AuditAsync(AuthEvents.RegisterExisting, http, existing.Id, ct: ct);
            if (await MailBudgetAsync(existing.Id, ct))
            {
                if (existing.EmailConfirmed)
                {
                    await mailer.SendAsync(address, "Votre compte PosSaaS",
                        "Une inscription a été demandée avec cette adresse, qui a déjà un compte.\n"
                        + $"Si c'était vous, connectez-vous ou réinitialisez votre mot de passe : {settings.PublicOrigin}/auth/forgot-password\n"
                        + "Sinon, vous pouvez ignorer ce message.", ct);
                    await AuditAsync(AuthEvents.RegisterExisting + "_notified", http, existing.Id, ct: ct);
                }
                else
                {
                    await SendVerificationAsync(existing, ct);
                }
            }

            return new AuthStatus("verification_sent");
        }

        var user = new CloudUser { Id = Guid.CreateVersion7(), UserName = address, Email = address, CreatedAt = time.GetUtcNow() };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            // Concurrent registration of the same address: answer exactly as for an existing address.
            if (created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
            {
                return new AuthStatus("verification_sent");
            }

            throw new CloudException(400, AuthErrors.WeakPassword, "Mot de passe refusé : " + string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        await AuditAsync(AuthEvents.Registered, http, user.Id, ct: ct);
        await SendVerificationAsync(user, ct);
        return new AuthStatus("verification_sent");
    }

    public async Task<AuthStatus> ResendVerificationAsync(string email, CancellationToken ct)
    {
        var address = ValidEmail(email);
        var user = await users.FindByEmailAsync(address);
        if (user is { EmailConfirmed: false } && await MailBudgetAsync(user.Id, ct))
        {
            await SendVerificationAsync(user, ct);
        }

        return new AuthStatus("verification_sent");
    }

    public async Task<AuthStatus> VerifyEmailAsync(string token, HttpContext http, CancellationToken ct)
    {
        var row = await ConsumeTokenAsync(token, TokenPurposes.VerifyEmail, ct);
        var user = row?.UserId is { } id ? await users.FindByIdAsync(id.ToString("D")) : null;
        if (user is null)
        {
            throw InvalidToken();
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await AuditAsync(AuthEvents.EmailVerified, http, user.Id, ct: ct);
        }

        return new AuthStatus("verified");
    }

    // ---------------------------------------------------------------- login

    public async Task<LoginResult> LoginAsync(string email, string password, HttpContext http, CancellationToken ct)
    {
        await RevokeCurrentAsync(http, "replaced_by_login", ct); // no session fixation: a login always starts a new session
        var address = TryEmail(email);
        var user = address is null ? null : await users.FindByEmailAsync(address);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new CloudUser(), s_dummyHash.Value, password);
            await AuditAsync(AuthEvents.LoginFailed, http, null, ct: ct);
            throw InvalidCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            users.PasswordHasher.VerifyHashedPassword(user, s_dummyHash.Value, password);
            await AuditAsync(AuthEvents.LoginFailed, http, user.Id, ct: ct);
            throw InvalidCredentials();
        }

        if (password.Length > MaxPasswordLength || !await users.CheckPasswordAsync(user, password))
        {
            await FailedAttemptAsync(user, AuthEvents.LoginFailed, http, null, ct);
            throw InvalidCredentials();
        }

        if (!user.EmailConfirmed)
        {
            // Only reachable with the right password: it reveals nothing to someone who does not know it.
            await AuditAsync(AuthEvents.LoginUnverified, http, user.Id, ct: ct);
            throw new CloudException(403, AuthErrors.EmailNotVerified, "Adresse non vérifiée : ouvrez le lien reçu par e-mail (ou demandez-en un nouveau).");
        }

        // The failure counter is shared by both factors and only reset by a complete sign-in: knowing the password
        // must not buy fresh second-factor guesses.
        if (await MfaEnabledAsync(user.Id, ct))
        {
            var pending = await sessions.CreateAsync(user, SessionKinds.MfaPending, http, ct);
            await AuditAsync(AuthEvents.LoginSucceeded + "_password", http, user.Id, pending.Id, ct: ct);
            return new LoginResult("mfa_required");
        }

        await users.ResetAccessFailedCountAsync(user);
        var session = await sessions.CreateAsync(user, SessionKinds.Full, http, ct);
        await AuditAsync(AuthEvents.LoginSucceeded, http, user.Id, session.Id, ct: ct);
        return new LoginResult("signed_in");
    }

    /// <summary>Second step: a TOTP code or one recovery code. Failures count toward the account lockout.</summary>
    public async Task<LoginResult> CompleteMfaAsync(UserPrincipal principal, string? code, string? recoveryCode, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        if (await users.IsLockedOutAsync(user))
        {
            await sessions.RevokeAsync(user.Id, principal.SessionId, "locked_out", ct);
            SessionService.ClearCookie(http);
            throw InvalidCredentials();
        }

        var usedRecovery = false;
        var ok = code is not null ? await AcceptTotpAsync(user.Id, code, ct) : recoveryCode is not null && (usedRecovery = await RedeemRecoveryCodeAsync(user.Id, recoveryCode, ct));
        if (!ok)
        {
            await FailedAttemptAsync(user, AuthEvents.MfaChallengeFailed, http, principal.SessionId, ct);
            if (await users.IsLockedOutAsync(user))
            {
                await sessions.RevokeAsync(user.Id, principal.SessionId, "locked_out", ct);
                SessionService.ClearCookie(http);
            }

            throw new CloudException(401, AuthErrors.InvalidCode, "Code invalide.");
        }

        await users.ResetAccessFailedCountAsync(user);
        await sessions.RevokeAsync(user.Id, principal.SessionId, "mfa_completed", ct);
        var session = await sessions.CreateAsync(user, SessionKinds.Full, http, ct); // new token: the pending one is dead
        await AuditAsync(AuthEvents.MfaChallengeSucceeded, http, user.Id, session.Id, ct: ct);
        if (usedRecovery)
        {
            await AuditAsync(AuthEvents.RecoveryCodeUsed, http, user.Id, session.Id, ct: ct);
            return new LoginResult("signed_in", await RemainingRecoveryCodesAsync(user.Id, ct));
        }

        return new LoginResult("signed_in");
    }

    public async Task LogoutAsync(UserPrincipal principal, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await sessions.RevokeAsync(principal.UserId, principal.SessionId, "logout", ct);
        SessionService.ClearCookie(http);
        await AuditAsync(AuthEvents.Logout, http, principal.UserId, principal.SessionId, ct: ct);
    }

    // ---------------------------------------------------------------- passwords

    public async Task<AuthStatus> ChangePasswordAsync(UserPrincipal principal, string currentPassword, string newPassword, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        if (currentPassword.Length > MaxPasswordLength || !await users.CheckPasswordAsync(user, currentPassword))
        {
            await FailedAttemptAsync(user, AuthEvents.LoginFailed, http, principal.SessionId, ct);
            throw InvalidCredentials();
        }

        await ValidatePasswordAsync(newPassword, user.Email!);
        var changed = await users.ChangePasswordAsync(user, currentPassword, newPassword); // also renews the security stamp
        if (!changed.Succeeded)
        {
            throw new CloudException(400, AuthErrors.WeakPassword, "Mot de passe refusé.");
        }

        await RestartSessionsAsync(user, "password_changed", http, ct);
        await AuditAsync(AuthEvents.PasswordChanged, http, user.Id, ct: ct);
        await mailer.SendAsync(user.Email!, "Mot de passe modifié",
            "Le mot de passe de votre compte PosSaaS vient d'être modifié ; les autres sessions ont été fermées.\n"
            + $"Si ce n'était pas vous : {settings.PublicOrigin}/auth/forgot-password", ct);
        return new AuthStatus("password_changed");
    }

    public async Task<AuthStatus> ForgotPasswordAsync(string email, HttpContext http, CancellationToken ct)
    {
        var address = ValidEmail(email);
        var user = await users.FindByEmailAsync(address);
        if (user is { EmailConfirmed: true } && await MailBudgetAsync(user.Id, ct))
        {
            var token = await IssueTokenAsync(TokenPurposes.ResetPassword, user.Id, null, null, s_resetLifetime, ct);
            await mailer.SendAsync(address, "Réinitialisation du mot de passe",
                $"Pour choisir un nouveau mot de passe (lien valable 1 heure, une seule fois) :\n{settings.PublicOrigin}/auth/reset-password#token={token}\n"
                + "Si vous n'avez rien demandé, ignorez ce message : votre mot de passe reste inchangé.", ct);
            await AuditAsync(AuthEvents.PasswordResetRequested, http, user.Id, ct: ct);
        }

        return new AuthStatus("reset_sent");
    }

    /// <summary>Sets a new password from an emailed token, ends every session and lifts the lockout. No automatic sign-in.</summary>
    public async Task<AuthStatus> ResetPasswordAsync(string token, string newPassword, HttpContext http, CancellationToken ct)
    {
        var row = await FindTokenAsync(token, TokenPurposes.ResetPassword, ct);
        var user = row?.UserId is { } id ? await users.FindByIdAsync(id.ToString("D")) : null;
        if (row is null || user is null)
        {
            throw InvalidToken();
        }

        await ValidatePasswordAsync(newPassword, user.Email!);
        if (await ConsumeTokenAsync(token, TokenPurposes.ResetPassword, ct) is null)
        {
            throw InvalidToken(); // consumed concurrently
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, newPassword);
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await db.AuthTokens
            .Where(t => t.UserId == user.Id && t.Purpose == TokenPurposes.ResetPassword && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ConsumedAt, time.GetUtcNow()), ct);
        await sessions.RevokeAllAsync(user.Id, null, "password_reset", ct);
        await AuditAsync(AuthEvents.PasswordReset, http, user.Id, ct: ct);
        return new AuthStatus("password_reset");
    }

    // ---------------------------------------------------------------- MFA

    public async Task<TotpSetup> StartTotpSetupAsync(UserPrincipal principal, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        var mfa = await db.UserMfa.SingleOrDefaultAsync(m => m.UserId == user.Id, ct);
        if (mfa?.EnabledAt is not null)
        {
            throw new CloudException(409, AuthErrors.MfaAlreadyEnabled, "L'authentification à deux facteurs est déjà active.");
        }

        var secret = AuthSecrets.NewTotpSecret();
        if (mfa is null)
        {
            mfa = new UserMfa { UserId = user.Id };
            db.UserMfa.Add(mfa);
        }

        MfaSecrets.Seal(keys, mfa, secret); // encrypted at rest; shown to the user once, below
        mfa.CreatedAt = time.GetUtcNow();
        mfa.LastUsedStep = 0;
        await db.SaveChangesAsync(ct);
        await AuditAsync(AuthEvents.MfaSetupStarted, http, user.Id, principal.SessionId, ct: ct);
        var setup = new TotpSetup(AuthSecrets.Base32(secret), AuthSecrets.OtpAuthUri("PosSaaS", user.Email!, secret));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        return setup;
    }

    /// <summary>Enables TOTP with a first valid code, issues recovery codes, ends other sessions and re-keys this one.</summary>
    public async Task<RecoveryCodes> ConfirmTotpAsync(UserPrincipal principal, string code, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        var mfa = await db.UserMfa.SingleOrDefaultAsync(m => m.UserId == user.Id, ct)
            ?? throw new CloudException(409, AuthErrors.MfaNotEnabled, "Commencez par configurer l'application d'authentification.");
        if (mfa.EnabledAt is not null)
        {
            throw new CloudException(409, AuthErrors.MfaAlreadyEnabled, "L'authentification à deux facteurs est déjà active.");
        }

        var seed = OpenSeed(mfa);
        var step = seed is null ? null : AuthSecrets.VerifyTotp(seed, code, time.GetUtcNow(), mfa.LastUsedStep);
        if (step is null)
        {
            await FailedAttemptAsync(user, AuthEvents.MfaChallengeFailed, http, principal.SessionId, ct);
            throw new CloudException(401, AuthErrors.InvalidCode, "Code invalide.");
        }

        mfa.LastUsedStep = step.Value;
        mfa.EnabledAt = time.GetUtcNow();
        var codes = ReplaceRecoveryCodes(user.Id);
        await db.SaveChangesAsync(ct);
        await users.UpdateSecurityStampAsync(user);
        await RestartSessionsAsync(user, "mfa_enabled", http, ct);
        await AuditAsync(AuthEvents.MfaEnabled, http, user.Id, ct: ct);
        return new RecoveryCodes(codes);
    }

    public async Task<RecoveryCodes> RegenerateRecoveryCodesAsync(UserPrincipal principal, string code, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        if (!await MfaEnabledAsync(user.Id, ct))
        {
            throw new CloudException(409, AuthErrors.MfaNotEnabled, "L'authentification à deux facteurs n'est pas active.");
        }

        if (!await AcceptTotpAsync(user.Id, code, ct))
        {
            await FailedAttemptAsync(user, AuthEvents.MfaChallengeFailed, http, principal.SessionId, ct);
            throw new CloudException(401, AuthErrors.InvalidCode, "Code invalide.");
        }

        await db.RecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        var codes = ReplaceRecoveryCodes(user.Id);
        await db.SaveChangesAsync(ct);
        await AuditAsync(AuthEvents.RecoveryCodesRegenerated, http, user.Id, principal.SessionId, ct: ct);
        return new RecoveryCodes(codes);
    }

    /// <summary>Requires the password and a current code. Refused for privileged users (owners must keep MFA).</summary>
    public async Task<AuthStatus> DisableMfaAsync(UserPrincipal principal, string password, string code, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        if (await IsPrivilegedAsync(user.Id, ct))
        {
            throw new CloudException(403, AuthErrors.MfaRequiredForRole, "Les propriétaires doivent garder l'authentification à deux facteurs.");
        }

        if (!await MfaEnabledAsync(user.Id, ct))
        {
            throw new CloudException(409, AuthErrors.MfaNotEnabled, "L'authentification à deux facteurs n'est pas active.");
        }

        if (password.Length > MaxPasswordLength || !await users.CheckPasswordAsync(user, password) || !await AcceptTotpAsync(user.Id, code, ct))
        {
            await FailedAttemptAsync(user, AuthEvents.MfaChallengeFailed, http, principal.SessionId, ct);
            throw InvalidCredentials();
        }

        await db.RecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.UserMfa.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(ct);
        await users.UpdateSecurityStampAsync(user);
        await RestartSessionsAsync(user, "mfa_disabled", http, ct);
        await AuditAsync(AuthEvents.MfaDisabled, http, user.Id, ct: ct);
        return new AuthStatus("mfa_disabled");
    }

    // ---------------------------------------------------------------- identity and sessions

    public async Task<MeResponse> MeAsync(UserPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == principal.UserId, ct);
        var memberships = (await db.UserMembershipsAsync(user.Id, ct))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MembershipInfo(m.OrganizationId.ToString(), m.Role))
            .ToList();
        var mfaEnabled = await MfaEnabledAsync(user.Id, ct);
        return new MeResponse(user.Id.ToString("D"), user.Email!, user.EmailConfirmed, mfaEnabled, memberships.Count > 0,
            mfaEnabled ? await RemainingRecoveryCodesAsync(user.Id, ct) : 0, memberships);
    }

    public async Task<IReadOnlyList<SessionInfo>> SessionsAsync(UserPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = time.GetUtcNow();
        var idleLimit = now - SessionService.IdleTimeout;
        var rows = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == principal.UserId && s.RevokedAt == null && s.Kind == SessionKinds.Full && s.ExpiresAt > now && s.LastSeenAt > idleLimit)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);
        return [.. rows.Select(s => new SessionInfo(s.Id.ToString("D"), s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.IpAddress, s.UserAgent, s.Id == principal.SessionId))];
    }

    public async Task<bool> RevokeSessionAsync(UserPrincipal principal, Guid sessionId, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!await sessions.RevokeAsync(principal.UserId, sessionId, "revoked_by_user", ct))
        {
            return false; // unknown, already revoked, or another user's: indistinguishable
        }

        if (sessionId == principal.SessionId)
        {
            SessionService.ClearCookie(http);
        }

        await AuditAsync(AuthEvents.SessionRevoked, http, principal.UserId, sessionId, ct: ct);
        return true;
    }

    public async Task<AuthStatus> RevokeOtherSessionsAsync(UserPrincipal principal, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await sessions.RevokeAllAsync(principal.UserId, principal.SessionId, "revoked_by_user", ct);
        await AuditAsync(AuthEvents.OtherSessionsRevoked, http, principal.UserId, principal.SessionId, ct: ct);
        return new AuthStatus("revoked");
    }

    // ---------------------------------------------------------------- owner onboarding

    /// <summary>
    /// Platform-operator action (CLI only, never an API): invites the first owner of an existing organization.
    /// The token is only emailed, never printed or logged.
    /// </summary>
    public async Task<DateTimeOffset> InviteOwnerAsync(Guid organizationId, string email, CancellationToken ct)
    {
        var address = ValidEmail(email);
        if (!await db.InTenantAsync(organizationId, () => db.Organizations.AnyAsync(o => o.Id == organizationId, ct), ct))
        {
            throw new CliException("Organisation introuvable.");
        }

        var token = await IssueTokenAsync(TokenPurposes.OwnerInvite, null, organizationId, users.NormalizeEmail(address), InviteLifetime, ct);
        await mailer.SendAsync(address, "Invitation : propriétaire PosSaaS",
            "Vous êtes invité(e) comme propriétaire d'une organisation PosSaaS.\n"
            + "1. Créez votre compte avec cette adresse et vérifiez-la.\n2. Activez l'authentification à deux facteurs.\n"
            + $"3. Acceptez l'invitation (valable 72 heures, une seule fois) : {settings.PublicOrigin}/auth/accept-invite#token={token}", ct);
        await AuditAsync(AuthEvents.OwnerInvited, null, null, organizationId: organizationId, ct: ct);
        return time.GetUtcNow() + InviteLifetime;
    }

    /// <summary>Accepting requires a verified address equal to the invited one and enabled MFA (owners are privileged).</summary>
    public async Task<OnboardingResult> AcceptOwnerInviteAsync(UserPrincipal principal, string token, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw InvalidCredentials();
        var row = await FindTokenAsync(token, TokenPurposes.OwnerInvite, ct);
        if (row is null || !user.EmailConfirmed || !string.Equals(row.NormalizedEmail, user.NormalizedEmail, StringComparison.Ordinal))
        {
            throw InvalidToken();
        }

        if (!await MfaEnabledAsync(user.Id, ct))
        {
            throw new CloudException(403, AuthErrors.MfaRequired, "Activez l'authentification à deux facteurs avant d'accepter l'invitation.");
        }

        var organizationId = row.OrganizationId!.Value; // from the server-side token row, never from the request
        await using var tx = await db.BeginTenantAsync(organizationId, ct);
        if (await ConsumeTokenAsync(token, TokenPurposes.OwnerInvite, ct) is null)
        {
            throw InvalidToken();
        }

        var existing = await db.OrganizationMemberships.SingleOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == user.Id, ct);
        if (existing is null)
        {
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = organizationId, UserId = user.Id, Role = MembershipRoles.Owner, CreatedAt = time.GetUtcNow() });
        }
        else
        {
            // Re-activated or promoted: an owner is organization-wide; the version tells clients it changed.
            existing.RevokedAt = null;
            existing.Role = MembershipRoles.Owner;
            existing.BusinessId = null;
            existing.BranchId = null;
            existing.UpdatedAt = time.GetUtcNow();
            existing.Version++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Privilege changed: re-key this session and end the others.
        await users.UpdateSecurityStampAsync(user);
        await RestartSessionsAsync(user, "privilege_granted", http, ct);
        await AuditAsync(AuthEvents.OwnerInviteAccepted, http, user.Id, organizationId: organizationId, ct: ct);
        return new OnboardingResult(organizationId.ToString("D"), MembershipRoles.Owner);
    }

    // ---------------------------------------------------------------- helpers

    private async Task SendVerificationAsync(CloudUser user, CancellationToken ct)
    {
        var token = await IssueTokenAsync(TokenPurposes.VerifyEmail, user.Id, null, null, s_verifyLifetime, ct);
        await mailer.SendAsync(user.Email!, "Vérifiez votre adresse",
            $"Pour activer votre compte PosSaaS (lien valable 24 heures, une seule fois) :\n{settings.PublicOrigin}/auth/verify-email#token={token}\n"
            + "Si vous n'avez rien demandé, ignorez ce message.", ct);
    }

    /// <summary>At most 3 security mails per account per 15 minutes (no mail bombing through the anonymous endpoints).</summary>
    private async Task<bool> MailBudgetAsync(Guid userId, CancellationToken ct)
    {
        var since = time.GetUtcNow() - s_mailWindow;
        var sent = await db.AuthTokens.CountAsync(t => t.UserId == userId && t.CreatedAt > since, ct)
            + await db.AuthAuditEvents.CountAsync(e => e.UserId == userId && e.Event == AuthEvents.RegisterExisting + "_notified" && e.OccurredAt > since, ct);
        if (sent >= MaxMailsPerWindow)
        {
            LogMailBudget(logger, userId);
            return false;
        }

        return true;
    }

    private async Task<string> IssueTokenAsync(string purpose, Guid? userId, Guid? organizationId, string? normalizedEmail, TimeSpan lifetime, CancellationToken ct)
    {
        var token = AuthSecrets.NewToken();
        var now = time.GetUtcNow();
        db.AuthTokens.Add(new AuthToken
        {
            TokenHash = AuthSecrets.Hash(token),
            Purpose = purpose,
            UserId = userId,
            OrganizationId = organizationId,
            NormalizedEmail = normalizedEmail,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    private async Task<AuthToken?> FindTokenAsync(string token, string purpose, CancellationToken ct)
    {
        if (token is not { Length: >= 40 and <= 64 })
        {
            return null;
        }

        var hash = AuthSecrets.Hash(token);
        var now = time.GetUtcNow();
        return await db.AuthTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose && t.ConsumedAt == null && t.ExpiresAt > now, ct);
    }

    /// <summary>Atomically marks a live token as used (a concurrent second use finds nothing).</summary>
    private async Task<AuthToken?> ConsumeTokenAsync(string token, string purpose, CancellationToken ct)
    {
        var row = await FindTokenAsync(token, purpose, ct);
        if (row is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var updated = await db.AuthTokens
            .Where(t => t.TokenHash == row.TokenHash && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ConsumedAt, now), ct);
        return updated == 1 ? row : null;
    }

    private async Task<bool> AcceptTotpAsync(Guid userId, string code, CancellationToken ct)
    {
        var mfa = await db.UserMfa.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.EnabledAt != null, ct);
        if (mfa is null)
        {
            return false;
        }

        var seed = OpenSeed(mfa);
        var step = seed is null ? null : AuthSecrets.VerifyTotp(seed, code, time.GetUtcNow(), mfa.LastUsedStep);
        if (seed is null || step is null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
            return false;
        }

        // Conditional update: two concurrent requests with the same code cannot both succeed.
        var updated = await db.UserMfa
            .Where(m => m.UserId == userId && m.LastUsedStep < step.Value)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.LastUsedStep, step.Value), ct);

        // Lazy migration: a legacy plaintext seed, or one under a retired key, is re-sealed with the active key.
        if (updated == 1 && mfa.TotpKeyId != keys.ActiveKeyId)
        {
            await MfaSecrets.ResealAsync(db, keys, mfa, seed, ct);
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        return updated == 1;
    }

    /// <summary>Null (and an error log, no secret) if the stored seed cannot be decrypted: tampered, another user's, or unknown key.</summary>
    private byte[]? OpenSeed(UserMfa mfa)
    {
        try
        {
            return MfaSecrets.Open(keys, mfa);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            LogSeedUnreadable(logger, mfa.UserId, mfa.TotpKeyId ?? "(legacy)");
            return null;
        }
    }

    private async Task<bool> RedeemRecoveryCodeAsync(Guid userId, string code, CancellationToken ct)
    {
        if (code.Length > 40)
        {
            return false;
        }

        var hash = AuthSecrets.HashRecoveryCode(code);
        var updated = await db.RecoveryCodes
            .Where(r => r.UserId == userId && r.CodeHash == hash && r.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.UsedAt, time.GetUtcNow()), ct);
        return updated == 1;
    }

    private List<string> ReplaceRecoveryCodes(Guid userId)
    {
        var now = time.GetUtcNow();
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => AuthSecrets.NewRecoveryCode()).ToList();
        db.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCode { Id = Guid.CreateVersion7(), UserId = userId, CodeHash = AuthSecrets.HashRecoveryCode(c), CreatedAt = now }));
        return codes;
    }

    private Task<int> RemainingRecoveryCodesAsync(Guid userId, CancellationToken ct) =>
        db.RecoveryCodes.CountAsync(r => r.UserId == userId && r.UsedAt == null, ct);

    private Task<bool> MfaEnabledAsync(Guid userId, CancellationToken ct) =>
        db.UserMfa.AnyAsync(m => m.UserId == userId && m.EnabledAt != null, ct);

    private async Task<bool> IsPrivilegedAsync(Guid userId, CancellationToken ct) =>
        (await db.UserMembershipsAsync(userId, ct)).Count > 0;

    private async Task FailedAttemptAsync(CloudUser user, string auditEvent, HttpContext http, Guid? sessionId, CancellationToken ct)
    {
        await users.AccessFailedAsync(user);
        await AuditAsync(auditEvent, http, user.Id, sessionId, ct: ct);
        if (await users.IsLockedOutAsync(user))
        {
            await AuditAsync(AuthEvents.LockedOut, http, user.Id, sessionId, ct: ct);
        }
    }

    /// <summary>Ends all the user's sessions and opens a fresh one for this client (security stamp already renewed).</summary>
    private async Task RestartSessionsAsync(CloudUser user, string reason, HttpContext http, CancellationToken ct)
    {
        await sessions.RevokeAllAsync(user.Id, null, reason, ct);
        var fresh = await users.FindByIdAsync(user.Id.ToString("D")) ?? user;
        await sessions.CreateAsync(fresh, SessionKinds.Full, http, ct);
    }

    private async Task RevokeCurrentAsync(HttpContext http, string reason, CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(SessionService.CookieName, out var token) && token is { Length: >= 40 and <= 64 })
        {
            var hash = AuthSecrets.Hash(token);
            await db.UserSessions.Where(s => (s.TokenHash == hash || s.PreviousTokenHash == hash) && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, time.GetUtcNow()).SetProperty(s => s.RevokeReason, reason), ct);
        }
    }

    private async Task AuditAsync(string auditEvent, HttpContext? http, Guid? userId, Guid? sessionId = null, Guid? organizationId = null, CancellationToken ct = default)
    {
        if (organizationId is { } organization)
        {
            // An organization's row of the security log is written inside that tenant (row-level security).
            await db.InTenantAsync(organization, () =>
            {
                AuthAudit.Add(db, time, auditEvent, http, userId, sessionId, organizationId);
                return db.SaveChangesAsync(ct);
            }, ct);
            return;
        }

        AuthAudit.Add(db, time, auditEvent, http, userId, sessionId, organizationId);
        await db.SaveChangesAsync(ct);
    }

    private async Task ValidatePasswordAsync(string password, string email)
    {
        if (password is null || password.Length > MaxPasswordLength)
        {
            throw new CloudException(400, AuthErrors.WeakPassword, $"Mot de passe de 12 à {MaxPasswordLength} caractères requis.");
        }

        var probe = new CloudUser { UserName = email, Email = email };
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, probe, password);
            if (!result.Succeeded)
            {
                throw new CloudException(400, AuthErrors.WeakPassword, "Mot de passe refusé : " + string.Join(" ", result.Errors.Select(e => e.Description)));
            }
        }

        if (password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && email.Split('@')[0].Length >= 4)
        {
            throw new CloudException(400, AuthErrors.WeakPassword, "Le mot de passe ne doit pas contenir l'adresse e-mail.");
        }
    }

    internal static string ValidEmail(string? email) =>
        TryEmail(email) ?? throw new CloudException(400, AuthErrors.InvalidEmail, "Adresse e-mail invalide.");

    private static string? TryEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Any(char.IsControl))
        {
            return null;
        }

        try
        {
            var parsed = new MailAddress(email.Trim());
            return parsed.Address == email.Trim() && string.IsNullOrEmpty(parsed.DisplayName) && parsed.Host.Contains('.', StringComparison.Ordinal) ? parsed.Address : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static CloudException InvalidCredentials() =>
        new(401, AuthErrors.InvalidCredentials, "Identifiants invalides, ou compte temporairement verrouillé après trop d'essais.");

    private static CloudException InvalidToken() =>
        new(400, AuthErrors.InvalidToken, "Lien invalide, expiré ou déjà utilisé.");

    [LoggerMessage(Level = LogLevel.Error, Message = "MFA secret of user {UserId} cannot be decrypted (key {KeyId}): code refused; check POS_CLOUD_DATA_KEYS and run check-config")]
    private static partial void LogSeedUnreadable(ILogger logger, Guid userId, string keyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security mail not sent for user {UserId}: per-account limit reached")]
    private static partial void LogMailBudget(ILogger logger, Guid userId);
}

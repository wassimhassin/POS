using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Pos.Cloud.Identity;

public sealed record AccessInfo(string OrganizationId, string Role, ScopeInfo Scope, IReadOnlyList<string> Permissions);

public sealed record BranchInfo(string Id, string BusinessId, string Name);

public sealed record MemberInfo(string UserId, string Email, string Role, ScopeInfo Scope, int Version, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record InviteMemberRequest(string Email, string Role, Guid? BusinessId = null, Guid? BranchId = null);

public sealed record InvitationCreated(string InvitationId, DateTimeOffset ExpiresAt);

public sealed record InvitationInfo(string Id, string Email, string Role, ScopeInfo Scope, string InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record ChangeMemberRequest(string Role, int ExpectedVersion, Guid? BusinessId = null, Guid? BranchId = null);

public sealed record MembershipAccepted(string OrganizationId, string Role, ScopeInfo Scope);

public static class AccessErrors
{
    public const string InvalidRole = "invalid_role";
    public const string InvalidScope = "invalid_scope";
    public const string AlreadyMember = "already_member";
    public const string LastOwner = "last_owner";
    public const string VersionConflict = "version_conflict";
    public const string TooManyInvitations = "too_many_invitations";
}

/// <summary>
/// Membership management (doc 14). Every change runs in a transaction that locks the organization row, so changes of one
/// organization are serialized: the actor's authority is re-read under the lock (a concurrent demotion of the actor
/// wins), and the last active owner can never be demoted or revoked, even by two owners acting at the same time.
/// Every change ends the affected user's sessions (and renews the security stamp).
/// </summary>
public sealed partial class MembershipService(
    CloudDb db,
    UserManager<CloudUser> users,
    SessionService sessions,
    IAuthMailer mailer,
    AuthSettings settings,
    TimeProvider time,
    ILogger<MembershipService> logger)
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);
    public const int MaxPendingInvitations = 100;

    // ---------------------------------------------------------------- reads (scope-filtered)

    public static AccessInfo Access(MemberAccess member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new AccessInfo(member.OrganizationId.ToString("D"), member.Role, member.Scope.ToInfo(), member.Permissions);
    }

    public async Task<IReadOnlyList<BranchInfo>> BranchesAsync(MemberAccess member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        var query = db.Branches.AsNoTracking().Where(b => b.OrganizationId == member.OrganizationId);
        if (member.Scope.BranchId is { } branch)
        {
            query = query.Where(b => b.Id == branch);
        }
        else if (member.Scope.BusinessId is { } business)
        {
            query = query.Where(b => b.BusinessId == business);
        }

        var rows = await db.InTenantAsync(member.OrganizationId, () => query.OrderBy(b => b.Name).ThenBy(b => b.Id).ToListAsync(ct), ct);
        return [.. rows.Select(b => new BranchInfo(b.Id.ToString("D"), b.BusinessId.ToString("D"), b.Name))];
    }

    public async Task<IReadOnlyList<MemberInfo>> MembersAsync(MemberAccess member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        var memberships = Within(db.OrganizationMemberships.AsNoTracking().Where(m => m.OrganizationId == member.OrganizationId && m.RevokedAt == null), member.Scope);
        var rows = await db.InTenantAsync(member.OrganizationId, () => (
            from m in memberships
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            orderby m.CreatedAt, m.UserId
            select new { Membership = m, u.Email }).ToListAsync(ct), ct);
        return [.. rows.Select(r => ToInfo(r.Membership, r.Email!))];
    }

    public async Task<IReadOnlyList<InvitationInfo>> InvitationsAsync(MemberAccess member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        var now = time.GetUtcNow();
        var query = db.MembershipInvitations.AsNoTracking()
            .Where(i => i.OrganizationId == member.OrganizationId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);
        if (member.Scope.BranchId is { } branch)
        {
            query = query.Where(i => i.BranchId == branch);
        }
        else if (member.Scope.BusinessId is { } business)
        {
            query = query.Where(i => i.BusinessId == business);
        }

        var rows = await db.InTenantAsync(member.OrganizationId, () => query.OrderBy(i => i.CreatedAt).ToListAsync(ct), ct);
        return [.. rows.Select(i => new InvitationInfo(i.Id.ToString("D"), i.NormalizedEmail.ToLowerInvariant(), i.Role,
            new AccessScope(i.BusinessId, i.BranchId).ToInfo(), i.InvitedBy.ToString("D"), i.CreatedAt, i.ExpiresAt))];
    }

    // ---------------------------------------------------------------- invitations

    /// <summary>Invites an address (with or without an account) for a role strictly below the actor's (owners: any), inside the actor's scope.</summary>
    public async Task<InvitationCreated> InviteAsync(MemberAccess actor, InviteMemberRequest request, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var address = AuthService.ValidEmail(request.Email);
        var normalized = users.NormalizeEmail(address);
        await using var tx = await db.BeginTenantAsync(actor.OrganizationId, ct);
        var scope = await ResolveScopeAsync(actor.OrganizationId, request.Role, request.BusinessId, request.BranchId, ct);
        await LockOrganizationAsync(actor.OrganizationId, ct);
        var current = await ReloadAsync(actor, ct) ?? throw OrganizationAccessFilter.NotFound();
        if (!current.Has(Permissions.MembersInvite) || !CanGrant(current, request.Role, scope))
        {
            await tx.RollbackAsync(ct);
            await DeniedAsync(current, http, null, $"invite role={request.Role};scope={scope}", ct);
            throw OrganizationAccessFilter.Forbidden();
        }

        var isMember = await (
            from m in db.OrganizationMemberships
            join u in db.Users on m.UserId equals u.Id
            where m.OrganizationId == actor.OrganizationId && m.RevokedAt == null && u.NormalizedEmail == normalized
            select m.UserId).AnyAsync(ct);
        if (isMember)
        {
            throw new CloudException(409, AccessErrors.AlreadyMember, "Cette personne est déjà membre de l'organisation.");
        }

        var now = time.GetUtcNow();
        var pending = db.MembershipInvitations.Where(i => i.OrganizationId == actor.OrganizationId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);
        if (await pending.CountAsync(ct) >= MaxPendingInvitations)
        {
            throw new CloudException(409, AccessErrors.TooManyInvitations, $"Trop d'invitations en attente (maximum {MaxPendingInvitations}).");
        }

        // A new invitation for the same address replaces the previous one (one live link per person).
        var superseded = await pending.Where(i => i.NormalizedEmail == normalized).Select(i => i.Id).ToListAsync(ct);
        if (superseded.Count > 0)
        {
            await db.MembershipInvitations.Where(i => superseded.Contains(i.Id)).ExecuteUpdateAsync(u => u.SetProperty(i => i.RevokedAt, now), ct);
            foreach (var id in superseded)
            {
                AuthAudit.Add(db, time, AccessEvents.InvitationRevoked, http, current.UserId, current.SessionId, current.OrganizationId, details: $"invitation={id:D};reason=superseded");
            }
        }

        var token = AuthSecrets.NewToken();
        var invitation = new MembershipInvitation
        {
            Id = Guid.CreateVersion7(),
            TokenHash = AuthSecrets.Hash(token),
            OrganizationId = actor.OrganizationId,
            NormalizedEmail = normalized,
            Role = request.Role,
            BusinessId = scope.BusinessId,
            BranchId = scope.BranchId,
            InvitedBy = current.UserId,
            CreatedAt = now,
            ExpiresAt = now + InvitationLifetime,
        };
        db.MembershipInvitations.Add(invitation);
        AuthAudit.Add(db, time, AccessEvents.MemberInvited, http, current.UserId, current.SessionId, current.OrganizationId,
            details: $"invitation={invitation.Id:D};role={request.Role};scope={scope}");
        await db.SaveChangesAsync(ct);
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == actor.OrganizationId).Select(o => o.Name).SingleAsync(ct);
        await tx.CommitAsync(ct);

        await mailer.SendAsync(address, "Invitation à rejoindre une organisation PosSaaS",
            $"Vous êtes invité(e) à rejoindre l'organisation « {organization} » sur PosSaaS (rôle : {request.Role}).\n"
            + "1. Créez votre compte avec cette adresse (ou connectez-vous) et vérifiez-la.\n2. Activez l'authentification à deux facteurs.\n"
            + $"3. Acceptez l'invitation (valable 72 heures, une seule fois) : {settings.PublicOrigin}/auth/join#token={token}\n"
            + "Si vous ne vous attendiez pas à cette invitation, ignorez ce message.", ct);
        return new InvitationCreated(invitation.Id.ToString("D"), invitation.ExpiresAt);
    }

    public async Task<bool> RevokeInvitationAsync(MemberAccess actor, Guid invitationId, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await db.BeginTenantAsync(actor.OrganizationId, ct);
        await LockOrganizationAsync(actor.OrganizationId, ct);
        var current = await ReloadAsync(actor, ct) ?? throw OrganizationAccessFilter.NotFound();
        var now = time.GetUtcNow();
        var invitation = await db.MembershipInvitations.SingleOrDefaultAsync(
            i => i.Id == invitationId && i.OrganizationId == actor.OrganizationId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        if (invitation is null || !current.Scope.Covers(new AccessScope(invitation.BusinessId, invitation.BranchId)))
        {
            return false;
        }

        if (!current.Has(Permissions.MembersInvite) || !MembershipRoles.Outranks(current.Role, invitation.Role))
        {
            await tx.RollbackAsync(ct);
            await DeniedAsync(current, http, null, $"revoke invitation={invitationId:D}", ct);
            throw OrganizationAccessFilter.Forbidden();
        }

        invitation.RevokedAt = now;
        AuthAudit.Add(db, time, AccessEvents.InvitationRevoked, http, current.UserId, current.SessionId, current.OrganizationId, details: $"invitation={invitationId:D};reason=revoked");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Accepts an invitation: verified account with the invited address and enabled MFA (every member is privileged),
    /// single use, before expiry, and only if the inviter still has the authority to grant that role and scope.
    /// </summary>
    public async Task<MembershipAccepted> AcceptAsync(UserPrincipal principal, string token, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = await users.FindByIdAsync(principal.UserId.ToString("D")) ?? throw new CloudException(401, "unauthorized", "Authentification requise ou refusée.");
        if (token is not { Length: >= 40 and <= 64 })
        {
            throw InvalidToken();
        }

        var hash = AuthSecrets.Hash(token);
        var now = time.GetUtcNow();

        // The token alone names its organization (pre-tenant lookup); everything else runs inside that tenant only.
        var organizationId = await db.InvitationOrganizationAsync(hash, ct) ?? throw InvalidToken();
        await using var tx = await db.BeginTenantAsync(organizationId, ct);
        var invitation = await db.MembershipInvitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        if (invitation is null || !user.EmailConfirmed || !string.Equals(invitation.NormalizedEmail, user.NormalizedEmail, StringComparison.Ordinal))
        {
            throw InvalidToken();
        }

        if (!await db.UserMfa.AnyAsync(m => m.UserId == user.Id && m.EnabledAt != null, ct))
        {
            throw new CloudException(403, AuthErrors.MfaRequired, "Activez l'authentification à deux facteurs avant d'accepter l'invitation.");
        }

        var scope = new AccessScope(invitation.BusinessId, invitation.BranchId);
        await LockOrganizationAsync(invitation.OrganizationId, ct);

        // The inviter's authority is checked now, not when the link was sent: a demoted or removed inviter's links die.
        var inviter = await db.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == invitation.InvitedBy && m.RevokedAt == null, ct);
        if (inviter is null || !MembershipRoles.Permissions(inviter.Role).Contains(Permissions.MembersInvite)
            || !MembershipRoles.Outranks(inviter.Role, invitation.Role) || !new AccessScope(inviter.BusinessId, inviter.BranchId).Covers(scope))
        {
            await db.MembershipInvitations.Where(i => i.Id == invitation.Id && i.RevokedAt == null).ExecuteUpdateAsync(u => u.SetProperty(i => i.RevokedAt, now), ct);
            AuthAudit.Add(db, time, AccessEvents.InvitationRevoked, http, user.Id, principal.SessionId, invitation.OrganizationId, details: $"invitation={invitation.Id:D};reason=inviter_authority");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw InvalidToken();
        }

        var consumed = await db.MembershipInvitations
            .Where(i => i.Id == invitation.Id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.AcceptedAt, now).SetProperty(i => i.AcceptedBy, user.Id), ct);
        if (consumed != 1)
        {
            throw InvalidToken(); // a concurrent acceptance or revocation won
        }

        var existing = await db.OrganizationMemberships.SingleOrDefaultAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == user.Id, ct);
        if (existing is { RevokedAt: null })
        {
            throw new CloudException(409, AccessErrors.AlreadyMember, "Vous êtes déjà membre de l'organisation.");
        }

        if (existing is null)
        {
            db.OrganizationMemberships.Add(new OrganizationMembership
            {
                OrganizationId = invitation.OrganizationId,
                UserId = user.Id,
                Role = invitation.Role,
                BusinessId = scope.BusinessId,
                BranchId = scope.BranchId,
                CreatedAt = now,
            });
        }
        else
        {
            existing.Role = invitation.Role;
            existing.BusinessId = scope.BusinessId;
            existing.BranchId = scope.BranchId;
            existing.RevokedAt = null;
            existing.UpdatedAt = now;
            existing.Version++;
        }

        AuthAudit.Add(db, time, AccessEvents.InvitationAccepted, http, user.Id, principal.SessionId, invitation.OrganizationId, user.Id,
            $"invitation={invitation.Id:D};role={invitation.Role};scope={scope}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Privilege granted: renew the stamp, end every session, and open a fresh one for this client.
        await users.UpdateSecurityStampAsync(user);
        await sessions.RevokeAllAsync(user.Id, null, "privilege_granted", ct);
        await sessions.CreateAsync(await users.FindByIdAsync(user.Id.ToString("D")) ?? user, SessionKinds.Full, http, ct);
        return new MembershipAccepted(invitation.OrganizationId.ToString("D"), invitation.Role, scope.ToInfo());
    }

    // ---------------------------------------------------------------- changes

    public async Task<MemberInfo> ChangeAsync(MemberAccess actor, Guid targetUserId, ChangeMemberRequest request, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await db.BeginTenantAsync(actor.OrganizationId, ct);
        var scope = await ResolveScopeAsync(actor.OrganizationId, request.Role, request.BusinessId, request.BranchId, ct);
        await LockOrganizationAsync(actor.OrganizationId, ct);
        var current = await ReloadAsync(actor, ct) ?? throw OrganizationAccessFilter.NotFound();
        var target = await TargetAsync(current, targetUserId, ct);
        var targetScope = new AccessScope(target.BusinessId, target.BranchId);
        if (target.Version != request.ExpectedVersion)
        {
            throw new CloudException(409, AccessErrors.VersionConflict, "Ce membre a été modifié entre-temps : rechargez-le.");
        }

        if (!current.Has(Permissions.MembersManage) || !MembershipRoles.Outranks(current.Role, target.Role) || !CanGrant(current, request.Role, scope))
        {
            await tx.RollbackAsync(ct);
            await DeniedAsync(current, http, targetUserId, $"change to role={request.Role};scope={scope}", ct);
            throw OrganizationAccessFilter.Forbidden();
        }

        if (target.Role == request.Role && targetScope == scope)
        {
            return ToInfo(target, await EmailAsync(targetUserId, ct)); // nothing to change
        }

        if (target.Role == MembershipRoles.Owner && request.Role != MembershipRoles.Owner && !await OtherOwnerAsync(target, ct))
        {
            await tx.RollbackAsync(ct);
            await LastOwnerAsync(current, http, targetUserId, ct);
        }

        var before = $"{target.Role}/{targetScope}";
        target.Role = request.Role;
        target.BusinessId = scope.BusinessId;
        target.BranchId = scope.BranchId;
        target.UpdatedAt = time.GetUtcNow();
        target.Version++;
        AuthAudit.Add(db, time, AccessEvents.MemberChanged, http, current.UserId, current.SessionId, current.OrganizationId, targetUserId,
            $"from={before};to={request.Role}/{scope};version={target.Version}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await EndSessionsAsync(targetUserId, "permission_changed", http, targetUserId == actor.UserId, ct);
        return ToInfo(target, await EmailAsync(targetUserId, ct));
    }

    public async Task RevokeAsync(MemberAccess actor, Guid targetUserId, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await db.BeginTenantAsync(actor.OrganizationId, ct);
        await LockOrganizationAsync(actor.OrganizationId, ct);
        var current = await ReloadAsync(actor, ct) ?? throw OrganizationAccessFilter.NotFound();
        var target = await TargetAsync(current, targetUserId, ct);
        if (!current.Has(Permissions.MembersManage) || !MembershipRoles.Outranks(current.Role, target.Role))
        {
            await tx.RollbackAsync(ct);
            await DeniedAsync(current, http, targetUserId, $"revoke role={target.Role}", ct);
            throw OrganizationAccessFilter.Forbidden();
        }

        if (target.Role == MembershipRoles.Owner && !await OtherOwnerAsync(target, ct))
        {
            await tx.RollbackAsync(ct);
            await LastOwnerAsync(current, http, targetUserId, ct);
        }

        var now = time.GetUtcNow();
        target.RevokedAt = now;
        target.UpdatedAt = now;
        target.Version++;
        AuthAudit.Add(db, time, AccessEvents.MemberRevoked, http, current.UserId, current.SessionId, current.OrganizationId, targetUserId,
            $"role={target.Role};scope={new AccessScope(target.BusinessId, target.BranchId)};version={target.Version}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await EndSessionsAsync(targetUserId, "membership_revoked", http, targetUserId == actor.UserId, ct);
    }

    // ---------------------------------------------------------------- helpers

    private static bool CanGrant(MemberAccess actor, string role, AccessScope scope) =>
        MembershipRoles.Outranks(actor.Role, role) && actor.Scope.Covers(scope);

    private static IQueryable<OrganizationMembership> Within(IQueryable<OrganizationMembership> query, AccessScope scope) =>
        scope.BranchId is { } branch ? query.Where(m => m.BranchId == branch)
        : scope.BusinessId is { } business ? query.Where(m => m.BusinessId == business)
        : query;

    /// <summary>
    /// Validates role and scope against THIS organization: a business or branch of another organization is
    /// indistinguishable from one that does not exist (400 invalid_scope, no probing).
    /// </summary>
    private async Task<AccessScope> ResolveScopeAsync(Guid organizationId, string? role, Guid? businessId, Guid? branchId, CancellationToken ct)
    {
        if (!MembershipRoles.IsKnown(role))
        {
            throw new CloudException(400, AccessErrors.InvalidRole, $"Rôle inconnu : {string.Join(", ", MembershipRoles.All)} attendu.");
        }

        AccessScope scope;
        if (branchId is { } branch)
        {
            var owner = await db.Branches.AsNoTracking().Where(b => b.Id == branch && b.OrganizationId == organizationId).Select(b => (Guid?)b.BusinessId).SingleOrDefaultAsync(ct);
            if (owner is null || (businessId is { } stated && stated != owner))
            {
                throw InvalidScope();
            }

            scope = new AccessScope(owner, branch);
        }
        else if (businessId is { } business)
        {
            if (!await db.Businesses.AnyAsync(b => b.Id == business && b.OrganizationId == organizationId, ct))
            {
                throw InvalidScope();
            }

            scope = new AccessScope(business, null);
        }
        else
        {
            scope = AccessScope.Organization;
        }

        if (MembershipRoles.OrganizationWideOnly(role!) && scope.BusinessId is not null)
        {
            throw new CloudException(400, AccessErrors.InvalidScope, "Les propriétaires et administrateurs couvrent toute l'organisation.");
        }

        return scope;
    }

    /// <summary>Serializes every membership change of one organization (row lock; does not block foreign-key checks).</summary>
    private Task<int> LockOrganizationAsync(Guid organizationId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM organizations WHERE id = {organizationId} FOR NO KEY UPDATE", ct);

    /// <summary>The actor's membership as it is now, under the lock (null if revoked meanwhile).</summary>
    private async Task<MemberAccess?> ReloadAsync(MemberAccess actor, CancellationToken ct)
    {
        var row = await db.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.OrganizationId == actor.OrganizationId && m.UserId == actor.UserId && m.RevokedAt == null, ct);
        return row is null ? null : actor with { Role = row.Role, Scope = new AccessScope(row.BusinessId, row.BranchId), Version = row.Version };
    }

    /// <summary>A live member of the organization inside the actor's scope; anything else is 404.</summary>
    private async Task<OrganizationMembership> TargetAsync(MemberAccess actor, Guid targetUserId, CancellationToken ct)
    {
        var target = await db.OrganizationMemberships.SingleOrDefaultAsync(m => m.OrganizationId == actor.OrganizationId && m.UserId == targetUserId && m.RevokedAt == null, ct);
        return target is not null && actor.Scope.Covers(new AccessScope(target.BusinessId, target.BranchId)) ? target : throw OrganizationAccessFilter.NotFound();
    }

    private Task<bool> OtherOwnerAsync(OrganizationMembership target, CancellationToken ct) =>
        db.OrganizationMemberships.AnyAsync(m => m.OrganizationId == target.OrganizationId && m.UserId != target.UserId && m.RevokedAt == null && m.Role == MembershipRoles.Owner, ct);

    private async Task<string> EmailAsync(Guid userId, CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleAsync(ct))!;

    /// <summary>Renews the security stamp and ends every session of a user whose access changed.</summary>
    private async Task EndSessionsAsync(Guid userId, string reason, HttpContext http, bool self, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString("D"));
        if (user is not null)
        {
            await users.UpdateSecurityStampAsync(user);
        }

        var ended = await sessions.RevokeAllAsync(userId, null, reason, ct);
        LogSessionsEnded(logger, userId, ended, reason);
        if (self)
        {
            SessionService.ClearCookie(http);
        }
    }

    private async Task DeniedAsync(MemberAccess actor, HttpContext http, Guid? target, string details, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.InTenantAsync(actor.OrganizationId, () =>
        {
            AuthAudit.Add(db, time, AccessEvents.PermissionDenied, http, actor.UserId, actor.SessionId, actor.OrganizationId, target, details);
            return db.SaveChangesAsync(ct);
        }, ct);
    }

    private async Task LastOwnerAsync(MemberAccess actor, HttpContext http, Guid target, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.InTenantAsync(actor.OrganizationId, () =>
        {
            AuthAudit.Add(db, time, AccessEvents.LastOwnerProtected, http, actor.UserId, actor.SessionId, actor.OrganizationId, target);
            return db.SaveChangesAsync(ct);
        }, ct);
        throw new CloudException(409, AccessErrors.LastOwner, "L'organisation doit garder au moins un propriétaire actif.");
    }

    private static MemberInfo ToInfo(OrganizationMembership m, string email) =>
        new(m.UserId.ToString("D"), email, m.Role, new AccessScope(m.BusinessId, m.BranchId).ToInfo(), m.Version, m.CreatedAt, m.UpdatedAt);

    private static CloudException InvalidScope() => new(400, AccessErrors.InvalidScope, "Périmètre invalide pour cette organisation.");

    private static CloudException InvalidToken() => new(400, AuthErrors.InvalidToken, "Lien invalide ou expiré.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Access of user {UserId} changed ({Reason}): {Count} session(s) ended")]
    private static partial void LogSessionsEnded(ILogger logger, Guid userId, int count, string reason);
}

/// <summary>Organization access endpoints (doc 14 §4): session users only, deny by default, scope from the server.</summary>
public static class MembershipApi
{
    public static void Map(RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        var organization = v1.MapGroup("/organizations/{organizationId:guid}").WithTags("Organization access")
            .RequireAuthorization(AuthApi.UserPolicy)
            .AddEndpointFilter<OrganizationAccessFilter>()
            .ProducesErrors(403, 404);

        StructureApi.Map(organization);

        organization.MapGet("/access", (HttpContext h) => MembershipService.Access(Member(h)))
            .RequirePermission(Permissions.OrganizationRead)
            .WithName("GetOrganizationAccess").WithSummary("The caller's role, scope and permissions in the organization.")
            .Produces<AccessInfo>();

        organization.MapGet("/branches", (HttpContext h, MembershipService s, CancellationToken ct) => s.BranchesAsync(Member(h), ct))
            .RequirePermission(Permissions.OrganizationRead)
            .WithName("ListAccessibleBranches").WithSummary("Branches inside the caller's scope.")
            .Produces<IReadOnlyList<BranchInfo>>();

        organization.MapGet("/members", (HttpContext h, MembershipService s, CancellationToken ct) => s.MembersAsync(Member(h), ct))
            .RequirePermission(Permissions.MembersRead)
            .WithName("ListMembers").WithSummary("Active members whose scope lies inside the caller's scope.")
            .Produces<IReadOnlyList<MemberInfo>>();

        organization.MapPatch("/members/{userId:guid}", (Guid userId, ChangeMemberRequest r, HttpContext h, MembershipService s, CancellationToken ct) =>
                s.ChangeAsync(Member(h), userId, r, h, ct))
            .RequirePermission(Permissions.MembersManage).RequireRateLimiting(AuthApi.AuthRateLimit)
            .WithName("ChangeMember").WithSummary("Change a member's role and scope (expectedVersion required); ends the member's sessions.")
            .Produces<MemberInfo>().ProducesErrors(409);

        organization.MapDelete("/members/{userId:guid}", async (Guid userId, HttpContext h, MembershipService s, CancellationToken ct) =>
            {
                await s.RevokeAsync(Member(h), userId, h, ct);
                return Results.NoContent();
            })
            .RequirePermission(Permissions.MembersManage).RequireRateLimiting(AuthApi.AuthRateLimit)
            .WithName("RevokeMember").WithSummary("Revoke a membership; ends the member's sessions. The last owner cannot be revoked.")
            .Produces(StatusCodes.Status204NoContent).ProducesErrors(409);

        organization.MapGet("/invitations", (HttpContext h, MembershipService s, CancellationToken ct) => s.InvitationsAsync(Member(h), ct))
            .RequirePermission(Permissions.MembersInvite)
            .WithName("ListInvitations").WithSummary("Pending invitations inside the caller's scope.")
            .Produces<IReadOnlyList<InvitationInfo>>();

        organization.MapPost("/invitations", async (InviteMemberRequest r, HttpContext h, MembershipService s, CancellationToken ct) =>
                Results.Json(await s.InviteAsync(Member(h), r, h, ct), statusCode: StatusCodes.Status202Accepted))
            .RequirePermission(Permissions.MembersInvite).RequireRateLimiting(AuthApi.MailRateLimit)
            .WithName("InviteMember").WithSummary("Email a 72-hour single-use invitation for a role below the caller's, inside the caller's scope.")
            .Produces<InvitationCreated>(StatusCodes.Status202Accepted).ProducesErrors(409);

        organization.MapDelete("/invitations/{invitationId:guid}", async (Guid invitationId, HttpContext h, MembershipService s, CancellationToken ct) =>
                await s.RevokeInvitationAsync(Member(h), invitationId, h, ct) ? Results.NoContent() : Results.NotFound(new Pos.Sync.CloudError("not_found", "Ressource introuvable.")))
            .RequirePermission(Permissions.MembersInvite)
            .WithName("RevokeInvitation").WithSummary("Cancel a pending invitation.")
            .Produces(StatusCodes.Status204NoContent);

        v1.MapPost("/invitations/accept", (TokenRequest r, MembershipService s, HttpContext h, CancellationToken ct) => s.AcceptAsync(Principal(h), r.Token, h, ct))
            .RequireAuthorization(AuthApi.UserPolicy).RequireRateLimiting(AuthApi.AuthRateLimit)
            .WithTags("Organization access").ProducesErrors(403, 409)
            .WithName("AcceptInvitation").WithSummary("Join an organization with an emailed invitation (verified, matching address and enabled MFA required).")
            .Produces<MembershipAccepted>();
    }

    private static MemberAccess Member(HttpContext http) =>
        http.Items[typeof(MemberAccess)] as MemberAccess ?? throw OrganizationAccessFilter.Forbidden();

    private static UserPrincipal Principal(HttpContext http) =>
        http.Items[typeof(UserPrincipal)] as UserPrincipal ?? throw new CloudException(401, "unauthorized", "Authentification requise ou refusée.");
}

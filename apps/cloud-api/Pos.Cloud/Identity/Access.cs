using Microsoft.EntityFrameworkCore;

namespace Pos.Cloud.Identity;

/// <summary>
/// Permission catalog (doc 14 §2). Codes are stable data: they appear in API responses and audit details. New
/// capabilities add codes here; roles map to sets of codes in <see cref="MembershipRoles"/>.
/// </summary>
public static class Permissions
{
    /// <summary>See the organization's structure (businesses, branches) within the member's scope.</summary>
    public const string OrganizationRead = "organization.read";

    /// <summary>List members whose scope lies within the member's scope.</summary>
    public const string MembersRead = "members.read";

    /// <summary>Invite people (and list or cancel invitations) for roles below one's own, within one's scope.</summary>
    public const string MembersInvite = "members.invite";

    /// <summary>Change the role/scope of, or revoke, members below one's own role, within one's scope.</summary>
    public const string MembersManage = "members.manage";

    /// <summary>Rename the organization (doc 16).</summary>
    public const string OrganizationManage = "organization.manage";

    /// <summary>Create businesses and rename businesses within one's scope.</summary>
    public const string BusinessesManage = "businesses.manage";

    /// <summary>Create branches in a business within one's scope, and rename branches within one's scope.</summary>
    public const string BranchesManage = "branches.manage";

    public static readonly IReadOnlyList<string> All = [OrganizationRead, MembersRead, MembersInvite, MembersManage, OrganizationManage, BusinessesManage, BranchesManage];
}

/// <summary>Roles, ranked. A role can only be granted, changed or revoked by a strictly higher role, except by owners.</summary>
public static class MembershipRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string Viewer = "viewer";

    public static readonly IReadOnlyList<string> All = [Owner, Admin, Manager, Viewer];

    internal const string CheckSql = "role IN ('owner', 'admin', 'manager', 'viewer')";

    private static readonly Dictionary<string, (int Rank, string[] Permissions)> s_roles = new(StringComparer.Ordinal)
    {
        [Owner] = (4, [.. Identity.Permissions.All]),
        [Admin] = (3, [.. Identity.Permissions.All.Where(p => p != Identity.Permissions.OrganizationManage)]),
        [Manager] = (2, [Identity.Permissions.OrganizationRead, Identity.Permissions.MembersRead, Identity.Permissions.BranchesManage]),
        [Viewer] = (1, [Identity.Permissions.OrganizationRead]),
    };

    public static bool IsKnown(string? role) => role is not null && s_roles.ContainsKey(role);

    public static int Rank(string role) => s_roles.TryGetValue(role, out var r) ? r.Rank : 0;

    /// <summary>The permissions of a role; unknown roles have none (deny by default).</summary>
    public static IReadOnlyList<string> Permissions(string role) => s_roles.TryGetValue(role, out var r) ? r.Permissions : [];

    /// <summary>Owners and admins administer the whole organization: they cannot be scoped to a business or branch.</summary>
    public static bool OrganizationWideOnly(string role) => role is Owner or Admin;

    /// <summary>Whether <paramref name="actorRole"/> may grant <paramref name="role"/> (or act on a member holding it).</summary>
    public static bool Outranks(string actorRole, string role) => actorRole == Owner || Rank(role) < Rank(actorRole);
}

public static class ScopeKinds
{
    public const string Organization = "organization";
    public const string Business = "business";
    public const string Branch = "branch";
}

/// <summary>Where a membership applies inside its organization (same shape as report-key scopes).</summary>
public sealed record AccessScope(Guid? BusinessId, Guid? BranchId)
{
    public static readonly AccessScope Organization = new(null, null);

    public string Kind => BranchId is not null ? ScopeKinds.Branch : BusinessId is not null ? ScopeKinds.Business : ScopeKinds.Organization;

    /// <summary>True if everything in <paramref name="other"/> is also inside this scope.</summary>
    public bool Covers(AccessScope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return BusinessId is null
            || (other.BusinessId == BusinessId && (BranchId is null || other.BranchId == BranchId));
    }

    public bool CoversBranch(Guid businessId, Guid branchId) => Covers(new AccessScope(businessId, branchId));

    public ScopeInfo ToInfo() => new(Kind, BusinessId?.ToString("D"), BranchId?.ToString("D"));

    public override string ToString() => BranchId is { } b ? $"branch:{b:D}" : BusinessId is { } biz ? $"business:{biz:D}" : "organization";
}

public sealed record ScopeInfo(string Kind, string? BusinessId, string? BranchId);

/// <summary>The caller's access to one organization, evaluated from the database for this request.</summary>
public sealed record MemberAccess(Guid OrganizationId, Guid UserId, Guid SessionId, string Role, AccessScope Scope, int Version)
{
    public IReadOnlyList<string> Permissions => MembershipRoles.Permissions(Role);

    public bool Has(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>Required permission of an organization endpoint (metadata read by <see cref="OrganizationAccessFilter"/>).</summary>
public sealed record PermissionRequirement(string Permission);

/// <summary>
/// Evaluates organization access per request, from the database: a change of role, scope or revocation applies to the
/// very next request. Nothing about authorization is cached in the session or the cookie.
/// </summary>
public sealed class OrganizationAccess(CloudDb db)
{
    /// <summary>The live membership of a verified user in the organization, or null (not a member, revoked, unverified).</summary>
    public async Task<MemberAccess?> EvaluateAsync(UserPrincipal principal, Guid organizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        // The caller's own memberships (pre-tenant lookup bound to the live session): the tenant context is entered
        // only after this proves membership, so a path naming another organization never opens its rows.
        var row = (await db.SessionMembershipsAsync(principal.SessionId, principal.UserId, ct)).SingleOrDefault(m => m.OrganizationId == organizationId);
        if (row is null || !MembershipRoles.IsKnown(row.Role) || !await db.Users.AsNoTracking().AnyAsync(u => u.Id == principal.UserId && u.EmailConfirmed, ct))
        {
            return null;
        }

        return new MemberAccess(organizationId, principal.UserId, principal.SessionId, row.Role, new AccessScope(row.BusinessId, row.BranchId), row.Version);
    }
}

/// <summary>
/// Deny by default for every route under <c>/v1/organizations/{organizationId}</c>: the organization comes from the path,
/// the access from the caller's membership (never from the request body). Not a member → 404 (no probing of other
/// tenants); member without the endpoint's permission → 403; endpoint without a declared permission → 403.
/// </summary>
public sealed partial class OrganizationAccessFilter(OrganizationAccess access, CloudDb db, TimeProvider time, ILogger<OrganizationAccessFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var principal = http.Items[typeof(UserPrincipal)] as UserPrincipal
            ?? throw new CloudException(401, "unauthorized", "Authentification requise ou refusée.");
        if (!Guid.TryParse(http.GetRouteValue("organizationId")?.ToString(), out var organizationId))
        {
            throw NotFound();
        }

        var member = await access.EvaluateAsync(principal, organizationId, http.RequestAborted);
        if (member is null)
        {
            // The membership lookup is bound to a live session: if this request's session was ended meanwhile (e.g. a
            // concurrent role change by another owner), the caller is signed out (401), not a non-member (404).
            throw await db.UserSessions.AnyAsync(s => s.Id == principal.SessionId && s.RevokedAt == null, http.RequestAborted)
                ? NotFound()
                : new CloudException(401, "unauthorized", "Authentification requise ou refusée.");
        }
        var required = http.GetEndpoint()?.Metadata.GetMetadata<PermissionRequirement>();
        if (required is null)
        {
            LogUndeclared(logger, http.Request.Path);
            throw Forbidden();
        }

        if (!member.Has(required.Permission))
        {
            if (required.Permission is not (Permissions.OrganizationRead or Permissions.MembersRead)) // every refused write is audited
            {
                await db.InTenantAsync(organizationId, () =>
                {
                    AuthAudit.Add(db, time, AccessEvents.PermissionDenied, http, member.UserId, member.SessionId, organizationId, details: $"permission={required.Permission}");
                    return db.SaveChangesAsync(http.RequestAborted);
                }, http.RequestAborted);
            }

            throw Forbidden();
        }

        http.Items[typeof(MemberAccess)] = member;
        return await next(context);
    }

    public static CloudException NotFound() => new(404, "not_found", "Ressource introuvable.");

    public static CloudException Forbidden() => new(403, "forbidden", "Accès refusé.");

    [LoggerMessage(Level = LogLevel.Error, Message = "Organization endpoint {Path} declares no permission: denied (deny by default)")]
    private static partial void LogUndeclared(ILogger logger, string path);
}

/// <summary>Audit event names of access management (stable).</summary>
public static class AccessEvents
{
    public const string MemberInvited = "member_invited";
    public const string InvitationRevoked = "member_invitation_revoked";
    public const string InvitationAccepted = "member_invitation_accepted";
    public const string MemberChanged = "member_changed";
    public const string MemberRevoked = "member_revoked";
    public const string PermissionDenied = "permission_denied";
    public const string LastOwnerProtected = "last_owner_protected";
}

/// <summary>Writes one row of the append-only security log (the caller saves). Never addresses, tokens or codes.</summary>
public static class AuthAudit
{
    public static void Add(CloudDb db, TimeProvider time, string auditEvent, HttpContext? http, Guid? userId, Guid? sessionId = null,
        Guid? organizationId = null, Guid? targetUserId = null, string? details = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        var agent = http?.Request.Headers.UserAgent.ToString();
        db.AuthAuditEvents.Add(new AuthAuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = time.GetUtcNow(),
            Event = auditEvent,
            UserId = userId,
            OrganizationId = organizationId,
            SessionId = sessionId,
            TargetUserId = targetUserId,
            Details = details is null ? null : details.Length <= 200 ? details : details[..200],
            IpAddress = http?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrEmpty(agent) ? null : agent.Length <= 200 ? agent : agent[..200],
        });
    }
}

public static class AccessEndpointExtensions
{
    /// <summary>Declares the permission an organization endpoint requires (checked by <see cref="OrganizationAccessFilter"/>).</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown permission {permission}.", nameof(permission));
        }

        return builder.WithMetadata(new PermissionRequirement(permission));
    }
}

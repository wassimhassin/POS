using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Pos.Cloud;

/// <summary>
/// Database-level tenant isolation (doc 15, ADR-027). Every tenant-owned table has a row-level security policy that
/// shows and accepts only rows of the organization set for the CURRENT TRANSACTION; without one, nothing. The
/// organization always comes from an authenticated principal (gateway credential, report key, verified membership,
/// invitation or onboarding token), never from the request.
/// </summary>
public static class Tenancy
{
    /// <summary>Tables protected by the <c>tenant_isolation</c> policy (organizations by <c>id</c>, order lines through their order).</summary>
    public static readonly IReadOnlyList<string> TenantTables =
    [
        "organizations", "businesses", "branches", "gateway_credentials", "provisioning_codes", "branch_sync_states", "sync_events",
        "orders", "order_lines", "branch_products", "report_keys", "organization_memberships", "membership_invitations",
    ];

    /// <summary>The security log: account rows (no organization) and organization rows inside their tenant.</summary>
    public const string AuditTable = "auth_audit_events";

    /// <summary>Account-level tables: owned by a user, not by an organization (reached by the session's user id only).</summary>
    public static readonly IReadOnlyList<string> AccountTables =
    [
        "users", "user_claims", "user_logins", "user_tokens", "user_sessions", "auth_tokens", "user_mfa", "user_recovery_codes",
    ];

    /// <summary>Functions the runtime role must be able to execute (granted at deploy time; PUBLIC has none).</summary>
    public static readonly IReadOnlyList<string> Functions =
    [
        "app_current_organization()", "app_enter_tenant(uuid)", "app_gateway_credential(uuid)", "app_report_key(uuid, text)",
        "app_provisioning_code_organization(text)", "app_gateway_key_registered(text)", "app_invitation_organization(bytea)",
        "app_session_memberships(uuid, uuid)", "app_purge_expired_invitations(timestamp with time zone, integer)",
    ];

    /// <summary>
    /// Begins a transaction bound to one organization. The context ends with the transaction (commit, rollback or
    /// dispose), so a pooled connection never carries it into another request. Entering a second organization in the
    /// same transaction is refused by the database.
    /// </summary>
    public static async Task<IDbContextTransaction> BeginTenantAsync(this CloudDb db, Guid organizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A tenant transaction needs an organization.", nameof(organizationId));
        }

        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT app_enter_tenant({organizationId})", ct);
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> in a tenant transaction and commits it.</summary>
    public static async Task<T> InTenantAsync<T>(this CloudDb db, Guid organizationId, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var tx = await db.BeginTenantAsync(organizationId, ct);
        var result = await work();
        await tx.CommitAsync(ct);
        return result;
    }

    // ---------------------------------------------------------------- pre-tenant lookups (SECURITY DEFINER, one row each)

    /// <summary>Gateway credential by id, to verify a request signature before the tenant is known.</summary>
    public static Task<GatewayLookup?> GatewayCredentialAsync(this CloudDb db, Guid gatewayId, CancellationToken ct) =>
        db.Database.SqlQuery<GatewayLookup>($"""
            SELECT organization_id AS "OrganizationId", business_id AS "BusinessId", branch_id AS "BranchId", public_key AS "PublicKey", revoked AS "Revoked"
            FROM app_gateway_credential({gatewayId})
            """).SingleOrDefaultAsync(ct);

    /// <summary>
    /// The scope of an active report key, only when the presented secret's hash matches: without the secret the lookup
    /// answers nothing (it never returns the stored hash).
    /// </summary>
    public static Task<ReportKeyLookup?> ReportKeyAsync(this CloudDb db, Guid keyId, string secretSha256, CancellationToken ct) =>
        db.Database.SqlQuery<ReportKeyLookup>($"""
            SELECT organization_id AS "OrganizationId", business_id AS "BusinessId", branch_id AS "BranchId"
            FROM app_report_key({keyId}, {secretSha256})
            """).SingleOrDefaultAsync(ct);

    /// <summary>The organization a provisioning code (by hash) was issued in, or null.</summary>
    public static Task<Guid?> ProvisioningCodeOrganizationAsync(this CloudDb db, string codeHash, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"""SELECT app_provisioning_code_organization({codeHash}) AS "Value" """).SingleAsync(ct);

    /// <summary>Whether a gateway public key is registered anywhere (keys are globally unique).</summary>
    public static Task<bool> GatewayKeyRegisteredAsync(this CloudDb db, string fingerprint, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""SELECT app_gateway_key_registered({fingerprint}) AS "Value" """).SingleAsync(ct);

    /// <summary>The organization of a membership invitation (by token hash), or null.</summary>
    public static Task<Guid?> InvitationOrganizationAsync(this CloudDb db, byte[] tokenHash, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"""SELECT app_invitation_organization({tokenHash}) AS "Value" """).SingleAsync(ct);

    /// <summary>
    /// The live memberships of the user of a live (not revoked) session, across organizations: the session must belong to
    /// the user, so the lookup cannot enumerate an arbitrary user's memberships by id alone.
    /// </summary>
    public static Task<List<UserMembershipLookup>> SessionMembershipsAsync(this CloudDb db, Guid sessionId, Guid userId, CancellationToken ct) =>
        db.Database.SqlQuery<UserMembershipLookup>($"""
            SELECT organization_id AS "OrganizationId", role AS "Role", business_id AS "BusinessId",
                   branch_id AS "BranchId", version AS "Version", created_at AS "CreatedAt"
            FROM app_session_memberships({sessionId}, {userId})
            """).ToListAsync(ct);

    /// <summary>Maintenance: deletes up to <paramref name="limit"/> invitations expired before <paramref name="cutoff"/>, in every organization.</summary>
    public static Task<int> PurgeExpiredInvitationsAsync(this CloudDb db, DateTimeOffset cutoff, int limit, CancellationToken ct) =>
        db.Database.SqlQuery<int>($"""SELECT app_purge_expired_invitations({cutoff}, {limit}) AS "Value" """).SingleAsync(ct);
}

public sealed record GatewayLookup(Guid OrganizationId, Guid BusinessId, Guid BranchId, byte[] PublicKey, bool Revoked);

public sealed record ReportKeyLookup(Guid OrganizationId, Guid? BusinessId, Guid? BranchId);

public sealed record UserMembershipLookup(Guid OrganizationId, string Role, Guid? BusinessId, Guid? BranchId, int Version, DateTimeOffset CreatedAt);

/// <summary>Endpoint filter: runs the handler inside a tenant transaction of the authenticated principal's organization.</summary>
public static class TenantEndpoints
{
    public static TBuilder InTenantOf<TBuilder>(this TBuilder builder, Func<HttpContext, Guid> organization) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var db = http.RequestServices.GetRequiredService<CloudDb>();
            await using var tx = await db.BeginTenantAsync(organization(http), http.RequestAborted);
            var result = await next(context);
            await tx.CommitAsync(http.RequestAborted);
            return result;
        });
}

/// <summary>Audit event names of tenant administration by the platform (CLI) and by gateways (stable).</summary>
public static class PlatformEvents
{
    public const string OrganizationCreated = "platform_organization_created";
    public const string BusinessCreated = "platform_business_created";
    public const string BranchCreated = "platform_branch_created";
    public const string GatewayCodeIssued = "platform_gateway_code_issued";
    public const string GatewayRevoked = "platform_gateway_revoked";
    public const string ReportKeyCreated = "platform_report_key_created";
    public const string GatewaysListed = "platform_gateways_listed";
    public const string GatewayRegistered = "gateway_registered";
    public const string GatewayReplaced = "gateway_replaced";
}

/// <summary>Deploy-time verification of row-level security, as the runtime role (used by <c>check-config</c>).</summary>
internal static class RowLevelSecurity
{
    internal sealed record RoleState(bool BypassRls, int OwnedObjects, string PresetContext);

    internal sealed record TableState(string Name, bool Enabled, string Policies);

    public static async Task<List<string>> CheckAsync(CloudDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var runtime = CloudSettings.RuntimeDb;
        var errors = new List<string>();

        // 1. The role itself: policies never apply to a BYPASSRLS role or to the owner of a table.
        var role = await db.Database.SqlQueryRaw<RoleState>("""
            SELECT r.rolbypassrls AS "BypassRls",
                   (SELECT count(*)::int FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = 'public' AND pg_has_role(current_user, c.relowner, 'USAGE')) AS "OwnedObjects",
                   coalesce(current_setting('app.organization_id', true), '') || coalesce(current_setting('app.tenant_txn', true), '') AS "PresetContext"
            FROM pg_roles r WHERE r.rolname = current_user
            """).SingleAsync();
        if (role.BypassRls)
        {
            errors.Add($"{runtime} : le rôle applicatif ne doit pas avoir BYPASSRLS (ALTER ROLE … NOBYPASSRLS).");
        }

        if (role.OwnedObjects > 0)
        {
            errors.Add($"{runtime} : le rôle applicatif possède {role.OwnedObjects} objet(s) du schéma ; un propriétaire échappe à la sécurité par ligne.");
        }

        if (role.PresetContext.Length > 0)
        {
            errors.Add($"{runtime} : un contexte d'organisation est prédéfini pour ce rôle ou cette base (ALTER ROLE/DATABASE … RESET app.organization_id).");
        }

        // 2. Every table is classified, and every tenant table carries exactly the expected policies.
        var tables = await db.Database.SqlQueryRaw<TableState>("""
            SELECT c.relname::text AS "Name", c.relrowsecurity AS "Enabled",
                   coalesce((SELECT string_agg(p.polname::text || chr(31) || p.polcmd::text || chr(31) || (CASE WHEN p.polpermissive THEN 'p' ELSE 'r' END) || chr(31)
                                               || coalesce(pg_get_expr(p.polqual, p.polrelid), '') || chr(31) || coalesce(pg_get_expr(p.polwithcheck, p.polrelid), ''),
                                               chr(30) ORDER BY p.polname)
                             FROM pg_policy p WHERE p.polrelid = c.oid), '') AS "Policies"
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname <> '__EFMigrationsHistory'
            ORDER BY 1
            """).ToListAsync();
        var unclassified = new List<string>();
        var unprotected = new List<string>();
        foreach (var table in tables)
        {
            var expected = Tenancy.TenantTables.Contains(table.Name, StringComparer.Ordinal) ? Expected.Tenant
                : table.Name == Tenancy.AuditTable ? Expected.Audit
                : Tenancy.AccountTables.Contains(table.Name, StringComparer.Ordinal) ? Expected.Account
                : Expected.Unknown;
            if (expected == Expected.Unknown)
            {
                unclassified.Add(table.Name);
            }
            else if (expected != Expected.Account && !(table.Enabled && PoliciesMatch(table, expected)))
            {
                unprotected.Add(table.Name);
            }
        }

        var missing = Tenancy.TenantTables.Append(Tenancy.AuditTable).Except(tables.Select(t => t.Name), StringComparer.Ordinal).ToList();
        if (unclassified.Count > 0)
        {
            errors.Add($"{runtime} : table(s) ni d'organisation ni de compte : {string.Join(", ", unclassified)} (classez-les et protégez-les, doc 15).");
        }

        if (unprotected.Count + missing.Count > 0)
        {
            errors.Add($"{runtime} : sécurité par ligne absente ou modifiée sur {string.Join(", ", unprotected.Concat(missing))} (réappliquez la migration CloudRowLevelSecurity).");
        }

        // 3. The tenant functions are callable by this role (and only by roles granted at deploy time).
        var notExecutable = await db.Database.SqlQueryRaw<string>(
            "SELECT f AS \"Value\" FROM unnest({0}::text[]) f WHERE to_regprocedure(f) IS NULL OR NOT has_function_privilege(current_user, to_regprocedure(f), 'EXECUTE')",
            new object[] { Tenancy.Functions.ToArray() }).ToListAsync(); // ONE text[] parameter (a bare string[] would bind as params object[])
        if (notExecutable.Count > 0)
        {
            errors.Add($"{runtime} : fonction(s) inaccessibles : {string.Join(", ", notExecutable)} (GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public).");
        }

        // 4. End to end: outside a tenant transaction, tenant rows are invisible whatever the database holds.
        if (errors.Count == 0)
        {
            var visible = await db.Database.SqlQueryRaw<long>(
                "SELECT (SELECT count(*) FROM organizations) + (SELECT count(*) FROM organization_memberships) + (SELECT count(*) FROM sync_events) AS \"Value\"").SingleAsync();
            if (visible > 0)
            {
                errors.Add($"{runtime} : des données d'organisation sont visibles sans contexte d'organisation : sécurité par ligne inopérante.");
            }
        }

        return errors;
    }

    private enum Expected
    {
        Unknown,
        Tenant,
        Audit,
        Account,
    }

    private static bool PoliciesMatch(TableState table, Expected expected)
    {
        var policies = table.Policies.Length == 0 ? [] : table.Policies.Split('\u001e').Select(p => p.Split('\u001f')).ToList(); // record/unit separators: expressions span lines
        if (expected == Expected.Audit)
        {
            return policies.Count == 2
                && policies.Any(p => p is ["tenant_audit_append", "a", "p", "", var check] && check.Contains("app_current_organization()", StringComparison.Ordinal))
                && policies.Any(p => p is ["tenant_audit_read", "r", "p", var qual, ""] && qual.Contains("app_current_organization()", StringComparison.Ordinal));
        }

        // order_lines inherits its tenant through orders (itself protected).
        var marker = table.Name == "order_lines" ? "FROM orders" : "app_current_organization()";
        return policies is [["tenant_isolation", "*", "p", var qualifier, var withCheck]]
            && qualifier.Contains(marker, StringComparison.Ordinal) && withCheck.Contains(marker, StringComparison.Ordinal);
    }
}

/// <summary>
/// The dedicated platform role (doc 16 §4, ADR-032): provisioning without schema ownership. It bypasses row-level
/// security (platform commands address any organization by id) but holds exactly the table privileges the commands
/// need: no DDL, no DELETE or TRUNCATE, no UPDATE except gateway revocation, no access to sales, members or accounts.
/// </summary>
public static class PlatformRole
{
    public const string Select = "SELECT";
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";

    /// <summary>Table → privileges, exact (more is refused, less is reported).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Privileges = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["__EFMigrationsHistory"] = [Select],
        ["organizations"] = [Select, Insert],
        ["businesses"] = [Select, Insert],
        ["branches"] = [Select, Insert],
        ["gateway_credentials"] = [Select, Update],
        ["branch_sync_states"] = [Insert],
        ["provisioning_codes"] = [Insert],
        ["report_keys"] = [Insert],
        ["auth_tokens"] = [Insert],
        ["auth_audit_events"] = [Insert],
    };

    /// <summary>The deploy-time grants for <paramref name="role"/> (an SQL identifier chosen by the operator).</summary>
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var lines = new List<string> { $"GRANT USAGE ON SCHEMA public TO {role};" };
        lines.AddRange(Privileges.Select(p => $"GRANT {string.Join(", ", p.Value)} ON \"{p.Key}\" TO {role};"));
        lines.Add($"GRANT EXECUTE ON FUNCTION app_current_organization(), app_enter_tenant(uuid) TO {role};");
        return string.Join('\n', lines);
    }

    internal sealed record RoleState(string Name, bool Superuser, bool BypassRls, bool Elevated, int OwnedObjects, bool CanCreate);

    internal sealed record Grant(string Table, string Privilege);

    /// <summary>Verifies the CURRENT connection's role is a correctly configured platform role; returns the problems.</summary>
    internal static async Task<List<string>> CheckAsync(CloudDb db, string variable)
    {
        ArgumentNullException.ThrowIfNull(db);
        var errors = new List<string>();
        var role = await db.Database.SqlQueryRaw<RoleState>("""
            SELECT r.rolname::text AS "Name", r.rolsuper AS "Superuser", r.rolbypassrls AS "BypassRls",
                   (r.rolcreaterole OR r.rolcreatedb OR r.rolreplication) AS "Elevated",
                   (SELECT count(*)::int FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = 'public' AND pg_has_role(current_user, c.relowner, 'USAGE')) AS "OwnedObjects",
                   has_schema_privilege(current_user, 'public', 'CREATE') AS "CanCreate"
            FROM pg_roles r WHERE r.rolname = current_user
            """).SingleAsync();
        if (role.Superuser || role.Elevated || role.OwnedObjects > 0 || role.CanCreate)
        {
            errors.Add($"{variable} : le rôle de plateforme doit être dédié (ni superutilisateur, ni propriétaire du schéma, ni CREATE/CREATEROLE/CREATEDB/REPLICATION) ; utilisez un rôle distinct de celui des migrations.");
            return errors; // the privilege detail below is meaningless for an owner or a superuser
        }

        if (!role.BypassRls)
        {
            errors.Add($"{variable} : opération de plateforme refusée ; ce rôle est soumis à la sécurité par ligne (il faut un rôle de plateforme BYPASSRLS dédié).");
        }

        var held = await db.Database.SqlQueryRaw<Grant>("""
            SELECT c.relname::text AS "Table", p.privilege AS "Privilege"
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS p(privilege)
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm') AND has_table_privilege(current_user, c.oid, p.privilege)
            """).ToListAsync();
        var excess = held.Where(g => !(Privileges.TryGetValue(g.Table, out var allowed) && allowed.Contains(g.Privilege, StringComparer.Ordinal)))
            .Select(g => $"{g.Privilege} {g.Table}").Order(StringComparer.Ordinal).ToList();
        var missing = Privileges.SelectMany(p => p.Value.Select(v => (Table: p.Key, Privilege: v)))
            .Where(r => !held.Any(g => g.Table == r.Table && g.Privilege == r.Privilege))
            .Select(r => $"{r.Privilege} {r.Table}").Order(StringComparer.Ordinal).ToList();
        if (excess.Count > 0)
        {
            errors.Add($"{variable} : le rôle de plateforme a des droits superflus ({string.Join(", ", excess.Take(8))}{(excess.Count > 8 ? ", …" : "")}) ; appliquez exactement les GRANT du doc 16.");
        }

        if (missing.Count > 0)
        {
            errors.Add($"{variable} : le rôle de plateforme n'a pas les droits nécessaires ({string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : "")}).");
        }

        var functions = await db.Database.SqlQueryRaw<bool>(
            "SELECT has_function_privilege(current_user, 'app_enter_tenant(uuid)', 'EXECUTE') AND has_function_privilege(current_user, 'app_current_organization()', 'EXECUTE') AS \"Value\"")
            .SingleAsync();
        if (!functions)
        {
            errors.Add($"{variable} : le rôle de plateforme ne peut pas exécuter app_enter_tenant/app_current_organization.");
        }

        return errors;
    }
}

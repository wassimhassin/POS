using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Pos.Gateway.Tests;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>Phase 2.1F: organization → business → branch management API, platform role, narrowed lookups (doc 16).</summary>
public sealed class StructureManagementTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private readonly List<Account> _accounts = [];

    public Task InitializeAsync() => _cloud.StartAsync();

    public async Task DisposeAsync()
    {
        _accounts.ForEach(a => a.Client.Dispose());
        await _cloud.DisposeAsync();
    }

    private sealed record Account(UserClient Client, string Email, Guid Id, List<string> RecoveryCodes)
    {
        /// <summary>A new full session with a recovery code (no 30 s TOTP wait).</summary>
        public async Task SignInAgainAsync()
        {
            Assert.Equal("mfa_required", await Auth.LoginAsync(Client, Email));
            var code = RecoveryCodes[0];
            RecoveryCodes.RemoveAt(0);
            await Auth.JsonAsync(await Client.PostAsync("v1/auth/login/mfa", new { recoveryCode = code }), HttpStatusCode.OK);
        }
    }

    private sealed record Tenant(Guid Org, Guid Business1, Guid Branch1, Guid Business2, Guid Branch2, Account Owner);

    private async Task<Account> AccountAsync(string prefix)
    {
        var client = new UserClient(_cloud);
        var email = Auth.NewEmail(prefix);
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        await Auth.LoginAsync(client, email);
        var (_, _, codes) = await Auth.EnableTotpAsync(client);
        var me = await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK);
        var account = new Account(client, email, Guid.Parse(me.GetProperty("id").GetString()!), codes);
        _accounts.Add(account);
        return account;
    }

    private async Task<Tenant> TenantAsync(string name)
    {
        var org = _cloud.CreateOrganization(name);
        var b1 = _cloud.CreateBusiness(org, "Café");
        var b2 = _cloud.CreateBusiness(org, "Restaurant");
        var owner = await AccountAsync("owner");
        _cloud.Admin("owner-invite", "--org", org.ToString(), "--email", owner.Email);
        var token = Auth.LatestToken(_cloud, owner.Email, "/auth/accept-invite")!;
        await Auth.JsonAsync(await owner.Client.PostAsync("v1/auth/onboarding/accept", new { token }), HttpStatusCode.OK);
        return new Tenant(org, b1, _cloud.CreateBranch(b1, "Centre"), b2, _cloud.CreateBranch(b2, "Port"), owner);
    }

    private async Task<Account> JoinAsync(Tenant t, string role, Guid? businessId = null, Guid? branchId = null)
    {
        var member = await AccountAsync(role);
        await Auth.JsonAsync(await t.Owner.Client.PostAsync($"v1/organizations/{t.Org}/invitations", new { email = member.Email, role, businessId, branchId }), HttpStatusCode.Accepted);
        var token = Auth.LatestToken(_cloud, member.Email, "/auth/join")!;
        await Auth.JsonAsync(await member.Client.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.OK);
        return member;
    }

    private long Audit(string auditEvent, Guid org) =>
        _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = @p0 AND organization_id = @p1", auditEvent, org);

    private static async Task<int> VersionAsync(HttpResponseMessage response) =>
        (await Auth.JsonAsync(response, HttpStatusCode.OK)).GetProperty("version").GetInt32();

    [Fact]
    public async Task Owners_manage_the_structure_through_the_api_with_validation_versions_and_audit()
    {
        var t = await TenantAsync("Groupe");
        var o = t.Owner.Client;
        var path = $"v1/organizations/{t.Org}";

        // Organization: read, rename with the expected version, stale version refused.
        var organization = await Auth.JsonAsync(await o.GetAsync(path), HttpStatusCode.OK);
        Assert.Equal(["createdAt", "id", "name", "updatedAt", "version"], organization.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, organization.GetProperty("version").GetInt32());
        var renamed = await Auth.JsonAsync(await o.PatchAsync(path, new { name = "  Groupe Sud  ", expectedVersion = 1 }), HttpStatusCode.OK);
        Assert.Equal("Groupe Sud", renamed.GetProperty("name").GetString()); // trimmed
        Assert.Equal(2, renamed.GetProperty("version").GetInt32());
        await Auth.ErrorAsync(await o.PatchAsync(path, new { name = "Autre", expectedVersion = 1 }), HttpStatusCode.Conflict, "version_conflict");

        // Businesses: create (server id, 201), list, read, rename; names unique per organization, case-insensitive.
        var created = await Auth.JsonAsync(await o.PostAsync($"{path}/businesses", new { name = "Boulangerie" }), HttpStatusCode.Created);
        var business = Guid.Parse(created.GetProperty("id").GetString()!);
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        await Auth.ErrorAsync(await o.PostAsync($"{path}/businesses", new { name = "boulangerie" }), HttpStatusCode.Conflict, "name_taken");
        // ILIKE wildcards in a name are literal: "100% _pur_" does not make "100x xpurx" taken.
        await Auth.JsonAsync(await o.PostAsync($"{path}/businesses", new { name = "100% _pur_" }), HttpStatusCode.Created);
        await Auth.JsonAsync(await o.PostAsync($"{path}/businesses", new { name = "100x xpurx" }), HttpStatusCode.Created);
        var list = await Auth.JsonAsync(await o.GetAsync($"{path}/businesses"), HttpStatusCode.OK);
        Assert.Contains(business.ToString("D"), list.EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        Assert.Equal("Boulangerie", (await Auth.JsonAsync(await o.GetAsync($"{path}/businesses/{business}"), HttpStatusCode.OK)).GetProperty("name").GetString());
        Assert.Equal(2, await VersionAsync(await o.PatchAsync($"{path}/businesses/{business}", new { name = "Boulangerie Nord", expectedVersion = 1 })));
        await Auth.ErrorAsync(await o.PatchAsync($"{path}/businesses/{business}", new { name = "CAFÉ", expectedVersion = 2 }), HttpStatusCode.Conflict, "name_taken");

        // Branches: create in a business (server id), read, rename; unique per business.
        var branchJson = await Auth.JsonAsync(await o.PostAsync($"{path}/businesses/{business}/branches", new { name = "Gare" }), HttpStatusCode.Created);
        var branch = Guid.Parse(branchJson.GetProperty("id").GetString()!);
        Assert.Equal(business.ToString("D"), branchJson.GetProperty("businessId").GetString());
        await Auth.ErrorAsync(await o.PostAsync($"{path}/businesses/{business}/branches", new { name = "GARE" }), HttpStatusCode.Conflict, "name_taken");
        await Auth.JsonAsync(await o.PostAsync($"{path}/businesses/{t.Business1}/branches", new { name = "Gare" }), HttpStatusCode.Created); // other business: fine
        Assert.Equal(2, await VersionAsync(await o.PatchAsync($"{path}/branches/{branch}", new { name = "Gare Centrale", expectedVersion = 1 })));
        Assert.Equal("Gare Centrale", (await Auth.JsonAsync(await o.GetAsync($"{path}/branches/{branch}"), HttpStatusCode.OK)).GetProperty("name").GetString());
        var listed = await Auth.JsonAsync(await o.GetAsync($"{path}/businesses/{business}/branches"), HttpStatusCode.OK);
        Assert.Equal([(branch.ToString("D"), 2)], listed.EnumerateArray().Select(b => (b.GetProperty("id").GetString(), b.GetProperty("version").GetInt32())));

        // Validation: names (empty, too long, control characters), unknown or immutable fields, missing version.
        foreach (var bad in new[] { "", "   ", new string('x', 121), "Nom\u0007" })
        {
            await Auth.ErrorAsync(await o.PostAsync($"{path}/businesses", new { name = bad }), HttpStatusCode.BadRequest, "invalid_name");
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await o.PostAsync($"{path}/businesses", new { name = "Fixe", id = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.PatchAsync($"{path}/branches/{branch}", new { name = "X", expectedVersion = 2, businessId = t.Business1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.PatchAsync($"{path}/businesses/{business}", new { name = "X", expectedVersion = 2, organizationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.PatchAsync($"{path}/branches/{branch}", new { name = "X" })).StatusCode);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM branches WHERE id = @p0 AND business_id = @p1 AND name = 'Gare Centrale' AND version = 2", branch, business));

        // Every mutation is audited in the organization with ids only.
        Assert.Equal(1, Audit("organization_updated", t.Org));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'business_created' AND organization_id = @p0 AND details = @p1 AND user_id = @p2",
            t.Org, $"business={business:D}", t.Owner.Id));
        Assert.Equal(1, Audit("business_updated", t.Org));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'branch_created' AND details = @p0", $"branch={branch:D};business={business:D}"));
        Assert.Equal(1, Audit("branch_updated", t.Org));
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE organization_id = @p0 AND (details LIKE '%Gare%' OR details LIKE '%Boulangerie%')", t.Org));

        // A branch created through the API is complete: the platform provisions a gateway on it, which syncs.
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM branch_sync_states WHERE branch_id = @p0", branch));
        using var gateway = await RawGatewayClient.RegisterAsync(_cloud, branch);
        await gateway.SendOkAsync(TestEvents.Order(1, 1));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM orders WHERE branch_id = @p0", branch));
    }

    [Fact]
    public async Task Scoped_members_manage_only_inside_their_scope_and_cannot_escalate()
    {
        var t = await TenantAsync("Groupe");
        var path = $"v1/organizations/{t.Org}";
        var admin = await JoinAsync(t, "admin");
        var businessManager = await JoinAsync(t, "manager", businessId: t.Business1);
        var branchManager = await JoinAsync(t, "manager", branchId: t.Branch1);
        var viewer = await JoinAsync(t, "viewer");
        var denied = Audit("permission_denied", t.Org);

        // Admin: businesses and branches, but not the organization itself (owner only).
        await Auth.JsonAsync(await admin.Client.PostAsync($"{path}/businesses", new { name = "Traiteur" }), HttpStatusCode.Created);
        await Auth.ErrorAsync(await admin.Client.PatchAsync(path, new { name = "Pris", expectedVersion = 1 }), HttpStatusCode.Forbidden, "forbidden");

        // Business manager: branches of its business only; no business creation or rename.
        var bm = businessManager.Client;
        Assert.Equal([t.Business1.ToString("D")], (await Auth.JsonAsync(await bm.GetAsync($"{path}/businesses"), HttpStatusCode.OK)).EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        var own = Guid.Parse((await Auth.JsonAsync(await bm.PostAsync($"{path}/businesses/{t.Business1}/branches", new { name = "Plage" }), HttpStatusCode.Created)).GetProperty("id").GetString()!);
        Assert.Equal(2, await VersionAsync(await bm.PatchAsync($"{path}/branches/{t.Branch1}", new { name = "Centre-ville", expectedVersion = 1 })));
        await Auth.ErrorAsync(await bm.PostAsync($"{path}/businesses", new { name = "Mien" }), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await bm.PatchAsync($"{path}/businesses/{t.Business1}", new { name = "Mien", expectedVersion = 1 }), HttpStatusCode.Forbidden, "forbidden");
        // Outside its business: indistinguishable from nothing (404), reads and writes alike.
        await Auth.ErrorAsync(await bm.GetAsync($"{path}/businesses/{t.Business2}"), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await bm.GetAsync($"{path}/branches/{t.Branch2}"), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await bm.PostAsync($"{path}/businesses/{t.Business2}/branches", new { name = "Intrus" }), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await bm.PatchAsync($"{path}/branches/{t.Branch2}", new { name = "Intrus", expectedVersion = 1 }), HttpStatusCode.NotFound, "not_found");

        // Branch manager: renames its own branch; cannot create a sibling (its scope does not cover the business).
        var br = branchManager.Client;
        Assert.Equal([t.Branch1.ToString("D")], (await Auth.JsonAsync(await br.GetAsync($"{path}/businesses/{t.Business1}/branches"), HttpStatusCode.OK)).EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        await Auth.ErrorAsync(await br.GetAsync($"{path}/businesses/{t.Business2}/branches"), HttpStatusCode.NotFound, "not_found");
        Assert.Equal(3, await VersionAsync(await br.PatchAsync($"{path}/branches/{t.Branch1}", new { name = "Centre", expectedVersion = 2 })));
        await Auth.ErrorAsync(await br.PostAsync($"{path}/businesses/{t.Business1}/branches", new { name = "Frère" }), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await br.GetAsync($"{path}/branches/{own}"), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await br.PatchAsync($"{path}/branches/{own}", new { name = "Pris", expectedVersion = 1 }), HttpStatusCode.NotFound, "not_found");

        // Viewer: reads everything in scope, changes nothing.
        Assert.Equal("Centre", (await Auth.JsonAsync(await viewer.Client.GetAsync($"{path}/branches/{t.Branch1}"), HttpStatusCode.OK)).GetProperty("name").GetString());
        await Auth.ErrorAsync(await viewer.Client.PatchAsync($"{path}/branches/{t.Branch1}", new { name = "V", expectedVersion = 3 }), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await viewer.Client.PostAsync($"{path}/businesses", new { name = "V" }), HttpStatusCode.Forbidden, "forbidden");

        Assert.True(Audit("permission_denied", t.Org) - denied >= 6);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM branches WHERE business_id = @p0 AND name = 'Plage'", t.Business1));
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM branches WHERE name IN ('Intrus', 'Frère')"));
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM businesses WHERE name IN ('Mien', 'V')"));

        // Revoked: the very next request sees nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.Client.DeleteAsync($"{path}/members/{businessManager.Id}")).StatusCode);
        await businessManager.SignInAgainAsync();
        await Auth.ErrorAsync(await businessManager.Client.GetAsync($"{path}/businesses/{t.Business1}"), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await businessManager.Client.PostAsync($"{path}/businesses/{t.Business1}/branches", new { name = "Après" }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Another_organization_is_invisible_and_errors_do_not_reveal_its_resources()
    {
        var a = await TenantAsync("Alpha");
        var b = await TenantAsync("Beta");
        var o = a.Owner.Client;
        var pathA = $"v1/organizations/{a.Org}";

        // B's organization through B's path: not a member → 404, the same as an organization that does not exist.
        var other = await o.GetAsync($"v1/organizations/{b.Org}");
        var none = await o.GetAsync($"v1/organizations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(await none.Content.ReadAsStringAsync(), await other.Content.ReadAsStringAsync());
        await Auth.ErrorAsync(await o.PatchAsync($"v1/organizations/{b.Org}", new { name = "Pris", expectedVersion = 1 }), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await o.PostAsync($"v1/organizations/{b.Org}/businesses", new { name = "Pris" }), HttpStatusCode.NotFound, "not_found");

        // B's ids through A's path (where the caller is owner): exactly the answer for random ids.
        async Task<string> Body(HttpResponseMessage r)
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        var random = Guid.NewGuid();
        Assert.Equal(await Body(await o.GetAsync($"{pathA}/businesses/{random}")), await Body(await o.GetAsync($"{pathA}/businesses/{b.Business1}")));
        Assert.Equal(await Body(await o.GetAsync($"{pathA}/branches/{random}")), await Body(await o.GetAsync($"{pathA}/branches/{b.Branch1}")));
        Assert.Equal(await Body(await o.GetAsync($"{pathA}/businesses/{random}/branches")), await Body(await o.GetAsync($"{pathA}/businesses/{b.Business1}/branches")));
        Assert.Equal(await Body(await o.PatchAsync($"{pathA}/businesses/{random}", new { name = "Pris", expectedVersion = 1 })),
            await Body(await o.PatchAsync($"{pathA}/businesses/{b.Business1}", new { name = "Pris", expectedVersion = 1 })));
        Assert.Equal(await Body(await o.PatchAsync($"{pathA}/branches/{random}", new { name = "Pris", expectedVersion = 1 })),
            await Body(await o.PatchAsync($"{pathA}/branches/{b.Branch1}", new { name = "Pris", expectedVersion = 1 })));
        Assert.Equal(await Body(await o.PostAsync($"{pathA}/businesses/{random}/branches", new { name = "Pris" })),
            await Body(await o.PostAsync($"{pathA}/businesses/{b.Business1}/branches", new { name = "Pris" })));
        // A name used in B is free in A (uniqueness never answers across tenants).
        await Auth.JsonAsync(await o.PostAsync($"{pathA}/businesses", new { name = "Restaurant 2" }), HttpStatusCode.Created);
        await Auth.JsonAsync(await b.Owner.Client.PostAsync($"v1/organizations/{b.Org}/businesses", new { name = "Restaurant 2" }), HttpStatusCode.Created);

        // B unchanged; A's lists never contain B.
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM businesses WHERE organization_id = @p0 AND name = 'Pris'", b.Org) + _cloud.Count("SELECT COUNT(*) FROM branches WHERE name = 'Pris'"));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM organizations WHERE id = @p0 AND version = 1", b.Org));
        var businesses = (await Auth.JsonAsync(await o.GetAsync($"{pathA}/businesses"), HttpStatusCode.OK)).EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain(b.Business1.ToString("D"), businesses);
        Assert.DoesNotContain(b.Business2.ToString("D"), businesses);
    }

    [Fact]
    public async Task Concurrent_updates_and_creations_are_serialized_with_predictable_errors()
    {
        var t = await TenantAsync("Groupe");
        var path = $"v1/organizations/{t.Org}";
        var admin = await JoinAsync(t, "admin");
        UserClient[] clients = [t.Owner.Client, admin.Client];

        // Eight renames with the same expected version: one wins, seven get version_conflict.
        var renames = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            clients[i % 2].PatchAsync($"{path}/branches/{t.Branch1}", new { name = $"Centre {i}", expectedVersion = 1 })));
        Assert.Equal(1, renames.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var conflict in renames.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await Auth.ErrorAsync(conflict, HttpStatusCode.Conflict, "version_conflict");
        }

        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM branches WHERE id = @p0 AND version = 2", t.Branch1));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'branch_updated' AND organization_id = @p0", t.Org));

        // Eight creations of the same name (different case): exactly one business; the others name_taken.
        var creations = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            clients[i % 2].PostAsync($"{path}/businesses", new { name = i % 2 == 0 ? "Glacier" : "GLACIER" })));
        Assert.Equal(1, creations.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var taken in creations.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await Auth.ErrorAsync(taken, HttpStatusCode.Conflict, "name_taken");
        }

        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM businesses WHERE organization_id = @p0 AND lower(name) = 'glacier'", t.Org));

        // Renames of two siblings to the same new name at once: one wins.
        var swap = await Task.WhenAll(
            t.Owner.Client.PatchAsync($"{path}/businesses/{t.Business1}", new { name = "Fusion", expectedVersion = 1 }),
            admin.Client.PatchAsync($"{path}/businesses/{t.Business2}", new { name = "Fusion", expectedVersion = 1 }));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], swap.Select(r => r.StatusCode).Order());
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM businesses WHERE organization_id = @p0 AND name = 'Fusion'", t.Org));

        // Distinct names in parallel: all created, none lost.
        var distinct = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => clients[i % 2].PostAsync($"{path}/businesses/{t.Business2}/branches", new { name = $"Kiosque {i}" })));
        Assert.All(distinct, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(6, _cloud.Count("SELECT COUNT(*) FROM branches WHERE business_id = @p0 AND name LIKE 'Kiosque %'", t.Business2));
    }

    [Fact]
    public async Task The_platform_role_is_dedicated_least_privilege_and_its_listing_is_audited()
    {
        var t = await TenantAsync("Groupe");
        var db = _cloud.Database;

        // Platform commands require the dedicated role: unset, the schema owner, or the runtime role are refused.
        void Refused(string? connection, string expected)
        {
            var (code, output) = _cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_ADMIN_DB"] = connection }, "org-create", "--name", "Refusée");
            Assert.True(code == 2 && output.Contains(expected, StringComparison.Ordinal), $"{code}: {output}");
        }

        Refused(null, "POS_CLOUD_ADMIN_DB");
        Refused(PostgresCluster.Instance.AdminConnection(db), "distinct de celui de POS_CLOUD_MIGRATION_DB");
        Refused(PostgresCluster.Instance.AppConnection(db), "distinct de celui de POS_CLOUD_DB");
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM organizations WHERE name = 'Refusée'"));

        // The role cannot read sales, members or accounts, delete anything, or rewrite the security log.
        await using (var platform = new NpgsqlConnection(PostgresCluster.Instance.PlatformConnection(db)))
        {
            await platform.OpenAsync();
            foreach (var sql in new[]
            {
                "SELECT count(*) FROM orders", "SELECT count(*) FROM sync_events", "SELECT count(*) FROM organization_memberships",
                "SELECT count(*) FROM users", "SELECT count(*) FROM user_sessions", "SELECT count(*) FROM auth_audit_events",
                "DELETE FROM businesses", "UPDATE organizations SET name = 'x'", "UPDATE auth_audit_events SET details = ''", "TRUNCATE report_keys",
                "CREATE TABLE intrus (id int)",
            })
            {
                await using var command = new NpgsqlCommand(sql, platform);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{sql}: {ex.SqlState}");
            }
        }

        // An extra privilege is refused (by the command and by check-config) until revoked.
        PostgresCluster.Instance.Execute(db, "GRANT DELETE ON businesses TO pos_platform;");
        try
        {
            var (code, output) = _cloud.AdminRaw("org-create", "--name", "Refusée");
            Assert.True(code == 2 && output.Contains("droits superflus (DELETE businesses)", StringComparison.Ordinal), $"{code}: {output}");
            var (checkCode, checkOutput) = CheckConfig();
            Assert.True(checkCode != 0 && checkOutput.Contains("droits superflus", StringComparison.Ordinal), $"{checkCode}: {checkOutput}");
        }
        finally
        {
            PostgresCluster.Instance.Execute(db, "REVOKE DELETE ON businesses FROM pos_platform;");
        }

        var (okCode, okOutput) = CheckConfig();
        Assert.True(okCode == 0 && okOutput.Contains("platform_role=ok", StringComparison.Ordinal), $"{okCode}: {okOutput}");

        // The printed grants (applied by the kit, as by operators) never include DELETE, TRUNCATE or audit UPDATE.
        var grants = _cloud.Admin("platform-grants", "--role", "pos_platform");
        Assert.Contains("GRANT INSERT ON \"auth_audit_events\" TO pos_platform;", grants, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, UPDATE ON \"gateway_credentials\" TO pos_platform;", grants, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", grants, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATE", grants, StringComparison.Ordinal);
        Assert.Equal(2, _cloud.AdminRaw("platform-grants", "--role", "x; DROP TABLE y").ExitCode);

        // Listing gateways is audited per organization listed (ids and counts only).
        using var gateway = await RawGatewayClient.RegisterAsync(_cloud, t.Branch1);
        Assert.Contains(gateway.GatewayId, _cloud.Admin("gateways"), StringComparison.Ordinal);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'platform_gateways_listed' AND organization_id = @p0 AND details = 'branch=*;count=1'", t.Org));
        _cloud.Admin("gateways", "--branch", t.Branch2.ToString());
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'platform_gateways_listed' AND organization_id = @p0 AND details = @p1",
            t.Org, $"branch={t.Branch2:D};count=0"));
    }

    [Fact]
    public async Task Pre_tenant_lookups_answer_only_with_their_secret_or_a_live_session()
    {
        var t = await TenantAsync("Groupe");
        var db = _cloud.Database;
        var token = CloudProcess.Value(_cloud.Admin("report-key", "--org", t.Org.ToString(), "--name", "Compta"), "report_key");
        var parts = token.Split('.');
        var keyId = Guid.ParseExact(parts[1], "N");
        var secretHash = SyncProtocol.Sha256Hex(Encoding.ASCII.GetBytes(parts[2]));
        var sessionId = (Guid)(await ScalarAsync("SELECT id FROM user_sessions WHERE user_id = @p0 AND revoked_at IS NULL ORDER BY created_at DESC LIMIT 1", t.Owner.Id))!;

        // The old, wider lookups are gone; the new ones are not executable by PUBLIC.
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM pg_proc WHERE proname IN ('app_user_memberships') OR (proname = 'app_report_key' AND pronargs = 1)"));
        Assert.Equal(false, await ScalarAsync("SELECT has_function_privilege('public', 'app_report_key(uuid, text)', 'EXECUTE') OR has_function_privilege('public', 'app_session_memberships(uuid, uuid)', 'EXECUTE')"));

        // Report key: nothing without the matching secret hash, and the stored hash is never returned.
        Assert.Equal(0L, await AppScalarAsync("SELECT count(*) FROM app_report_key(@p0, @p1)", keyId, new string('0', 64)));
        Assert.Equal(1L, await AppScalarAsync("SELECT count(*) FROM app_report_key(@p0, @p1)", keyId, secretHash));
        Assert.Equal(3L, await AppScalarAsync("SELECT count(*) FROM information_schema.parameters WHERE specific_name LIKE 'app_report_key%' AND parameter_mode = 'OUT'"));

        // Memberships: only for the user of a live session of that user.
        Assert.Equal(1L, await AppScalarAsync("SELECT count(*) FROM app_session_memberships(@p0, @p1)", sessionId, t.Owner.Id));
        Assert.Equal(0L, await AppScalarAsync("SELECT count(*) FROM app_session_memberships(@p0, @p1)", Guid.NewGuid(), t.Owner.Id));
        var other = await AccountAsync("other");
        var otherSession = (Guid)(await ScalarAsync("SELECT id FROM user_sessions WHERE user_id = @p0 AND revoked_at IS NULL LIMIT 1", other.Id))!;
        Assert.Equal(0L, await AppScalarAsync("SELECT count(*) FROM app_session_memberships(@p0, @p1)", otherSession, t.Owner.Id)); // someone else's session
        PostgresCluster.Instance.Execute(db, $"UPDATE user_sessions SET revoked_at = now() WHERE id = '{sessionId}'");
        Assert.Equal(0L, await AppScalarAsync("SELECT count(*) FROM app_session_memberships(@p0, @p1)", sessionId, t.Owner.Id));

        // Reports still authenticate through the narrowed lookup; a wrong secret is refused.
        using var http = _cloud.Http(token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"v1/reports/organizations/{t.Org:D}")).StatusCode);
        using var wrong = _cloud.Http($"{parts[0]}.{parts[1]}.{new string('A', 64)}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync($"v1/reports/organizations/{t.Org:D}")).StatusCode);
    }

    private (int ExitCode, string Output) CheckConfig() => _cloud.AdminRaw(["check-config", .. _cloud.RunArgs().Skip(1)]);

    private async Task<object?> ScalarAsync(string sql, params object[] parameters) =>
        await ScalarAsync(PostgresCluster.Instance.AdminConnection(_cloud.Database), sql, parameters);

    private async Task<object?> AppScalarAsync(string sql, params object[] parameters) =>
        await ScalarAsync(PostgresCluster.Instance.AppConnection(_cloud.Database), sql, parameters);

    private static async Task<object?> ScalarAsync(string connectionString, string sql, object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return await command.ExecuteScalarAsync();
    }
}

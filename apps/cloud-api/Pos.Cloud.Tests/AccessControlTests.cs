using System.Net;
using System.Text.Json;
using Npgsql;
using Pos.Gateway.Tests;

namespace Pos.Cloud.Tests;

/// <summary>Phase 2.1D: roles, scopes and membership management (doc 14). Every organization is created per test.</summary>
public sealed class AccessControlTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private readonly List<Account> _accounts = [];

    public Task InitializeAsync() => _cloud.StartAsync();

    public async Task DisposeAsync()
    {
        _accounts.ForEach(a => a.Client.Dispose());
        await _cloud.DisposeAsync();
    }

    /// <summary>A verified user with TOTP enabled and a full session; recovery codes for later sign-ins (no 30 s TOTP waits).</summary>
    private sealed class Account(UserClient client, string email, Guid id, List<string> recoveryCodes)
    {
        public UserClient Client => client;
        public string Email => email;
        public Guid Id => id;

        public async Task SignInAgainAsync()
        {
            Assert.Equal("mfa_required", await Auth.LoginAsync(client, email));
            var code = recoveryCodes[0];
            recoveryCodes.RemoveAt(0);
            await Auth.JsonAsync(await client.PostAsync("v1/auth/login/mfa", new { recoveryCode = code }), HttpStatusCode.OK);
        }
    }

    private sealed record Structure(Guid Org, Guid Business1, Guid BranchA, Guid BranchC, Guid Business2, Guid BranchD);

    private Structure NewStructure(string name)
    {
        var org = _cloud.CreateOrganization(name);
        var b1 = _cloud.CreateBusiness(org, "Café");
        var b2 = _cloud.CreateBusiness(org, "Restaurant");
        return new Structure(org, b1, _cloud.CreateBranch(b1, "Centre"), _cloud.CreateBranch(b1, "Gare"), b2, _cloud.CreateBranch(b2, "Port"));
    }

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

    private async Task<Account> OwnerAsync(Guid org)
    {
        var owner = await AccountAsync("owner");
        _cloud.Admin("owner-invite", "--org", org.ToString(), "--email", owner.Email);
        var token = Auth.LatestToken(_cloud, owner.Email, "/auth/accept-invite")!;
        await Auth.JsonAsync(await owner.Client.PostAsync("v1/auth/onboarding/accept", new { token }), HttpStatusCode.OK);
        return owner;
    }

    private static Task<HttpResponseMessage> InviteAsync(Account actor, Guid org, string email, string role, Guid? businessId = null, Guid? branchId = null) =>
        actor.Client.PostAsync($"v1/organizations/{org}/invitations", new { email, role, businessId, branchId });

    private async Task<Account> JoinAsync(Account actor, Guid org, string role, Guid? businessId = null, Guid? branchId = null)
    {
        var member = await AccountAsync(role);
        await Auth.JsonAsync(await InviteAsync(actor, org, member.Email, role, businessId, branchId), HttpStatusCode.Accepted);
        var token = Auth.LatestToken(_cloud, member.Email, "/auth/join")!;
        var joined = await Auth.JsonAsync(await member.Client.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.OK);
        Assert.Equal(role, joined.GetProperty("role").GetString());
        return member;
    }

    private static Task<HttpResponseMessage> ChangeAsync(Account actor, Guid org, Guid user, string role, int expectedVersion, Guid? businessId = null, Guid? branchId = null) =>
        actor.Client.PatchAsync($"v1/organizations/{org}/members/{user}", new { role, expectedVersion, businessId, branchId });

    private static async Task<int> VersionAsync(Account reader, Guid org, Guid user)
    {
        var members = await Auth.JsonAsync(await reader.Client.GetAsync($"v1/organizations/{org}/members"), HttpStatusCode.OK);
        return members.EnumerateArray().Single(m => m.GetProperty("userId").GetString() == user.ToString("D")).GetProperty("version").GetInt32();
    }

    private static List<string> Ids(JsonElement array, string property) => [.. array.EnumerateArray().Select(e => e.GetProperty(property).GetString()!).Order()];

    private static List<string> Sorted(params Guid[] ids) => [.. ids.Select(i => i.ToString("D")).Order()];

    private long Audit(string auditEvent, Guid org) =>
        _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = @p0 AND organization_id = @p1", auditEvent, org);

    [Fact]
    public async Task Roles_grant_their_catalog_permissions_inside_their_scope_and_nothing_else()
    {
        var s = NewStructure("Groupe");
        var owner = await OwnerAsync(s.Org);
        var admin = await JoinAsync(owner, s.Org, "admin");
        var manager = await JoinAsync(owner, s.Org, "manager", branchId: s.BranchA); // business inferred from the branch
        var viewer = await JoinAsync(admin, s.Org, "viewer", businessId: s.Business1);

        async Task<JsonElement> Access(Account a) => await Auth.JsonAsync(await a.Client.GetAsync($"v1/organizations/{s.Org}/access"), HttpStatusCode.OK);
        var ownerAccess = await Access(owner);
        Assert.Equal("owner", ownerAccess.GetProperty("role").GetString());
        Assert.Equal("organization", ownerAccess.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(["organization.read", "members.read", "members.invite", "members.manage", "organization.manage", "businesses.manage", "branches.manage"], ownerAccess.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        var managerAccess = await Access(manager);
        Assert.Equal("branch", managerAccess.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(s.Business1.ToString("D"), managerAccess.GetProperty("scope").GetProperty("businessId").GetString());
        Assert.Equal(s.BranchA.ToString("D"), managerAccess.GetProperty("scope").GetProperty("branchId").GetString());
        Assert.Equal(["organization.read", "members.read", "branches.manage"], managerAccess.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["organization.read"], (await Access(viewer)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // Structure, filtered by scope.
        async Task<List<string>> Branches(Account a) => Ids(await Auth.JsonAsync(await a.Client.GetAsync($"v1/organizations/{s.Org}/branches"), HttpStatusCode.OK), "id");
        Assert.Equal(Sorted(s.BranchA, s.BranchC, s.BranchD), await Branches(owner));
        Assert.Equal(Sorted(s.BranchA, s.BranchC), await Branches(viewer)); // business scope
        Assert.Equal(Sorted(s.BranchA), await Branches(manager));          // branch scope

        // Members: everyone for organization-wide readers; only the branch for its manager; nothing for a viewer.
        var all = await Auth.JsonAsync(await admin.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.OK);
        Assert.Equal(Sorted(owner.Id, admin.Id, manager.Id, viewer.Id), Ids(all, "userId"));
        Assert.Equal(Sorted(manager.Id), Ids(await Auth.JsonAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.OK), "userId"));
        await Auth.ErrorAsync(await viewer.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.Forbidden, "forbidden");

        // Write permissions are denied (and audited) for roles without them.
        await Auth.ErrorAsync(await InviteAsync(viewer, s.Org, Auth.NewEmail("x"), "viewer"), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await InviteAsync(manager, s.Org, Auth.NewEmail("x"), "viewer", branchId: s.BranchA), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await manager.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{viewer.Id}"), HttpStatusCode.Forbidden, "forbidden");
        Assert.True(Audit("permission_denied", s.Org) >= 3);

        // Signed out or not a member: nothing to see (401 / 404, never 403 that would confirm the organization).
        using var anonymous = new UserClient(_cloud);
        await Auth.ErrorAsync(await anonymous.GetAsync($"v1/organizations/{s.Org}/access"), HttpStatusCode.Unauthorized, "unauthorized");
        var outsider = await AccountAsync("outsider");
        await Auth.ErrorAsync(await outsider.Client.GetAsync($"v1/organizations/{s.Org}/access"), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await outsider.Client.GetAsync($"v1/organizations/{Guid.NewGuid()}/access"), HttpStatusCode.NotFound, "not_found");

        // /v1/me lists the roles too (unchanged shape).
        var me = await Auth.JsonAsync(await manager.Client.GetAsync("v1/me"), HttpStatusCode.OK);
        Assert.Equal("manager", Assert.Single(me.GetProperty("memberships").EnumerateArray()).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Other_organizations_and_their_businesses_or_branches_are_unreachable()
    {
        var x = NewStructure("Alpha");
        var y = NewStructure("Beta");
        var ownerX = await OwnerAsync(x.Org);
        var ownerY = await OwnerAsync(y.Org);
        var memberY = await JoinAsync(ownerY, y.Org, "viewer");

        // Another tenant's organization behaves as if it did not exist.
        foreach (var path in new[] { "access", "branches", "members", "invitations" })
        {
            await Auth.ErrorAsync(await ownerX.Client.GetAsync($"v1/organizations/{y.Org}/{path}"), HttpStatusCode.NotFound, "not_found");
        }

        await Auth.ErrorAsync(await ChangeAsync(ownerX, y.Org, memberY.Id, "manager", 1), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await ownerX.Client.DeleteAsync($"v1/organizations/{y.Org}/members/{memberY.Id}"), HttpStatusCode.NotFound, "not_found");
        await Auth.ErrorAsync(await InviteAsync(ownerX, y.Org, Auth.NewEmail("x"), "viewer"), HttpStatusCode.NotFound, "not_found");

        // A member of Y addressed through X's path is not found either.
        await Auth.ErrorAsync(await ChangeAsync(ownerX, x.Org, memberY.Id, "manager", 1), HttpStatusCode.NotFound, "not_found");

        // Scopes are taken from THIS organization only: Y's branch is as invalid as a random id (same answer).
        var foreign = await InviteAsync(ownerX, x.Org, Auth.NewEmail("x"), "manager", branchId: y.BranchA);
        var random = await InviteAsync(ownerX, x.Org, Auth.NewEmail("x"), "manager", branchId: Guid.NewGuid());
        await Auth.ErrorAsync(foreign, HttpStatusCode.BadRequest, "invalid_scope");
        await Auth.ErrorAsync(random, HttpStatusCode.BadRequest, "invalid_scope");
        await Auth.ErrorAsync(await InviteAsync(ownerX, x.Org, Auth.NewEmail("x"), "manager", businessId: x.Business1, branchId: x.BranchD), HttpStatusCode.BadRequest, "invalid_scope"); // branch of another business
        var viewerX = await JoinAsync(ownerX, x.Org, "viewer");
        await Auth.ErrorAsync(await ChangeAsync(ownerX, x.Org, viewerX.Id, "manager", 1, businessId: y.Business1), HttpStatusCode.BadRequest, "invalid_scope");

        // Defence in depth: the database refuses a membership scoped to another organization's branch.
        Assert.ThrowsAny<PostgresException>(() => PostgresCluster.Instance.Execute(_cloud.Database,
            $"UPDATE organization_memberships SET business_id = '{y.Business1}', branch_id = '{y.BranchA}', role = 'manager' WHERE user_id = '{viewerX.Id}'"));
    }

    [Fact]
    public async Task Nobody_can_grant_or_act_on_a_role_at_or_above_their_own()
    {
        var s = NewStructure("Groupe");
        var owner = await OwnerAsync(s.Org);
        var admin = await JoinAsync(owner, s.Org, "admin");
        var admin2 = await JoinAsync(owner, s.Org, "admin");
        var manager = await JoinAsync(admin, s.Org, "manager", branchId: s.BranchA);

        // Admins invite below their rank only.
        await Auth.ErrorAsync(await InviteAsync(admin, s.Org, Auth.NewEmail("x"), "owner"), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await InviteAsync(admin, s.Org, Auth.NewEmail("x"), "admin"), HttpStatusCode.Forbidden, "forbidden");

        // ...and change/revoke below their rank only, never upwards and never themselves upwards.
        await Auth.ErrorAsync(await ChangeAsync(admin, s.Org, owner.Id, "admin", 1), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await ChangeAsync(admin, s.Org, admin2.Id, "manager", 1, branchId: s.BranchA), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await ChangeAsync(admin, s.Org, manager.Id, "admin", 1), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await ChangeAsync(admin, s.Org, admin.Id, "owner", 1), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await admin.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{owner.Id}"), HttpStatusCode.Forbidden, "forbidden");
        await Auth.ErrorAsync(await admin.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{admin2.Id}"), HttpStatusCode.Forbidden, "forbidden");

        // Role/scope rules: owners and admins are organization-wide; unknown roles are refused.
        await Auth.ErrorAsync(await InviteAsync(owner, s.Org, Auth.NewEmail("x"), "admin", branchId: s.BranchA), HttpStatusCode.BadRequest, "invalid_scope");
        await Auth.ErrorAsync(await InviteAsync(owner, s.Org, Auth.NewEmail("x"), "superuser"), HttpStatusCode.BadRequest, "invalid_role");

        // What is allowed: an admin moves a manager to another branch and makes them a viewer.
        var moved = await Auth.JsonAsync(await ChangeAsync(admin, s.Org, manager.Id, "viewer", 1, branchId: s.BranchC), HttpStatusCode.OK);
        Assert.Equal("viewer", moved.GetProperty("role").GetString());
        Assert.Equal(s.BranchC.ToString("D"), moved.GetProperty("scope").GetProperty("branchId").GetString());
        Assert.Equal(2, moved.GetProperty("version").GetInt32());

        // Nothing above was applied, and the refusals are audited with ids only.
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM organization_memberships WHERE organization_id = @p0 AND role = 'owner' AND revoked_at IS NULL", s.Org));
        Assert.Equal(2, _cloud.Count("SELECT COUNT(*) FROM organization_memberships WHERE organization_id = @p0 AND role = 'admin' AND revoked_at IS NULL", s.Org));
        Assert.True(Audit("permission_denied", s.Org) >= 8);
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE organization_id = @p0 AND details LIKE '%@%'", s.Org));
    }

    [Fact]
    public async Task The_last_active_owner_can_be_neither_demoted_nor_revoked()
    {
        var s = NewStructure("Groupe");
        var owner = await OwnerAsync(s.Org);
        await Auth.ErrorAsync(await ChangeAsync(owner, s.Org, owner.Id, "admin", 1), HttpStatusCode.Conflict, "last_owner");
        await Auth.ErrorAsync(await owner.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{owner.Id}"), HttpStatusCode.Conflict, "last_owner");
        Assert.Equal(2, Audit("last_owner_protected", s.Org));

        // With a second owner, the first may step down; then the second is the last one.
        var second = await JoinAsync(owner, s.Org, "owner");
        await Auth.JsonAsync(await ChangeAsync(owner, s.Org, owner.Id, "admin", 1), HttpStatusCode.OK);
        await Auth.ErrorAsync(await owner.Client.GetAsync($"v1/organizations/{s.Org}/access"), HttpStatusCode.Unauthorized, "unauthorized"); // own sessions ended too
        await Auth.ErrorAsync(await second.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{second.Id}"), HttpStatusCode.Conflict, "last_owner");
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM organization_memberships WHERE organization_id = @p0 AND role = 'owner' AND revoked_at IS NULL", s.Org));
    }

    [Fact]
    public async Task Concurrent_membership_changes_are_serialized()
    {
        var s = NewStructure("Groupe");
        var a = await OwnerAsync(s.Org);
        var b = await JoinAsync(a, s.Org, "owner");

        // Two owners demote each other at the same moment: exactly one wins. The loser has lost the authority to act:
        // 403 when its request was already inside (actor re-read under the lock), 401 when its sessions ended first.
        var results = await Task.WhenAll(ChangeAsync(a, s.Org, b.Id, "admin", 1), ChangeAsync(b, s.Org, a.Id, "admin", 1));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM organization_memberships WHERE organization_id = @p0 AND role = 'owner' AND revoked_at IS NULL", s.Org));

        // The remaining owner and a fresh co-owner both step down at once: one succeeds, the other is the last owner.
        var survivor = results[0].StatusCode == HttpStatusCode.OK ? a : b;
        var c = await JoinAsync(survivor, s.Org, "owner");
        var leaving = await Task.WhenAll(
            survivor.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{survivor.Id}"),
            c.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{c.Id}"));
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], leaving.Select(r => r.StatusCode).Order());
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM organization_memberships WHERE organization_id = @p0 AND role = 'owner' AND revoked_at IS NULL", s.Org));

        // Two administrators editing the same member from the same version: one change, one version conflict.
        var owner = leaving[0].StatusCode == HttpStatusCode.NoContent ? c : survivor;
        var admin = await JoinAsync(owner, s.Org, "admin");
        var target = await JoinAsync(owner, s.Org, "viewer");
        var edits = await Task.WhenAll(
            ChangeAsync(owner, s.Org, target.Id, "manager", 1, branchId: s.BranchA),
            ChangeAsync(admin, s.Org, target.Id, "viewer", 1, businessId: s.Business2));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], edits.Select(r => r.StatusCode).Order());
        await Auth.ErrorAsync(edits.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "version_conflict");
        Assert.Equal(2, await VersionAsync(owner, s.Org, target.Id));
    }

    [Fact]
    public async Task Invitations_are_single_use_expire_and_bind_to_the_address_and_the_inviters_authority()
    {
        var s = NewStructure("Groupe");
        var owner = await OwnerAsync(s.Org);
        var admin = await JoinAsync(owner, s.Org, "admin");

        // Bound to the invited, verified address; MFA first (the refusal does not consume it); then single use.
        var invitee = Auth.NewEmail("invitee");
        await Auth.JsonAsync(await InviteAsync(admin, s.Org, invitee, "viewer", branchId: s.BranchA), HttpStatusCode.Accepted);
        var token = Auth.LatestToken(_cloud, invitee, "/auth/join")!;
        var intruder = await AccountAsync("intruder");
        await Auth.ErrorAsync(await intruder.Client.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.BadRequest, "invalid_token");
        using (var noMfa = new UserClient(_cloud))
        {
            await Auth.RegisterVerifiedAsync(_cloud, noMfa, invitee);
            await Auth.LoginAsync(noMfa, invitee);
            await Auth.ErrorAsync(await noMfa.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.Forbidden, "mfa_required");
            await Auth.EnableTotpAsync(noMfa);
            await Auth.JsonAsync(await noMfa.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.OK);
            await Auth.ErrorAsync(await noMfa.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.BadRequest, "invalid_token"); // replay
            await Auth.ErrorAsync(await InviteAsync(admin, s.Org, invitee, "viewer"), HttpStatusCode.Conflict, "already_member");
        }

        Assert.DoesNotContain(token, string.Join('\n', _cloud.Log), StringComparison.Ordinal);
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM membership_invitations WHERE token_hash = convert_to(@p0, 'UTF8')", token)); // only the hash is stored

        // Expired.
        var late = await AccountAsync("late");
        await Auth.JsonAsync(await InviteAsync(admin, s.Org, late.Email, "viewer"), HttpStatusCode.Accepted);
        var lateToken = Auth.LatestToken(_cloud, late.Email, "/auth/join")!;
        _cloud.Count("WITH u AS (UPDATE membership_invitations SET expires_at = now() - interval '1 second' WHERE normalized_email = upper(@p0) RETURNING 1) SELECT COUNT(*) FROM u", late.Email);
        await Auth.ErrorAsync(await late.Client.PostAsync("v1/invitations/accept", new { token = lateToken }), HttpStatusCode.BadRequest, "invalid_token");

        // Superseded by a newer invitation, then cancelled.
        var twice = await AccountAsync("twice");
        await Auth.JsonAsync(await InviteAsync(admin, s.Org, twice.Email, "viewer"), HttpStatusCode.Accepted);
        var first = Auth.LatestToken(_cloud, twice.Email, "/auth/join")!;
        var created = await Auth.JsonAsync(await InviteAsync(owner, s.Org, twice.Email, "manager", businessId: s.Business2), HttpStatusCode.Accepted);
        string? second = null;
        await Wait.UntilAsync(() => (second = Auth.LatestToken(_cloud, twice.Email, "/auth/join")) != first, "second invitation mail");
        await Auth.ErrorAsync(await twice.Client.PostAsync("v1/invitations/accept", new { token = first }), HttpStatusCode.BadRequest, "invalid_token");
        var pending = await Auth.JsonAsync(await admin.Client.GetAsync($"v1/organizations/{s.Org}/invitations"), HttpStatusCode.OK);
        Assert.Contains(pending.EnumerateArray(), i => i.GetProperty("id").GetString() == created.GetProperty("invitationId").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"v1/organizations/{s.Org}/invitations/{created.GetProperty("invitationId").GetString()}")).StatusCode);
        await Auth.ErrorAsync(await twice.Client.PostAsync("v1/invitations/accept", new { token = second }), HttpStatusCode.BadRequest, "invalid_token");

        // An invitation dies with its inviter's authority (demoted or revoked before acceptance).
        var pendingUser = await AccountAsync("pending");
        await Auth.JsonAsync(await InviteAsync(admin, s.Org, pendingUser.Email, "manager", branchId: s.BranchC), HttpStatusCode.Accepted);
        var orphan = Auth.LatestToken(_cloud, pendingUser.Email, "/auth/join")!;
        await Auth.JsonAsync(await ChangeAsync(owner, s.Org, admin.Id, "viewer", await VersionAsync(owner, s.Org, admin.Id)), HttpStatusCode.OK);
        await Auth.ErrorAsync(await pendingUser.Client.PostAsync("v1/invitations/accept", new { token = orphan }), HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'member_invitation_revoked' AND organization_id = @p0 AND details LIKE '%reason=inviter_authority'", s.Org));
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE organization_id = @p0 AND details LIKE '%@%'", s.Org));
    }

    [Fact]
    public async Task Permission_changes_apply_to_the_next_request_and_end_the_members_sessions()
    {
        var s = NewStructure("Groupe");
        var owner = await OwnerAsync(s.Org);
        var manager = await JoinAsync(owner, s.Org, "manager", businessId: s.Business1);
        await Auth.JsonAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.OK);

        // Demotion: every session of the member ends at once; after signing in again the new role applies.
        await Auth.JsonAsync(await ChangeAsync(owner, s.Org, manager.Id, "viewer", 1, businessId: s.Business1), HttpStatusCode.OK);
        await Auth.ErrorAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM user_sessions WHERE user_id = @p0 AND revoked_at IS NULL", manager.Id));
        Assert.True(_cloud.Count("SELECT COUNT(*) FROM user_sessions WHERE user_id = @p0 AND revoke_reason = 'permission_changed'", manager.Id) >= 1);
        await manager.SignInAgainAsync();
        await Auth.ErrorAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.Forbidden, "forbidden");

        // Grants are never cached: a change made in the database (e.g. by an operator) applies to the very next request.
        _cloud.Count("WITH u AS (UPDATE organization_memberships SET role = 'manager' WHERE user_id = @p0 RETURNING 1) SELECT COUNT(*) FROM u", manager.Id);
        await Auth.JsonAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.OK);

        // Revocation: sessions end, and the organization is gone for the user.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"v1/organizations/{s.Org}/members/{manager.Id}")).StatusCode);
        await Auth.ErrorAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/access"), HttpStatusCode.Unauthorized, "unauthorized");
        await manager.SignInAgainAsync();
        await Auth.ErrorAsync(await manager.Client.GetAsync($"v1/organizations/{s.Org}/access"), HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await Auth.JsonAsync(await manager.Client.GetAsync("v1/me"), HttpStatusCode.OK)).GetProperty("memberships").EnumerateArray());

        // The owner's own session was untouched; the audit trail names actor, target and the change, without addresses.
        await Auth.JsonAsync(await owner.Client.GetAsync($"v1/organizations/{s.Org}/members"), HttpStatusCode.OK);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'member_changed' AND organization_id = @p0 AND user_id = @p1 AND target_user_id = @p2 AND details LIKE 'from=manager/business:%;to=viewer/business:%'", s.Org, owner.Id, manager.Id));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'member_revoked' AND organization_id = @p0 AND target_user_id = @p1", s.Org, manager.Id));

        // A revoked member can be invited back (same row, new version).
        var token = await Reinvite(owner, s.Org, manager.Email);
        await Auth.JsonAsync(await manager.Client.PostAsync("v1/invitations/accept", new { token }), HttpStatusCode.OK);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM organization_memberships WHERE user_id = @p0 AND revoked_at IS NULL AND version = 4", manager.Id));
    }

    private async Task<string> Reinvite(Account owner, Guid org, string email)
    {
        var before = Auth.LatestToken(_cloud, email, "/auth/join");
        await Auth.JsonAsync(await InviteAsync(owner, org, email, "viewer"), HttpStatusCode.Accepted);
        string? token = null;
        await Wait.UntilAsync(() => (token = Auth.LatestToken(_cloud, email, "/auth/join")) != before, "re-invitation mail");
        return token!;
    }

    [Fact]
    public void Check_config_requires_the_runtime_role_to_reach_the_access_tables()
    {
        string[] checkConfig = ["check-config", .. _cloud.RunArgs().Skip(1)];
        Assert.Equal(0, _cloud.AdminRaw(checkConfig).ExitCode);
        PostgresCluster.Instance.Execute(_cloud.Database, "REVOKE ALL ON membership_invitations FROM pos_app;");
        try
        {
            var (exitCode, output) = _cloud.AdminRaw(checkConfig);
            Assert.Equal(2, exitCode);
            Assert.Contains("ne peut pas lire/écrire membership_invitations", output, StringComparison.Ordinal);
        }
        finally
        {
            PostgresCluster.Instance.Execute(_cloud.Database, "GRANT SELECT, INSERT, UPDATE, DELETE ON membership_invitations TO pos_app;");
        }
    }
}

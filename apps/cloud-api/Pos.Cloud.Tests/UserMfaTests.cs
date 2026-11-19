using System.Net;

namespace Pos.Cloud.Tests;

/// <summary>Phase 2.1B: TOTP enrolment, login challenge, recovery codes, MFA lockout and owner onboarding.</summary>
public sealed class UserMfaTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();

    public Task InitializeAsync() => _cloud.StartAsync();

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    [Fact]
    public async Task Totp_enrolment_challenge_replay_protection_and_recovery_codes()
    {
        var email = Auth.NewEmail("mfa");
        using var client = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        Assert.Equal("signed_in", await Auth.LoginAsync(client, email));

        // A wrong first code does not enable anything.
        await Auth.JsonAsync(await client.PostAsync("v1/me/mfa/totp"), HttpStatusCode.OK);
        await Auth.ErrorAsync(await client.PostAsync("v1/me/mfa/totp/confirm", new { code = "000000" }), HttpStatusCode.Unauthorized, "invalid_code");
        Assert.False((await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK)).GetProperty("mfaEnabled").GetBoolean());

        using var other = new UserClient(_cloud);
        Assert.Equal("signed_in", await Auth.LoginAsync(other, email));
        var before = client.SessionToken;
        var (secret, step, codes) = await Auth.EnableTotpAsync(client);
        Assert.Equal(10, codes.Count);
        Assert.All(codes, c => Assert.Matches(Auth.RecoveryCodeFormat(), c));
        Assert.Equal(10, codes.Distinct().Count());
        Assert.NotEqual(before, client.SessionToken); // re-keyed
        await Auth.ErrorAsync(await other.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized"); // others ended
        var me = await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK);
        Assert.True(me.GetProperty("mfaEnabled").GetBoolean());
        Assert.Equal(10, me.GetProperty("recoveryCodesRemaining").GetInt32());
        await Auth.ErrorAsync(await client.PostAsync("v1/me/mfa/totp"), HttpStatusCode.Conflict, "mfa_already_enabled");
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM user_recovery_codes WHERE code_hash::text LIKE '%' || @p0 || '%'", codes[0]));

        // Login now stops at the challenge: the pending session reaches nothing else.
        using var c2 = new UserClient(_cloud);
        Assert.Equal("mfa_required", await Auth.LoginAsync(c2, email));
        var pending = c2.SessionToken!;
        await Auth.ErrorAsync(await c2.GetAsync("v1/me"), HttpStatusCode.Forbidden, "mfa_required");
        await Auth.ErrorAsync(await c2.GetAsync("v1/me/sessions"), HttpStatusCode.Forbidden, "mfa_required");

        // The code already used for enrolment cannot be replayed.
        await Auth.ErrorAsync(await c2.PostAsync("v1/auth/login/mfa", new { code = Auth.Totp(secret, step) }), HttpStatusCode.Unauthorized, "invalid_code");
        var (next, nextStep) = await Auth.NextTotpAsync(secret, step);
        Assert.Equal("signed_in", (await Auth.JsonAsync(await c2.PostAsync("v1/auth/login/mfa", new { code = next }), HttpStatusCode.OK)).GetProperty("status").GetString());
        Assert.NotEqual(pending, c2.SessionToken);
        await Auth.JsonAsync(await c2.GetAsync("v1/me"), HttpStatusCode.OK);
        using (var replay = new UserClient(_cloud))
        {
            replay.SetSessionToken(pending);
            await Auth.ErrorAsync(await replay.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        }

        // A signed-in session cannot use the challenge endpoint again.
        await Auth.ErrorAsync(await c2.PostAsync("v1/auth/login/mfa", new { code = next }), HttpStatusCode.Forbidden, "forbidden");

        // Recovery code: works once, any formatting, and the count drops.
        using var c3 = new UserClient(_cloud);
        Assert.Equal("mfa_required", await Auth.LoginAsync(c3, email));
        var loose = codes[0].Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var recovered = await Auth.JsonAsync(await c3.PostAsync("v1/auth/login/mfa", new { recoveryCode = loose }), HttpStatusCode.OK);
        Assert.Equal(9, recovered.GetProperty("recoveryCodesRemaining").GetInt32());
        using var c4 = new UserClient(_cloud);
        Assert.Equal("mfa_required", await Auth.LoginAsync(c4, email));
        await Auth.ErrorAsync(await c4.PostAsync("v1/auth/login/mfa", new { recoveryCode = codes[0] }), HttpStatusCode.Unauthorized, "invalid_code");

        // Regeneration replaces every code (old unused ones die).
        (next, nextStep) = await Auth.NextTotpAsync(secret, nextStep);
        var fresh = await Auth.JsonAsync(await c3.PostAsync("v1/me/mfa/recovery-codes", new { code = next }), HttpStatusCode.OK);
        Assert.Equal(10, fresh.GetProperty("codes").GetArrayLength());
        await Auth.ErrorAsync(await c4.PostAsync("v1/auth/login/mfa", new { recoveryCode = codes[1] }), HttpStatusCode.Unauthorized, "invalid_code");
        Assert.Equal("signed_in", (await Auth.JsonAsync(await c4.PostAsync("v1/auth/login/mfa", new { recoveryCode = fresh.GetProperty("codes")[0].GetString() }), HttpStatusCode.OK)).GetProperty("status").GetString());

        // A non-privileged user may turn MFA off with password and code.
        (next, _) = await Auth.NextTotpAsync(secret, nextStep);
        await Auth.JsonAsync(await c4.PostAsync("v1/me/mfa/disable", new { password = Auth.Password, code = next }), HttpStatusCode.OK);
        using var c5 = new UserClient(_cloud);
        Assert.Equal("signed_in", await Auth.LoginAsync(c5, email));
    }

    [Fact]
    public async Task Failed_second_factors_lock_the_account_and_kill_the_pending_session()
    {
        var email = Auth.NewEmail("mfalock");
        using var client = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        await Auth.LoginAsync(client, email);
        var (secret, step, _) = await Auth.EnableTotpAsync(client);

        using var attacker = new UserClient(_cloud);
        Assert.Equal("mfa_required", await Auth.LoginAsync(attacker, email)); // knows the password
        for (var i = 0; i < 4; i++)
        {
            await Auth.ErrorAsync(await attacker.PostAsync("v1/auth/login/mfa", new { code = "123456" }), HttpStatusCode.Unauthorized, "invalid_code");
        }

        await Auth.ErrorAsync(await attacker.PostAsync("v1/auth/login/mfa", new { recoveryCode = "AAAA-BBBB-CCCC-DDDD" }), HttpStatusCode.Unauthorized, "invalid_code");

        // Locked: the pending session is gone, and even the right code or password gets nowhere.
        var (code, _) = await Auth.NextTotpAsync(secret, step);
        await Auth.ErrorAsync(await attacker.PostAsync("v1/auth/login/mfa", new { code }), HttpStatusCode.Unauthorized, "unauthorized");
        await Auth.ErrorAsync(await attacker.PostAsync("v1/auth/login", new { email, password = Auth.Password }), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.True(_cloud.Count("SELECT COUNT(*) FROM auth_audit_events e JOIN users u ON u.id = e.user_id WHERE u.normalized_email = upper(@p0) AND e.event = 'account_locked_out'", email) >= 1);
    }

    [Fact]
    public async Task Owner_onboarding_is_invitation_only_and_requires_mfa()
    {
        var organization = _cloud.CreateOrganization("Groupe");
        var email = Auth.NewEmail("owner");

        // Operator CLI: the token is mailed, never printed.
        var output = _cloud.Admin("owner-invite", "--org", organization.ToString(), "--email", email);
        Assert.Contains("invite=sent", output, StringComparison.Ordinal);
        var token = Auth.LatestToken(_cloud, email, "/auth/accept-invite")!;
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join('\n', _cloud.Log), StringComparison.Ordinal);
        Assert.NotEqual(0, _cloud.AdminRaw("owner-invite", "--org", Guid.NewGuid().ToString(), "--email", email).ExitCode); // unknown org

        using var owner = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, owner, email);
        await Auth.LoginAsync(owner, email);

        // Another verified user cannot use the owner's invitation.
        using (var intruder = new UserClient(_cloud))
        {
            var other = Auth.NewEmail("intruder");
            await Auth.RegisterVerifiedAsync(_cloud, intruder, other);
            await Auth.LoginAsync(intruder, other);
            await Auth.ErrorAsync(await intruder.PostAsync("v1/auth/onboarding/accept", new { token }), HttpStatusCode.BadRequest, "invalid_token");
        }

        // MFA first; the refusal does not consume the invitation.
        await Auth.ErrorAsync(await owner.PostAsync("v1/auth/onboarding/accept", new { token }), HttpStatusCode.Forbidden, "mfa_required");
        var (secret, step, _) = await Auth.EnableTotpAsync(owner);
        var accepted = await Auth.JsonAsync(await owner.PostAsync("v1/auth/onboarding/accept", new { token }), HttpStatusCode.OK);
        Assert.Equal(organization.ToString("D"), accepted.GetProperty("organizationId").GetString());
        Assert.Equal("owner", accepted.GetProperty("role").GetString());
        await Auth.ErrorAsync(await owner.PostAsync("v1/auth/onboarding/accept", new { token }), HttpStatusCode.BadRequest, "invalid_token"); // single use

        var me = await Auth.JsonAsync(await owner.GetAsync("v1/me"), HttpStatusCode.OK);
        Assert.True(me.GetProperty("mfaRequired").GetBoolean());
        var membership = Assert.Single(me.GetProperty("memberships").EnumerateArray());
        Assert.Equal(organization.ToString("D"), membership.GetProperty("organizationId").GetString());

        // Owners must keep MFA.
        var (code, _) = await Auth.NextTotpAsync(secret, step);
        await Auth.ErrorAsync(await owner.PostAsync("v1/me/mfa/disable", new { password = Auth.Password, code }), HttpStatusCode.Forbidden, "mfa_required_for_role");

        // A privileged user without MFA (e.g. MFA removed by an operator) is confined to enrolment.
        _cloud.Count("WITH d AS (DELETE FROM user_mfa WHERE user_id = (SELECT id FROM users WHERE normalized_email = upper(@p0)) RETURNING 1) SELECT COUNT(*) FROM d", email);
        await Auth.JsonAsync(await owner.GetAsync("v1/me"), HttpStatusCode.OK);
        await Auth.ErrorAsync(await owner.PostAsync("v1/me/password", new { currentPassword = Auth.Password, newPassword = Auth.OtherPassword }), HttpStatusCode.Forbidden, "mfa_required");
        await Auth.JsonAsync(await owner.PostAsync("v1/me/mfa/totp"), HttpStatusCode.OK);

        // Expired invitations are refused.
        var late = Auth.NewEmail("late");
        _cloud.Admin("owner-invite", "--org", organization.ToString(), "--email", late);
        var lateToken = Auth.LatestToken(_cloud, late, "/auth/accept-invite")!;
        _cloud.Count("WITH u AS (UPDATE auth_tokens SET expires_at = now() - interval '1 minute' WHERE normalized_email = upper(@p0) RETURNING 1) SELECT COUNT(*) FROM u", late);
        using var lateClient = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, lateClient, late);
        await Auth.LoginAsync(lateClient, late);
        await Auth.EnableTotpAsync(lateClient);
        await Auth.ErrorAsync(await lateClient.PostAsync("v1/auth/onboarding/accept", new { token = lateToken }), HttpStatusCode.BadRequest, "invalid_token");
    }
}

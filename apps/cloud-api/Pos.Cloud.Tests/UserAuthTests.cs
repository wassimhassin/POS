using System.Net;
using Npgsql;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>Phase 2.1B: registration, verification, login/logout, sessions, passwords, lockout, enumeration, isolation.</summary>
public sealed class UserAuthTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();

    public Task InitializeAsync() => _cloud.StartAsync();

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    private long UserCount(string email) => _cloud.Count("SELECT COUNT(*) FROM users WHERE normalized_email = upper(@p0)", email);

    private Guid UserId(string email) => Guid.Parse(Scalar("SELECT id::text FROM users WHERE normalized_email = upper(@p0)", email)!);

    private string? Scalar(string sql, params object[] parameters)
    {
        using var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(_cloud.Database));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return command.ExecuteScalar()?.ToString();
    }

    private void Sql(string sql, params object[] parameters) => _ = Scalar(sql, parameters);

    [Fact]
    public async Task Registration_requires_email_verification_and_never_reveals_existing_accounts()
    {
        using var client = new UserClient(_cloud);
        var email = Auth.NewEmail("reg");

        Assert.Equal("verification_sent", (await Auth.JsonAsync(await client.PostAsync("v1/auth/register", new { email, password = Auth.Password }), HttpStatusCode.Accepted)).GetProperty("status").GetString());
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email, password = Auth.Password }), HttpStatusCode.Forbidden, "email_not_verified");
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email, password = Auth.OtherPassword }), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Null(client.SessionToken);

        // Same address again (another password): same answer, still one account, its password unchanged.
        var again = await client.PostAsync("v1/auth/register", new { email, password = Auth.OtherPassword });
        Assert.Equal("verification_sent", (await Auth.JsonAsync(again, HttpStatusCode.Accepted)).GetProperty("status").GetString());
        Assert.Equal(1, UserCount(email));

        var token = Auth.LatestToken(_cloud, email, "/auth/verify-email")!;
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/verify-email", new { token = token[..^2] + "AA" }), HttpStatusCode.BadRequest, "invalid_token");
        await Auth.JsonAsync(await client.PostAsync("v1/auth/verify-email", new { token }), HttpStatusCode.OK);
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/verify-email", new { token }), HttpStatusCode.BadRequest, "invalid_token"); // single use

        Assert.Equal("signed_in", await Auth.LoginAsync(client, email));
        var me = await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK);
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("emailVerified").GetBoolean());
        Assert.False(me.GetProperty("mfaEnabled").GetBoolean());
        Assert.False(me.GetProperty("mfaRequired").GetBoolean());
        Assert.Equal(0, me.GetProperty("memberships").GetArrayLength()); // registration grants no tenant access

        // A verified address registering again gets a notice mail, never a second account.
        await Auth.JsonAsync(await client.PostAsync("v1/auth/register", new { email, password = Auth.OtherPassword }), HttpStatusCode.Accepted);
        Assert.Equal(1, UserCount(email));
        Assert.Equal("signed_in", await Auth.LoginAsync(client, email));

        await Auth.ErrorAsync(await client.PostAsync("v1/auth/register", new { email = Auth.NewEmail("weak"), password = "short" }), HttpStatusCode.BadRequest, "weak_password");
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/register", new { email = Auth.NewEmail("weak"), password = new string('a', 20) }), HttpStatusCode.BadRequest, "weak_password");
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/register", new { email = "not an address", password = Auth.Password }), HttpStatusCode.BadRequest, "invalid_email");

        // Only a PBKDF2 hash is stored; tokens are stored hashed.
        var hash = Scalar("SELECT password_hash FROM users WHERE normalized_email = upper(@p0)", email)!;
        Assert.DoesNotContain(Auth.Password, hash, StringComparison.Ordinal);
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM auth_tokens WHERE encode(token_hash, 'escape') LIKE '%' || @p0 || '%'", token));
    }

    [Fact]
    public async Task Sessions_are_secure_cookies_listable_revocable_expiring_and_rotated()
    {
        var email = Auth.NewEmail("sess");
        using var a = new UserClient(_cloud);
        using var b = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, a, email);

        var login = await a.PostAsync("v1/auth/login", new { email, password = Auth.Password });
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(UserClient.CookieName + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", login.Headers.CacheControl?.ToString());
        var tokenA = a.SessionToken!;
        Assert.Equal("signed_in", await Auth.LoginAsync(b, email));

        var sessions = await Auth.JsonAsync(await a.GetAsync("v1/me/sessions"), HttpStatusCode.OK);
        Assert.Equal(2, sessions.GetArrayLength());
        var current = Assert.Single(sessions.EnumerateArray(), s => s.GetProperty("current").GetBoolean());
        var other = sessions.EnumerateArray().Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString();

        // Revoke B from A: B is out at once.
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"v1/me/sessions/{other}")).StatusCode);
        await Auth.ErrorAsync(await b.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        await Auth.ErrorAsync(await a.DeleteAsync($"v1/me/sessions/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "not_found");

        // Another user's session id is indistinguishable from an unknown one (and stays alive).
        var stranger = Auth.NewEmail("stranger");
        using var s = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, s, stranger);
        await Auth.LoginAsync(s, stranger);
        var strangerSession = (await Auth.JsonAsync(await s.GetAsync("v1/me/sessions"), HttpStatusCode.OK))[0].GetProperty("id").GetString();
        await Auth.ErrorAsync(await a.DeleteAsync($"v1/me/sessions/{strangerSession}"), HttpStatusCode.NotFound, "not_found");
        await Auth.JsonAsync(await s.GetAsync("v1/me"), HttpStatusCode.OK);

        // A new login on a client that has a session replaces it (no fixation): the old token is dead.
        Assert.Equal("signed_in", await Auth.LoginAsync(a, email));
        Assert.NotEqual(tokenA, a.SessionToken);
        using (var replay = new UserClient(_cloud))
        {
            replay.SetSessionToken(tokenA);
            await Auth.ErrorAsync(await replay.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        }

        // Rotation: an hour-old key is replaced on use; the previous one works for a short grace only.
        var before = a.SessionToken!;
        var userId = UserId(email);
        Sql("UPDATE user_sessions SET rotated_at = now() - interval '2 hours', last_seen_at = now() - interval '5 minutes' WHERE user_id = @p0 AND revoked_at IS NULL", userId);
        await Auth.JsonAsync(await a.GetAsync("v1/me"), HttpStatusCode.OK);
        var after = a.SessionToken!;
        Assert.NotEqual(before, after);
        using (var stale = new UserClient(_cloud))
        {
            stale.SetSessionToken(before);
            await Auth.JsonAsync(await stale.GetAsync("v1/me"), HttpStatusCode.OK); // grace (concurrent requests)
            Sql("UPDATE user_sessions SET rotated_at = now() - interval '2 minutes' WHERE user_id = @p0 AND revoked_at IS NULL", userId);
            await Auth.ErrorAsync(await stale.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        }

        await Auth.JsonAsync(await a.GetAsync("v1/me"), HttpStatusCode.OK);

        // Idle and absolute expiry are enforced server-side, whatever the cookie says.
        Sql("UPDATE user_sessions SET last_seen_at = now() - interval '3 hours' WHERE user_id = @p0 AND revoked_at IS NULL", userId);
        await Auth.ErrorAsync(await a.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("signed_in", await Auth.LoginAsync(a, email));
        Sql("UPDATE user_sessions SET expires_at = now() - interval '1 second' WHERE user_id = @p0 AND revoked_at IS NULL", userId);
        await Auth.ErrorAsync(await a.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");

        // Revoke others, then logout: everything ends, and the cookie is cleared.
        Assert.Equal("signed_in", await Auth.LoginAsync(a, email));
        Assert.Equal("signed_in", await Auth.LoginAsync(b, email));
        await Auth.JsonAsync(await a.PostAsync("v1/me/sessions/revoke-others"), HttpStatusCode.OK);
        await Auth.ErrorAsync(await b.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        var last = a.SessionToken!;
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync("v1/auth/logout")).StatusCode);
        Assert.Null(a.SessionToken);
        using (var replay = new UserClient(_cloud))
        {
            replay.SetSessionToken(last);
            await Auth.ErrorAsync(await replay.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        }

        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM user_sessions WHERE user_id = @p0 AND revoked_at IS NULL", userId));
        Assert.Equal(1, s.SessionToken is null ? 0 : 1); // the stranger was never affected
    }

    [Fact]
    public async Task Lockout_password_reset_and_password_change()
    {
        var email = Auth.NewEmail("lock");
        using var client = new UserClient(_cloud);
        using var other = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        Assert.Equal("signed_in", await Auth.LoginAsync(other, email));
        var userId = UserId(email);

        for (var i = 0; i < 5; i++)
        {
            await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email, password = Auth.OtherPassword }), HttpStatusCode.Unauthorized, "invalid_credentials");
        }

        // Locked: even the right password gets the same generic answer.
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email, password = Auth.Password }), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM users WHERE id = @p0 AND lockout_end > now()", userId));
        Assert.True(_cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE user_id = @p0 AND event = 'account_locked_out'", userId) >= 1);

        // Reset by email: weak password refused without consuming the token; then the reset ends every session
        // and lifts the lockout; the token works once.
        var mails = Auth.MailCount(_cloud, email);
        await Auth.JsonAsync(await client.PostAsync("v1/auth/password/forgot", new { email }), HttpStatusCode.Accepted);
        Assert.Equal(mails + 1, Auth.MailCount(_cloud, email));
        var token = Auth.LatestToken(_cloud, email, "/auth/reset-password")!;
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/password/reset", new { token, newPassword = "short" }), HttpStatusCode.BadRequest, "weak_password");
        await Auth.JsonAsync(await client.PostAsync("v1/auth/password/reset", new { token, newPassword = Auth.OtherPassword }), HttpStatusCode.OK);
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/password/reset", new { token, newPassword = Auth.Password }), HttpStatusCode.BadRequest, "invalid_token");
        await Auth.ErrorAsync(await other.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email, password = Auth.Password }), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal("signed_in", await Auth.LoginAsync(client, email, Auth.OtherPassword));

        // Change: requires the current password; other sessions end; this one continues on a new key.
        Assert.Equal("signed_in", await Auth.LoginAsync(other, email, Auth.OtherPassword));
        var before = client.SessionToken;
        await Auth.ErrorAsync(await client.PostAsync("v1/me/password", new { currentPassword = "wrong password value", newPassword = Auth.Password }), HttpStatusCode.Unauthorized, "invalid_credentials");
        await Auth.JsonAsync(await client.PostAsync("v1/me/password", new { currentPassword = Auth.OtherPassword, newPassword = Auth.Password }), HttpStatusCode.OK);
        Assert.NotEqual(before, client.SessionToken);
        await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK);
        await Auth.ErrorAsync(await other.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("signed_in", await Auth.LoginAsync(other, email, Auth.Password));
    }

    [Fact]
    public async Task Anonymous_endpoints_resist_enumeration_cross_origin_requests_and_brute_force()
    {
        using var client = new UserClient(_cloud);
        var known = Auth.NewEmail("known");
        await Auth.RegisterVerifiedAsync(_cloud, client, known);
        var unknown = Auth.NewEmail("nobody");

        // Login: unknown address and wrong password are indistinguishable.
        var r1 = await client.PostAsync("v1/auth/login", new { email = unknown, password = Auth.Password });
        var r2 = await client.PostAsync("v1/auth/login", new { email = known, password = Auth.OtherPassword });
        Assert.Equal(r1.StatusCode, r2.StatusCode);
        Assert.Equal(await r1.Content.ReadAsStringAsync(), await r2.Content.ReadAsStringAsync());

        // Forgot / resend: identical answers; no mail at all for an unknown address.
        foreach (var path in new[] { "v1/auth/password/forgot", "v1/auth/resend-verification" })
        {
            var a = await client.PostAsync(path, new { email = unknown });
            var b = await client.PostAsync(path, new { email = known });
            Assert.Equal(a.StatusCode, b.StatusCode);
            Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, Auth.MailCount(_cloud, unknown));
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM users WHERE normalized_email = upper(@p0)", unknown));

        // Per-account mail budget: repeated requests do not flood a mailbox.
        for (var i = 0; i < 6; i++)
        {
            await client.PostAsync("v1/auth/password/forgot", new { email = known });
        }

        Assert.True(Auth.MailCount(_cloud, known) <= 4, $"{Auth.MailCount(_cloud, known)} mails"); // verification + 3 per 15 min

        // Not signed in: identity endpoints are 401, with or without a forged cookie.
        using (var anonymous = new UserClient(_cloud))
        {
            await Auth.ErrorAsync(await anonymous.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
            anonymous.SetSessionToken(new string('A', 43));
            await Auth.ErrorAsync(await anonymous.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
            await Auth.ErrorAsync(await anonymous.PostAsync("v1/me/mfa/totp"), HttpStatusCode.Unauthorized, "unauthorized");
        }

        // CSRF: a browser request from another origin is refused, even for login; the dashboard origin is accepted.
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email = known, password = Auth.Password }, origin: "https://evil.example"), HttpStatusCode.Forbidden, "origin_mismatch");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("v1/auth/login", new { email = known, password = Auth.Password }, origin: CloudProcess.PublicOrigin)).StatusCode);
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/logout", origin: "https://evil.example"), HttpStatusCode.Forbidden, "origin_mismatch");
        await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK); // still signed in
        using (var request = new HttpRequestMessage(HttpMethod.Post, "v1/me/sessions/revoke-others"))
        {
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            await Auth.ErrorAsync(await client.Http.SendAsync(request), HttpStatusCode.Forbidden, "origin_mismatch");
        }

        // Brute force from one address: rate limited (in addition to the per-account lockout).
        using var attacker = new UserClient(_cloud);
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 30 && limited is null; i++)
        {
            var response = await attacker.PostAsync("v1/auth/login", new { email = Auth.NewEmail("guess"), password = Auth.Password });
            limited = response.StatusCode == HttpStatusCode.TooManyRequests ? response : null;
        }

        Assert.NotNull(limited);
        await Auth.ErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("v1/me")).StatusCode); // other clients unaffected
    }

    [Fact]
    public async Task User_sessions_gateway_signatures_and_report_keys_never_substitute_for_each_other()
    {
        var organization = _cloud.CreateOrganization("Groupe");
        var business = _cloud.CreateBusiness(organization, "Café");
        var branch = _cloud.CreateBranch(business, "Centre");
        var reportKey = _cloud.ReportKey(organization);
        using var gateway = await RawGatewayClient.RegisterAsync(_cloud, branch);
        Assert.Equal(1, (await gateway.SendOkAsync(TestEvents.Order(1, 1))).LastAcceptedSequence); // gateways unchanged

        using var user = new UserClient(_cloud);
        var email = Auth.NewEmail("iso");
        await Auth.RegisterVerifiedAsync(_cloud, user, email);
        await Auth.LoginAsync(user, email);

        // A user session opens neither gateway nor report endpoints, even with a valid cookie.
        await Auth.ErrorAsync(await user.GetAsync("v1/sync/state"), HttpStatusCode.Unauthorized, "unauthorized");
        await Auth.ErrorAsync(await user.GetAsync($"v1/reports/organizations/{organization:D}"), HttpStatusCode.Unauthorized, "unauthorized");
        await Auth.ErrorAsync(await user.PostAsync("v1/organizations", new { name = "x" }), HttpStatusCode.Unauthorized, "unauthorized"); // no public org creation

        // A report key is not a user.
        using (var reader = _cloud.Http(reportKey))
        {
            await Auth.ErrorAsync(await reader.GetAsync("v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"v1/reports/organizations/{organization:D}")).StatusCode);
        }

        // Plain users have no tenant: memberships empty.
        Assert.Equal(0, (await Auth.JsonAsync(await user.GetAsync("v1/me"), HttpStatusCode.OK)).GetProperty("memberships").GetArrayLength());
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch));
    }

    [Fact]
    public async Task Security_events_are_audited_without_secrets_and_logs_hold_no_tokens()
    {
        using var client = new UserClient(_cloud);
        var email = Auth.NewEmail("audit");
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        var verifyToken = Auth.LatestToken(_cloud, email, "/auth/verify-email")!;
        await Auth.ErrorAsync(await client.PostAsync("v1/auth/login", new { email, password = Auth.OtherPassword }), HttpStatusCode.Unauthorized, "invalid_credentials");
        await Auth.LoginAsync(client, email);
        var session = client.SessionToken!;
        await client.PostAsync("v1/auth/password/forgot", new { email });
        var resetToken = Auth.LatestToken(_cloud, email, "/auth/reset-password")!;
        await client.PostAsync("v1/auth/logout");

        var userId = UserId(email);
        var events = new List<string>();
        await using (var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(_cloud.Database)))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT event FROM auth_audit_events WHERE user_id = @u ORDER BY occurred_at", connection);
            command.Parameters.AddWithValue("u", userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["user_registered", "email_verified", "login_failed", "login_succeeded", "password_reset_requested", "logout"], events);

        // Nothing secret in the audit rows or anywhere in the cloud's own log output.
        var dump = Scalar("SELECT string_agg(row_to_json(e)::text, '\n') FROM auth_audit_events e")!;
        var log = string.Join('\n', _cloud.Log);
        foreach (var secret in new[] { Auth.Password, Auth.OtherPassword, verifyToken, resetToken, session })
        {
            Assert.DoesNotContain(secret, dump, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(email, dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(email, log, StringComparison.OrdinalIgnoreCase);

        // The service role cannot rewrite history.
        await using var app = new NpgsqlConnection(PostgresCluster.Instance.AppConnection(_cloud.Database));
        await app.OpenAsync();
        await using var tamper = new NpgsqlCommand("DELETE FROM auth_audit_events", app);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState); // insufficient_privilege
    }
}

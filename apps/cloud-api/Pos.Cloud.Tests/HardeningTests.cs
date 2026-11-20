using System.Diagnostics;
using System.Net;
using Npgsql;
using Pos.Gateway.Tests;

namespace Pos.Cloud.Tests;

/// <summary>Phase 2.1C: security mails through a (fake, loopback) SMTP relay, with retries and without leaks.</summary>
public sealed class SmtpMailTests : IAsyncLifetime
{
    private const string SmtpPassword = "Zq7SmtpSecretMarker9";
    private readonly FakeSmtpServer _smtp = new();
    private readonly CloudProcess _cloud = new();

    public async Task InitializeAsync()
    {
        _cloud.WithEnvironment("POS_CLOUD_MAIL_DIR", "")
            .WithEnvironment("POS_CLOUD_SMTP_HOST", "127.0.0.1")
            .WithEnvironment("POS_CLOUD_SMTP_PORT", _smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("POS_CLOUD_SMTP_TLS", "none") // allowed only towards a loopback relay
            .WithEnvironment("POS_CLOUD_SMTP_USER", "relay-user")
            .WithEnvironment("POS_CLOUD_SMTP_PASSWORD", SmtpPassword)
            .WithEnvironment("POS_CLOUD_MAIL_FROM", "PosSaaS <securite@example.tn>");
        await _cloud.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _cloud.DisposeAsync();
        await _smtp.DisposeAsync();
    }

    private string? Token(string email, string path) => _smtp.TokenFor(email, path);

    /// <summary>Waits like <see cref="Wait.UntilAsync"/>; a timeout reports the relay counters and the cloud log tail (no secrets are logged).</summary>
    private async Task WaitAsync(Func<bool> condition, string what, int timeoutSeconds = 20)
    {
        try
        {
            await Wait.UntilAsync(condition, what, timeoutSeconds);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"{ex.Message} (smtp attempts={_smtp.Attempts}, delivered={_smtp.Messages.Count}); cloud log tail:\n{string.Join('\n', _cloud.Log.TakeLast(40))}", ex);
        }
    }

    [Fact]
    public async Task Verification_reset_and_invitation_mails_are_delivered_over_smtp_without_leaking()
    {
        Assert.Equal(0, _cloud.AdminRaw(["check-config", .. _cloud.RunArgs().Skip(1)]).ExitCode);
        using var client = new UserClient(_cloud);
        var email = Auth.NewEmail("smtp");
        await Auth.JsonAsync(await client.PostAsync("v1/auth/register", new { email, password = Auth.Password }), HttpStatusCode.Accepted);
        await WaitAsync(() => Token(email, "/auth/verify-email") is not null, "verification mail over SMTP");
        var verify = Token(email, "/auth/verify-email")!;
        await Auth.JsonAsync(await client.PostAsync("v1/auth/verify-email", new { token = verify }), HttpStatusCode.OK);

        var message = Assert.Single(_smtp.Messages);
        Assert.Contains("securite@example.tn", message.From, StringComparison.Ordinal);
        Assert.Equal([email], message.To);
        Assert.Equal("relay-user", message.User);
        Assert.Equal(SmtpPassword, message.Password); // AUTH used the configured credentials

        await Auth.JsonAsync(await client.PostAsync("v1/auth/password/forgot", new { email }), HttpStatusCode.Accepted);
        await WaitAsync(() => Token(email, "/auth/reset-password") is not null, "reset mail over SMTP");
        var reset = Token(email, "/auth/reset-password")!;
        await Auth.JsonAsync(await client.PostAsync("v1/auth/password/reset", new { token = reset, newPassword = Auth.OtherPassword }), HttpStatusCode.OK);

        // Operator CLI: delivered inline, through the same relay.
        var organization = _cloud.CreateOrganization("Groupe");
        var owner = Auth.NewEmail("owner");
        Assert.Contains("invite=sent", _cloud.Admin("owner-invite", "--org", organization.ToString(), "--email", owner), StringComparison.Ordinal);
        Assert.NotNull(Token(owner, "/auth/accept-invite"));

        var log = string.Join('\n', _cloud.Log);
        foreach (var secret in new[] { SmtpPassword, verify, reset, email, owner })
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("delivered via smtp (attempt 1)", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_relay_failures_are_retried_in_the_background_without_slowing_the_request()
    {
        using var client = new UserClient(_cloud);
        var warm = Auth.NewEmail("warm"); // first request after start (JIT, EF model) is not what is measured
        await Auth.JsonAsync(await client.PostAsync("v1/auth/register", new { email = warm, password = Auth.Password }), HttpStatusCode.Accepted);
        await WaitAsync(() => Token(warm, "/auth/verify-email") is not null, "warm-up mail");
        var before = _smtp.Attempts;

        // A slow relay (6 s per connection) that also fails twice: a request waiting on it would take > 18 s.
        _smtp.GreetingDelay = TimeSpan.FromSeconds(6);
        _smtp.FailNext("451 4.3.0 try again later", "421 4.3.2 service not available");
        var email = Auth.NewEmail("retry");
        var clock = Stopwatch.StartNew();
        await Auth.JsonAsync(await client.PostAsync("v1/auth/register", new { email, password = Auth.Password }), HttpStatusCode.Accepted);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the request waited for the relay ({clock.Elapsed})");

        await WaitAsync(() => Token(email, "/auth/verify-email") is not null, "mail delivered after retries", 40); // by design: 3 x 6 s greeting + 2 s + 4 s backoff = 24 s
        Assert.Equal(before + 3, _smtp.Attempts);
        var log = string.Join('\n', _cloud.Log);
        Assert.Contains("attempt 1 via smtp failed (SmtpException 451", log, StringComparison.Ordinal);
        Assert.Contains("attempt 2 via smtp failed (SmtpException 421", log, StringComparison.Ordinal);
        Assert.Contains("delivered via smtp (attempt 3)", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permanent_rejections_are_not_retried_and_the_cli_reports_them()
    {
        _smtp.FailNext("550 5.7.1 relaying denied");
        using var client = new UserClient(_cloud);
        var email = Auth.NewEmail("perm");
        await Auth.JsonAsync(await client.PostAsync("v1/auth/register", new { email, password = Auth.Password }), HttpStatusCode.Accepted);
        await WaitAsync(() => _cloud.Log.Any(l => l.Contains("permanent failure at attempt 1", StringComparison.Ordinal)), "permanent failure logged");
        await Task.Delay(TimeSpan.FromSeconds(3)); // longer than the first backoff: nothing else is attempted
        Assert.Equal(1, _smtp.Attempts);
        Assert.Empty(_smtp.Messages);

        // The user can simply ask again once the relay accepts.
        await Auth.JsonAsync(await client.PostAsync("v1/auth/resend-verification", new { email }), HttpStatusCode.Accepted);
        await WaitAsync(() => Token(email, "/auth/verify-email") is not null, "resent verification");

        _smtp.FailNext("550 5.7.1 relaying denied");
        var organization = _cloud.CreateOrganization("Groupe");
        var (exitCode, output) = _cloud.AdminRaw("owner-invite", "--org", organization.ToString(), "--email", Auth.NewEmail("owner"));
        Assert.NotEqual(0, exitCode);
        Assert.Contains("E-mail non remis", output, StringComparison.Ordinal);
        Assert.DoesNotContain(SmtpPassword, output, StringComparison.Ordinal);
    }
}

/// <summary>Phase 2.1C: TOTP seeds are encrypted at rest, bound to their user, rotatable and migratable.</summary>
public sealed class MfaSecretProtectionTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();

    public Task InitializeAsync() => _cloud.StartAsync();

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    private byte[] StoredSeed(Guid user) => Bytes("SELECT totp_secret FROM user_mfa WHERE user_id = @p0", user);

    private string? KeyId(Guid user) => Text("SELECT totp_key_id FROM user_mfa WHERE user_id = @p0", user);

    private Guid UserId(string email) => Guid.Parse(Text("SELECT id::text FROM users WHERE normalized_email = upper(@p0)", email)!);

    private object? Scalar(string sql, params object[] parameters)
    {
        using var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(_cloud.Database));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private string? Text(string sql, params object[] parameters) => Scalar(sql, parameters)?.ToString();

    private byte[] Bytes(string sql, params object[] parameters) => (byte[])Scalar(sql, parameters)!;

    private string[] CheckConfig() => ["check-config", .. _cloud.RunArgs().Skip(1)];

    private async Task RestartWithKeysAsync(string keys)
    {
        await _cloud.KillAsync();
        _cloud.WithEnvironment("POS_CLOUD_DATA_KEYS", keys);
        await _cloud.StartAsync();
    }

    private async Task<(string Email, byte[] Seed, long Step)> UserWithMfaAsync(string prefix)
    {
        var email = Auth.NewEmail(prefix);
        using var client = new UserClient(_cloud);
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        await Auth.LoginAsync(client, email);
        var (seed, step, _) = await Auth.EnableTotpAsync(client);
        return (email, seed, step);
    }

    private async Task<long> SignInWithTotpAsync(string email, byte[] seed, long lastStep, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var client = new UserClient(_cloud);
        Assert.Equal("mfa_required", await Auth.LoginAsync(client, email));
        var (code, step) = await Auth.NextTotpAsync(seed, lastStep);
        var response = await client.PostAsync("v1/auth/login/mfa", new { code });
        if (expected == HttpStatusCode.OK)
        {
            await Auth.JsonAsync(response, HttpStatusCode.OK);
        }
        else
        {
            await Auth.ErrorAsync(response, expected, "invalid_code");
        }

        return step;
    }

    [Fact]
    public async Task Seeds_are_encrypted_bound_to_their_user_and_survive_key_rotation()
    {
        var (aliceEmail, aliceSeed, aliceStep) = await UserWithMfaAsync("alice");
        var (bobEmail, bobSeed, bobStep) = await UserWithMfaAsync("bob");
        var alice = UserId(aliceEmail);
        var bob = UserId(bobEmail);

        // At rest: not the seed, not containing it, under the active key.
        var stored = StoredSeed(alice);
        Assert.NotEqual(aliceSeed, stored);
        Assert.False(stored.AsSpan().IndexOf(aliceSeed) >= 0);
        Assert.Equal("test1", KeyId(alice));

        // Rotation: a new active key first, the old one kept for decryption.
        var newKey = CloudProcess.NewDataKey("test2");
        await RestartWithKeysAsync($"{newKey},{_cloud.DataKeys}");
        var (_, output) = _cloud.AdminRaw(CheckConfig());
        Assert.Contains("mfa_rekey_needed=2", output, StringComparison.Ordinal);

        aliceStep = await SignInWithTotpAsync(aliceEmail, aliceSeed, aliceStep); // old key still decrypts
        Assert.Equal("test2", KeyId(alice)); // and the successful use re-sealed it (lazy migration)
        Assert.Equal("test1", KeyId(bob));

        var rekey = _cloud.Admin("mfa-rekey");
        Assert.Contains("resealed=1", rekey, StringComparison.Ordinal);
        Assert.Contains("remaining=0", rekey, StringComparison.Ordinal);
        Assert.Equal("test2", KeyId(bob));
        Assert.DoesNotContain("mfa_rekey_needed", _cloud.AdminRaw(CheckConfig()).Output, StringComparison.Ordinal);

        // The old key can now be removed.
        await RestartWithKeysAsync(newKey);
        bobStep = await SignInWithTotpAsync(bobEmail, bobSeed, bobStep);

        // A ciphertext copied onto another user's row does not decrypt (bound to the user id): refused, not a crash.
        _cloud.Count("WITH u AS (UPDATE user_mfa SET totp_secret = (SELECT totp_secret FROM user_mfa WHERE user_id = @p0) WHERE user_id = @p1 RETURNING 1) SELECT COUNT(*) FROM u", alice, bob);
        await SignInWithTotpAsync(bobEmail, aliceSeed, bobStep, HttpStatusCode.Unauthorized);
        Assert.Contains(_cloud.Log, l => l.Contains($"MFA secret of user {bob} cannot be decrypted", StringComparison.Ordinal));

        // Dropping a key that still protects seeds is a configuration error.
        await RestartWithKeysAsync(CloudProcess.NewDataKey("test3"));
        var (exitCode, invalid) = _cloud.AdminRaw(CheckConfig());
        Assert.Equal(2, exitCode);
        Assert.Contains("clé absente de la configuration", invalid, StringComparison.Ordinal);
        Assert.DoesNotContain(newKey.Split(':')[1], invalid, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_plaintext_seeds_keep_working_and_are_migrated()
    {
        var (email, seed, step) = await UserWithMfaAsync("legacy");
        var user = UserId(email);
        // State of a row written before 2.1C: plaintext seed, no key id.
        _cloud.Count("WITH u AS (UPDATE user_mfa SET totp_secret = @p1, totp_key_id = NULL WHERE user_id = @p0 RETURNING 1) SELECT COUNT(*) FROM u", user, seed);
        Assert.Contains("mfa_rekey_needed=1", _cloud.AdminRaw(CheckConfig()).Output, StringComparison.Ordinal);

        step = await SignInWithTotpAsync(email, seed, step); // still accepted, and re-sealed
        Assert.Equal("test1", KeyId(user));
        Assert.NotEqual(seed, StoredSeed(user));

        _cloud.Count("WITH u AS (UPDATE user_mfa SET totp_secret = @p1, totp_key_id = NULL WHERE user_id = @p0 RETURNING 1) SELECT COUNT(*) FROM u", user, seed);
        Assert.Contains("resealed=1", _cloud.Admin("mfa-rekey"), StringComparison.Ordinal);
        Assert.Equal("test1", KeyId(user));
        await SignInWithTotpAsync(email, seed, step);
    }
}

/// <summary>Phase 2.1C: bounded retention of sessions, tokens and abandoned MFA setups; audit kept and pruned only by the owner.</summary>
public sealed class AuthRetentionTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();

    public Task InitializeAsync() => _cloud.StartAsync();

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    private void Sql(string sql, params object[] parameters) => _cloud.Count($"WITH x AS ({sql} RETURNING 1) SELECT COUNT(*) FROM x", parameters);

    private void Session(Guid user, string label, string revokedAt, string expiresAt) => Sql($"""
        INSERT INTO user_sessions (id, user_id, token_hash, kind, security_stamp, created_at, rotated_at, last_seen_at, expires_at, revoked_at, revoke_reason, user_agent)
        VALUES (gen_random_uuid(), @p0, decode(md5(random()::text), 'hex'), 'full', 'x', now() - interval '40 days', now() - interval '40 days',
                now() - interval '40 days', {expiresAt}, {revokedAt}, NULL, @p1)
        """, user, label);

    private void Token(Guid user, string label, string expiresAt) => Sql($"""
        INSERT INTO auth_tokens (token_hash, purpose, user_id, created_at, expires_at, normalized_email)
        VALUES (decode(md5(random()::text), 'hex'), 'verify_email', @p0, now() - interval '40 days', {expiresAt}, @p1)
        """, user, label);

    [Fact]
    public async Task Cleanup_removes_only_ended_sessions_expired_tokens_and_abandoned_setups()
    {
        using var client = new UserClient(_cloud);
        var email = Auth.NewEmail("ret");
        await Auth.RegisterVerifiedAsync(_cloud, client, email);
        await Auth.LoginAsync(client, email); // a live session that must survive
        var user = Guid.Parse(Value("SELECT id::text FROM users WHERE normalized_email = upper(@p0)", email));

        Session(user, "old-revoked", "now() - interval '31 days'", "now() + interval '1 hour'");
        Session(user, "old-expired", "NULL", "now() - interval '31 days'");
        Session(user, "recent-revoked", "now() - interval '10 days'", "now() + interval '1 hour'");
        Session(user, "recent-expired", "NULL", "now() - interval '2 days'");
        Token(user, "old", "now() - interval '8 days'");
        Token(user, "recent", "now() - interval '1 day'");
        Token(user, "live", "now() + interval '1 hour'");
        Sql("INSERT INTO user_mfa (user_id, totp_secret, created_at, last_used_step) VALUES (@p0, '\\x00', now() - interval '2 days', 0)", user);

        var output = _cloud.Admin("cleanup");
        Assert.Contains("sessions=2", output, StringComparison.Ordinal);
        Assert.Contains("tokens=1", output, StringComparison.Ordinal); // the verification token used at registration is recent
        Assert.Contains("abandoned_mfa=1", output, StringComparison.Ordinal);
        Assert.Contains("complete=1", output, StringComparison.Ordinal);

        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM user_sessions WHERE user_agent IN ('old-revoked', 'old-expired')"));
        Assert.Equal(2, _cloud.Count("SELECT COUNT(*) FROM user_sessions WHERE user_agent IN ('recent-revoked', 'recent-expired')"));
        Assert.Equal(2, _cloud.Count("SELECT COUNT(*) FROM auth_tokens WHERE normalized_email IN ('recent', 'live')"));
        await Auth.JsonAsync(await client.GetAsync("v1/me"), HttpStatusCode.OK); // live session untouched

        // An enabled MFA is never "abandoned", however old.
        using var mfaClient = new UserClient(_cloud);
        var mfaEmail = Auth.NewEmail("mfaold");
        await Auth.RegisterVerifiedAsync(_cloud, mfaClient, mfaEmail);
        await Auth.LoginAsync(mfaClient, mfaEmail);
        await Auth.EnableTotpAsync(mfaClient);
        Sql("UPDATE user_mfa SET created_at = now() - interval '400 days' WHERE enabled_at IS NOT NULL");
        Assert.Contains("abandoned_mfa=0", _cloud.Admin("cleanup"), StringComparison.Ordinal);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM user_mfa WHERE enabled_at IS NOT NULL"));
    }

    [Fact]
    public void Audit_history_is_pruned_only_by_the_owner_role_and_never_below_a_year()
    {
        Sql("INSERT INTO auth_audit_events (id, occurred_at, event) VALUES (gen_random_uuid(), now() - interval '500 days', 'old')");
        Sql("INSERT INTO auth_audit_events (id, occurred_at, event) VALUES (gen_random_uuid(), now() - interval '370 days', 'kept')");

        _cloud.Admin("cleanup"); // the service's own cleanup never touches the audit log
        Assert.Equal(2, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event IN ('old', 'kept')"));
        Assert.Contains("audit_deleted=1", _cloud.Admin("audit-prune"), StringComparison.Ordinal);
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'old'"));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'kept'")); // default 400 days

        var (exitCode, output) = _cloud.AdminRaw("audit-prune", "--older-than-days", "30");
        Assert.Equal(2, exitCode);
        Assert.Contains("au moins 365 jours", output, StringComparison.Ordinal);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'kept'"));

        // With the runtime role (as the service runs), the delete is refused by the database.
        var (denied, _) = _cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_MIGRATION_DB"] = PostgresCluster.Instance.AppConnection(_cloud.Database) },
            "audit-prune", "--older-than-days", "365");
        Assert.NotEqual(0, denied);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'kept'"));
    }

    private string Value(string sql, params object[] parameters)
    {
        using var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(_cloud.Database));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return command.ExecuteScalar()!.ToString()!;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>
/// Phase 2.1A: one error envelope for every failure, /v1 routing, and the OpenAPI document of the public contract.
/// </summary>
public sealed class ApiFoundationTests : IAsyncLifetime
{
    private static readonly string[] s_forbiddenInDocument =
        // Auth request fields are named "password"/"code"; stored secrets and their hashes must never appear.
        ["passwordhash", "password_hash", "tokenhash", "token_hash", "totpsecret", "totp_secret", "securitystamp", "security_stamp", "codehash", "secret_sha256", "secretsha256", "pos_app", "fault", "privatekey", "private_key", "connection", "127.0.0.1", "localhost"];

    private readonly CloudProcess _cloud = new();
    private Guid _organization;
    private string _reportKey = "";

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _organization = _cloud.CreateOrganization("Groupe");
        _reportKey = _cloud.ReportKey(_organization);
    }

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    /// <summary>The repository's committed OpenAPI snapshot (regenerate: <c>Pos.Cloud openapi --out …</c>).</summary>
    public static string SnapshotPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PosSaaS.slnx")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("PosSaaS.slnx"), "docs", "api", "cloud-v1.openapi.json");
        }
    }

    /// <summary>
    /// Exactly { code, message }: gateways in the field deserialize CloudError with strict JSON (unknown members are
    /// rejected), and detect revocation from the code. An extra member would break them, so the shape is frozen.
    /// </summary>
    private static async Task<CloudError> AssertEnvelopeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["code", "message"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var error = JsonSerializer.Deserialize<CloudError>(body, SyncProtocol.Json)!;
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }

    [Fact]
    public async Task Framework_failures_use_the_error_envelope()
    {
        using var anonymous = _cloud.Http();
        using var reader = _cloud.Http(_reportKey);

        // Deny by default: an anonymous caller learns nothing about which routes exist.
        await AssertEnvelopeAsync(await anonymous.GetAsync("v1/does-not-exist"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertEnvelopeAsync(await anonymous.GetAsync("v2/sync/state"), HttpStatusCode.Unauthorized, "unauthorized");

        // Authenticated: unknown route, unknown version, wrong method.
        await AssertEnvelopeAsync(await reader.GetAsync("v1/reports/nothing-here"), HttpStatusCode.NotFound, "not_found");
        await AssertEnvelopeAsync(await reader.GetAsync($"v2/reports/organizations/{_organization:D}"), HttpStatusCode.NotFound, "not_found");
        await AssertEnvelopeAsync(await reader.DeleteAsync($"v1/reports/organizations/{_organization:D}"), HttpStatusCode.MethodNotAllowed, "method_not_allowed");

        // Existing behaviour is unchanged: an existing route still answers normally under /v1.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"v1/reports/organizations/{_organization:D}")).StatusCode);
        await AssertEnvelopeAsync(await reader.GetAsync($"v1/reports/organizations/{Guid.NewGuid():D}"), HttpStatusCode.NotFound, "not_found");

        // Body problems: wrong media type, malformed JSON, unknown member (strict wire format).
        // A wrong media type fails endpoint matching: the framework's 415 endpoint is not anonymous, so deny-by-default
        // answers 401 to an anonymous caller and 415 to an authenticated one.
        using var text = new StringContent("{}", Encoding.UTF8, "text/plain");
        await AssertEnvelopeAsync(await anonymous.PostAsync("v1/gateways/register", text), HttpStatusCode.Unauthorized, "unauthorized");
        using var text2 = new StringContent("{}", Encoding.UTF8, "text/plain");
        await AssertEnvelopeAsync(await reader.PostAsync("v1/gateways/register", text2), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        using var malformed = new StringContent("{\"code\":", Encoding.UTF8, "application/json");
        await AssertEnvelopeAsync(await anonymous.PostAsync("v1/gateways/register", malformed), HttpStatusCode.BadRequest, "invalid_request");
        using var unknown = new StringContent("{\"code\":\"a\",\"branchId\":\"b\",\"publicKey\":\"c\",\"signature\":\"d\",\"extra\":1}", Encoding.UTF8, "application/json");
        await AssertEnvelopeAsync(await anonymous.PostAsync("v1/gateways/register", unknown), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Rate_limited_requests_use_the_error_envelope_and_say_when_to_retry()
    {
        // Its own process: the registration limit is per IP, and other tests in this class also call registration.
        await using var cloud = new CloudProcess();
        await cloud.StartAsync();
        using var http = cloud.Http();
        var request = new RegisterGatewayRequest("not-a-code", Guid.NewGuid().ToString("D"), "AAAA", "AAAA");
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 20 && limited is null; i++)
        {
            var response = await http.PostAsJsonAsync("v1/gateways/register", request, SyncProtocol.Json);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
            else
            {
                Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
            }
        }

        Assert.NotNull(limited);
        await AssertEnvelopeAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task OpenApi_is_not_served_unless_enabled()
    {
        using var anonymous = _cloud.Http();
        await AssertEnvelopeAsync(await anonymous.GetAsync("openapi/v1.json"), HttpStatusCode.Unauthorized, "unauthorized");
        using var reader = _cloud.Http(_reportKey);
        await AssertEnvelopeAsync(await reader.GetAsync("openapi/v1.json"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Committed_OpenAPI_document_matches_the_code_and_exposes_no_internals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-openapi-{Guid.NewGuid():N}.json");
        try
        {
            _cloud.Admin("openapi", "--out", path);
            var generated = await File.ReadAllTextAsync(path);
            if (Environment.GetEnvironmentVariable("POS_UPDATE_OPENAPI") == "1")
            {
                await File.WriteAllTextAsync(SnapshotPath, generated);
            }

            var committed = (await File.ReadAllTextAsync(SnapshotPath)).ReplaceLineEndings("\n");
            Assert.True(committed == generated,
                "docs/api/cloud-v1.openapi.json is out of date: regenerate it with `Pos.Cloud openapi --out docs/api/cloud-v1.openapi.json` (or POS_UPDATE_OPENAPI=1) and review the diff.");

            var lower = generated.ToLowerInvariant();
            Assert.All(s_forbiddenInDocument, term => Assert.DoesNotContain(term, lower, StringComparison.Ordinal));

            var document = JsonNode.Parse(generated)!;
            Assert.Null(document["servers"]);
            var operations = document["paths"]!.AsObject()
                .SelectMany(p => p.Value!.AsObject().Select(o => (Path: p.Key, Method: o.Key, Operation: o.Value!)))
                .ToList();
            Assert.Equal(36, operations.Count);
            Assert.All(operations, o => Assert.StartsWith("/v1/", o.Path, StringComparison.Ordinal));
            Assert.All(operations, o => Assert.NotNull(o.Operation["responses"]!["401"]));
            foreach (var (opPath, method, operation) in operations)
            {
                var schemes = operation["security"]?.AsArray().SelectMany(r => r!.AsObject().Select(s => s.Key)).ToList() ?? [];
                string[] anonymous = ["/v1/gateways/register", "/v1/auth/register", "/v1/auth/verify-email", "/v1/auth/resend-verification", "/v1/auth/login", "/v1/auth/password/forgot", "/v1/auth/password/reset"];
                string[] expected = anonymous.Contains(opPath) ? []
                    : opPath.StartsWith("/v1/reports/", StringComparison.Ordinal) ? ["ReportKey"]
                    : opPath.StartsWith("/v1/auth/", StringComparison.Ordinal) || opPath.StartsWith("/v1/me", StringComparison.Ordinal)
                        || opPath.StartsWith("/v1/organizations/", StringComparison.Ordinal) || opPath.StartsWith("/v1/invitations/", StringComparison.Ordinal) ? ["Session"]
                    : ["PosGateway"];
                Assert.True(expected.SequenceEqual(schemes), $"{method} {opPath}: [{string.Join(',', schemes)}]");
            }

            var error = document["components"]!["schemas"]!["CloudError"]!;
            Assert.Equal(["code", "message"], error["properties"]!.AsObject().Select(p => p.Key));
            Assert.False(error["additionalProperties"]!.GetValue<bool>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenApi_can_be_served_on_request_and_is_the_committed_contract()
    {
        await using var cloud = new CloudProcess();
        cloud.RunArguments.Add("--openapi");
        await cloud.StartAsync();
        using var anonymous = cloud.Http();
        var response = await anonymous.GetAsync("openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var served = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(SnapshotPath));
        Assert.True(JsonNode.DeepEquals(committed, served), "The served document differs from docs/api/cloud-v1.openapi.json.");
    }
}

/// <summary>Phase 2.1A: settings are validated before anything listens, and secrets never appear in messages.</summary>
public sealed class ConfigValidationTests : IAsyncLifetime
{
    private const string SecretMarker = "Zq7SecretMarker9";
    private readonly CloudProcess _cloud = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    private string[] CheckConfig() => ["check-config", .. _cloud.RunArgs().Skip(1)];

    [Fact]
    public void A_correct_configuration_passes()
    {
        var (exitCode, output) = _cloud.AdminRaw(CheckConfig());
        Assert.True(exitCode == 0, output);
        Assert.Contains("config=ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_missing_setting_is_reported_at_once_by_name()
    {
        var (exitCode, output) = _cloud.AdminRaw(
            new Dictionary<string, string?> { ["POS_CLOUD_DB"] = null, ["POS_CLOUD_CERT_PASSWORD"] = null },
            "check-config", "--certificate", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx"));
        Assert.Equal(2, exitCode);
        Assert.Contains("--listen", output, StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_DB", output, StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_CERT_PASSWORD", output, StringComparison.Ordinal);
        Assert.Contains("--certificate : fichier introuvable", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_are_never_echoed()
    {
        var unreadable = $"Host=127.0.0.1;Password={SecretMarker};Not A Real Keyword={SecretMarker}";
        var (exitCode, output) = _cloud.AdminRaw(
            new Dictionary<string, string?> { ["POS_CLOUD_DB"] = unreadable, ["POS_CLOUD_CERT_PASSWORD"] = SecretMarker + "-wrong" },
            CheckConfig());
        Assert.Equal(2, exitCode);
        Assert.Contains("POS_CLOUD_DB : chaîne de connexion illisible", output, StringComparison.Ordinal);
        Assert.Contains("--certificate : impossible d'ouvrir", output, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, output, StringComparison.Ordinal);

        // A readable string that points nowhere: the connection error is reported without host, user or password.
        var unreachable = $"Host=127.0.0.1;Port={PostgresCluster.FreePort()};Username=pos_app;Password={SecretMarker};Database=none;Timeout=3";
        (exitCode, output) = _cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_DB"] = unreachable }, CheckConfig());
        Assert.Equal(2, exitCode);
        Assert.Contains("POS_CLOUD_DB : connexion impossible", output, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_must_not_use_the_schema_owner_or_hold_DDL_rights()
    {
        var owner = PostgresCluster.Instance.AdminConnection(_cloud.Database);
        var (exitCode, output) = _cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_DB"] = owner }, CheckConfig());
        Assert.Equal(2, exitCode);
        Assert.Contains("rôle de données distinct", output, StringComparison.Ordinal);

        // Same check against the database: the app role must not be able to create objects (this test's own database).
        PostgresCluster.Instance.Execute(_cloud.Database, "GRANT CREATE ON SCHEMA public TO pos_app;");
        try
        {
            (exitCode, output) = _cloud.AdminRaw(CheckConfig());
            Assert.Equal(2, exitCode);
            Assert.Contains("droits sur les données", output, StringComparison.Ordinal);
        }
        finally
        {
            PostgresCluster.Instance.Execute(_cloud.Database, "REVOKE CREATE ON SCHEMA public FROM pos_app;");
        }

        Assert.Equal(0, _cloud.AdminRaw(CheckConfig()).ExitCode);
    }

    [Fact]
    public void User_authentication_settings_are_required_and_validated()
    {
        var (exitCode, output) = _cloud.AdminRaw(
            new Dictionary<string, string?> { ["POS_CLOUD_PUBLIC_ORIGIN"] = null, ["POS_CLOUD_MAIL_DIR"] = null }, CheckConfig());
        Assert.Equal(2, exitCode);
        Assert.Contains("POS_CLOUD_PUBLIC_ORIGIN : variable d'environnement requise", output, StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_MAIL_DIR ou POS_CLOUD_SMTP_HOST : un transport d'e-mails de sécurité est requis", output, StringComparison.Ordinal);

        foreach (var origin in new[] { "http://dashboard.test", "https://dashboard.test/app", "https://dashboard.test/", "dashboard.test" })
        {
            (exitCode, output) = _cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_PUBLIC_ORIGIN"] = origin }, CheckConfig());
            Assert.True(exitCode == 2, origin);
            Assert.Contains("POS_CLOUD_PUBLIC_ORIGIN : doit être une origine", output, StringComparison.Ordinal);
        }

        (exitCode, output) = _cloud.AdminRaw(
            new Dictionary<string, string?> { ["POS_CLOUD_MAIL_DIR"] = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}") }, CheckConfig());
        Assert.Equal(2, exitCode);
        Assert.Contains("POS_CLOUD_MAIL_DIR : dossier absolu existant requis", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Mail_transport_and_data_keys_are_validated_without_echoing_secrets()
    {
        (int, string) Check(Dictionary<string, string?> env) => _cloud.AdminRaw(env, CheckConfig());
        var smtp = new Dictionary<string, string?>
        {
            ["POS_CLOUD_MAIL_DIR"] = null,
            ["POS_CLOUD_SMTP_HOST"] = "127.0.0.1",
            ["POS_CLOUD_SMTP_TLS"] = "none",
            ["POS_CLOUD_MAIL_FROM"] = "securite@example.tn",
        };
        Assert.Equal(0, Check(smtp).Item1); // a valid SMTP configuration (no connection is attempted)

        string Errors(Dictionary<string, string?> changes)
        {
            var env = new Dictionary<string, string?>(smtp);
            foreach (var (k, v) in changes)
            {
                env[k] = v;
            }

            var (exitCode, output) = Check(env);
            Assert.True(exitCode == 2, output);
            Assert.DoesNotContain(SecretMarker, output, StringComparison.Ordinal);
            return output;
        }

        Assert.Contains("choisissez un seul transport", Errors(new() { ["POS_CLOUD_MAIL_DIR"] = _cloud.MailDirectory }), StringComparison.Ordinal);
        Assert.Contains("un transport d'e-mails de sécurité est requis", Errors(new() { ["POS_CLOUD_SMTP_HOST"] = null }), StringComparison.Ordinal);
        Assert.Contains("« none » n'est autorisé que vers un relais local", Errors(new() { ["POS_CLOUD_SMTP_HOST"] = "smtp.example.tn" }), StringComparison.Ordinal);
        Assert.Contains("465 (TLS implicite)", Errors(new() { ["POS_CLOUD_SMTP_PORT"] = "465" }), StringComparison.Ordinal);
        Assert.Contains("port entre 1 et 65535", Errors(new() { ["POS_CLOUD_SMTP_PORT"] = "70000" }), StringComparison.Ordinal);
        Assert.Contains("à définir ensemble", Errors(new() { ["POS_CLOUD_SMTP_PASSWORD"] = SecretMarker }), StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_MAIL_FROM : expéditeur requis", Errors(new() { ["POS_CLOUD_MAIL_FROM"] = null }), StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_SMTP_TLS : « starttls »", Errors(new() { ["POS_CLOUD_SMTP_TLS"] = SecretMarker }), StringComparison.Ordinal);

        Assert.Contains("POS_CLOUD_DATA_KEYS : variable d'environnement requise", Errors(new() { ["POS_CLOUD_DATA_KEYS"] = null }), StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_DATA_KEYS : entrée 1 invalide", Errors(new() { ["POS_CLOUD_DATA_KEYS"] = $"k1:{SecretMarker}" }), StringComparison.Ordinal);
        var key = CloudProcess.NewDataKey("dup");
        Assert.Contains("identifiant de clé en double", Errors(new() { ["POS_CLOUD_DATA_KEYS"] = $"{key},{key}" }), StringComparison.Ordinal);
        Assert.DoesNotContain(key.Split(':')[1], Errors(new() { ["POS_CLOUD_DATA_KEYS"] = $"{key},{key}" }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_security_log_must_be_append_only_for_the_runtime_role()
    {
        PostgresCluster.Instance.Execute(_cloud.Database, "GRANT UPDATE, DELETE ON auth_audit_events TO pos_app;");
        try
        {
            var (exitCode, output) = _cloud.AdminRaw(CheckConfig());
            Assert.Equal(2, exitCode);
            Assert.Contains("journal de sécurité", output, StringComparison.Ordinal);
        }
        finally
        {
            PostgresCluster.Instance.Execute(_cloud.Database, "REVOKE UPDATE, DELETE ON auth_audit_events FROM pos_app;");
        }

        Assert.Equal(0, _cloud.AdminRaw(CheckConfig()).ExitCode);
    }

    [Fact]
    public void Run_refuses_an_invalid_configuration_before_listening()
    {
        var (exitCode, output) = _cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_CERT_PASSWORD"] = "short" }, _cloud.RunArgs());
        Assert.Equal(2, exitCode);
        Assert.Contains("Configuration invalide", output, StringComparison.Ordinal);
        Assert.Contains("POS_CLOUD_CERT_PASSWORD", output, StringComparison.Ordinal);
    }
}

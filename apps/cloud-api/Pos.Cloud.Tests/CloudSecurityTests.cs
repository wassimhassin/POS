using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Pos.Gateway.Tests;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>Gateway credentials, provisioning codes, report keys, transport and database privileges.</summary>
public sealed class CloudSecurityTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private Guid _organization;
    private Guid _business;
    private Guid _branch;
    private RawGatewayClient _gateway = null!;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _organization = _cloud.CreateOrganization("Groupe");
        _business = _cloud.CreateBusiness(_organization, "Café");
        _branch = _cloud.CreateBranch(_business, "Centre");
        _gateway = await RawGatewayClient.RegisterAsync(_cloud, _branch);
    }

    public async Task DisposeAsync()
    {
        _gateway.Dispose();
        await _cloud.DisposeAsync();
    }

    private long Events => _cloud.Count("SELECT COUNT(*) FROM sync_events");

    [Fact]
    public async Task Incorrect_gateway_credentials_are_refused_and_nothing_is_stored()
    {
        var json = JsonSerializer.Serialize(new SyncBatch([TestEvents.Order(1, 1)]), SyncProtocol.Json);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _gateway.SendRawAsync(json, signer: otherKey)).Status);                          // wrong key
        Assert.Equal(HttpStatusCode.Unauthorized, (await _gateway.SendRawAsync(json, gatewayId: Guid.NewGuid().ToString("D"))).Status);  // unknown gateway
        Assert.Equal(HttpStatusCode.Unauthorized, (await _gateway.SendRawAsync(json, signedBody: json.Replace("\"number\":1", "\"number\":2", StringComparison.Ordinal))).Status); // body altered after signing
        Assert.Equal(HttpStatusCode.Unauthorized, (await _gateway.SendRawAsync(json, clockSkew: TimeSpan.FromMinutes(-10))).Status);     // stale (replay later)
        Assert.Equal(HttpStatusCode.Unauthorized, (await _gateway.SendRawAsync(json, clockSkew: TimeSpan.FromMinutes(10))).Status);      // from the future

        using var http = _cloud.Http();
        foreach (var header in new[] { null, "PosGateway garbage", $"PosGateway {_gateway.GatewayId}:0:AAAA", "Bearer nope", "Basic YTpi" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/sync/events") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            if (header is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", header);
            }

            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // A valid REPORT key is not a gateway credential.
        using var reportHttp = _cloud.Http(_cloud.ReportKey(_organization));
        using var asReport = await reportHttp.PostAsync("v1/sync/events", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, asReport.StatusCode);

        Assert.Equal(0, Events);
        Assert.Equal(HttpStatusCode.OK, (await _gateway.SendAsync(TestEvents.Order(1, 1))).Status); // the genuine credential works
    }

    [Fact]
    public async Task A_revoked_gateway_is_refused_and_keeps_its_unsent_events()
    {
        var organization = _cloud.CreateOrganization("Groupe 2");
        var business = _cloud.CreateBusiness(organization, "Café 2");
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, organization, business, "Nord");
        var a = await branch.TerminalAsync("Caisse A");
        await BranchUnderTest.PlaceAsync(a);
        await branch.WaitSyncedAsync(_cloud);
        var synced = branch.AckedSequence;

        var gatewayId = _cloud.Admin("gateways", "--branch", branch.BranchId.ToString("D")).Split(' ')[0];
        _cloud.Admin("gateway-revoke", "--gateway", gatewayId);
        await BranchUnderTest.PlaceAsync(a);

        await Wait.UntilAsync(() => branch.LastSyncError?.Contains("refuse les identifiants", StringComparison.Ordinal) == true, "gateway reports the refusal");
        Assert.Equal(synced, branch.AckedSequence);
        Assert.True(branch.OutboxCount > synced); // kept for an operator decision, never dropped
        Assert.Equal(synced, _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch.BranchId));
    }

    [Fact]
    public async Task Provisioning_codes_are_single_use_bound_to_one_branch_and_require_key_possession()
    {
        var code = _cloud.GatewayCode(_branch);
        using var candidate = RawGatewayClient.Unregistered(_cloud);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        using (var wrongBranch = await candidate.RegisterRawAsync(code, Guid.NewGuid().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongBranch.StatusCode); // the gateway's own branch id must match the code's branch
        }

        using (var noPossession = await candidate.RegisterRawAsync(code, _branch.ToString("D"), signer: otherKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noPossession.StatusCode);
        }

        using (var unknown = await candidate.RegisterRawAsync("AAAAA-BBBBB-CCCCC-DDDDD", _branch.ToString("D")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        }

        using (var ok = await candidate.RegisterRawAsync(code, _branch.ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var second = RawGatewayClient.Unregistered(_cloud);
        using (var reused = await second.RegisterRawAsync(code, _branch.ToString("D")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        }

        Assert.Equal(2, _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE branch_id = @p0", _branch)); // setup + this one
        Assert.Equal(0, _cloud.Count("SELECT COUNT(*) FROM provisioning_codes WHERE code_hash = @p0 AND used_at IS NULL", SyncProtocol.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(SyncProtocol.NormalizeCode(code)))));
    }

    [Fact]
    public async Task Reports_require_a_valid_key_and_plain_http_is_not_served()
    {
        using var anonymous = _cloud.Http();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"v1/reports/branches/{_branch:D}/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"v1/reports/organizations/{_organization:D}")).StatusCode);
        // Unknown routes fall under the fallback policy, which challenges with BOTH schemes: the response must still
        // be a single well-formed 401 (regression: before the guard, the second challenge broke the response).
        for (var i = 0; i < 50; i++)
        {
            using var response = await anonymous.GetAsync("v1/anything");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("unauthorized", JsonSerializer.Deserialize<CloudError>(await response.Content.ReadAsStringAsync(), SyncProtocol.Json)?.Code);
        }

        var genuine = _cloud.ReportKey(_organization);
        var oneDigitOff = genuine[..^1] + (genuine[^1] == '0' ? '1' : '0'); // always differs from the genuine key
        foreach (var forged in new[] { "posrk.x.y", $"posrk.{Guid.NewGuid():N}.{new string('A', 64)}", oneDigitOff })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/reports/branches/{_branch:D}/orders");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
        }

        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => plain.GetAsync(new Uri($"http://127.0.0.1:{_cloud.Port}/v1/reports/branches/{_branch:D}/orders")));
    }

    [Fact]
    public async Task The_runtime_database_role_cannot_change_the_schema()
    {
        await using var connection = new NpgsqlConnection(PostgresCluster.Instance.AppConnection(_cloud.Database));
        await connection.OpenAsync();
        foreach (var sql in new[] { "CREATE TABLE intruder (id int);", "DROP TABLE orders;", "ALTER TABLE orders DROP CONSTRAINT ck_orders_status;", "DELETE FROM \"__EFMigrationsHistory\";" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(error.SqlState is "42501" or "42P01", $"{sql}: {error.SqlState}"); // insufficient privilege
        }
    }
}

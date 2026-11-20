using System.Net;
using System.Net.Http.Json;
using Pos.Core.Gateway;
using Pos.Gateway.Tests;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>
/// Phase 2.1C: a provisioning code is consumed by exactly one gateway key, and that key can repeat its
/// registration when the reply was lost (timeout, dropped connection, concurrent retries). Any other key is refused.
/// </summary>
public sealed class GatewayProvisioningTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private Guid _organization;
    private Guid _business;
    private Guid _branch;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _organization = _cloud.CreateOrganization("Groupe");
        _business = _cloud.CreateBusiness(_organization, "Café");
        _branch = _cloud.CreateBranch(_business, "Centre");
    }

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    private long Credentials() => _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE branch_id = @p0", _branch);

    private static async Task<RegisterGatewayResponse> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<RegisterGatewayResponse>(body, SyncProtocol.Json)!;
    }

    [Fact]
    public async Task The_same_key_can_repeat_a_registration_whose_reply_was_lost_and_gets_the_same_identity()
    {
        var code = _cloud.GatewayCode(_branch);
        using var gateway = RawGatewayClient.Unregistered(_cloud);
        var first = await OkAsync(await gateway.RegisterRawAsync(code, _branch.ToString("D")));
        var again = await OkAsync(await gateway.RegisterRawAsync(code, _branch.ToString("D"))); // reply "lost", retried
        Assert.Equal(first, again);
        Assert.Equal(1, Credentials());
        Assert.Contains(_cloud.Log, l => l.Contains("repeated its registration (same key)", StringComparison.Ordinal));

        // Another identity never gets in with that code, even with a valid proof for its own key.
        using var other = RawGatewayClient.Unregistered(_cloud);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.RegisterRawAsync(code, _branch.ToString("D"))).StatusCode);
        // Proof by another key over our public key: refused (possession is still required for a replay).
        using var forger = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gateway.RegisterRawAsync(code, _branch.ToString("D"), signer: forger)).StatusCode);
        // Wrong branch with the right key: refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await gateway.RegisterRawAsync(code, Guid.NewGuid().ToString("D"))).StatusCode);
        Assert.Equal(1, Credentials());

        // The registered identity works.
        using var signed = new RawGatewayClientFromKey(_cloud, gateway, first.GatewayId);
        Assert.Equal(HttpStatusCode.OK, (await signed.StateAsync()).StatusCode);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_key_yield_one_credential_and_a_racing_second_key_loses()
    {
        var code = _cloud.GatewayCode(_branch);
        using var a = RawGatewayClient.Unregistered(_cloud);
        using var b = RawGatewayClient.Unregistered(_cloud);
        var attempts = Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? a : b).RegisterRawAsync(code, _branch.ToString("D"))).ToList();
        var responses = await Task.WhenAll(attempts);

        // Exactly one key wins every one of its attempts (same identity each time); the other loses all of its.
        var byKey = responses.Select((r, i) => (Key: i % 2, r.StatusCode)).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.StatusCode).Distinct().ToList());
        Assert.All(byKey.Values, statuses => Assert.Single(statuses));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], byKey.Values.Select(v => v[0]).Order());
        var ids = new HashSet<string>();
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            ids.Add((await OkAsync(r)).GatewayId);
        }

        Assert.Single(ids);
        Assert.Equal(1, Credentials());
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE id::text = @p0", ids.Single()));
    }

    [Fact]
    public async Task A_replay_is_refused_once_the_credential_is_revoked_or_the_window_has_passed()
    {
        var code = _cloud.GatewayCode(_branch);
        using var gateway = RawGatewayClient.Unregistered(_cloud);
        var registration = await OkAsync(await gateway.RegisterRawAsync(code, _branch.ToString("D")));

        _cloud.Count("WITH u AS (UPDATE provisioning_codes SET used_at = now() - interval '8 days' WHERE used_by_gateway_id = @p0::uuid RETURNING 1) SELECT COUNT(*) FROM u", registration.GatewayId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gateway.RegisterRawAsync(code, _branch.ToString("D"))).StatusCode);

        _cloud.Count("WITH u AS (UPDATE provisioning_codes SET used_at = now() WHERE used_by_gateway_id = @p0::uuid RETURNING 1) SELECT COUNT(*) FROM u", registration.GatewayId);
        Assert.Equal(HttpStatusCode.OK, (await gateway.RegisterRawAsync(code, _branch.ToString("D"))).StatusCode);

        _cloud.Admin("gateway-revoke", "--gateway", registration.GatewayId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gateway.RegisterRawAsync(code, _branch.ToString("D"))).StatusCode); // never revives
        Assert.Equal(1, Credentials());
    }

    [Fact]
    public async Task A_gateway_whose_registration_reply_times_out_links_on_retry_even_across_a_restart()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Port", link: false);
        var terminal = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(terminal) };

        // The relay holds every reply longer than the gateway's 5 s request timeout: the cloud commits the
        // registration, the gateway never sees the answer.
        await using var relay = new TcpProxy(_cloud.Port) { ResponseDelay = TimeSpan.FromSeconds(8) };
        branch.Gateway.Run("cloud-link", "--url", relay.Url, "--code", _cloud.GatewayCode(branch.BranchId), "--pin", _cloud.Fingerprint);
        await Wait.UntilAsync(() => _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE branch_id = @p0", branch.BranchId) == 1, "registration committed in the cloud", 60);
        await Wait.UntilAsync(() => branch.Gateway.Log.Any(l => l.Contains("délai dépassé", StringComparison.Ordinal)), "gateway timed out", 60);
        Assert.Equal(0, branch.Gateway.Count("SELECT COUNT(*) FROM cloud_link WHERE gateway_id IS NOT NULL"));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM cloud_link WHERE pending_private_key IS NOT NULL"));

        // The gateway restarts (the pending key is persisted), the network recovers: the retry uses the same key.
        await branch.Gateway.KillAsync();
        relay.ResponseDelay = TimeSpan.Zero;
        await branch.Gateway.StartAsync();
        await branch.WaitSyncedAsync(_cloud, 60);
        await branch.AssertCloudMatchesAsync(_cloud, orders);

        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE branch_id = @p0", branch.BranchId));
        Assert.Equal(0, branch.Gateway.Count("SELECT COUNT(*) FROM cloud_link WHERE pending_private_key IS NOT NULL OR pending_code IS NOT NULL"));
        Assert.Contains(_cloud.Log, l => l.Contains("repeated its registration (same key)", StringComparison.Ordinal));
    }
}

/// <summary>Signs requests with an existing raw client's key under a given gateway id.</summary>
internal sealed class RawGatewayClientFromKey(CloudProcess cloud, RawGatewayClient source, string gatewayId) : IDisposable
{
    private readonly HttpClient _http = cloud.Http();

    public Task<HttpResponseMessage> StateAsync()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = source.Key.SignData(SyncProtocol.RequestPayload(gatewayId, "GET", "/v1/sync/state", timestamp, SyncProtocol.Sha256Hex([])),
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var request = new HttpRequestMessage(HttpMethod.Get, "v1/sync/state");
        request.Headers.TryAddWithoutValidation("Authorization", $"{SyncProtocol.GatewayScheme} {gatewayId}:{timestamp}:{Convert.ToBase64String(signature)}");
        return _http.SendAsync(request);
    }

    public void Dispose() => _http.Dispose();
}

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;
using Pos.Core;
using Pos.Core.Gateway;

namespace Pos.Gateway.Tests;

/// <summary>LAN security: nothing is reachable without enrollment + authentication, inputs are strict.</summary>
public sealed class GatewaySecurityTests : IAsyncLifetime
{
    private readonly GatewayProcess _gateway = new();
    private SimTerminal _a = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        await _gateway.StartAsync();
        _a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        _http = RawGateway.Http(_gateway);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _a.DisposeAsync();
        await _gateway.DisposeAsync();
    }

    private string Orders => $"v1/branches/{_gateway.BranchId}/orders";

    [Fact]
    public async Task Every_business_endpoint_requires_an_authenticated_terminal()
    {
        var orderId = Wait.NewId();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, Orders),
            (HttpMethod.Post, Orders),
            (HttpMethod.Get, $"{Orders}/{orderId}"),
            (HttpMethod.Post, $"{Orders}/{orderId}/items"),
            (HttpMethod.Post, $"{Orders}/{orderId}/submit"),
            (HttpMethod.Get, $"v1/branches/{_gateway.BranchId}/me"),
            (HttpMethod.Post, $"v1/branches/{_gateway.BranchId}/operations/{orderId}/resolve"),
            (HttpMethod.Get, "v1/unknown"),
            (HttpMethod.Get, "/"),
        })
        {
            foreach (var token in new string?[] { null, "forged-token", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })
            {
                var (status, _) = await RawGateway.SendAsync(_http, method, path, token, "{}");
                Assert.True(status == HttpStatusCode.Unauthorized, $"{method} {path} with token '{token}' → {status}");
            }
        }

        // Real-time channel too.
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_gateway.Url), "v1/hub"), o =>
            {
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == _gateway.Fingerprint;
            })
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task Plain_http_is_not_served()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _gateway.Port);
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => plain.GetAsync(new Uri($"http://127.0.0.1:{_gateway.Port}/{Orders}")));
    }

    [Fact]
    public async Task Pairing_code_is_single_use_and_requires_possession_of_the_private_key()
    {
        var (code, fingerprint) = _gateway.Enroll("Caisse B");

        // Signature made with a key that is not the one being registered → refused, code not consumed.
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var realKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var terminalId = Wait.NewId();
        var forged = new EnrollRequest(code, terminalId, Convert.ToBase64String(realKey.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(otherKey.SignData(GatewayProtocol.EnrollPayload(terminalId, code, fingerprint), HashAlgorithmName.SHA256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("v1/enroll", forged)).StatusCode);

        await using var b = SimTerminal.Create();
        await b.Client.EnrollAsync(_gateway.Url, fingerprint, code);

        await using var c = SimTerminal.Create();
        var reused = await Assert.ThrowsAsync<PosValidationException>(() => c.Client.EnrollAsync(_gateway.Url, fingerprint, code));
        Assert.Equal("unauthorized", reused.Code);
        var guessed = await Assert.ThrowsAsync<PosValidationException>(() => c.Client.EnrollAsync(_gateway.Url, fingerprint, "AAAAA-BBBBB-CCCCC-DDDDD"));
        Assert.Equal("unauthorized", guessed.Code);
        Assert.Equal(2, _gateway.Count("SELECT COUNT(*) FROM terminals;"));
    }

    [Fact]
    public async Task Terminal_refuses_a_gateway_whose_certificate_does_not_match_the_pin()
    {
        var (code, _) = _gateway.Enroll("Caisse B");
        await using var b = SimTerminal.Create();
        var wrongPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var ex = await Assert.ThrowsAsync<PosValidationException>(() => b.Client.EnrollAsync(_gateway.Url, wrongPin, code));
        Assert.Equal("certificate_mismatch", ex.Code);
        Assert.Null(b.Store.GetLink());
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM terminals;"));
    }

    [Fact]
    public async Task Tokens_are_bound_to_the_terminals_branch()
    {
        var token = await RawGateway.LoginAsync(_http, _a, _gateway.BranchId);
        Assert.Equal(HttpStatusCode.OK, (await RawGateway.SendAsync(_http, HttpMethod.Get, Orders, token)).Status);
        var (status, body) = await RawGateway.SendAsync(_http, HttpMethod.Get, $"v1/branches/{Wait.NewId()}/orders", token);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("branch_mismatch", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_requires_a_fresh_single_use_challenge_signed_by_the_enrolled_key()
    {
        var challenge = (await (await _http.PostAsJsonAsync("v1/auth/challenge", new ChallengeRequest(_a.TerminalId))).Content.ReadFromJsonAsync<ChallengeResponse>())!;
        var payload = GatewayProtocol.AuthPayload(_gateway.BranchId, _a.TerminalId, challenge.Nonce, _gateway.Fingerprint);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bad = new TokenRequest(_a.TerminalId, challenge.Nonce, Convert.ToBase64String(stranger.SignData(payload, HashAlgorithmName.SHA256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("v1/auth/token", bad)).StatusCode);

        // The nonce was burned by the failed attempt: even a valid signature cannot reuse it.
        var good = new TokenRequest(_a.TerminalId, challenge.Nonce, Convert.ToBase64String(_a.Store.Sign(payload)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("v1/auth/token", good)).StatusCode);
    }

    [Fact]
    public async Task Revoked_terminal_is_disconnected_and_cannot_reconnect()
    {
        var token = await RawGateway.LoginAsync(_http, _a, _gateway.BranchId);
        _gateway.Run("revoke", "--terminal", _a.TerminalId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RawGateway.SendAsync(_http, HttpMethod.Get, Orders, token)).Status);
        await _a.WaitUntilAsync(() => _a.Client.Status.State == GatewayState.Offline, "live connection closed after revocation");
        await Task.Delay(1500);
        Assert.Equal(GatewayState.Offline, _a.Client.Status.State);
        Assert.Contains("refusé", _a.Client.Status.LastError, StringComparison.Ordinal);
        var blocked = await Assert.ThrowsAsync<PosValidationException>(() => _a.Client.CreateOrderAsync(Wait.NewId(), null));
        Assert.Equal("gateway_unavailable", blocked.Code);
    }

    [Theory]
    [InlineData("""{"operationId":"$ID","label":null,"extra":1}""")]
    [InlineData("""{"operationId":"$ID"}""")]
    [InlineData("""{"operationId":"not-a-guid","label":null}""")]
    [InlineData("""{"operationId":"$ID","label":"x\u0001"}""")]
    [InlineData("""{"operationId":"$ID","label":12}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""not json""")]
    public async Task Malformed_bodies_are_rejected(string json)
    {
        var token = await RawGateway.LoginAsync(_http, _a, _gateway.BranchId);
        var (status, body) = await RawGateway.SendAsync(_http, HttpMethod.Post, Orders, token, json.Replace("$ID", Wait.NewId(), StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM shared_orders;"));
    }

    [Fact]
    public async Task Oversized_bodies_are_rejected()
    {
        var token = await RawGateway.LoginAsync(_http, _a, _gateway.BranchId);
        var huge = $$"""{"operationId":"{{Wait.NewId()}}","label":"{{new string('x', 100_000)}}"}""";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await RawGateway.SendAsync(_http, HttpMethod.Post, Orders, token, huge)).Status);
    }
}

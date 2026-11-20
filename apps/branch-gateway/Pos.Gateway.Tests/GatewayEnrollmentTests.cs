using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Pos.Core;
using Pos.Core.Gateway;

namespace Pos.Gateway.Tests;

/// <summary>
/// Phase 2.1C: a pairing code is consumed by one terminal identity, which can repeat its enrollment when the reply
/// was lost (timeout under load); the terminal retries automatically. Any other identity is still refused.
/// </summary>
public sealed class GatewayEnrollmentTests : IAsyncLifetime
{
    private readonly GatewayProcess _gateway = new();

    public Task InitializeAsync() => _gateway.StartAsync();

    public Task DisposeAsync() => _gateway.DisposeAsync().AsTask();

    private static EnrollRequest Request(string code, string terminalId, ECDsa key, string fingerprint)
    {
        code = GatewayProtocol.NormalizeCode(code);
        return new EnrollRequest(code, terminalId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(key.SignData(GatewayProtocol.EnrollPayload(terminalId, code, fingerprint), HashAlgorithmName.SHA256)));
    }

    [Fact]
    public async Task The_same_terminal_can_repeat_an_enrollment_and_no_other_identity_can_use_the_code()
    {
        var (code, fingerprint) = _gateway.Enroll("Caisse B");
        using var http = RawGateway.Http(_gateway);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var terminalId = Wait.NewId();

        var first = await http.PostAsJsonAsync("v1/enroll", Request(code, terminalId, key, fingerprint));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var again = await http.PostAsJsonAsync("v1/enroll", Request(code, terminalId, key, fingerprint)); // reply lost, retried
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM terminals;"));

        // Same terminal id with another key, or another terminal: refused.
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("v1/enroll", Request(code, terminalId, otherKey, fingerprint))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("v1/enroll", Request(code, Wait.NewId(), otherKey, fingerprint))).StatusCode);

        // A revoked terminal never comes back through its old code.
        _gateway.Run("revoke", "--terminal", terminalId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("v1/enroll", Request(code, terminalId, key, fingerprint))).StatusCode);
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM terminals;"));
    }

    [Fact]
    public async Task A_terminal_whose_enrollment_reply_is_lost_retries_and_ends_up_enrolled_once()
    {
        var (code, fingerprint) = _gateway.Enroll("Caisse C");
        await using var relay = new TcpProxy(_gateway.Port) { ResponseDelay = TimeSpan.FromSeconds(30) }; // beyond one attempt
        await using var terminal = SimTerminal.Create();

        var enrolling = terminal.Client.EnrollAsync(relay.Url, fingerprint, code);
        await Wait.UntilAsync(() => _gateway.Count("SELECT COUNT(*) FROM terminals;") == 1, "enrollment committed by the gateway", 30);
        relay.ResponseDelay = TimeSpan.Zero; // the network recovers while the first reply is still held

        var enrolled = await enrolling; // the retry gets the same enrollment back
        Assert.Equal("Caisse C", enrolled.TerminalName);
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM terminals;"));
        terminal.Start();
        await terminal.WaitConnectedAsync();
    }

    [Fact]
    public async Task An_unreachable_gateway_still_fails_cleanly_after_the_bounded_retries()
    {
        await using var terminal = SimTerminal.Create();
        var (code, fingerprint) = _gateway.Enroll("Caisse D");
        await using var relay = new TcpProxy(_gateway.Port);
        relay.Cut(silent: false); // every connection refused
        var started = DateTime.UtcNow;
        var failure = await Assert.ThrowsAsync<PosValidationException>(() => terminal.Client.EnrollAsync(relay.Url, fingerprint, code));
        Assert.Equal("gateway_unavailable", failure.Code);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), "retries are bounded");
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM terminals;"));
    }
}

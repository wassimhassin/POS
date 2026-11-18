using Microsoft.Data.Sqlite;
using Pos.Core;
using Pos.Core.Gateway;

namespace Pos.Gateway.Tests;

/// <summary>Gateway crash/restart, terminal disconnection/restart, interrupted operations, SQLite recovery.</summary>
public sealed class GatewayRecoveryTests : IAsyncLifetime
{
    private readonly GatewayProcess _gateway = new();

    public Task InitializeAsync() => _gateway.StartAsync();

    public Task DisposeAsync() => _gateway.DisposeAsync().AsTask();

    [Fact]
    public async Task Gateway_crash_and_restart_terminals_fail_closed_then_recover_the_same_state()
    {
        await using var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        await using var b = await SimTerminal.EnrolledAsync(_gateway, "Caisse B");
        var orderId = (await a.Client.CreateOrderAsync(Wait.NewId(), "Table 1")).Order.Id;
        await a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 2);
        await b.Client.AddItemAsync(Wait.NewId(), orderId, "p-pizza", 1);
        var before = await WaitSameVersion(a, b, orderId, 3);

        await _gateway.KillAsync();
        await a.WaitUntilAsync(() => a.Client.Status.State == GatewayState.Offline, "A detects the outage");
        await b.WaitUntilAsync(() => b.Client.Status.State == GatewayState.Offline, "B detects the outage");

        // Fail closed: no local write, no offline queue, no divergence.
        var blocked = await Assert.ThrowsAsync<PosValidationException>(() => b.Client.AddItemAsync(Wait.NewId(), orderId, "p-crepe", 1));
        Assert.Equal("gateway_unavailable", blocked.Code);
        Assert.Empty(b.Store.ListPending());
        Assert.Equal(Wait.Json(before), Wait.Json(b.Client.Order(orderId))); // last known state still displayed (read-only)

        await _gateway.StartAsync();
        await a.WaitConnectedAsync();
        await b.WaitConnectedAsync();
        Assert.Equal(Wait.Json(before), Wait.Json(a.Client.Order(orderId)));
        Assert.Equal(Wait.Json(before), Wait.Json(b.Client.Order(orderId)));

        // Work continues after recovery, and both see it.
        await b.Client.AddItemAsync(Wait.NewId(), orderId, "p-crepe", 1);
        await WaitSameVersion(a, b, orderId, 4);
        Assert.Equal("ok", IntegrityCheck());
    }

    [Fact]
    public async Task Terminal_restart_keeps_its_identity_and_reconnects_without_new_enrollment()
    {
        var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var terminalId = a.TerminalId;
        var dataDir = a.DataDir;
        var orderId = (await a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        await a.StopAsync();

        await using var restarted = SimTerminal.Reopen(dataDir);
        Assert.Equal(terminalId, restarted.TerminalId);
        restarted.Start();
        await restarted.WaitConnectedAsync();
        Assert.NotNull(restarted.Client.Order(orderId));
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM terminals;"));
    }

#if DEBUG
    [Fact]
    public async Task Submission_committed_but_never_acknowledged_is_resolved_exactly_once()
    {
        await using var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var orderId = (await a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        var version = (await a.Client.AddItemAsync(Wait.NewId(), orderId, "p-pizza", 1)).Order.Version;

        // Restart the gateway with a fault: it dies right after committing the submission, before replying.
        await _gateway.KillAsync();
        await a.WaitUntilAsync(() => a.Client.Status.State == GatewayState.Offline, "offline");
        _gateway.WithEnvironment("POS_GATEWAY_FAULT_CRASH_AFTER_COMMIT", "submit");
        await _gateway.StartAsync();
        await a.WaitConnectedAsync();

        var unknown = await Assert.ThrowsAsync<PosValidationException>(() => a.Client.SubmitAsync(Wait.NewId(), orderId, version));
        Assert.Equal("gateway_unavailable", unknown.Code);
        Assert.Single(a.Store.ListPending()); // outcome unknown, durably remembered

        _gateway.WithEnvironment("POS_GATEWAY_FAULT_CRASH_AFTER_COMMIT", "");
        await _gateway.StartAsync();
        await a.WaitConnectedAsync();
        await a.WaitUntilAsync(() => a.Store.ListPending().Count == 0, "pending operation resolved");

        Assert.Equal(SharedOrderStatus.Submitted, a.Client.Order(orderId)!.Status);
        Assert.Contains(a.Client.Snapshot().Notices, n => n.Message.Contains("appliquée une seule fois", StringComparison.Ordinal));
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM operations WHERE order_id = $id AND type = 'submit';", ("$id", orderId)));
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM print_intents WHERE order_id = $id;", ("$id", orderId)));
    }
#endif

    [Fact]
    public async Task Operation_that_never_reached_the_gateway_is_abandoned_and_can_never_apply_later()
    {
        var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var orderId = (await a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        var dataDir = a.DataDir;
        await a.StopAsync();

        // The POS crashed after recording the intent but before the request left the machine.
        var lostId = Wait.NewId();
        await using var restarted = SimTerminal.Reopen(dataDir);
        restarted.Store.AddPending(lostId, "add_item", "Ajout de 1 × p-espresso");
        restarted.Start();
        await restarted.WaitConnectedAsync();
        await restarted.WaitUntilAsync(() => restarted.Store.ListPending().Count == 0, "pending resolved");
        Assert.Contains(restarted.Client.Snapshot().Notices, n => n.Message.Contains("NON appliquée", StringComparison.Ordinal));

        // A delayed copy of that request arriving later is refused.
        var late = await Assert.ThrowsAsync<PosValidationException>(() => restarted.Client.AddItemAsync(lostId, orderId, "p-espresso", 1));
        Assert.Equal("operation_abandoned", late.Code);
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM shared_order_lines WHERE order_id = $id;", ("$id", orderId)));
    }

    [Fact]
    public async Task Online_backup_is_consistent_and_usable()
    {
        await using var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var orderId = (await a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        await a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 1);

        var backupDir = Directory.CreateTempSubdirectory("pos-gw-backup-").FullName;
        try
        {
            var file = Path.Combine(backupDir, "gateway.db");
            Assert.Contains("vérifiée", _gateway.Run("backup", "--out", file), StringComparison.Ordinal);
            using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM shared_orders;";
            Assert.Equal(2L, command.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(backupDir, recursive: true);
        }
    }

    [Fact]
    public async Task Corrupted_database_stops_the_gateway_instead_of_serving_bad_state()
    {
        await _gateway.KillAsync();
        var db = Path.Combine(_gateway.DataDir, "gateway.db");
        foreach (var sidecar in new[] { db + "-wal", db + "-shm" })
        {
            File.Delete(sidecar);
        }

        // Overwrite a few pages in the middle of the file (simulated disk corruption).
        using (var stream = new FileStream(db, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 4096 * 2;
            stream.Write(Enumerable.Repeat((byte)0xAB, 4096 * 3).ToArray());
        }

        var (exitCode, output) = _gateway.RunRaw("status");
        Assert.NotEqual(0, exitCode);
        Assert.True(output.Contains("integrity", StringComparison.OrdinalIgnoreCase) || output.Contains("malformed", StringComparison.OrdinalIgnoreCase), output);
    }

    private static async Task<SharedOrder> WaitSameVersion(SimTerminal a, SimTerminal b, string orderId, long version)
    {
        await a.WaitUntilAsync(() => a.Client.Order(orderId)?.Version == version, $"A at v{version}");
        await b.WaitUntilAsync(() => b.Client.Order(orderId)?.Version == version, $"B at v{version}");
        Assert.Equal(Wait.Json(a.Client.Order(orderId)), Wait.Json(b.Client.Order(orderId)));
        return a.Client.Order(orderId)!;
    }

    private string IntegrityCheck()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_gateway.DataDir, "gateway.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return (string)command.ExecuteScalar()!;
    }
}

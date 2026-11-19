using Pos.Core.Gateway;
using Pos.Gateway.Tests;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>
/// Real gateway processes (SQLite outbox) synchronizing to a real cloud process (PostgreSQL): normal flow,
/// outages, restarts on both sides, lost acknowledgments, blocked events, upgrade of an existing gateway.
/// </summary>
public sealed class CloudSyncTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private Guid _organization;
    private Guid _business;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _organization = _cloud.CreateOrganization("Groupe Test");
        _business = _cloud.CreateBusiness(_organization, "Café Test");
    }

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    [Fact]
    public async Task Normal_sync_branch_orders_reach_the_cloud_with_the_branch_state()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre");
        var a = await branch.TerminalAsync("Caisse A");
        var order = (await a.Client.CreateOrderAsync(Wait.NewId(), "Table 4")).Order;
        await a.Client.AddItemAsync(Wait.NewId(), order.Id, "p-espresso", 2);
        await a.Client.AddItemAsync(Wait.NewId(), order.Id, "p-pizza", 1);
        var submitted = (await a.Client.SubmitAsync(Wait.NewId(), order.Id, 3)).Order;

        await branch.WaitSyncedAsync(_cloud);

        // 1 catalog snapshot + 4 order operations, each exactly once.
        Assert.Equal(5, branch.OutboxCount);
        await branch.AssertCloudMatchesAsync(_cloud, [submitted]);
        Assert.Equal(branch.Gateway.Count("SELECT COUNT(*) FROM products"), _cloud.Count("SELECT COUNT(*) FROM branch_products WHERE branch_id = @p0", branch.BranchId));
        Assert.Equal(2, _cloud.Count("SELECT COUNT(*) FROM order_lines WHERE branch_id = @p0 AND order_id = @p1", branch.BranchId, Guid.Parse(order.Id)));

        // The tenant chain recorded in the gateway comes from the cloud, and the local identity did not change.
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM cloud_link WHERE organization_id = @o AND business_id = @b",
            ("@o", _organization.ToString("D")), ("@b", _business.ToString("D"))));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM gateway_identity WHERE branch_id = @b", ("@b", branch.BranchId.ToString("D"))));
    }

    [Fact]
    public async Task Orders_created_during_a_cloud_outage_and_across_a_gateway_restart_sync_exactly_once_after_recovery()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre");
        var a = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(a) };
        await branch.WaitSyncedAsync(_cloud);
        var syncedBeforeOutage = branch.AckedSequence;

        // Internet/cloud outage: the branch keeps working on the LAN, events accumulate durably.
        await _cloud.KillAsync();
        orders.Add(await BranchUnderTest.PlaceAsync(a));
        orders.Add(await BranchUnderTest.PlaceAsync(a, submit: false, productId: "p-pizza"));
        await Wait.UntilAsync(() => branch.LastSyncError?.Contains("injoignable", StringComparison.Ordinal) == true, "outage detected by the gateway");
        Assert.Equal(syncedBeforeOutage, branch.AckedSequence);
        Assert.True(branch.OutboxCount > syncedBeforeOutage);

        // Gateway restart (crash) during the outage: nothing pending is lost, sequences stay contiguous.
        var pendingBeforeRestart = branch.OutboxCount;
        await branch.Gateway.KillAsync();
        await branch.Gateway.StartAsync();
        await a.WaitConnectedAsync();
        Assert.Equal(pendingBeforeRestart, branch.OutboxCount);
        orders.Add(await BranchUnderTest.PlaceAsync(a));
        var reopened = orders[2];
        reopened = (await a.Client.AddItemAsync(Wait.NewId(), reopened.Id, "p-croissant", 1)).Order;
        orders[2] = reopened;
        Assert.Equal(branch.OutboxCount, branch.Gateway.Count("SELECT MAX(sequence) FROM sync_outbox"));
        Assert.Equal(syncedBeforeOutage, branch.AckedSequence);

        // Recovery: everything reaches the cloud, once, with the branch's state.
        await _cloud.StartAsync();
        await branch.WaitSyncedAsync(_cloud);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
        Assert.Equal(4, _cloud.Count("SELECT COUNT(*) FROM orders WHERE branch_id = @p0", branch.BranchId));
        await Wait.UntilAsync(() => branch.LastSyncError is null, "sync error cleared after recovery");
    }

    [Fact]
    public async Task Cloud_killed_while_draining_a_large_backlog_then_restarted_ends_with_each_event_once()
    {
        // Generous request timeout: provisioning codes are single use, so a registration whose reply arrives after the
        // gateway gave up cannot be retried with the same code (known limitation, doc 12 §5) and the drain never starts.
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre", link: false, batchSize: 1, syncTimeoutSeconds: 30);
        var a = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder>();
        for (var i = 0; i < 25; i++)
        {
            orders.Add(await BranchUnderTest.PlaceAsync(a, submit: i % 3 != 0));
        }

        // Linking starts draining ~70 events one by one; the cloud is killed in the middle of it. Each reply is held
        // 150 ms by a relay, so the drain takes ≥ 10 s and the kill deterministically lands mid-backlog (without the
        // relay, a loaded machine could poll late and find the backlog already drained).
        await using var relay = new TcpProxy(_cloud.Port) { ResponseDelay = TimeSpan.FromMilliseconds(150) };
        branch.Gateway.Run("cloud-link", "--url", relay.Url, "--code", _cloud.GatewayCode(branch.BranchId), "--pin", _cloud.Fingerprint);
        // Kill at the first committed event: the remaining ~66 single-event batches still need ≥ 10 s, a wide margin
        // over polling and kill latency. Reaching that point is slow under load (every relayed reply is held), hence 90 s.
        try
        {
            await Wait.UntilAsync(() => _cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", branch.BranchId) >= 1, "sync under way", 90);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("No event committed within 90 s.\n--- gateway ---\n" + string.Join('\n', branch.Gateway.Log.TakeLast(40))
                + "\n--- cloud ---\n" + string.Join('\n', _cloud.Log.TakeLast(40)));
        }

        await _cloud.KillAsync();

        // Whatever was in flight: the cloud's checkpoint equals its stored events (no partial batch), and the
        // gateway never acknowledged more than the cloud committed.
        var cloudLast = _cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", branch.BranchId);
        Assert.Equal(cloudLast, _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch.BranchId));
        var acked = branch.AckedSequence;
        var outbox = branch.OutboxCount;
        Assert.True(acked <= cloudLast, $"Gateway acknowledged {acked} but the cloud committed only {cloudLast}.");
        // Precondition of the scenario (timing): the kill landed before the whole backlog was drained.
        Assert.True(cloudLast < outbox, $"The backlog was fully drained ({cloudLast}/{outbox}) before the cloud was killed.");

        relay.ResponseDelay = TimeSpan.Zero;
        await _cloud.StartAsync();
        await branch.WaitSyncedAsync(_cloud, 60);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
    }

#if DEBUG
    /// <summary>The cloud commits, then the acknowledgment is lost (connection dropped, or the cloud dies before replying).</summary>
    [Theory]
    [InlineData("drop-ack:2")]
    [InlineData("crash-after-commit:1")]
    public async Task Lost_acknowledgment_the_gateway_replays_and_the_cloud_recognizes_duplicates(string fault)
    {
        _cloud.WithEnvironment("POS_CLOUD_FAULT", fault);
        await _cloud.KillAsync();
        await _cloud.StartAsync();

        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre", link: false);
        var a = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(a), await BranchUnderTest.PlaceAsync(a) };
        await branch.LinkAsync(_cloud);

        if (fault.StartsWith("crash", StringComparison.Ordinal))
        {
            await Wait.UntilAsync(() => _cloud.HasExited, "cloud crashed right after committing");
            Assert.Equal(0, branch.AckedSequence); // committed in the cloud, never acknowledged to the gateway
            Assert.Equal(branch.OutboxCount, _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch.BranchId));
            _cloud.WithEnvironment("POS_CLOUD_FAULT", "");
            await _cloud.StartAsync();
        }

        await branch.WaitSyncedAsync(_cloud);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
        Assert.True(branch.Gateway.Count("SELECT MAX(attempts) FROM sync_outbox") >= 2, "the same events were sent more than once");
        Assert.Contains(_cloud.Log, line => line.Contains("duplicates (already applied", StringComparison.Ordinal) && !line.Contains(" 0 duplicates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cloud_crash_before_commit_leaves_no_partial_batch_and_sync_resumes()
    {
        _cloud.WithEnvironment("POS_CLOUD_FAULT", "crash-before-commit:2");
        await _cloud.KillAsync();
        await _cloud.StartAsync();

        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre", link: false, batchSize: 3);
        var a = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(a), await BranchUnderTest.PlaceAsync(a) };
        await branch.LinkAsync(_cloud);

        await Wait.UntilAsync(() => _cloud.HasExited, "cloud killed in the middle of the second batch");
        Assert.Equal(3, _cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", branch.BranchId));
        Assert.Equal(3, _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch.BranchId)); // batch 2 rolled back entirely
        Assert.Equal(3, branch.AckedSequence);

        _cloud.WithEnvironment("POS_CLOUD_FAULT", "");
        await _cloud.StartAsync();
        await branch.WaitSyncedAsync(_cloud);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
    }
#endif

    [Fact]
    public async Task A_rejected_event_blocks_synchronization_and_is_never_skipped_or_discarded()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre");
        var a = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(a) };
        await branch.WaitSyncedAsync(_cloud);
        var synced = branch.AckedSequence;

        // An event the cloud cannot accept (here: a schema version it does not support), followed by valid ones.
        // The payload is altered while the gateway is stopped, so no batch read earlier is still in flight.
        await _cloud.KillAsync();
        orders.Add(await BranchUnderTest.PlaceAsync(a));
        orders.Add(await BranchUnderTest.PlaceAsync(a));
        await branch.Gateway.KillAsync();
        var bad = synced + 1;
        branch.ExecuteGatewaySql($"UPDATE sync_outbox SET payload = json_set(payload, '$.schemaVersion', 99) WHERE sequence = {bad};");
        await branch.Gateway.StartAsync();
        await _cloud.StartAsync();

        await Wait.UntilAsync(() => branch.Gateway.Count("SELECT COALESCE(blocked_sequence, 0) FROM sync_state") == bad, "sync blocked on the rejected event");
        await Task.Delay(3000); // several retry cycles
        Assert.Equal(synced, branch.AckedSequence);
        Assert.Equal(synced, _cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", branch.BranchId));
        Assert.Contains("event_rejected", branch.LastSyncError, StringComparison.Ordinal);
        Assert.Equal(branch.OutboxCount, branch.Gateway.Count("SELECT MAX(sequence) FROM sync_outbox")); // nothing deleted
        Assert.True(branch.Gateway.Count($"SELECT attempts FROM sync_outbox WHERE sequence = {bad}") >= 2);

        // Operator resolution (here: the event is repaired), then synchronization resumes from exactly there.
        branch.ExecuteGatewaySql($"UPDATE sync_outbox SET payload = json_set(payload, '$.schemaVersion', 1) WHERE sequence = {bad};");
        await branch.WaitSyncedAsync(_cloud);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
    }

    [Fact]
    public async Task Upgrading_a_phase_1_3_gateway_exports_its_existing_orders_once()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre", link: false);
        var a = await branch.TerminalAsync("Caisse A");
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(a), await BranchUnderTest.PlaceAsync(a, submit: false) };

        // Back to the Phase 1.3 schema (no outbox), as an already-deployed gateway would be.
        await branch.Gateway.KillAsync();
        branch.ExecuteGatewaySql("DROP TABLE recovery_state; DROP TABLE recovery_quarantine; DROP TABLE backup_config; DROP TABLE backup_runs; " +
            "DROP TABLE sync_outbox; DROP TABLE cloud_link; DROP TABLE sync_state; DELETE FROM gateway_schema WHERE version IN (2, 3);");
        await branch.Gateway.StartAsync();
        await a.WaitConnectedAsync();

        // Migrations 2-3 re-applied: one snapshot per existing order (+ the catalog), recovery state normal; terminal identities unchanged.
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM recovery_state WHERE status = 'normal'"));
        Assert.Equal(2, branch.Gateway.Count("SELECT COUNT(*) FROM sync_outbox WHERE type = 'order_imported'"));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM terminals WHERE id = @t AND status = 'active'", ("@t", a.TerminalId)));

        await branch.LinkAsync(_cloud);
        await branch.WaitSyncedAsync(_cloud);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
    }
}

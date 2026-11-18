using System.Net;
using System.Text.Json;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>Sync protocol at the wire level: idempotency, ordering, uniqueness, validation. Real cloud + PostgreSQL.</summary>
public sealed class CloudProtocolTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private Guid _branch;
    private RawGatewayClient _gateway = null!;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        var organization = _cloud.CreateOrganization("Groupe");
        var business = _cloud.CreateBusiness(organization, "Café");
        _branch = _cloud.CreateBranch(business, "Centre");
        _gateway = await RawGatewayClient.RegisterAsync(_cloud, _branch);
    }

    public async Task DisposeAsync()
    {
        _gateway.Dispose();
        await _cloud.DisposeAsync();
    }

    private long Events => _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", _branch);

    private long Orders => _cloud.Count("SELECT COUNT(*) FROM orders WHERE branch_id = @p0", _branch);

    private long Checkpoint => _cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", _branch);

    [Fact]
    public async Task A_replayed_batch_is_acknowledged_again_without_being_reapplied()
    {
        SyncEvent[] batch = [TestEvents.Order(1, 1), TestEvents.Order(2, 2), TestEvents.Order(3, 3)];
        var first = await _gateway.SendOkAsync(batch);
        Assert.Equal(3, first.LastAcceptedSequence);
        Assert.All(first.Outcomes, o => Assert.Equal(SyncOutcomeStatus.Accepted, o.Status));

        var replay = await _gateway.SendOkAsync(batch);
        Assert.Equal(3, replay.LastAcceptedSequence);
        Assert.All(replay.Outcomes, o => Assert.Equal(SyncOutcomeStatus.Duplicate, o.Status));
        Assert.Null(replay.Rejected);
        Assert.Equal(3, Events);
        Assert.Equal(3, Orders);
    }

    [Fact]
    public async Task An_overlapping_batch_skips_the_known_events_and_accepts_the_new_ones()
    {
        var e = Enumerable.Range(1, 4).Select(i => TestEvents.Order(i, i)).ToArray();
        await _gateway.SendOkAsync(e[0], e[1]);
        var result = await _gateway.SendOkAsync(e[1], e[2], e[3]);
        Assert.Equal([SyncOutcomeStatus.Duplicate, SyncOutcomeStatus.Accepted, SyncOutcomeStatus.Accepted], result.Outcomes.Select(o => o.Status));
        Assert.Equal(4, result.LastAcceptedSequence);
        Assert.Equal(4, Events);
    }

    [Fact]
    public async Task Concurrent_identical_batches_are_applied_exactly_once()
    {
        var batch = Enumerable.Range(1, 20).Select(i => TestEvents.Order(i, i)).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _gateway.SendAsync(batch)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        var parsed = results.Select(r => JsonSerializer.Deserialize<SyncBatchResult>(r.Body, SyncProtocol.Json)!).ToList();
        Assert.Equal(20, parsed.Sum(r => r.Outcomes.Count(o => o.Status == SyncOutcomeStatus.Accepted)));
        Assert.All(parsed, r => Assert.Equal(20, r.LastAcceptedSequence));
        Assert.Equal(20, Events);
        Assert.Equal(20, Orders);
    }

    [Fact]
    public async Task A_gap_is_refused_and_nothing_after_it_is_accepted()
    {
        await _gateway.SendOkAsync(TestEvents.Order(1, 1));
        var result = await _gateway.SendOkAsync(TestEvents.Order(3, 3), TestEvents.Order(4, 4));
        Assert.Equal(SyncRejectionCodes.SequenceGap, result.Rejected?.Code);
        Assert.Equal(2, result.Rejected?.ExpectedSequence);
        Assert.Equal(1, result.LastAcceptedSequence);
        Assert.Empty(result.Outcomes);
        Assert.Equal(1, Events);
    }

    [Fact]
    public async Task Different_content_for_a_known_sequence_or_event_id_is_a_conflict_never_an_overwrite()
    {
        var original = TestEvents.Order(1, 1, quantity: 1);
        await _gateway.SendOkAsync(original);

        // Same sequence and id, different payload (e.g. a gateway restored from an old backup).
        var altered = original with { Order = TestEvents.Order(1, 1, orderId: original.Order!.Id, quantity: 2).Order };
        var conflict = await _gateway.SendOkAsync(altered);
        Assert.Equal(SyncRejectionCodes.SequenceConflict, conflict.Rejected?.Code);

        // Same event id at a new sequence.
        var reused = await _gateway.SendOkAsync(original with { Sequence = 2 });
        Assert.Equal(SyncRejectionCodes.SequenceConflict, reused.Rejected?.Code);

        Assert.Equal(1, Events);
        Assert.Equal(1, _cloud.Count("SELECT quantity FROM order_lines WHERE branch_id = @p0", _branch));
    }

    [Fact]
    public async Task An_invalid_event_stops_the_batch_exactly_there()
    {
        var valid = TestEvents.Order(1, 1);
        var bad = TestEvents.Order(2, 2);
        bad = bad with { Order = bad.Order! with { TotalMinor = 1 } }; // inconsistent totals
        var after = TestEvents.Order(3, 3);

        var result = await _gateway.SendOkAsync(valid, bad, after);
        Assert.Equal(1, result.LastAcceptedSequence);
        Assert.Equal(SyncRejectionCodes.EventRejected, result.Rejected?.Code);
        Assert.Equal(2, result.Rejected?.Sequence);
        Assert.Equal(1, Events);
        Assert.Equal(1, Checkpoint);
    }

    [Fact]
    public async Task The_cloud_never_regresses_an_order_or_reuses_an_order_number()
    {
        var orderId = Guid.NewGuid().ToString("D");
        await _gateway.SendOkAsync(TestEvents.Order(1, 1, version: 2, orderId: orderId));

        var older = await _gateway.SendOkAsync(TestEvents.Order(2, 1, version: 1, orderId: orderId));
        Assert.Equal(SyncRejectionCodes.EventRejected, older.Rejected?.Code);

        var sameNumber = await _gateway.SendOkAsync(TestEvents.Order(2, 1)); // another order with number 1
        Assert.Equal(SyncRejectionCodes.EventRejected, sameNumber.Rejected?.Code);
        Assert.Equal(1, Orders);
        Assert.Equal(2, _cloud.Count("SELECT version FROM orders WHERE branch_id = @p0", _branch));
    }

    [Fact]
    public async Task Malformed_batches_and_injected_tenant_fields_are_rejected()
    {
        var json = JsonSerializer.Serialize(new SyncBatch([TestEvents.Order(1, 1)]), SyncProtocol.Json);

        // A client cannot even state a tenant/branch: unknown fields are refused (strict JSON).
        var injected = json.Insert(1, $"\"branchId\":\"{Guid.NewGuid():D}\",");
        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendRawAsync(injected)).Status);
        var injectedInEvent = json.Replace("\"sequence\":1,", $"\"sequence\":1,\"organizationId\":\"{Guid.NewGuid():D}\",", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendRawAsync(injectedInEvent)).Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendRawAsync("{\"events\":[]}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendAsync([.. Enumerable.Range(1, 101).Select(i => TestEvents.Order(i, i))])).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendAsync(TestEvents.Order(1, 1), TestEvents.Order(3, 3))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendRawAsync("{\"events\":[{\"sequence\":\"1\"}]}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _gateway.SendRawAsync("not json")).Status);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _gateway.SendRawAsync(new string(' ', SyncProtocol.MaxBodyBytes + 1) + json)).Status);
        Assert.Equal(0, Events);
    }
}

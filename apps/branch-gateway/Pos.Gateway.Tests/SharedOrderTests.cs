using Pos.Core;
using Pos.Core.Gateway;

namespace Pos.Gateway.Tests;

/// <summary>Two terminals coordinating through one real gateway process.</summary>
public sealed class SharedOrderTests : IAsyncLifetime
{
    private readonly GatewayProcess _gateway = new();
    private SimTerminal _a = null!;
    private SimTerminal _b = null!;

    public async Task InitializeAsync()
    {
        await _gateway.StartAsync();
        _a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        _b = await SimTerminal.EnrolledAsync(_gateway, "Caisse B");
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        await _gateway.DisposeAsync();
    }

    [Fact]
    public async Task Each_terminal_sees_the_other_terminals_changes()
    {
        var created = await _a.Client.CreateOrderAsync(Wait.NewId(), "Table 4");
        var orderId = created.Order.Id;
        await _b.WaitUntilAsync(() => _b.Client.Order(orderId) is not null, "B receives the new order");
        Assert.Equal("Table 4", _b.Client.Order(orderId)!.Label);
        Assert.Equal("Caisse A", _b.Client.Order(orderId)!.CreatedByName);

        await _b.Client.AddItemAsync(Wait.NewId(), orderId, "p-pizza", 1);
        await _a.WaitUntilAsync(() => _a.Client.Order(orderId)?.Lines.Count == 1, "A receives B's item");
        var seenByA = _a.Client.Order(orderId)!;
        Assert.Equal(("p-pizza", 14_000L, "Caisse B"), (seenByA.Lines[0].ProductId, seenByA.Lines[0].UnitPriceMinor, seenByA.UpdatedByName));
        Assert.Equal(2, seenByA.Version);
    }

    [Fact]
    public async Task Concurrent_additions_from_both_terminals_are_all_kept()
    {
        var orderId = (await _a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        await _b.WaitUntilAsync(() => _b.Client.Order(orderId) is not null, "order visible on B");

        var adds = Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 1),
            _b.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 1),
        });
        await Task.WhenAll(adds);

        await _a.WaitUntilAsync(() => _a.Client.Order(orderId)!.Version == 41, "A converges");
        await _b.WaitUntilAsync(() => _b.Client.Order(orderId)!.Version == 41, "B converges");
        Assert.Equal(40, _a.Client.Order(orderId)!.Lines.Single().Quantity);
        Assert.Equal(Wait.Json(_a.Client.Order(orderId)), Wait.Json(_b.Client.Order(orderId)));
        Assert.Equal(40, _gateway.Count("SELECT quantity FROM shared_order_lines WHERE order_id = $id;", ("$id", orderId)));
    }

    [Fact]
    public async Task Conflicting_overwrites_are_rejected_not_lost()
    {
        var orderId = (await _a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        var v = (await _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-croissant", 2)).Order.Version;

        // Both terminals saw version v and both try to overwrite the same quantity.
        var fromA = _a.Client.SetQuantityAsync(Wait.NewId(), orderId, "p-croissant", 5, v);
        var fromB = _b.Client.SetQuantityAsync(Wait.NewId(), orderId, "p-croissant", 1, v);
        var outcomes = await Task.WhenAll(Settle(fromA), Settle(fromB));

        Assert.Single(outcomes, o => o.Error is null);
        var conflict = Assert.Single(outcomes, o => o.Error is not null).Error!;
        Assert.Equal("version_conflict", conflict.Code);

        var winner = outcomes.Single(o => o.Error is null).Result!.Order;
        Assert.Equal(v + 1, winner.Version);
        await _b.WaitUntilAsync(() => _b.Client.Order(orderId)!.Version == v + 1, "loser refreshed to the winning state");
        Assert.Equal(winner.Lines.Single().Quantity, _b.Client.Order(orderId)!.Lines.Single().Quantity);
    }

    [Fact]
    public async Task Duplicate_submission_creates_one_order_and_one_print_intent_per_station()
    {
        var orderId = (await _a.Client.CreateOrderAsync(Wait.NewId(), "Terrasse")).Order.Id;
        await _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 2);      // bar
        await _b.Client.AddItemAsync(Wait.NewId(), orderId, "p-pizza", 1);         // kitchen
        await _b.Client.AddItemAsync(Wait.NewId(), orderId, "p-jus-orange", 1);    // bar
        await _a.WaitUntilAsync(() => _a.Client.Order(orderId)!.Version == 4, "A up to date");

        var submitId = Wait.NewId();
        var first = await _a.Client.SubmitAsync(submitId, orderId, 4);
        Assert.False(first.Replayed);
        Assert.Equal(SharedOrderStatus.Submitted, first.Order.Status);
        Assert.Equal(["bar", "kitchen"], first.PrintIntents.Select(i => i.StationId).Order());

        // Same operation replayed (lost response, double click): original result, nothing re-applied.
        var replay = await _a.Client.SubmitAsync(submitId, orderId, 4);
        Assert.True(replay.Replayed);
        Assert.Equal(first.PrintIntents.ToArray(), replay.PrintIntents.ToArray());
        Assert.Equal(first.Position, replay.Position);

        // Another terminal submits the same order: refused.
        var other = await Assert.ThrowsAsync<PosValidationException>(() => _b.Client.SubmitAsync(Wait.NewId(), orderId, 5));
        Assert.Equal("order_already_submitted", other.Code);

        // Ground truth in the gateway database.
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE id = $id;", ("$id", orderId)));
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM operations WHERE order_id = $id AND type = 'submit';", ("$id", orderId)));
        Assert.Equal(2, _gateway.Count("SELECT COUNT(*) FROM print_intents WHERE order_id = $id;", ("$id", orderId)));
        Assert.Equal(2, _gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE order_id = $id;", ("$id", orderId)));
        Assert.Equal(first.PrintIntents.OrderBy(i => i.StationId).ToArray(), (await _b.Client.PrintIntentsAsync(orderId)).OrderBy(i => i.StationId).ToArray());

        // Submitted orders are frozen.
        var late = await Assert.ThrowsAsync<PosValidationException>(() => _b.Client.AddItemAsync(Wait.NewId(), orderId, "p-croissant", 1));
        Assert.Equal("order_already_submitted", late.Code);
    }

    [Fact]
    public async Task Submitting_a_version_the_operator_did_not_see_is_refused()
    {
        var orderId = (await _a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        var seen = (await _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 1)).Order.Version;
        await _b.Client.AddItemAsync(Wait.NewId(), orderId, "p-pizza", 1); // added after A looked

        var stale = await Assert.ThrowsAsync<PosValidationException>(() => _a.Client.SubmitAsync(Wait.NewId(), orderId, seen));
        Assert.Equal("version_conflict", stale.Code);
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM print_intents WHERE order_id = $id;", ("$id", orderId)));
    }

    [Fact]
    public async Task Idempotency_keys_cannot_be_reused_for_another_request_or_by_another_terminal()
    {
        var orderId = (await _a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        var key = Wait.NewId();
        await _a.Client.AddItemAsync(key, orderId, "p-espresso", 1);

        var changedPayload = await Assert.ThrowsAsync<PosValidationException>(() => _a.Client.AddItemAsync(key, orderId, "p-espresso", 3));
        Assert.Equal("idempotency_key_reused", changedPayload.Code);
        var otherTerminal = await Assert.ThrowsAsync<PosValidationException>(() => _b.Client.AddItemAsync(key, orderId, "p-espresso", 1));
        Assert.Equal("idempotency_key_reused", otherTerminal.Code);
        Assert.Equal(1, _gateway.Count("SELECT quantity FROM shared_order_lines WHERE order_id = $id;", ("$id", orderId)));
    }

    [Fact]
    public async Task Prices_come_from_the_gateway_catalog_and_inputs_are_validated()
    {
        var orderId = (await _a.Client.CreateOrderAsync(Wait.NewId(), null)).Order.Id;
        foreach (var (call, code) in new (Func<Task>, string)[]
        {
            (() => _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-unknown", 1), "unknown_product"),
            (() => _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 0), "invalid_quantity"),
            (() => _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 100), "invalid_quantity"),
            (() => _a.Client.AddItemAsync(Wait.NewId(), Wait.NewId(), "p-espresso", 1), "order_not_found"),
            (() => _a.Client.CreateOrderAsync(Wait.NewId(), new string('x', 61)), "invalid_label"),
            (() => _a.Client.SetQuantityAsync(Wait.NewId(), orderId, "p-espresso", 1, 1), "line_not_found"),
            (() => _a.Client.SubmitAsync(Wait.NewId(), orderId, 1), "empty_order"),
        })
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<PosValidationException>(call)).Code);
        }

        var line = (await _a.Client.AddItemAsync(Wait.NewId(), orderId, "p-espresso", 3)).Order.Lines.Single();
        Assert.Equal((2_500L, 7_500L, "Café express", "bar"), (line.UnitPriceMinor, line.LineTotalMinor, line.Name, line.StationId));
    }

    private static async Task<(OperationResult? Result, PosValidationException? Error)> Settle(Task<OperationResult> task)
    {
        try
        {
            return (await task, null);
        }
        catch (PosValidationException ex)
        {
            return (null, ex);
        }
    }
}

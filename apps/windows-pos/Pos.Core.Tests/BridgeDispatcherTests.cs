using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Bridge;
using Pos.Core.Catalog;
using Pos.Core.Data;
using Pos.Core.Orders;

namespace Pos.Core.Tests;

public sealed class BridgeDispatcherTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly BridgeDispatcher _dispatcher;

    public BridgeDispatcherTests()
    {
        _dispatcher = Create(_test.Database);
    }

    public void Dispose() => _test.Dispose();

    private static BridgeDispatcher Create(PosDatabase db) =>
        new(new ProductRepository(db), new DraftOrderRepository(db), NullLoggerFactory.Instance);

    private static string Request(string type, object payload, string id = "req-1") =>
        JsonSerializer.Serialize(new { v = 1, id, type, payload });

    private static JsonElement Send(BridgeDispatcher dispatcher, string json) =>
        JsonDocument.Parse(dispatcher.Handle(json)).RootElement.Clone();

    private JsonElement Send(string json) => Send(_dispatcher, json);

    private static string ErrorCode(JsonElement response)
    {
        Assert.False(response.GetProperty("ok").GetBoolean());
        return response.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public void Allowlist_is_exactly_the_documented_commands()
    {
        Assert.Equal(
            new[] { "draft.clear", "draft.load", "draft.save", "log.client", "products.list" },
            _dispatcher.Commands.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Products_list_returns_catalog_with_correlated_id()
    {
        var response = Send(Request("products.list", new { }, id: "abc-123"));

        Assert.Equal("abc-123", response.GetProperty("id").GetString());
        Assert.True(response.GetProperty("ok").GetBoolean());
        var first = response.GetProperty("result")[0];
        Assert.Equal("p-espresso", first.GetProperty("id").GetString());
        Assert.Equal(2500, first.GetProperty("priceMinor").GetInt64());
    }

    [Fact]
    public void Draft_save_then_load_round_trips_through_restart()
    {
        var saved = Send(Request("draft.save", new { orderId = (string?)null, lines = new[] { new { productId = "p-crepe", quantity = 2 } } }));
        Assert.True(saved.GetProperty("ok").GetBoolean());
        var orderId = saved.GetProperty("result").GetProperty("id").GetString();

        var afterRestart = Create(_test.Restart());
        var loaded = Send(afterRestart, Request("draft.load", new { }));

        var result = loaded.GetProperty("result");
        Assert.Equal(orderId, result.GetProperty("id").GetString());
        Assert.Equal(16_000, result.GetProperty("totalMinor").GetInt64());
    }

    [Fact]
    public void Draft_load_returns_null_result_when_empty()
    {
        var response = Send(Request("draft.load", new { }));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public void Draft_clear_deletes_the_draft()
    {
        var saved = Send(Request("draft.save", new { orderId = (string?)null, lines = new[] { new { productId = "p-eau", quantity = 1 } } }));
        var orderId = saved.GetProperty("result").GetProperty("id").GetString();

        var cleared = Send(Request("draft.clear", new { orderId }));

        Assert.True(cleared.GetProperty("result").GetProperty("cleared").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Send(Request("draft.load", new { })).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Client_supplied_prices_are_rejected()
    {
        var json = Request("draft.save", new { orderId = (string?)null, lines = new[] { new { productId = "p-pizza", quantity = 1, unitPriceMinor = 1 } } });

        Assert.Equal("bad_request", ErrorCode(Send(json)));
    }

    [Fact]
    public void Validation_errors_are_returned_with_their_code()
    {
        var json = Request("draft.save", new { orderId = (string?)null, lines = new[] { new { productId = "p-pizza", quantity = 0 } } });

        Assert.Equal("invalid_quantity", ErrorCode(Send(json)));
    }

    [Theory]
    [InlineData("""{"v":1,"id":"r1","type":"fs.readFile","payload":{}}""", "unknown_command")]
    [InlineData("""{"v":1,"id":"r1","type":"__proto__","payload":{}}""", "unknown_command")]
    [InlineData("""{"v":2,"id":"r1","type":"products.list","payload":{}}""", "unsupported_version")]
    [InlineData("""{"id":"r1","type":"products.list","payload":{}}""", "unsupported_version")]
    [InlineData("""{"v":1,"id":"r1","type":"products.list"}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"products.list","payload":[]}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"products.list","payload":{"x":1}}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"products.list","payload":{},"extra":true}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"draft.save","payload":{"orderId":null,"lines":[{"productId":"p-eau","quantity":"2"}]}}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"draft.save","payload":{"orderId":null,"lines":[{"productId":"p-eau","quantity":1.5}]}}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"draft.save","payload":{"orderId":null,"lines":null}}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"draft.save","payload":{"lines":[]}}""", "bad_request")]
    [InlineData("""{"v":1,"id":"r1","type":"log.client","payload":{"level":"debug","message":"x"}}""", "bad_request")]
    [InlineData("""{"v":1,"id":"bad id!","type":"products.list","payload":{}}""", "bad_request")]
    [InlineData("""not json""", "bad_request")]
    [InlineData("""[1,2,3]""", "bad_request")]
    public void Malformed_or_unauthorized_requests_are_rejected(string json, string expectedCode)
    {
        Assert.Equal(expectedCode, ErrorCode(Send(json)));
    }

    [Fact]
    public void Oversized_messages_are_rejected_before_parsing()
    {
        var json = Request("log.client", new { level = "info", message = new string('x', BridgeDispatcher.MaxMessageLength) });

        Assert.Equal("payload_too_large", ErrorCode(Send(json)));
    }

    [Fact]
    public void Deeply_nested_payloads_are_rejected()
    {
        var json = """{"v":1,"id":"r1","type":"products.list","payload":""" + new string('[', 100) + new string(']', 100) + "}";

        Assert.Equal("bad_request", ErrorCode(Send(json)));
    }

    [Fact]
    public void Client_log_is_accepted()
    {
        var response = Send(Request("log.client", new { level = "error", message = "boom", detail = "stack" }));

        Assert.True(response.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Unexpected_failures_return_a_generic_error_without_internals()
    {
        // Point the dispatcher at a path that cannot be opened as a database file.
        var dir = Path.Combine(_test.Directory, "not-a-file.db");
        Directory.CreateDirectory(dir);
        var broken = new PosDatabase(dir, NullLogger<PosDatabase>.Instance);
        var dispatcher = new BridgeDispatcher(new ProductRepository(broken), new DraftOrderRepository(broken), NullLoggerFactory.Instance);

        var response = Send(dispatcher, Request("products.list", new { }));

        Assert.Equal("internal_error", ErrorCode(response));
        Assert.DoesNotContain(dir, response.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("req-1", response.GetProperty("id").GetString());
    }
}

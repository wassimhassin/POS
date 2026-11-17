using Pos.Core.Printing;

namespace Pos.Core.Tests;

public sealed class PrintRoutingTests : IDisposable
{
    private readonly PrintFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Default_routing_sends_each_item_to_its_station()
    {
        var order = _f.Order(("p-espresso", 2), ("p-croissant", 1), ("p-pizza", 1), ("p-citronnade", 1));

        var result = _f.Service.EnqueueStationTickets(order.Id, confirmDuplicate: false);

        Assert.Equal(["kitchen", "bar", "counter"], result.Jobs.Select(j => j.DestinationId));
        Assert.Empty(result.UnroutedItems);
        Assert.All(result.Jobs, j => Assert.Equal(PrintJobStatus.Queued, j.Status));
        Assert.Equal(1, _f.Signals);

        Assert.Equal(58, result.Jobs.Single(j => j.DestinationId == "bar").PaperWidthMm);
        _f.Worker.ProcessDue();
        var printed = _f.Printer.Printed.ToDictionary(p => p.Document[..p.Document.IndexOf('#', StringComparison.Ordinal)].Trim(), p => Cp858(p.Data));
        Assert.Contains("2 x Café express", printed["POS Bon Bar"], StringComparison.Ordinal);
        Assert.Contains("1 x Citronnade", printed["POS Bon Bar"], StringComparison.Ordinal);
        Assert.DoesNotContain("Pizza", printed["POS Bon Bar"], StringComparison.Ordinal);
        Assert.Contains("1 x Pizza margherita", printed["POS Bon Cuisine"], StringComparison.Ordinal);
        Assert.Contains("1 x Croissant", printed["POS Bon Comptoir"], StringComparison.Ordinal);
    }

    private static string Cp858(byte[] data) => TicketRenderer.CreateEncoding().GetString(data);

    [Fact]
    public void Routing_is_configurable_per_product()
    {
        _f.Settings.SetProductStation("p-croissant", "kitchen");
        _f.Settings.SetProductStation("p-eau", null);
        var order = _f.Order(("p-croissant", 1), ("p-pizza", 1), ("p-eau", 3));

        var result = _f.Service.EnqueueStationTickets(order.Id, confirmDuplicate: false);

        var job = Assert.Single(result.Jobs);
        Assert.Equal("kitchen", job.DestinationId);
        Assert.Equal(["Eau minérale 50 cl"], result.UnroutedItems);
    }

    [Fact]
    public void Order_with_no_routed_items_is_rejected()
    {
        _f.Settings.SetProductStation("p-eau", null);
        var order = _f.Order(("p-eau", 1));

        var ex = Assert.Throws<PosValidationException>(() => _f.Service.EnqueueStationTickets(order.Id, false));

        Assert.Equal("nothing_to_route", ex.Code);
        Assert.Empty(_f.Queue.ListRecent());
    }

    [Theory]
    [InlineData("receipt", "invalid_station")]
    [InlineData("nowhere", "unknown_destination")]
    public void Products_can_only_be_routed_to_preparation_stations(string station, string code)
    {
        Assert.Equal(code, Assert.Throws<PosValidationException>(() => _f.Settings.SetProductStation("p-pizza", station)).Code);
    }

    [Fact]
    public void Routing_unknown_product_is_rejected() =>
        Assert.Equal("unknown_product", Assert.Throws<PosValidationException>(() => _f.Settings.SetProductStation("p-nope", "bar")).Code);

    [Theory]
    [InlineData(57)]
    [InlineData(112)]
    public void Only_58_and_80_mm_paper_is_accepted(int width) =>
        Assert.Equal("invalid_paper_width", Assert.Throws<PosValidationException>(() => _f.Settings.UpdateDestination("bar", "X", width)).Code);

    [Fact]
    public void Paper_width_is_snapshotted_on_the_job_at_enqueue_time()
    {
        _f.Settings.UpdateDestination("receipt", "Thermal 58", 58);
        var order = _f.Order(("p-pizza", 1));

        var job = Assert.Single(_f.Service.EnqueueReceipt(order.Id, false).Jobs);
        _f.Settings.UpdateDestination("receipt", "Thermal 80", 80);

        Assert.Equal(58, job.PaperWidthMm);
        Assert.Equal(58, _f.Service.Preview(job.Id).PaperWidthMm);
    }

    [Fact]
    public void Receipt_content_comes_from_the_persisted_order()
    {
        var order = _f.Order(("p-cappuccino", 2), ("p-salade-cesar", 1));

        var job = Assert.Single(_f.Service.EnqueueReceipt(order.Id, false).Jobs);
        var text = _f.Service.Preview(job.Id).Text;

        Assert.Contains("Salade César", text, StringComparison.Ordinal);
        Assert.Contains("21,000", text, StringComparison.Ordinal); // 2 × 4,500 + 12,000
        Assert.Contains($"#{job.OrderNumber}", text, StringComparison.Ordinal);
        Assert.Equal(order.Id.Replace("-", string.Empty, StringComparison.Ordinal)[..6].ToUpperInvariant(), job.OrderNumber);
    }

    [Fact]
    public void Printing_a_missing_order_is_rejected() =>
        Assert.Equal("draft_not_found", Assert.Throws<PosValidationException>(() => _f.Service.EnqueueReceipt(Guid.NewGuid().ToString(), false)).Code);

    [Fact]
    public void Printing_the_same_order_again_requires_duplicate_confirmation()
    {
        var order = _f.Order(("p-pizza", 1), ("p-espresso", 1));
        _f.Service.EnqueueStationTickets(order.Id, false);

        var ex = Assert.Throws<PosValidationException>(() => _f.Service.EnqueueStationTickets(order.Id, false));
        Assert.Equal("duplicate_confirmation_required", ex.Code);
        Assert.Equal(2, _f.Queue.ListRecent().Count); // nothing partially enqueued

        var copies = _f.Service.EnqueueStationTickets(order.Id, confirmDuplicate: true);
        Assert.All(copies.Jobs, j => Assert.Equal(1, j.CopyNumber));
        Assert.Contains("Ne pas preparer en double", _f.Service.Preview(copies.Jobs[0].Id).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_jobs_do_not_count_as_already_printed()
    {
        _f.Printer.Unavailable("Thermal 80");
        var order = _f.Order(("p-pizza", 1));
        var first = Assert.Single(_f.Service.EnqueueReceipt(order.Id, false).Jobs);
        for (var i = 0; i < PrintQueue.MaxAutoAttempts; i++)
        {
            _f.Worker.ProcessDue();
            _f.Time.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(PrintJobStatus.Failed, _f.Queue.Get(first.Id)!.Status);

        var again = Assert.Single(_f.Service.EnqueueReceipt(order.Id, confirmDuplicate: false).Jobs);
        Assert.Equal(0, again.CopyNumber);
    }

    [Fact]
    public void Printer_discovery_is_exposed()
    {
        var printers = _f.Service.ListPrinters();

        Assert.Equal(["Thermal 80", "Thermal 58"], printers.Select(p => p.Name));
    }

    [Fact]
    public void Ticket_bytes_reach_the_configured_printer()
    {
        var order = _f.Order(("p-the-menthe", 1));
        _f.Service.EnqueueStationTickets(order.Id, false);

        _f.Worker.ProcessDue();

        var (printer, document, data) = Assert.Single(_f.Printer.Printed);
        Assert.Equal("Thermal 58", printer);
        Assert.StartsWith("POS Bon Bar #", document, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x1B, 0x40 }, data[..2]);
        // 58 mm, double-width text: 16 columns → the name wraps with a hanging indent.
        Assert.Contains("1 x Thé à la\n", Cp858(data), StringComparison.Ordinal);
        Assert.Contains("    menthe\n", Cp858(data), StringComparison.Ordinal);
    }
}

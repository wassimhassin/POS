using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Data;
using Pos.Core.Orders;
using Pos.Core.Printing;

namespace Pos.Core.Tests;

/// <summary>Controllable clock (avoids a test-only package dependency).</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Scriptable printer: records every job and returns configured outcomes per printer.</summary>
internal sealed class FakePrinterGateway : IPrinterGateway
{
    private readonly Dictionary<string, Func<PrintAttemptResult>> _behaviour = new(StringComparer.OrdinalIgnoreCase);
    private int _nextJobId = 100;

    public List<(string Printer, string Document, byte[] Data)> Printed { get; } = [];

    public List<PrinterInfo> Installed { get; } =
    [
        new("Thermal 80", true, true, "Prête", "USB001", "Generic / Text Only"),
        new("Thermal 58", false, true, "Prête", "USB002", "Generic / Text Only"),
    ];

    public void Unavailable(string printer) =>
        _behaviour[printer] = () => PrintAttemptResult.NotSent("Imprimante indisponible : Hors ligne.");

    public void Uncertain(string printer) =>
        _behaviour[printer] = () => PrintAttemptResult.Uncertain("Écriture interrompue.", 7);

    public void Throws(string printer) => _behaviour[printer] = () => throw new IOException("USB cable pulled");

    public void Healthy(string printer) => _behaviour.Remove(printer);

    public IReadOnlyList<PrinterInfo> ListPrinters() => Installed;

    public PrintAttemptResult PrintRaw(string printerName, string documentName, byte[] data)
    {
        if (_behaviour.TryGetValue(printerName, out var behaviour))
        {
            var result = behaviour();
            if (result.Outcome == PrintOutcome.NotSent)
            {
                return result;
            }

            Printed.Add((printerName, documentName, data));
            return result;
        }

        Printed.Add((printerName, documentName, data));
        return PrintAttemptResult.Sent(_nextJobId++);
    }
}

/// <summary>Real SQLite + queue + worker + service wired to a fake printer and a manual clock.</summary>
internal sealed class PrintFixture : IDisposable
{
    public PrintFixture()
    {
        Time = new ManualTime(new DateTimeOffset(2026, 11, 17, 14, 30, 0, TimeSpan.Zero));
        Build(Test.Database);
        Settings.UpdateDestination("receipt", "Thermal 80", 80);
        Settings.UpdateDestination("kitchen", "Thermal 80", 80);
        Settings.UpdateDestination("bar", "Thermal 58", 58);
        Settings.UpdateDestination("counter", "Thermal 58", 58);
    }

    public TestDatabase Test { get; } = new();

    public ManualTime Time { get; }

    public FakePrinterGateway Printer { get; } = new();

    public DraftOrderRepository Drafts { get; private set; } = null!;

    public PrintSettingsRepository Settings { get; private set; } = null!;

    public PrintQueue Queue { get; private set; } = null!;

    public PrintWorker Worker { get; private set; } = null!;

    public PrintService Service { get; private set; } = null!;

    public int Signals { get; private set; }

    /// <summary>Simulates an application restart: new connections and new component instances.</summary>
    public void Restart() => Build(Test.Restart());

    public DraftOrder Order(params (string ProductId, int Quantity)[] lines) =>
        Drafts.Save(null, lines.Select(l => new DraftLineInput(l.ProductId, l.Quantity)).ToList());

    public void Dispose()
    {
        Worker.Dispose();
        Test.Dispose();
    }

    private void Build(PosDatabase database)
    {
        Drafts = new DraftOrderRepository(database, Time);
        Settings = new PrintSettingsRepository(database);
        Queue = new PrintQueue(database, NullLogger<PrintQueue>.Instance, Time);
        Worker?.Dispose();
        Worker = new PrintWorker(Queue, Settings, Printer, NullLogger<PrintWorker>.Instance, TimeZoneInfo.Utc);
        Service = new PrintService(database, Drafts, Settings, Queue, Printer, () => Signals++, Time, TimeZoneInfo.Utc);
    }
}

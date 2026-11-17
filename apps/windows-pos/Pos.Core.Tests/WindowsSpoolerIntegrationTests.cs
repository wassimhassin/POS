using Pos.Core.Printing;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Exercise the REAL Windows print spooler (no physical printer required). These validate the
/// winspool interop and error paths — not thermal hardware output.
///
/// Opt-in hardware test: set POS_TEST_PRINTER to the name of an installed ESC/POS printer to send
/// a real sample receipt (paper must then be checked by a person).
/// </summary>
public sealed class WindowsSpoolerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void Discovers_installed_printers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var printers = new WindowsSpoolerPrinter().ListPrinters();

        foreach (var p in printers)
        {
            output.WriteLine($"{p.Name} | default={p.IsDefault} | available={p.IsAvailable} | {p.Status} | port={p.PortName} | driver={p.DriverName}");
        }

        Assert.All(printers, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.True(printers.Count(p => p.IsDefault) <= 1);
    }

    [Fact]
    public void Missing_printer_is_reported_as_not_sent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = new WindowsSpoolerPrinter().PrintRaw($"POS missing printer {Guid.NewGuid():N}", "POS test", [0x1B, 0x40]);

        Assert.Equal(PrintOutcome.NotSent, result.Outcome);
        Assert.Contains("introuvable", result.Error, StringComparison.Ordinal);
        output.WriteLine(result.Error);
    }

    [Fact]
    public void Raw_job_through_the_spooler_to_a_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // "Microsoft Print to PDF" ships with Windows; with an output file the spooler writes the job
        // there instead of prompting. Its v4 driver may legitimately refuse the RAW datatype — either
        // way the outcome must be a clean NotSent or a Sent, never an exception or Uncertain.
        const string printer = "Microsoft Print to PDF";
        if (!new WindowsSpoolerPrinter().ListPrinters().Any(p => p.Name == printer))
        {
            output.WriteLine($"'{printer}' not installed: skipped.");
            return;
        }

        var outputFile = Path.Combine(Path.GetTempPath(), $"pos-spool-{Guid.NewGuid():N}.prn");
        try
        {
            var bytes = TicketRenderer.ToEscPos([new TicketLine("POS spooler test")]);
            var result = WindowsSpoolerPrinter.PrintRaw(printer, "POS spooler test", bytes, outputFile);
            output.WriteLine($"Outcome={result.Outcome} SpoolerJob={result.SpoolerJobId} Error={result.Error}");
            Assert.True(result.Outcome is PrintOutcome.Sent or PrintOutcome.NotSent, result.Error);

            if (result.Outcome == PrintOutcome.Sent)
            {
                // The spooler despools asynchronously: wait briefly for the output file (informational only).
                for (var i = 0; i < 50 && !(File.Exists(outputFile) && new FileInfo(outputFile).Length > 0); i++)
                {
                    Thread.Sleep(100);
                }

                var written = File.Exists(outputFile) ? File.ReadAllBytes(outputFile) : [];
                output.WriteLine($"Spooled file: {written.Length} bytes; identical to sent bytes: {written.AsSpan().SequenceEqual(bytes)}");
            }
        }
        finally
        {
            File.Delete(outputFile);
        }
    }

    [Fact]
    public void Hardware_sample_receipt_when_POS_TEST_PRINTER_is_set()
    {
        var printer = Environment.GetEnvironmentVariable("POS_TEST_PRINTER");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(printer))
        {
            output.WriteLine("POS_TEST_PRINTER not set: hardware test skipped.");
            return;
        }

        var width = int.TryParse(Environment.GetEnvironmentVariable("POS_TEST_PAPER_MM"), out var w) ? w : 80;
        var source = new TicketSource(TicketLayout.ReceiptKind, "HWTEST", DateTimeOffset.UtcNow, null,
            [new TicketItem("Café express", 2, 2_500, 5_000), new TicketItem("Crêpe chocolat", 1, 8_000, 8_000)], 3, 13_000, "TND");
        var bytes = TicketRenderer.ToEscPos(TicketLayout.Build(source, width, 0, TimeZoneInfo.Local));

        var result = new WindowsSpoolerPrinter().PrintRaw(printer, "POS hardware test", bytes);

        output.WriteLine($"Outcome={result.Outcome} SpoolerJob={result.SpoolerJobId} Error={result.Error}");
        Assert.Equal(PrintOutcome.Sent, result.Outcome);
    }
}

namespace Pos.Core.Printing;

/// <summary>Access to installed printers. Implemented by the Windows spooler; faked in tests.</summary>
public interface IPrinterGateway
{
    IReadOnlyList<PrinterInfo> ListPrinters();

    /// <summary>Sends raw bytes as one print job. Must not throw; failures are described by the result.</summary>
    PrintAttemptResult PrintRaw(string printerName, string documentName, byte[] data);
}

/// <param name="IsAvailable">False when Windows reports the printer offline, paused, in error, out of paper, etc.
/// Many USB thermal drivers do NOT report these states reliably; true does not guarantee printing.</param>
public sealed record PrinterInfo(string Name, bool IsDefault, bool IsAvailable, string Status, string? PortName, string? DriverName);

public enum PrintOutcome
{
    /// <summary>Nothing reached the spooler. Safe to retry automatically.</summary>
    NotSent,

    /// <summary>The Windows spooler accepted the complete job. Physical printing is NOT confirmed.</summary>
    Sent,

    /// <summary>The attempt failed after data may have reached the printer. Never retried automatically.</summary>
    Uncertain,
}

public sealed record PrintAttemptResult(PrintOutcome Outcome, string? Error = null, int? SpoolerJobId = null)
{
    public static PrintAttemptResult NotSent(string error) => new(PrintOutcome.NotSent, error);

    public static PrintAttemptResult Uncertain(string error, int? jobId = null) => new(PrintOutcome.Uncertain, error, jobId);

    public static PrintAttemptResult Sent(int jobId) => new(PrintOutcome.Sent, null, jobId);
}

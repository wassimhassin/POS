using Microsoft.Extensions.Logging;

namespace Pos.Core.Printing;

/// <summary>
/// Processes due jobs one at a time: claim (→ printing) → render → spooler → record outcome.
/// Single consumer by design; run <see cref="RunAsync"/> on one background task.
/// </summary>
public sealed partial class PrintWorker(
    PrintQueue queue,
    PrintSettingsRepository settings,
    IPrinterGateway printer,
    ILogger<PrintWorker> logger,
    TimeZoneInfo? timeZone = null) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;

    /// <summary>Wakes the worker immediately (e.g. after enqueue or manual retry).</summary>
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ProcessDue();
            }
#pragma warning disable CA1031 // The worker must survive unexpected failures (e.g. transient SQLite errors).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWorkerError(logger, ex);
            }

            try
            {
                await _signal.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Attempts every job that is due now. Returns the number of attempts made.</summary>
    public int ProcessDue(int maxJobs = 50)
    {
        var processed = 0;
        while (processed < maxJobs && queue.ClaimNext() is { } job)
        {
            processed++;
            Attempt(job);
        }

        return processed;
    }

    private void Attempt(ClaimedJob job)
    {
        string? printerName = null;
        PrintAttemptResult result;
        try
        {
            var destination = settings.GetDestination(job.DestinationId);
            printerName = destination.PrinterName;
            if (printerName is null)
            {
                result = PrintAttemptResult.NotSent($"Aucune imprimante configurée pour « {destination.Name} ».");
            }
            else
            {
                var bytes = TicketRenderer.ToEscPos(TicketLayout.Build(job.Source, job.PaperWidthMm, job.CopyNumber, _timeZone));
                result = SafePrint(printerName, DocumentName(job), bytes);
            }
        }
#pragma warning disable CA1031 // Failure before contacting the printer: nothing was sent.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPrepareError(logger, ex, job.Id);
            result = PrintAttemptResult.NotSent($"Préparation du ticket impossible : {ex.Message}");
        }

        queue.RecordResult(job, printerName, result);
    }

    private PrintAttemptResult SafePrint(string printerName, string documentName, byte[] bytes)
    {
        try
        {
            return printer.PrintRaw(printerName, documentName, bytes);
        }
#pragma warning disable CA1031 // We cannot know how far an unexpected failure got: treat as uncertain.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPrinterException(logger, ex, printerName);
            return PrintAttemptResult.Uncertain($"Erreur inattendue pendant l'envoi : {ex.Message}");
        }
    }

    private static string DocumentName(ClaimedJob job)
    {
        var what = job.Kind == TicketLayout.ReceiptKind ? "Ticket client" : $"Bon {job.Source.StationName}";
        var copy = job.CopyNumber > 0 ? $" (copie {job.CopyNumber})" : string.Empty;
        return $"POS {what} #{job.OrderNumber}{copy}";
    }

    public void Dispose() => _signal.Dispose();

    [LoggerMessage(Level = LogLevel.Error, Message = "Print worker loop failed")]
    private static partial void LogWorkerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Print job {JobId} could not be prepared")]
    private static partial void LogPrepareError(ILogger logger, Exception exception, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Printer gateway threw while printing to {PrinterName}")]
    private static partial void LogPrinterException(ILogger logger, Exception exception, string printerName);
}

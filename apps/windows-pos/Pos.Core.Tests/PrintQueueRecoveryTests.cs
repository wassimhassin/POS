using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Data;
using Pos.Core.Printing;

namespace Pos.Core.Tests;

/// <summary>Durable queue behaviour: states, retries, restarts, unavailable printers, uncertain outcomes.</summary>
public sealed class PrintQueueRecoveryTests : IDisposable
{
    private readonly PrintFixture _f = new();

    public void Dispose() => _f.Dispose();

    private PrintJob EnqueueReceipt(params (string, int)[] lines) =>
        Assert.Single(_f.Service.EnqueueReceipt(_f.Order(lines.Length == 0 ? [("p-pizza", 1)] : lines).Id, false).Jobs);

    private string[] Events(string jobId) => _f.Queue.Events(jobId).Select(e => e.Event).ToArray();

    [Fact]
    public void Successful_attempt_is_recorded_as_sent_to_spooler_with_event_trail()
    {
        var job = EnqueueReceipt();

        Assert.Equal(1, _f.Worker.ProcessDue());

        var sent = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Sent, sent.Status);
        Assert.Equal(1, sent.Attempts);
        Assert.Equal(100, sent.SpoolerJobId);
        Assert.Equal("Thermal 80", sent.LastPrinter);
        Assert.NotNull(sent.SentAt);
        Assert.Equal(["enqueued", "attempt_started", "attempt_sent"], Events(job.Id));
        Assert.Equal(0, _f.Worker.ProcessDue()); // never re-sent
    }

    [Fact]
    public void Unavailable_printer_keeps_the_job_queued_with_backoff()
    {
        _f.Printer.Unavailable("Thermal 80");
        var job = EnqueueReceipt();

        _f.Worker.ProcessDue();

        var queued = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Queued, queued.Status);
        Assert.Equal(1, queued.Attempts);
        Assert.Contains("Hors ligne", queued.LastError, StringComparison.Ordinal);
        Assert.Equal(_f.Time.Now.AddSeconds(5), DateTimeOffset.Parse(queued.NextAttemptAt!, System.Globalization.CultureInfo.InvariantCulture));

        _f.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, _f.Worker.ProcessDue()); // still waiting
        _f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _f.Worker.ProcessDue());
        Assert.Equal(2, _f.Queue.Get(job.Id)!.Attempts);
        Assert.Empty(_f.Printer.Printed);
    }

    [Fact]
    public void Queued_jobs_survive_restart_and_print_once_the_printer_is_back()
    {
        _f.Printer.Unavailable("Thermal 80");
        var job = EnqueueReceipt(("p-crepe", 2));
        _f.Worker.ProcessDue();

        _f.Restart();
        _f.Printer.Healthy("Thermal 80");
        _f.Time.Advance(TimeSpan.FromMinutes(1));
        _f.Worker.ProcessDue();

        var sent = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Sent, sent.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.Single(_f.Printer.Printed);
        Assert.Equal(["enqueued", "attempt_started", "attempt_notsent", "attempt_started", "attempt_sent"], Events(job.Id));
    }

    [Fact]
    public void Automatic_retries_stop_after_the_limit_and_manual_retry_resumes()
    {
        _f.Printer.Unavailable("Thermal 80");
        var job = EnqueueReceipt();
        for (var i = 0; i < PrintQueue.MaxAutoAttempts + 2; i++)
        {
            _f.Worker.ProcessDue();
            _f.Time.Advance(TimeSpan.FromMinutes(2));
        }

        var failed = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Failed, failed.Status);
        Assert.Equal(PrintQueue.MaxAutoAttempts, failed.Attempts);
        Assert.Null(failed.NextAttemptAt);

        _f.Printer.Healthy("Thermal 80");
        var retried = _f.Service.Retry(job.Id);
        Assert.Equal(PrintJobStatus.Queued, retried.Status);
        _f.Worker.ProcessDue();

        var sent = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Sent, sent.Status);
        Assert.Equal(PrintQueue.MaxAutoAttempts + 1, sent.Attempts);
        Assert.Contains("manual_retry", Events(job.Id));
    }

    [Fact]
    public void Destination_without_printer_is_not_sent()
    {
        _f.Settings.UpdateDestination("receipt", null, 80);
        var job = EnqueueReceipt();

        _f.Worker.ProcessDue();

        var queued = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Queued, queued.Status);
        Assert.Contains("Aucune imprimante configurée", queued.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_follow_the_destination_printer_reassigned_after_enqueue()
    {
        _f.Printer.Unavailable("Thermal 80");
        var job = EnqueueReceipt();
        _f.Worker.ProcessDue();

        _f.Settings.UpdateDestination("receipt", "Thermal 58", 80); // replacement printer
        _f.Time.Advance(TimeSpan.FromMinutes(1));
        _f.Worker.ProcessDue();

        Assert.Equal("Thermal 58", _f.Queue.Get(job.Id)!.LastPrinter);
        Assert.Equal(PrintJobStatus.Sent, _f.Queue.Get(job.Id)!.Status);
    }

    [Fact]
    public void Uncertain_outcome_is_never_retried_automatically()
    {
        _f.Printer.Uncertain("Thermal 80");
        var job = EnqueueReceipt();

        _f.Worker.ProcessDue();
        _f.Time.Advance(TimeSpan.FromHours(1));
        _f.Worker.ProcessDue();

        var uncertain = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Uncertain, uncertain.Status);
        Assert.Equal(1, uncertain.Attempts);
        Assert.Equal(7, uncertain.SpoolerJobId);
        Assert.Equal("retry_not_allowed", Assert.Throws<PosValidationException>(() => _f.Service.Retry(job.Id)).Code);
    }

    [Fact]
    public void Printer_exception_is_treated_as_uncertain()
    {
        _f.Printer.Throws("Thermal 80");
        var job = EnqueueReceipt();

        _f.Worker.ProcessDue();

        var uncertain = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Uncertain, uncertain.Status);
        Assert.Contains("USB cable pulled", uncertain.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Crash_during_printing_becomes_uncertain_after_restart()
    {
        var job = EnqueueReceipt();
        Assert.NotNull(_f.Queue.ClaimNext()); // attempt started …and the process dies before recording the outcome

        _f.Restart();
        Assert.Equal(1, _f.Queue.RecoverInterrupted());
        _f.Worker.ProcessDue();

        var recovered = _f.Queue.Get(job.Id)!;
        Assert.Equal(PrintJobStatus.Uncertain, recovered.Status);
        Assert.Contains("résultat inconnu", recovered.LastError, StringComparison.Ordinal);
        Assert.Empty(_f.Printer.Printed);
        Assert.Equal(["enqueued", "attempt_started", "recovered_uncertain"], Events(job.Id));
    }

    [Fact]
    public void Reprint_requires_confirmation_and_creates_a_marked_duplicate()
    {
        var job = EnqueueReceipt();
        _f.Worker.ProcessDue();

        Assert.Equal("duplicate_confirmation_required", Assert.Throws<PosValidationException>(() => _f.Service.Reprint(job.Id, false)).Code);

        var reprint = Assert.Single(_f.Service.Reprint(job.Id, confirmDuplicate: true).Jobs);
        Assert.Equal(job.Id, reprint.ReprintOf);
        Assert.Equal(1, reprint.CopyNumber);
        _f.Worker.ProcessDue();

        Assert.Equal(2, _f.Printer.Printed.Count);
        Assert.Contains("(copie 1)", _f.Printer.Printed[1].Document, StringComparison.Ordinal);
        Assert.Contains("DUPLICATA n°1", TicketRenderer.CreateEncoding().GetString(_f.Printer.Printed[1].Data), StringComparison.Ordinal);
        Assert.Equal(PrintJobStatus.Sent, _f.Queue.Get(job.Id)!.Status); // original untouched

        var second = Assert.Single(_f.Service.Reprint(job.Id, true).Jobs);
        Assert.Equal(2, second.CopyNumber);
    }

    [Fact]
    public void Uncertain_job_can_be_reprinted_as_duplicate()
    {
        _f.Printer.Uncertain("Thermal 80");
        var job = EnqueueReceipt();
        _f.Worker.ProcessDue();
        _f.Printer.Healthy("Thermal 80");

        var reprint = Assert.Single(_f.Service.Reprint(job.Id, true).Jobs);
        _f.Worker.ProcessDue();

        Assert.Equal(PrintJobStatus.Sent, _f.Queue.Get(reprint.Id)!.Status);
        Assert.Equal(1, reprint.CopyNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reprint_is_refused_for_jobs_that_never_printed(bool exhaustRetries)
    {
        _f.Printer.Unavailable("Thermal 80");
        var job = EnqueueReceipt();
        if (exhaustRetries)
        {
            for (var i = 0; i < PrintQueue.MaxAutoAttempts; i++)
            {
                _f.Worker.ProcessDue();
                _f.Time.Advance(TimeSpan.FromMinutes(2));
            }
        }

        Assert.Equal("reprint_not_allowed", Assert.Throws<PosValidationException>(() => _f.Service.Reprint(job.Id, true)).Code);
    }

    [Fact]
    public void Retry_is_refused_for_sent_jobs()
    {
        var job = EnqueueReceipt();
        _f.Worker.ProcessDue();

        Assert.Equal("retry_not_allowed", Assert.Throws<PosValidationException>(() => _f.Service.Retry(job.Id)).Code);
    }

    [Fact]
    public void Jobs_for_one_printer_stay_in_order_while_other_printers_continue()
    {
        _f.Printer.Unavailable("Thermal 58"); // bar and counter
        var first = _f.Service.EnqueueStationTickets(_f.Order(("p-espresso", 1), ("p-pizza", 1)).Id, false).Jobs;
        _f.Time.Advance(TimeSpan.FromSeconds(1));
        var second = _f.Service.EnqueueStationTickets(_f.Order(("p-the-menthe", 1)).Id, false).Jobs;

        _f.Worker.ProcessDue();
        _f.Printer.Healthy("Thermal 58");
        _f.Worker.ProcessDue(); // first bar job still in backoff → second bar job must wait behind it

        Assert.Equal(PrintJobStatus.Sent, _f.Queue.Get(first.Single(j => j.DestinationId == "kitchen").Id)!.Status);
        Assert.Equal(PrintJobStatus.Queued, _f.Queue.Get(first.Single(j => j.DestinationId == "bar").Id)!.Status);
        Assert.Equal(PrintJobStatus.Queued, _f.Queue.Get(second.Single().Id)!.Status);
        Assert.Equal(0, _f.Queue.Get(second.Single().Id)!.Attempts);

        _f.Time.Advance(TimeSpan.FromSeconds(5));
        _f.Worker.ProcessDue();
        var barPrints = _f.Printer.Printed.Where(p => p.Printer == "Thermal 58").Select(p => p.Document).ToList();
        Assert.Equal(2, barPrints.Count);
        Assert.Contains(first[0].OrderNumber, barPrints[0], StringComparison.Ordinal);
        Assert.Contains(second[0].OrderNumber, barPrints[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_v1_database_is_upgraded_with_routing_and_keeps_existing_data()
    {
        using var test = new TestDatabase();
        // Recreate a v1 database by hand.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(test.FilePath + suffix);
        }

        var v1 = new PosDatabase(test.FilePath, NullLogger<PosDatabase>.Instance);
        using (var connection = v1.OpenConnection())
        {
            PosDatabase.Execute(connection, null, PosDatabase.MigrationScripts[0]);
            PosDatabase.Execute(connection, null, """
                INSERT INTO products VALUES ('p-old', 'OLD-1', 'Thé vert', 'Boissons chaudes', 2000, 'TND', 0, 1);
                INSERT INTO products VALUES ('p-food', 'OLD-2', 'Brik', 'Salé', 3000, 'TND', 1, 1);
                INSERT INTO draft_orders VALUES ('6f1c1a8e-1111-4222-8333-944445555666', 'TND', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO draft_order_lines VALUES ('6f1c1a8e-1111-4222-8333-944445555666', 1, 'p-old', 'Thé vert', 2000, 2);
                PRAGMA user_version = 1;
                """);
        }

        var upgraded = test.Restart();

        var products = new Catalog.ProductRepository(upgraded).ListActive();
        Assert.Equal("bar", products.Single(p => p.Id == "p-old").StationId);
        Assert.Equal("kitchen", products.Single(p => p.Id == "p-food").StationId);
        Assert.Equal(4_000, new Orders.DraftOrderRepository(upgraded).GetCurrent()!.TotalMinor);
        Assert.Equal(4, new PrintSettingsRepository(upgraded).ListDestinations().Count);
    }
}

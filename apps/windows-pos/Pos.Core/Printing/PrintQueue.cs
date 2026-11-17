using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pos.Core.Data;

namespace Pos.Core.Printing;

/// <summary>
/// Job states:
///   queued    waiting to be attempted (new, or after a failure where nothing reached the spooler)
///   printing  an attempt is in progress
///   sent      the Windows spooler accepted the job — physical printing is NOT confirmed
///   failed    automatic attempts exhausted, nothing was sent; operator may retry
///   uncertain an attempt was interrupted after data may have reached the printer (process crash,
///             write failure); never retried automatically — operator checks paper and may reprint
/// </summary>
public static class PrintJobStatus
{
    public const string Queued = "queued";
    public const string Printing = "printing";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";
}

public sealed record PrintJob(
    string Id,
    string Kind,
    string DestinationId,
    string DestinationName,
    string OrderId,
    string OrderNumber,
    int PaperWidthMm,
    int CopyNumber,
    string? ReprintOf,
    string Status,
    int Attempts,
    string? NextAttemptAt,
    string? LastError,
    string? LastPrinter,
    int? SpoolerJobId,
    string CreatedAt,
    string UpdatedAt,
    string? SentAt);

public sealed record PrintJobEvent(long Id, string At, string Event, string? Detail);

internal sealed record ClaimedJob(string Id, string Kind, string DestinationId, string OrderNumber, TicketSource Source, int PaperWidthMm, int CopyNumber, int AutoAttempts);

internal sealed record NewPrintJob(string Kind, string DestinationId, string OrderId, string OrderNumber, TicketSource Source, int PaperWidthMm, int CopyNumber, string? ReprintOf);

/// <summary>Durable SQLite print queue: every state change is transactional and recorded as an event.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Domain term: it is the print queue.")]
public sealed partial class PrintQueue(PosDatabase database, ILogger<PrintQueue> logger, TimeProvider? timeProvider = null)
{
    public const int MaxAutoAttempts = 5;

    /// <summary>Delay before automatic attempt n+1 after n failed attempts.</summary>
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    internal static readonly JsonSerializerOptions SourceJson = new(JsonSerializerDefaults.Web);

    private const string JobColumns = """
        j.id, j.kind, j.destination_id, d.name, j.order_id, j.order_number, j.paper_width_mm, j.copy_number,
        j.reprint_of, j.status, j.attempts, j.next_attempt_at, j.last_error, j.last_printer, j.spooler_job_id,
        j.created_at, j.updated_at, j.sent_at
        """;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    internal string Enqueue(SqliteConnection connection, SqliteTransaction tx, NewPrintJob job)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = Now();
        using var insert = Command(connection, tx, """
            INSERT INTO print_jobs (id, kind, destination_id, order_id, order_number, source_json, paper_width_mm,
                                    copy_number, reprint_of, status, created_at, updated_at)
            VALUES ($id, $kind, $dest, $order, $number, $source, $width, $copy, $reprintOf, 'queued', $now, $now);
            """);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$kind", job.Kind);
        insert.Parameters.AddWithValue("$dest", job.DestinationId);
        insert.Parameters.AddWithValue("$order", job.OrderId);
        insert.Parameters.AddWithValue("$number", job.OrderNumber);
        insert.Parameters.AddWithValue("$source", JsonSerializer.Serialize(job.Source, SourceJson));
        insert.Parameters.AddWithValue("$width", job.PaperWidthMm);
        insert.Parameters.AddWithValue("$copy", job.CopyNumber);
        insert.Parameters.AddWithValue("$reprintOf", (object?)job.ReprintOf ?? DBNull.Value);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();

        var detail = job.ReprintOf is null ? $"copy={job.CopyNumber}" : $"reprint_of={job.ReprintOf} copy={job.CopyNumber}";
        AddEvent(connection, tx, id, now, "enqueued", detail);
        LogEnqueued(logger, id, job.Kind, job.DestinationId, job.OrderNumber, job.CopyNumber, job.ReprintOf);
        return id;
    }

    /// <summary>Next copy number for an order/kind/destination, ignoring jobs that definitely never printed.</summary>
    internal static int NextCopyNumber(SqliteConnection connection, SqliteTransaction tx, string orderId, string kind, string destinationId)
    {
        using var query = Command(connection, tx, """
            SELECT COALESCE(MAX(copy_number) + 1, 0) FROM print_jobs
            WHERE order_id = $order AND kind = $kind AND destination_id = $dest AND status <> 'failed';
            """);
        query.Parameters.AddWithValue("$order", orderId);
        query.Parameters.AddWithValue("$kind", kind);
        query.Parameters.AddWithValue("$dest", destinationId);
        return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public PrintJob? Get(string id)
    {
        using var connection = database.OpenConnection();
        return Get(connection, null, id);
    }

    public IReadOnlyList<PrintJob> ListRecent(int limit = 50)
    {
        using var connection = database.OpenConnection();
        using var query = Command(connection, null, $"""
            SELECT {JobColumns} FROM print_jobs j JOIN print_destinations d ON d.id = j.destination_id
            ORDER BY j.created_at DESC, j.rowid DESC LIMIT $limit;
            """);
        query.Parameters.AddWithValue("$limit", limit);
        return ReadJobs(query);
    }

    public IReadOnlyList<PrintJobEvent> Events(string jobId)
    {
        using var connection = database.OpenConnection();
        using var query = Command(connection, null, "SELECT id, at, event, detail FROM print_job_events WHERE job_id = $id ORDER BY id;");
        query.Parameters.AddWithValue("$id", jobId);
        using var reader = query.ExecuteReader();
        var events = new List<PrintJobEvent>();
        while (reader.Read())
        {
            events.Add(new PrintJobEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return events;
    }

    internal (TicketSource Source, PrintJob Job) LoadSource(string jobId)
    {
        using var connection = database.OpenConnection();
        var job = Get(connection, null, jobId) ?? throw new PosValidationException("job_not_found", "Travail d'impression introuvable.");
        using var query = Command(connection, null, "SELECT source_json FROM print_jobs WHERE id = $id;");
        query.Parameters.AddWithValue("$id", jobId);
        var source = JsonSerializer.Deserialize<TicketSource>((string)query.ExecuteScalar()!, SourceJson)!;
        return (source, job);
    }

    /// <summary>
    /// Atomically moves the next due job to 'printing'. Jobs for one destination are strictly FIFO:
    /// a job waiting for its retry delay blocks later jobs for the same printer (keeps ticket order).
    /// </summary>
    internal ClaimedJob? ClaimNext()
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        var now = Now();
        using var claim = Command(connection, tx, """
            UPDATE print_jobs
            SET status = 'printing', attempts = attempts + 1, auto_attempts = auto_attempts + 1, updated_at = $now
            WHERE id = (
                SELECT j.id FROM print_jobs j
                WHERE j.status = 'queued' AND (j.next_attempt_at IS NULL OR j.next_attempt_at <= $now)
                  AND NOT EXISTS (
                      SELECT 1 FROM print_jobs e
                      WHERE e.destination_id = j.destination_id AND e.status IN ('queued', 'printing')
                        AND (e.created_at < j.created_at OR (e.created_at = j.created_at AND e.rowid < j.rowid)))
                ORDER BY j.created_at, j.rowid LIMIT 1)
            RETURNING id, kind, destination_id, order_number, source_json, paper_width_mm, copy_number, auto_attempts, attempts;
            """);
        claim.Parameters.AddWithValue("$now", now);

        ClaimedJob? job = null;
        var attempt = 0;
        using (var reader = claim.ExecuteReader())
        {
            if (reader.Read())
            {
                job = new ClaimedJob(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    JsonSerializer.Deserialize<TicketSource>(reader.GetString(4), SourceJson)!,
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7));
                attempt = reader.GetInt32(8);
            }
        }

        if (job is not null)
        {
            AddEvent(connection, tx, job.Id, now, "attempt_started", $"attempt={attempt}");
        }

        tx.Commit();
        return job;
    }

    /// <summary>Records the outcome of an attempt on a job previously claimed.</summary>
    internal void RecordResult(ClaimedJob job, string? printerName, PrintAttemptResult result)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        var now = _time.GetUtcNow();
        var nowText = now.ToString("O", CultureInfo.InvariantCulture);
        string status;
        string? nextAttempt = null;

        switch (result.Outcome)
        {
            case PrintOutcome.Sent:
                status = PrintJobStatus.Sent;
                break;
            case PrintOutcome.Uncertain:
                status = PrintJobStatus.Uncertain;
                break;
            default:
                if (job.AutoAttempts >= MaxAutoAttempts)
                {
                    status = PrintJobStatus.Failed;
                }
                else
                {
                    status = PrintJobStatus.Queued;
                    nextAttempt = now.Add(Backoff[Math.Min(job.AutoAttempts, Backoff.Length) - 1])
                        .ToString("O", CultureInfo.InvariantCulture);
                }

                break;
        }

        using var update = Command(connection, tx, """
            UPDATE print_jobs
            SET status = $status, next_attempt_at = $next, last_error = $error, last_printer = $printer,
                spooler_job_id = COALESCE($spoolerJob, spooler_job_id), updated_at = $now,
                sent_at = CASE WHEN $status = 'sent' THEN $now ELSE sent_at END
            WHERE id = $id AND status = 'printing';
            """);
        update.Parameters.AddWithValue("$id", job.Id);
        update.Parameters.AddWithValue("$status", status);
        update.Parameters.AddWithValue("$next", (object?)nextAttempt ?? DBNull.Value);
        update.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
        update.Parameters.AddWithValue("$printer", (object?)printerName ?? DBNull.Value);
        update.Parameters.AddWithValue("$spoolerJob", (object?)result.SpoolerJobId ?? DBNull.Value);
        update.Parameters.AddWithValue("$now", nowText);
        if (update.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"Print job {job.Id} is no longer in 'printing' state.");
        }

        var detail = result.Outcome == PrintOutcome.Sent
            ? $"printer={printerName} spooler_job={result.SpoolerJobId}"
            : $"printer={printerName} error={result.Error}" + (nextAttempt is null ? string.Empty : $" next_attempt={nextAttempt}");
        AddEvent(connection, tx, job.Id, nowText, $"attempt_{result.Outcome.ToString().ToLowerInvariant()}", detail);
        tx.Commit();

        LogAttempt(logger, job.Id, job.DestinationId, printerName, result.Outcome.ToString(), status, job.AutoAttempts, result.SpoolerJobId, result.Error);
    }

    /// <summary>
    /// Run at startup: a job left in 'printing' means the process stopped mid-attempt. The bytes may or
    /// may not have reached the printer, so the job becomes 'uncertain' (never silently re-sent).
    /// </summary>
    public int RecoverInterrupted()
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        var now = Now();
        var ids = new List<string>();
        using (var select = Command(connection, tx, "SELECT id FROM print_jobs WHERE status = 'printing';"))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            using var update = Command(connection, tx, """
                UPDATE print_jobs SET status = 'uncertain', updated_at = $now,
                    last_error = 'Application interrompue pendant l''impression : résultat inconnu.'
                WHERE id = $id;
                """);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
            AddEvent(connection, tx, id, now, "recovered_uncertain", "process stopped during attempt");
            LogRecovered(logger, id);
        }

        tx.Commit();
        return ids.Count;
    }

    /// <summary>Manual retry of a job that was definitely not sent (failed, or queued waiting for its delay).</summary>
    public PrintJob Retry(string jobId)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        var job = Get(connection, tx, jobId) ?? throw new PosValidationException("job_not_found", "Travail d'impression introuvable.");
        if (job.Status is not (PrintJobStatus.Failed or PrintJobStatus.Queued))
        {
            throw new PosValidationException(
                "retry_not_allowed",
                "Seuls les travaux en échec ou en attente peuvent être relancés. Utilisez « Réimprimer » pour un ticket déjà transmis.");
        }

        var now = Now();
        using var update = Command(connection, tx, """
            UPDATE print_jobs SET status = 'queued', auto_attempts = 0, next_attempt_at = NULL, updated_at = $now
            WHERE id = $id;
            """);
        update.Parameters.AddWithValue("$id", jobId);
        update.Parameters.AddWithValue("$now", now);
        update.ExecuteNonQuery();
        AddEvent(connection, tx, jobId, now, "manual_retry", $"previous_status={job.Status}");
        tx.Commit();
        LogManualRetry(logger, jobId, job.Status);
        return Get(jobId)!;
    }

    private static PrintJob? Get(SqliteConnection connection, SqliteTransaction? tx, string id)
    {
        using var query = Command(connection, tx, $"""
            SELECT {JobColumns} FROM print_jobs j JOIN print_destinations d ON d.id = j.destination_id WHERE j.id = $id;
            """);
        query.Parameters.AddWithValue("$id", id);
        return ReadJobs(query).SingleOrDefault();
    }

    private static List<PrintJob> ReadJobs(SqliteCommand query)
    {
        using var reader = query.ExecuteReader();
        var jobs = new List<PrintJob>();
        while (reader.Read())
        {
            string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            jobs.Add(new PrintJob(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), S(8), reader.GetString(9), reader.GetInt32(10),
                S(11), S(12), S(13), reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.GetString(15), reader.GetString(16), S(17)));
        }

        return jobs;
    }

    private static void AddEvent(SqliteConnection connection, SqliteTransaction tx, string jobId, string at, string evt, string? detail)
    {
        using var insert = Command(connection, tx, "INSERT INTO print_job_events (job_id, at, event, detail) VALUES ($job, $at, $event, $detail);");
        insert.Parameters.AddWithValue("$job", jobId);
        insert.Parameters.AddWithValue("$at", at);
        insert.Parameters.AddWithValue("$event", evt);
        insert.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Print job {JobId} enqueued {Kind} {DestinationId} order {OrderNumber} copy {CopyNumber} reprint of {ReprintOf}")]
    private static partial void LogEnqueued(ILogger logger, string jobId, string kind, string destinationId, string orderNumber, int copyNumber, string? reprintOf);

    [LoggerMessage(Level = LogLevel.Information, Message = "Print job {JobId} attempt on {DestinationId} via {PrinterName}: {Outcome} → {Status} (auto attempt {AutoAttempt}, spooler job {SpoolerJobId}) {Error}")]
    private static partial void LogAttempt(ILogger logger, string jobId, string destinationId, string? printerName, string outcome, string status, int autoAttempt, int? spoolerJobId, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Print job {JobId} was interrupted mid-attempt; marked uncertain")]
    private static partial void LogRecovered(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Print job {JobId} manually retried from {PreviousStatus}")]
    private static partial void LogManualRetry(ILogger logger, string jobId, string previousStatus);
}

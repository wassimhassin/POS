using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Orders;

namespace Pos.Core.Printing;

/// <param name="UnroutedItems">Names of products with no preparation station (no ticket printed for them).</param>
public sealed record EnqueueResult(IReadOnlyList<PrintJob> Jobs, IReadOnlyList<string> UnroutedItems);

public sealed record PrintPreview(string JobId, int PaperWidthMm, string Text);

/// <summary>
/// Printing use cases exposed to the UI. Ticket content is always built from data persisted in
/// SQLite (the draft order), never from values supplied by the UI.
/// </summary>
public sealed class PrintService(
    PosDatabase database,
    DraftOrderRepository drafts,
    PrintSettingsRepository settings,
    PrintQueue queue,
    IPrinterGateway printers,
    Action signalWorker,
    TimeProvider? timeProvider = null,
    TimeZoneInfo? timeZone = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;

    public IReadOnlyList<PrinterInfo> ListPrinters() => printers.ListPrinters();

    public IReadOnlyList<PrintDestination> ListDestinations() => settings.ListDestinations();

    public PrintDestination UpdateDestination(string id, string? printerName, int paperWidthMm) =>
        settings.UpdateDestination(id, printerName, paperWidthMm);

    public void SetProductStation(string productId, string? stationId) => settings.SetProductStation(productId, stationId);

    public IReadOnlyList<PrintJob> ListJobs() => queue.ListRecent();

    public EnqueueResult EnqueueReceipt(string orderId, bool confirmDuplicate)
    {
        var draft = LoadDraft(orderId);
        var source = new TicketSource(TicketLayout.ReceiptKind, OrderNumber(draft.Id), _time.GetUtcNow(), null,
            draft.Lines.Select(l => new TicketItem(l.Name, l.Quantity, l.UnitPriceMinor, l.LineTotalMinor)).ToList(),
            draft.ItemCount, draft.TotalMinor, draft.Currency);

        return InTransaction((connection, tx) =>
        {
            var destination = PrintSettingsRepository.GetDestination(connection, tx, PrintSettingsRepository.ReceiptDestinationId);
            var copy = CheckDuplicate(connection, tx, draft.Id, TicketLayout.ReceiptKind, destination, confirmDuplicate);
            var id = queue.Enqueue(connection, tx, new NewPrintJob(
                TicketLayout.ReceiptKind, destination.Id, draft.Id, source.OrderNumber, source, destination.PaperWidthMm, copy, null));
            return ([id], []);
        });
    }

    /// <summary>One ticket per preparation station that has items in the order (routing from SQLite).</summary>
    public EnqueueResult EnqueueStationTickets(string orderId, bool confirmDuplicate)
    {
        var draft = LoadDraft(orderId);
        var orderedAt = _time.GetUtcNow();

        return InTransaction((connection, tx) =>
        {
            var routing = LoadRouting(connection, tx, draft.Lines.Select(l => l.ProductId));
            var stations = settings.ListDestinations().Where(d => d.Kind == TicketLayout.StationKind).ToList();
            var unrouted = draft.Lines.Where(l => routing.GetValueOrDefault(l.ProductId) is null).Select(l => l.Name).ToList();

            // Validate every station before enqueuing anything: all tickets or none.
            var planned = new List<(PrintDestination Station, List<DraftLine> Lines, int Copy)>();
            foreach (var station in stations)
            {
                var lines = draft.Lines.Where(l => routing.GetValueOrDefault(l.ProductId) == station.Id).ToList();
                if (lines.Count > 0)
                {
                    planned.Add((station, lines, CheckDuplicate(connection, tx, draft.Id, TicketLayout.StationKind, station, confirmDuplicate)));
                }
            }

            if (planned.Count == 0)
            {
                throw new PosValidationException("nothing_to_route", "Aucun article de la commande n'est routé vers un poste de préparation.");
            }

            var ids = new List<string>();
            foreach (var (station, lines, copy) in planned)
            {
                var source = new TicketSource(TicketLayout.StationKind, OrderNumber(draft.Id), orderedAt, station.Name,
                    lines.Select(l => new TicketItem(l.Name, l.Quantity, l.UnitPriceMinor, l.LineTotalMinor)).ToList(),
                    lines.Sum(l => l.Quantity), lines.Sum(l => l.LineTotalMinor), draft.Currency);
                ids.Add(queue.Enqueue(connection, tx, new NewPrintJob(
                    TicketLayout.StationKind, station.Id, draft.Id, source.OrderNumber, source, station.PaperWidthMm, copy, null)));
            }

            return (ids, unrouted);
        });
    }

    /// <summary>
    /// Reprints a job that was sent or whose outcome is uncertain, as a new job clearly marked as a
    /// duplicate. Requires explicit confirmation because the original may already be on paper.
    /// </summary>
    public EnqueueResult Reprint(string jobId, bool confirmDuplicate)
    {
        var (source, original) = queue.LoadSource(jobId);
        if (original.Status is not (PrintJobStatus.Sent or PrintJobStatus.Uncertain))
        {
            throw new PosValidationException(
                "reprint_not_allowed",
                "Seuls les tickets transmis ou au résultat incertain peuvent être réimprimés. Utilisez « Réessayer » pour un échec.");
        }

        if (!confirmDuplicate)
        {
            throw DuplicateConfirmationRequired();
        }

        return InTransaction((connection, tx) =>
        {
            var destination = PrintSettingsRepository.GetDestination(connection, tx, original.DestinationId);
            var copy = Math.Max(1, PrintQueue.NextCopyNumber(connection, tx, original.OrderId, original.Kind, destination.Id));
            var id = queue.Enqueue(connection, tx, new NewPrintJob(
                original.Kind, destination.Id, original.OrderId, original.OrderNumber, source, destination.PaperWidthMm, copy, original.Id));
            return ([id], []);
        });
    }

    public PrintJob Retry(string jobId)
    {
        var job = queue.Retry(jobId);
        signalWorker();
        return job;
    }

    public PrintPreview Preview(string jobId)
    {
        var (source, job) = queue.LoadSource(jobId);
        var lines = TicketLayout.Build(source, job.PaperWidthMm, job.CopyNumber, _timeZone);
        return new PrintPreview(job.Id, job.PaperWidthMm, TicketRenderer.ToText(lines, job.PaperWidthMm));
    }

    internal static string OrderNumber(string orderId) => orderId.Replace("-", string.Empty, StringComparison.Ordinal)[..6].ToUpperInvariant();

    private DraftOrder LoadDraft(string orderId) =>
        drafts.Get(orderId) ?? throw new PosValidationException("draft_not_found", "La commande en cours n'existe plus.");

    private static int CheckDuplicate(SqliteConnection connection, SqliteTransaction tx, string orderId, string kind, PrintDestination destination, bool confirmDuplicate)
    {
        var copy = PrintQueue.NextCopyNumber(connection, tx, orderId, kind, destination.Id);
        if (copy > 0 && !confirmDuplicate)
        {
            throw DuplicateConfirmationRequired();
        }

        return copy;
    }

    private static PosValidationException DuplicateConfirmationRequired() => new(
        "duplicate_confirmation_required",
        "Ce ticket a peut-être déjà été imprimé. Une nouvelle impression produira un DOUBLON marqué comme tel.");

    private static Dictionary<string, string?> LoadRouting(SqliteConnection connection, SqliteTransaction tx, IEnumerable<string> productIds)
    {
        var routing = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT station_id FROM products WHERE id = $id;";
        var id = query.Parameters.Add("$id", SqliteType.Text);
        foreach (var productId in productIds.Distinct(StringComparer.Ordinal))
        {
            id.Value = productId;
            routing[productId] = query.ExecuteScalar() as string;
        }

        return routing;
    }

    private EnqueueResult InTransaction(Func<SqliteConnection, SqliteTransaction, (IReadOnlyList<string> Ids, IReadOnlyList<string> Unrouted)> work)
    {
        IReadOnlyList<string> ids;
        IReadOnlyList<string> unrouted;
        using (var connection = database.OpenConnection())
        using (var tx = connection.BeginTransaction())
        {
            (ids, unrouted) = work(connection, tx);
            tx.Commit();
        }

        signalWorker();
        return new EnqueueResult(ids.Select(id => queue.Get(id)!).ToList(), unrouted);
    }
}

using Microsoft.Data.Sqlite;
using Pos.Core.Data;

namespace Pos.Core.Printing;

/// <param name="Kind">"receipt" (customer receipt) or "station" (preparation: kitchen, bar, counter).</param>
public sealed record PrintDestination(string Id, string Name, string Kind, string? PrinterName, int PaperWidthMm);

/// <summary>Configurable printer assignment, paper width and product → station routing.</summary>
public sealed class PrintSettingsRepository(PosDatabase database)
{
    public const string ReceiptDestinationId = "receipt";
    private const int MaxPrinterNameLength = 256;

    public IReadOnlyList<PrintDestination> ListDestinations()
    {
        using var connection = database.OpenConnection();
        return Query(connection, null, null);
    }

    public PrintDestination GetDestination(string id)
    {
        using var connection = database.OpenConnection();
        return GetDestination(connection, null, id);
    }

    internal static PrintDestination GetDestination(SqliteConnection connection, SqliteTransaction? tx, string id) =>
        Query(connection, tx, id).SingleOrDefault()
        ?? throw new PosValidationException("unknown_destination", "Destination d'impression inconnue.");

    public PrintDestination UpdateDestination(string id, string? printerName, int paperWidthMm)
    {
        if (!TicketLayout.SupportedPaperWidths.Contains(paperWidthMm))
        {
            throw new PosValidationException("invalid_paper_width", "Largeur de papier non prise en charge (58 ou 80 mm).");
        }

        if (printerName is not null && (string.IsNullOrWhiteSpace(printerName) || printerName.Length > MaxPrinterNameLength))
        {
            throw new PosValidationException("invalid_printer", "Nom d'imprimante invalide.");
        }

        using var connection = database.OpenConnection();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE print_destinations SET printer_name = $printer, paper_width_mm = $width WHERE id = $id;";
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$printer", (object?)printerName ?? DBNull.Value);
        update.Parameters.AddWithValue("$width", paperWidthMm);
        if (update.ExecuteNonQuery() == 0)
        {
            throw new PosValidationException("unknown_destination", "Destination d'impression inconnue.");
        }

        return GetDestination(connection, null, id);
    }

    /// <summary>Routes a product to a preparation station; null removes routing (no ticket printed).</summary>
    public void SetProductStation(string productId, string? stationId)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        if (stationId is not null && GetDestination(connection, tx, stationId).Kind != TicketLayout.StationKind)
        {
            throw new PosValidationException("invalid_station", "Seuls les postes de préparation peuvent recevoir des produits.");
        }

        using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE products SET station_id = $station WHERE id = $id;";
        update.Parameters.AddWithValue("$id", productId);
        update.Parameters.AddWithValue("$station", (object?)stationId ?? DBNull.Value);
        if (update.ExecuteNonQuery() == 0)
        {
            throw new PosValidationException("unknown_product", "Produit inconnu.");
        }

        tx.Commit();
    }

    private static List<PrintDestination> Query(SqliteConnection connection, SqliteTransaction? tx, string? id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT id, name, kind, printer_name, paper_width_mm FROM print_destinations
            WHERE $id IS NULL OR id = $id ORDER BY sort_order;
            """;
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<PrintDestination>();
        while (reader.Read())
        {
            result.Add(new PrintDestination(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4)));
        }

        return result;
    }
}

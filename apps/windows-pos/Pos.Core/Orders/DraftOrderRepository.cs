using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;

namespace Pos.Core.Orders;

public sealed record DraftLineInput(string ProductId, int Quantity);

public sealed record DraftLine(string ProductId, string Name, long UnitPriceMinor, int Quantity, long LineTotalMinor);

public sealed record DraftOrder(
    string Id,
    string Currency,
    IReadOnlyList<DraftLine> Lines,
    int ItemCount,
    long TotalMinor,
    string UpdatedAt);

/// <summary>
/// Persists the terminal's in-progress (draft) order. Prices and names are always read from the
/// local catalog inside the write transaction; the UI only supplies product IDs and quantities.
/// </summary>
public sealed class DraftOrderRepository(PosDatabase database, TimeProvider? timeProvider = null)
{
    public const int MaxLines = 100;
    public const int MaxQuantity = 999;
    private const int MaxProductIdLength = 64;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Returns the most recently updated draft, or null when none exists.</summary>
    public DraftOrder? GetCurrent()
    {
        using var connection = database.OpenConnection();
        var id = PosDatabase.Scalar(connection, null,
            "SELECT id FROM draft_orders ORDER BY updated_at DESC, rowid DESC LIMIT 1;") as string;
        return id is null ? null : Load(connection, null, id);
    }

    /// <summary>Returns the draft with the given ID, or null.</summary>
    public DraftOrder? Get(string orderId)
    {
        ValidateOrderId(orderId);
        using var connection = database.OpenConnection();
        return Load(connection, null, orderId);
    }

    /// <summary>
    /// Creates (orderId == null) or fully replaces the lines of a draft, atomically.
    /// </summary>
    public DraftOrder Save(string? orderId, IReadOnlyList<DraftLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ValidateLines(lines);
        if (orderId is not null)
        {
            ValidateOrderId(orderId);
        }

        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();

        var priced = PriceLines(connection, tx, lines, out var currency);
        var now = _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        if (orderId is null)
        {
            orderId = Guid.NewGuid().ToString("D");
            using var insert = Command(connection, tx,
                "INSERT INTO draft_orders (id, currency, created_at, updated_at) VALUES ($id, $currency, $now, $now);");
            insert.Parameters.AddWithValue("$id", orderId);
            insert.Parameters.AddWithValue("$currency", currency);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = Command(connection, tx,
                "UPDATE draft_orders SET currency = $currency, updated_at = $now WHERE id = $id;");
            update.Parameters.AddWithValue("$id", orderId);
            update.Parameters.AddWithValue("$currency", currency);
            update.Parameters.AddWithValue("$now", now);
            if (update.ExecuteNonQuery() == 0)
            {
                throw new PosValidationException("draft_not_found", "La commande en cours n'existe plus.");
            }

            using var deleteLines = Command(connection, tx, "DELETE FROM draft_order_lines WHERE order_id = $id;");
            deleteLines.Parameters.AddWithValue("$id", orderId);
            deleteLines.ExecuteNonQuery();
        }

        using (var insertLine = Command(connection, tx, """
            INSERT INTO draft_order_lines (order_id, line_no, product_id, product_name, unit_price_minor, quantity)
            VALUES ($order, $lineNo, $product, $name, $price, $qty);
            """))
        {
            var lineNo = insertLine.Parameters.Add("$lineNo", SqliteType.Integer);
            var product = insertLine.Parameters.Add("$product", SqliteType.Text);
            var name = insertLine.Parameters.Add("$name", SqliteType.Text);
            var price = insertLine.Parameters.Add("$price", SqliteType.Integer);
            var qty = insertLine.Parameters.Add("$qty", SqliteType.Integer);
            insertLine.Parameters.AddWithValue("$order", orderId);

            for (var i = 0; i < priced.Count; i++)
            {
                lineNo.Value = i + 1;
                product.Value = priced[i].ProductId;
                name.Value = priced[i].Name;
                price.Value = priced[i].UnitPriceMinor;
                qty.Value = priced[i].Quantity;
                insertLine.ExecuteNonQuery();
            }
        }

        tx.Commit();
        // Read back what was committed, so the response reflects durable state.
        return Load(connection, null, orderId)!;
    }

    /// <summary>Deletes a draft and its lines. Returns false when it did not exist.</summary>
    public bool Delete(string orderId)
    {
        ValidateOrderId(orderId);
        using var connection = database.OpenConnection();
        using var delete = Command(connection, null, "DELETE FROM draft_orders WHERE id = $id;");
        delete.Parameters.AddWithValue("$id", orderId);
        return delete.ExecuteNonQuery() > 0;
    }

    private static void ValidateOrderId(string orderId)
    {
        if (!Guid.TryParseExact(orderId, "D", out _))
        {
            throw new PosValidationException("invalid_order_id", "Identifiant de commande invalide.");
        }
    }

    private static void ValidateLines(IReadOnlyList<DraftLineInput> lines)
    {
        if (lines.Count is 0 or > MaxLines)
        {
            throw new PosValidationException("invalid_lines", $"Une commande doit contenir entre 1 et {MaxLines} lignes.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.ProductId) || line.ProductId.Length > MaxProductIdLength)
            {
                throw new PosValidationException("invalid_product", "Produit invalide.");
            }

            if (line.Quantity is < 1 or > MaxQuantity)
            {
                throw new PosValidationException("invalid_quantity", $"La quantité doit être comprise entre 1 et {MaxQuantity}.");
            }

            if (!seen.Add(line.ProductId))
            {
                throw new PosValidationException("duplicate_product", "Un produit ne peut apparaître qu'une fois par commande.");
            }
        }
    }

    private static List<DraftLine> PriceLines(
        SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<DraftLineInput> lines, out string currency)
    {
        using var lookup = Command(connection, tx,
            "SELECT name, price_minor, currency FROM products WHERE id = $id AND is_active = 1;");
        var idParam = lookup.Parameters.Add("$id", SqliteType.Text);

        string? orderCurrency = null;
        var priced = new List<DraftLine>(lines.Count);
        foreach (var line in lines)
        {
            idParam.Value = line.ProductId;
            using var reader = lookup.ExecuteReader();
            if (!reader.Read())
            {
                throw new PosValidationException("unknown_product", "Un produit de la commande n'est plus disponible.");
            }

            var name = reader.GetString(0);
            var price = reader.GetInt64(1);
            var productCurrency = reader.GetString(2);
            orderCurrency ??= productCurrency;
            if (!string.Equals(orderCurrency, productCurrency, StringComparison.Ordinal))
            {
                throw new PosValidationException("mixed_currency", "Les produits d'une commande doivent avoir la même devise.");
            }

            priced.Add(new DraftLine(line.ProductId, name, price, line.Quantity, checked(price * line.Quantity)));
        }

        currency = orderCurrency!;
        return priced;
    }

    private static DraftOrder? Load(SqliteConnection connection, SqliteTransaction? tx, string orderId)
    {
        using var header = Command(connection, tx, "SELECT currency, updated_at FROM draft_orders WHERE id = $id;");
        header.Parameters.AddWithValue("$id", orderId);
        string currency, updatedAt;
        using (var reader = header.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            currency = reader.GetString(0);
            updatedAt = reader.GetString(1);
        }

        using var linesCmd = Command(connection, tx, """
            SELECT product_id, product_name, unit_price_minor, quantity
            FROM draft_order_lines WHERE order_id = $id ORDER BY line_no;
            """);
        linesCmd.Parameters.AddWithValue("$id", orderId);
        var lines = new List<DraftLine>();
        long total = 0;
        var itemCount = 0;
        using (var reader = linesCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var price = reader.GetInt64(2);
                var qty = reader.GetInt32(3);
                var lineTotal = checked(price * qty);
                lines.Add(new DraftLine(reader.GetString(0), reader.GetString(1), price, qty, lineTotal));
                total = checked(total + lineTotal);
                itemCount += qty;
            }
        }

        return new DraftOrder(orderId, currency, lines, itemCount, total, updatedAt);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }
}

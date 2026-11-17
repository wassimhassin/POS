using Pos.Core.Data;

namespace Pos.Core.Catalog;

/// <param name="StationId">Preparation station that receives this product's tickets; null = not routed.</param>
public sealed record Product(string Id, string Sku, string Name, string Category, long PriceMinor, string Currency, string? StationId);

public sealed class ProductRepository(PosDatabase database)
{
    public IReadOnlyList<Product> ListActive()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, sku, name, category, price_minor, currency, station_id
            FROM products
            WHERE is_active = 1
            ORDER BY sort_order, name;
            """;
        using var reader = command.ExecuteReader();
        var products = new List<Product>();
        while (reader.Read())
        {
            products.Add(new Product(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return products;
    }
}

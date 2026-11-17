using Microsoft.Data.Sqlite;

namespace Pos.Core.Data;

/// <summary>Prototype catalog. Prices are integer millimes (1 TND = 1000 millimes).</summary>
internal static class SampleData
{
    private static readonly (string Id, string Sku, string Name, string Category, long PriceMinor)[] Products =
    [
        ("p-espresso", "BOI-001", "Café express", "Boissons chaudes", 2_500),
        ("p-cappuccino", "BOI-002", "Cappuccino", "Boissons chaudes", 4_500),
        ("p-the-menthe", "BOI-003", "Thé à la menthe", "Boissons chaudes", 2_000),
        ("p-citronnade", "BOI-010", "Citronnade", "Boissons fraîches", 4_000),
        ("p-jus-orange", "BOI-011", "Jus d'orange pressé", "Boissons fraîches", 5_500),
        ("p-eau", "BOI-012", "Eau minérale 50 cl", "Boissons fraîches", 1_500),
        ("p-croissant", "VIE-001", "Croissant", "Viennoiseries", 2_000),
        ("p-pain-choco", "VIE-002", "Pain au chocolat", "Viennoiseries", 2_500),
        ("p-sandwich-thon", "SAL-001", "Sandwich thon", "Salé", 7_500),
        ("p-pizza", "SAL-002", "Pizza margherita", "Salé", 14_000),
        ("p-salade-cesar", "SAL-003", "Salade César", "Salé", 12_000),
        ("p-crepe", "SUC-001", "Crêpe chocolat", "Sucré", 8_000),
    ];

    /// <summary>Inserts the sample catalog when the products table is empty. Returns rows inserted.</summary>
    public static int SeedIfEmpty(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        var existing = (long)PosDatabase.Scalar(connection, tx, "SELECT COUNT(*) FROM products;")!;
        if (existing > 0)
        {
            return 0;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO products (id, sku, name, category, price_minor, currency, sort_order)
            VALUES ($id, $sku, $name, $category, $price, 'TND', $sort);
            """;
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var sku = insert.Parameters.Add("$sku", SqliteType.Text);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var category = insert.Parameters.Add("$category", SqliteType.Text);
        var price = insert.Parameters.Add("$price", SqliteType.Integer);
        var sort = insert.Parameters.Add("$sort", SqliteType.Integer);

        for (var i = 0; i < Products.Length; i++)
        {
            var p = Products[i];
            id.Value = p.Id;
            sku.Value = p.Sku;
            name.Value = p.Name;
            category.Value = p.Category;
            price.Value = p.PriceMinor;
            sort.Value = i;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return Products.Length;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Catalog;
using Pos.Core.Data;

namespace Pos.Core.Tests;

public sealed class PosDatabaseTests
{
    [Fact]
    public void Initialize_creates_schema_and_seeds_sample_products()
    {
        using var test = new TestDatabase();

        var products = new ProductRepository(test.Database).ListActive();

        Assert.Equal(12, products.Count);
        Assert.All(products, p => Assert.Equal("TND", p.Currency));
        Assert.All(products, p => Assert.True(p.PriceMinor > 0));
        Assert.Equal("p-espresso", products[0].Id);
    }

    [Fact]
    public void Initialize_is_idempotent_across_restarts()
    {
        using var test = new TestDatabase();

        var db = test.Restart();
        db.Initialize();

        using var connection = db.OpenConnection();
        Assert.Equal((long)PosDatabase.SchemaVersion, PosDatabase.Scalar(connection, null, "PRAGMA user_version;"));
        Assert.Equal(12L, PosDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM products;"));
        Assert.Equal("wal", PosDatabase.Scalar(connection, null, "PRAGMA journal_mode;"));
        Assert.Equal(1L, PosDatabase.Scalar(connection, null, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public void Initialize_refuses_a_database_from_a_newer_app_version()
    {
        using var test = new TestDatabase();
        using (var connection = test.Database.OpenConnection())
        {
            PosDatabase.Execute(connection, null, $"PRAGMA user_version = {PosDatabase.SchemaVersion + 1};");
        }

        var newer = new PosDatabase(test.FilePath, NullLogger<PosDatabase>.Instance);

        var ex = Assert.Throws<InvalidOperationException>(newer.Initialize);
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inactive_products_are_not_listed()
    {
        using var test = new TestDatabase();
        using (var connection = test.Database.OpenConnection())
        {
            PosDatabase.Execute(connection, null, "UPDATE products SET is_active = 0 WHERE id = 'p-pizza';");
        }

        var products = new ProductRepository(test.Database).ListActive();

        Assert.DoesNotContain(products, p => p.Id == "p-pizza");
        Assert.Equal(11, products.Count);
    }
}

using Pos.Core.Data;
using Pos.Core.Orders;

namespace Pos.Core.Tests;

public sealed class DraftOrderRepositoryTests
{
    [Fact]
    public void GetCurrent_returns_null_when_no_draft_exists()
    {
        using var test = new TestDatabase();
        Assert.Null(new DraftOrderRepository(test.Database).GetCurrent());
    }

    [Fact]
    public void Save_prices_lines_from_the_catalog_and_computes_totals()
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);

        var draft = repo.Save(null, [new("p-espresso", 3), new("p-croissant", 2)]);

        Assert.True(Guid.TryParse(draft.Id, out _));
        Assert.Equal("TND", draft.Currency);
        Assert.Equal(5, draft.ItemCount);
        Assert.Equal((3 * 2_500) + (2 * 2_000), draft.TotalMinor);
        Assert.Equal(["p-espresso", "p-croissant"], draft.Lines.Select(l => l.ProductId));
        Assert.Equal("Café express", draft.Lines[0].Name);
        Assert.Equal(7_500, draft.Lines[0].LineTotalMinor);
    }

    [Fact]
    public void Draft_survives_application_restart()
    {
        using var test = new TestDatabase();
        var saved = new DraftOrderRepository(test.Database).Save(null, [new("p-pizza", 1), new("p-eau", 4)]);

        var reopened = new DraftOrderRepository(test.Restart()).GetCurrent();

        Assert.NotNull(reopened);
        Assert.Equal(saved, reopened with { Lines = saved.Lines });
        Assert.Equal(saved.Lines, reopened.Lines);
        Assert.Equal(14_000 + (4 * 1_500), reopened.TotalMinor);
    }

    [Fact]
    public void Save_with_existing_id_replaces_lines()
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);
        var first = repo.Save(null, [new("p-espresso", 1), new("p-crepe", 1)]);

        var second = repo.Save(first.Id, [new("p-crepe", 2)]);

        Assert.Equal(first.Id, second.Id);
        var line = Assert.Single(second.Lines);
        Assert.Equal(("p-crepe", 2), (line.ProductId, line.Quantity));
        Assert.Equal(16_000, second.TotalMinor);
        Assert.Equal(second.Id, repo.GetCurrent()!.Id);
    }

    [Fact]
    public void Line_prices_are_snapshotted_at_save_time()
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);
        repo.Save(null, [new("p-espresso", 2)]);

        using (var connection = test.Database.OpenConnection())
        {
            PosDatabase.Execute(connection, null, "UPDATE products SET price_minor = 9999 WHERE id = 'p-espresso';");
        }

        Assert.Equal(5_000, repo.GetCurrent()!.TotalMinor);
    }

    [Fact]
    public void Delete_removes_draft_and_lines()
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);
        var draft = repo.Save(null, [new("p-espresso", 1)]);

        Assert.True(repo.Delete(draft.Id));
        Assert.False(repo.Delete(draft.Id));
        Assert.Null(repo.GetCurrent());

        using var connection = test.Database.OpenConnection();
        Assert.Equal(0L, PosDatabase.Scalar(connection, null, "SELECT COUNT(*) FROM draft_order_lines;"));
    }

    public static TheoryData<string, DraftLineInput[]> InvalidLines => new()
    {
        { "invalid_lines", [] },
        { "invalid_quantity", [new("p-espresso", 0)] },
        { "invalid_quantity", [new("p-espresso", 1000)] },
        { "invalid_quantity", [new("p-espresso", -3)] },
        { "duplicate_product", [new("p-espresso", 1), new("p-espresso", 2)] },
        { "unknown_product", [new("p-does-not-exist", 1)] },
        { "invalid_product", [new("", 1)] },
        { "invalid_product", [new(new string('x', 65), 1)] },
    };

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public void Save_rejects_invalid_lines(string expectedCode, DraftLineInput[] lines)
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);

        var ex = Assert.Throws<PosValidationException>(() => repo.Save(null, lines));

        Assert.Equal(expectedCode, ex.Code);
        Assert.Null(repo.GetCurrent());
    }

    [Fact]
    public void Save_rejects_too_many_lines()
    {
        using var test = new TestDatabase();
        var lines = Enumerable.Range(0, DraftOrderRepository.MaxLines + 1).Select(i => new DraftLineInput($"p{i}", 1)).ToArray();

        var ex = Assert.Throws<PosValidationException>(() => new DraftOrderRepository(test.Database).Save(null, lines));

        Assert.Equal("invalid_lines", ex.Code);
    }

    [Fact]
    public void Save_rejects_inactive_products()
    {
        using var test = new TestDatabase();
        using (var connection = test.Database.OpenConnection())
        {
            PosDatabase.Execute(connection, null, "UPDATE products SET is_active = 0 WHERE id = 'p-pizza';");
        }

        var ex = Assert.Throws<PosValidationException>(() => new DraftOrderRepository(test.Database).Save(null, [new("p-pizza", 1)]));

        Assert.Equal("unknown_product", ex.Code);
    }

    [Fact]
    public void Save_rejects_unknown_or_malformed_order_ids()
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);

        Assert.Equal("draft_not_found", Assert.Throws<PosValidationException>(() => repo.Save(Guid.NewGuid().ToString(), [new("p-eau", 1)])).Code);
        Assert.Equal("invalid_order_id", Assert.Throws<PosValidationException>(() => repo.Save("'; DROP TABLE products;--", [new("p-eau", 1)])).Code);
        Assert.Equal("invalid_order_id", Assert.Throws<PosValidationException>(() => repo.Delete("nope")).Code);
    }

    [Fact]
    public void Failed_save_does_not_modify_existing_draft()
    {
        using var test = new TestDatabase();
        var repo = new DraftOrderRepository(test.Database);
        var draft = repo.Save(null, [new("p-espresso", 2)]);

        Assert.Throws<PosValidationException>(() => repo.Save(draft.Id, [new("p-espresso", 1), new("p-unknown", 1)]));

        Assert.Equal(draft.Lines, repo.GetCurrent()!.Lines);
    }
}

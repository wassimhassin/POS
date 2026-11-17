using Pos.Core.Printing;

namespace Pos.Core.Tests;

public sealed class TicketFormattingTests
{
    private static readonly DateTimeOffset OrderedAt = new(2026, 11, 17, 14, 30, 0, TimeSpan.Zero);

    private static TicketSource Receipt(params TicketItem[] items) => new(
        TicketLayout.ReceiptKind, "4F2A9C", OrderedAt, null, items,
        items.Sum(i => i.Quantity), items.Sum(i => i.LineTotalMinor), "TND");

    private static TicketSource Station(string name, params TicketItem[] items) => new(
        TicketLayout.StationKind, "4F2A9C", OrderedAt, name, items,
        items.Sum(i => i.Quantity), items.Sum(i => i.LineTotalMinor), "TND");

    private static TicketItem Item(string name, int qty, long unit) => new(name, qty, unit, unit * qty);

    [Theory]
    [InlineData(0, "0,000")]
    [InlineData(5, "0,005")]
    [InlineData(2_500, "2,500")]
    [InlineData(30_500, "30,500")]
    [InlineData(1_234_500, "1 234,500")]
    [InlineData(-1_500, "-1,500")]
    public void Money_is_formatted_from_millimes(long millimes, string expected) =>
        Assert.Equal(expected, Money.FormatTnd(millimes));

    [Theory]
    [InlineData(58, 32)]
    [InlineData(80, 48)]
    public void Paper_width_maps_to_columns(int widthMm, int columns) => Assert.Equal(columns, TicketLayout.Columns(widthMm));

    [Fact]
    public void Unsupported_paper_width_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketLayout.Columns(76));

    [Fact]
    public void Receipt_58mm_layout_matches_expected_text()
    {
        var source = Receipt(Item("Cappuccino", 2, 4_500), Item("Croissant", 1, 2_000));

        var text = TicketRenderer.ToText(TicketLayout.Build(source, 58, 0, TimeZoneInfo.Utc), 58);

        const string expected =
            "  C A F E   P R O T O T Y P E\n" +
            "   Ticket client - NON FISCAL\n" +
            "--------------------------------\n" +
            "Commande                 #4F2A9C\n" +
            "Date            17/11/2026 14:30\n" +
            "--------------------------------\n" +
            "Cappuccino                 9,000\n" +
            "  2 x 4,500\n" +
            "Croissant                  2,000\n" +
            "--------------------------------\n" +
            "TOTAL (DT)                11,000\n" +
            "Articles                       3\n" +
            "--------------------------------\n" +
            "     Merci de votre visite\n" +
            "  Document de test sans valeur\n" +
            "            fiscale\n";
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(58)]
    [InlineData(80)]
    public void No_line_exceeds_the_paper_width(int widthMm)
    {
        var columns = TicketLayout.Columns(widthMm);
        var longName = "Salade César au poulet grillé, croûtons maison et parmesan affiné 24 mois";
        foreach (var source in new[]
        {
            Receipt(Item(longName, 12, 123_456), Item("Eau", 999, 1_500)),
            Station("Cuisine", Item(longName, 12, 123_456), Item("Supercalifragilisticexpialidocious", 1, 1)),
        })
        {
            foreach (var copy in new[] { 0, 3 })
            {
                var lines = TicketLayout.Build(source, widthMm, copy, TimeZoneInfo.Utc);
                Assert.All(lines, l => Assert.True(
                    l.Text.Length <= (l.Large ? columns / 2 : columns),
                    $"'{l.Text}' ({l.Text.Length}) exceeds {(l.Large ? columns / 2 : columns)} columns"));
                Assert.All(TicketRenderer.ToText(lines, widthMm).Split('\n'), t => Assert.True(t.Length <= columns, t));
            }
        }
    }

    [Fact]
    public void Receipt_total_comes_from_millimes_without_rounding()
    {
        var source = Receipt(Item("A", 3, 333), Item("B", 1, 1));

        var text = TicketRenderer.ToText(TicketLayout.Build(source, 80, 0, TimeZoneInfo.Utc), 80);

        Assert.Contains("1,000", text, StringComparison.Ordinal); // 3 × 0,333 + 0,001 = 1,000 exactly
        Assert.Contains("0,999", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_receipt_is_clearly_marked()
    {
        var original = TicketRenderer.ToText(TicketLayout.Build(Receipt(Item("Thé", 1, 2_000)), 80, 0, TimeZoneInfo.Utc), 80);
        var copy = TicketRenderer.ToText(TicketLayout.Build(Receipt(Item("Thé", 1, 2_000)), 80, 2, TimeZoneInfo.Utc), 80);

        Assert.DoesNotContain("DUPLICATA", original, StringComparison.Ordinal);
        Assert.Contains("*** DUPLICATA n°2 ***", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Station_ticket_shows_quantities_without_prices_and_wraps_with_indent()
    {
        var source = Station("Bar", Item("Jus d'orange pressé", 2, 5_500), Item("Café express", 1, 2_500));

        var lines = TicketLayout.Build(source, 58, 0, TimeZoneInfo.Utc);
        var texts = lines.Select(l => l.Text).ToList();

        Assert.Equal("BAR", texts[0]);
        Assert.True(lines[0].Large && lines[0].Bold);
        Assert.Contains("2 x Jus d'orange", texts);
        Assert.Contains("    pressé", texts);
        Assert.Contains("1 x Café express", texts);
        Assert.Contains("Articles : 3", texts);
        Assert.DoesNotContain(texts, t => t.Contains("5,500", StringComparison.Ordinal) || t.Contains("DT", StringComparison.Ordinal));
    }

    [Fact]
    public void Reprinted_station_ticket_warns_against_double_preparation()
    {
        var text = TicketRenderer.ToText(TicketLayout.Build(Station("Cuisine", Item("Pizza", 1, 14_000)), 80, 1, TimeZoneInfo.Utc), 80);

        Assert.Contains("R E I M P R E S S I O N", text, StringComparison.Ordinal);
        Assert.Contains("Ne pas preparer en double", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ticket_time_is_rendered_in_the_terminal_time_zone()
    {
        var tunis = TimeZoneInfo.CreateCustomTimeZone("Tunis", TimeSpan.FromHours(1), "Tunis", "Tunis");

        var text = TicketRenderer.ToText(TicketLayout.Build(Receipt(Item("Thé", 1, 2_000)), 80, 0, tunis), 80);

        Assert.Contains("17/11/2026 15:30", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrap_breaks_on_words_and_hard_splits_long_words()
    {
        Assert.Equal(["abc def", "ghi"], TicketLayout.Wrap("abc def ghi", 7));
        Assert.Equal(["abcde", "fghij", "k"], TicketLayout.Wrap("abcdefghijk", 5));
        Assert.Equal([string.Empty], TicketLayout.Wrap(string.Empty, 5));
    }

    [Fact]
    public void EscPos_output_initializes_selects_codepage_and_cuts()
    {
        var bytes = TicketRenderer.ToEscPos(TicketLayout.Build(Receipt(Item("Thé", 1, 2_000)), 58, 0, TimeZoneInfo.Utc));

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 19 }, bytes[..5]);
        Assert.Equal(new byte[] { 0x1D, 0x56, 66, 0 }, bytes[^4..]);
    }

    [Fact]
    public void EscPos_encodes_french_accents_in_cp858_and_styles_lines()
    {
        var lines = new[]
        {
            new TicketLine("Thé à la crème", LineAlign.Center, Bold: true, Large: true),
            new TicketLine("Prix → 5€"),
        };

        var bytes = TicketRenderer.ToEscPos(lines);

        // ESC a 1 (center), ESC E 1 (bold), GS ! 0x11 (double width+height), then CP858 text.
        var styled = new byte[] { 0x1B, 0x61, 1, 0x1B, 0x45, 1, 0x1D, 0x21, 0x11, (byte)'T', (byte)'h', 0x82, (byte)' ', 0x85 };
        Assert.True(bytes.AsSpan().IndexOf(styled) >= 0, "styled, CP858-encoded line not found");
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { (byte)'r', 0x8A, (byte)'m', (byte)'e' }) >= 0, "è not encoded as 0x8A");
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { (byte)'?', (byte)' ', (byte)'5', 0xD5 }) >= 0, "→ should become ? and € 0xD5");
    }
}

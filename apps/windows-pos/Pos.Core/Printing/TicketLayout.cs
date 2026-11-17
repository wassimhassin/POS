using System.Globalization;
using System.Text;

namespace Pos.Core.Printing;

/// <summary>Immutable snapshot of what a ticket prints. Stored as JSON with each print job.</summary>
/// <param name="Kind">"receipt" or "station".</param>
public sealed record TicketSource(
    string Kind,
    string OrderNumber,
    DateTimeOffset OrderedAt,
    string? StationName,
    IReadOnlyList<TicketItem> Items,
    int ItemCount,
    long TotalMinor,
    string Currency);

public sealed record TicketItem(string Name, int Quantity, long UnitPriceMinor, long LineTotalMinor);

public enum LineAlign
{
    Left,
    Center,
    Right,
}

/// <summary>One physical printed line. Large = double width + double height (half the columns).</summary>
public readonly record struct TicketLine(string Text, LineAlign Align = LineAlign.Left, bool Bold = false, bool Large = false);

/// <summary>
/// Lays out receipts and preparation tickets into fixed-width lines for 58 mm (32 columns) or
/// 80 mm (48 columns) paper (Font A, 12×24 dots — typical ESC/POS defaults; verify per printer model).
/// </summary>
public static class TicketLayout
{
    public const string ReceiptKind = "receipt";
    public const string StationKind = "station";
    public const string BusinessName = "CAFE PROTOTYPE";

    public static readonly IReadOnlyList<int> SupportedPaperWidths = [58, 80];

    public static int Columns(int paperWidthMm) => paperWidthMm switch
    {
        58 => 32,
        80 => 48,
        _ => throw new ArgumentOutOfRangeException(nameof(paperWidthMm), paperWidthMm, "Supported paper widths: 58, 80 mm."),
    };

    /// <param name="copyNumber">0 = original; n > 0 prints a visible DUPLICATA / RÉIMPRESSION banner.</param>
    public static IReadOnlyList<TicketLine> Build(TicketSource source, int paperWidthMm, int copyNumber, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeZone);
        var columns = Columns(paperWidthMm);
        var localTime = TimeZoneInfo.ConvertTime(source.OrderedAt, timeZone)
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var lines = new List<TicketLine>();

        return source.Kind switch
        {
            ReceiptKind => BuildReceipt(lines, source, columns, copyNumber, localTime),
            StationKind => BuildStation(lines, source, columns, copyNumber, localTime),
            _ => throw new ArgumentException($"Unknown ticket kind '{source.Kind}'.", nameof(source)),
        };
    }

    private static List<TicketLine> BuildReceipt(List<TicketLine> lines, TicketSource s, int columns, int copyNumber, string localTime)
    {
        AddWrapped(lines, BusinessName, columns / 2, LineAlign.Center, bold: true, large: true);
        lines.Add(new TicketLine("Ticket client - NON FISCAL", LineAlign.Center));
        if (copyNumber > 0)
        {
            lines.Add(new TicketLine(string.Empty));
            lines.Add(new TicketLine($"*** DUPLICATA n°{copyNumber} ***", LineAlign.Center, Bold: true));
        }

        lines.Add(Rule(columns));
        lines.Add(Columns2("Commande", $"#{s.OrderNumber}", columns));
        lines.Add(Columns2("Date", localTime, columns));
        lines.Add(Rule(columns));
        foreach (var item in s.Items)
        {
            AddColumns(lines, item.Name, Money.FormatTnd(item.LineTotalMinor), columns);
            if (item.Quantity > 1)
            {
                lines.Add(new TicketLine($"  {item.Quantity} x {Money.FormatTnd(item.UnitPriceMinor)}"));
            }
        }

        lines.Add(Rule(columns));
        lines.Add(Columns2($"TOTAL ({CurrencyLabel(s.Currency)})", Money.FormatTnd(s.TotalMinor), columns) with { Bold = true });
        lines.Add(Columns2("Articles", s.ItemCount.ToString(CultureInfo.InvariantCulture), columns));
        lines.Add(Rule(columns));
        lines.Add(new TicketLine("Merci de votre visite", LineAlign.Center));
        AddWrapped(lines, "Document de test sans valeur fiscale", columns, LineAlign.Center);
        return lines;
    }

    private static List<TicketLine> BuildStation(List<TicketLine> lines, TicketSource s, int columns, int copyNumber, string localTime)
    {
        var largeColumns = columns / 2;
        AddWrapped(lines, (s.StationName ?? "PREPARATION").ToUpperInvariant(), largeColumns, LineAlign.Center, bold: true, large: true);
        if (copyNumber > 0)
        {
            // Kitchen staff must not prepare twice: make the duplicate impossible to miss.
            AddWrapped(lines, $"REIMPRESSION n°{copyNumber}", largeColumns, LineAlign.Center, bold: true, large: true);
            AddWrapped(lines, "Ne pas preparer en double", columns, LineAlign.Center, bold: true);
        }

        lines.Add(new TicketLine($"Commande #{s.OrderNumber}", LineAlign.Center, Bold: true));
        lines.Add(new TicketLine(localTime, LineAlign.Center));
        lines.Add(Rule(columns));
        foreach (var item in s.Items)
        {
            var prefix = $"{item.Quantity} x ";
            var wrapped = Wrap(item.Name, largeColumns - prefix.Length);
            for (var i = 0; i < wrapped.Count; i++)
            {
                var lead = i == 0 ? prefix : new string(' ', prefix.Length);
                lines.Add(new TicketLine(lead + wrapped[i], Bold: true, Large: true));
            }
        }

        lines.Add(Rule(columns));
        lines.Add(new TicketLine($"Articles : {s.ItemCount.ToString(CultureInfo.InvariantCulture)}"));
        return lines;
    }

    private static string CurrencyLabel(string currency) => currency == "TND" ? "DT" : currency;

    private static TicketLine Rule(int columns) => new(new string('-', columns));

    /// <summary>Left text and right-aligned value on one line (left truncated if necessary).</summary>
    private static TicketLine Columns2(string left, string right, int columns)
    {
        var space = columns - right.Length - 1;
        var l = left.Length > space ? left[..space] : left;
        return new TicketLine(l.PadRight(space + 1) + right);
    }

    /// <summary>Left text wraps; the value is printed on the first line.</summary>
    private static void AddColumns(List<TicketLine> lines, string left, string right, int columns)
    {
        var wrapped = Wrap(left, columns - right.Length - 1);
        lines.Add(new TicketLine(wrapped[0].PadRight(columns - right.Length) + right));
        foreach (var rest in wrapped.Skip(1))
        {
            lines.Add(new TicketLine(rest));
        }
    }

    private static void AddWrapped(List<TicketLine> lines, string text, int width, LineAlign align, bool bold = false, bool large = false)
    {
        foreach (var part in Wrap(text, width))
        {
            lines.Add(new TicketLine(part, align, bold, large));
        }
    }

    /// <summary>Word wrap; words longer than the width are hard-split.</summary>
    internal static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = word;
            while (remaining.Length > 0)
            {
                var needed = current.Length == 0 ? remaining.Length : current.Length + 1 + remaining.Length;
                if (needed <= width)
                {
                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }

                    current.Append(remaining);
                    remaining = string.Empty;
                }
                else if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    result.Add(remaining[..width]);
                    remaining = remaining[width..];
                }
            }
        }

        if (current.Length > 0 || result.Count == 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}

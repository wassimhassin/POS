using System.Text;

namespace Pos.Core.Printing;

/// <summary>
/// Renders laid-out ticket lines either as ESC/POS bytes (sent RAW to the Windows spooler) or as a
/// plain-text preview with the same line breaks.
/// </summary>
public static class TicketRenderer
{
    /// <summary>Code page 858 (PC858 Latin-1 + €): covers French accents. ESC t 19 on Epson-compatible printers.</summary>
    public const int CodePage = 858;
    private const byte EscPosCodePageTable = 19;

    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;
    private const byte Lf = 0x0A;

    private static readonly Encoding TicketEncoding = CreateEncoding();

    public static string ToText(IReadOnlyList<TicketLine> lines, int paperWidthMm)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var columns = TicketLayout.Columns(paperWidthMm);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            // Large text is twice as wide on paper: align it within half the columns, then scale.
            var width = line.Large ? columns / 2 : columns;
            var text = Align(line.Text, width, line.Align);
            if (line.Large)
            {
                text = string.Concat(text.Select(c => c == ' ' ? "  " : c + " ")).TrimEnd();
            }

            sb.Append(text.TrimEnd()).Append('\n');
        }

        return sb.ToString();
    }

    public static byte[] ToEscPos(IReadOnlyList<TicketLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        using var ms = new MemoryStream();
        ms.Write([Esc, (byte)'@']);                       // initialize printer
        ms.Write([Esc, (byte)'t', EscPosCodePageTable]);  // select character code table PC858

        foreach (var line in lines)
        {
            ms.Write([Esc, (byte)'a', line.Align switch { LineAlign.Center => (byte)1, LineAlign.Right => (byte)2, _ => (byte)0 }]);
            ms.Write([Esc, (byte)'E', line.Bold ? (byte)1 : (byte)0]);
            ms.Write([Gs, (byte)'!', line.Large ? (byte)0x11 : (byte)0x00]);
            ms.Write(TicketEncoding.GetBytes(line.Text));
            ms.WriteByte(Lf);
        }

        ms.Write([Esc, (byte)'a', 0, Esc, (byte)'E', 0, Gs, (byte)'!', 0]); // reset styles
        ms.Write([Esc, (byte)'d', 4]);                    // feed 4 lines past the cutter
        ms.Write([Gs, (byte)'V', 66, 0]);                 // partial cut (ignored by printers without cutter)
        return ms.ToArray();
    }

    internal static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CodePage, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    private static string Align(string text, int width, LineAlign align)
    {
        if (text.Length >= width)
        {
            return text;
        }

        return align switch
        {
            LineAlign.Center => new string(' ', (width - text.Length) / 2) + text,
            LineAlign.Right => text.PadLeft(width),
            _ => text,
        };
    }
}

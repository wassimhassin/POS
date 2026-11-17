using System.Globalization;

namespace Pos.Core;

/// <summary>Formatting of integer minor units. TND uses 3 decimals (millimes). Never floating point.</summary>
public static class Money
{
    /// <summary>Formats millimes as "1 234,500" (space grouping, comma decimal).</summary>
    public static string FormatTnd(long millimes)
    {
        var sign = millimes < 0 ? "-" : string.Empty;
        var abs = millimes == long.MinValue ? throw new OverflowException() : Math.Abs(millimes);
        var whole = abs / 1000;
        var fraction = abs % 1000;
        var grouped = whole.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{grouped},{fraction:000}");
    }
}

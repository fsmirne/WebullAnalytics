using System;
using System.Globalization;
using Spectre.Console;

namespace WebullAnalytics;

public static class Formatters
{
    public static string FormatQty(decimal qty)
    {
        var text = qty.ToString("F", CultureInfo.InvariantCulture);
        if (text.Contains('.'))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }
        return text;
    }

    public static string FormatPrice(decimal value, string asset)
    {
        var text = asset.StartsWith("Option")
            ? value.ToString("F3", CultureInfo.InvariantCulture)
            : value.ToString("F2", CultureInfo.InvariantCulture);

        // Trim trailing zeros but keep at least 2 decimal places
        text = text.TrimEnd('0');
        if (!text.Contains('.'))
            return text + ".00";

        var parts = text.Split('.');
        if (parts[1].Length == 0)
            return parts[0] + ".00";
        if (parts[1].Length == 1)
            return text + "0";

        return text;
    }

    public static Markup FormatPnL(decimal value)
    {
        var style = value >= 0 ? "green" : "red";
        var text = value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        return new Markup($"[{style}]{text}[/]");
    }

    public static string FormatExpiry(DateTime? expiry)
    {
        if (!expiry.HasValue)
            return "-";

        return FormatOptionDate(expiry.Value);
    }

    public static string FormatOptionDate(DateTime expDate)
    {
        return expDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string FormatOptionDisplay(string root, DateTime expDate, decimal strike)
    {
        var strikeText = FormatQty(strike);
        return $"{root} {FormatOptionDate(expDate)} ${strikeText}";
    }
}

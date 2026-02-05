using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WebullAnalytics;

public static partial class ParsingHelpers
{
    private static readonly Regex OptionRegex = GenerateOptionRegex();

    [GeneratedRegex(@"^([A-Z]+)(\d{6})([CP])(\d{8})$")]
    private static partial Regex GenerateOptionRegex();

    public static DateTime? ParseTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        var parts = text.Split(' ');

        // Remove timezone suffix if present (e.g., "EST", "PST")
        if (parts.Length > 0 && parts[^1].All(char.IsLetter) && parts[^1].Length <= 4)
        {
            text = string.Join(" ", parts[..^1]);
        }

        if (DateTime.TryParseExact(text, "M/d/yyyy H:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            return result;
        }

        return null;
    }

    public static decimal? ParseDecimal(string? value)
    {
        if (value == null)
            return null;

        var text = value.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.StartsWith("@"))
            text = text[1..];

        text = text.Replace(",", "");

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;

        return null;
    }

    public static OptionParsed? ParseOptionSymbol(string symbol)
    {
        var match = OptionRegex.Match(symbol.Trim().ToUpper());
        if (!match.Success)
            return null;

        var root = match.Groups[1].Value;
        var yymmdd = match.Groups[2].Value;
        var callPut = match.Groups[3].Value;
        var strikeRaw = match.Groups[4].Value;

        var expDate = DateTime.ParseExact(yymmdd, "yyMMdd", CultureInfo.InvariantCulture);
        var strike = decimal.Parse(strikeRaw) / 1000m;

        return new OptionParsed(root, expDate, callPut, strike);
    }

    public static string StrategyKindFromName(string name)
    {
        var lowered = name.Replace(" ", "").ToLower();

        if (lowered.Contains("butterfly"))
            return "Butterfly";
        if (lowered.Contains("calendar"))
            return "Calendar";
        if (lowered.Contains("condor"))
            return "Condor";
        if (lowered.Contains("diagonal"))
            return "Diagonal";
        if (lowered.Contains("ironcondor"))
            return "IronCondor";
        if (lowered.Contains("spread"))
            return "Spread";
        if (lowered.Contains("straddle"))
            return "Straddle";
        if (lowered.Contains("strangle"))
            return "Strangle";

        return "Strategy";
    }
}

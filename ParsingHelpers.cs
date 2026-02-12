using System.Globalization;
using System.Text.RegularExpressions;

namespace WebullAnalytics;

/// <summary>
/// Utility methods for parsing option symbols, dates, and decimal values.
/// </summary>
public static partial class ParsingHelpers
{
    // OCC option symbol format: ROOT + YYMMDD + C/P + 8-digit strike (strike * 1000)
    // Example: GME260213C00025000 = GME Feb 13 2026 $25 Call
    private static readonly Regex OptionRegex = GenerateOptionRegex();

    [GeneratedRegex(@"^([A-Z]+)(\d{6})([CP])(\d{8})$")]
    private static partial Regex GenerateOptionRegex();

    // Maps strategy name keywords to standardized strategy types
    private static readonly Dictionary<string, string> StrategyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["butterfly"] = "Butterfly",
        ["calendar"] = "Calendar",
        ["condor"] = "Condor",
        ["diagonal"] = "Diagonal",
        ["ironcondor"] = "IronCondor",
        ["spread"] = "Spread",
        ["straddle"] = "Straddle",
        ["strangle"] = "Strangle",
        ["vertical"] = "Vertical"
    };

    /// <summary>
    /// Parses a datetime string in Webull's format (M/d/yyyy H:mm:ss with optional timezone).
    /// </summary>
    public static DateTime? ParseTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        var parts = text.Split(' ');

        // Remove timezone suffix if present (e.g., "EST", "PST")
        if (parts.Length > 0 && parts[^1].All(char.IsLetter) && parts[^1].Length <= 4)
            text = string.Join(" ", parts[..^1]);

        return DateTime.TryParseExact(text, "M/d/yyyy H:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : null;
    }

    /// <summary>
    /// Parses a decimal value, handling Webull's @ prefix and comma separators.
    /// </summary>
    public static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();

        return TryParseWebullDecimal(text, out var result) ? result : null;
    }

    /// <summary>
    /// Tries to parse a decimal value from Webull exports.
    /// Handles @ prefixes, commas, and occasional non-numeric characters.
    /// </summary>
    public static bool TryParseWebullDecimal(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
            return false;

		var text = Regex.Replace(value.Trim(), @"[^\d.\-]", "");

		return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Parses an OCC-format option symbol into its components.
    /// Returns null if the symbol doesn't match the expected format.
    /// </summary>
    public static OptionParsed? ParseOptionSymbol(string symbol)
    {
        var match = OptionRegex.Match(symbol.Trim().ToUpperInvariant());
        if (!match.Success)
            return null;

        var root = match.Groups[1].Value;
        var expiryDate = DateTime.ParseExact(match.Groups[2].Value, "yyMMdd", CultureInfo.InvariantCulture);
        var callPut = match.Groups[3].Value;
        var strike = decimal.Parse(match.Groups[4].Value) / 1000m;

        return new OptionParsed(root, expiryDate, callPut, strike);
    }

    /// <summary>
    /// Extracts the strategy type from a Webull strategy name.
    /// </summary>
    public static string StrategyKindFromName(string name)
    {
        var normalized = name.Replace(" ", "");

        return StrategyKeywords.Where(kvp => normalized.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)).Select(kvp => kvp.Value).FirstOrDefault() ?? "Strategy";
    }
}

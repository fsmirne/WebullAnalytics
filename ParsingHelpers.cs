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
	[GeneratedRegex(@"^([A-Z]+)(\d{6})([CP])(\d{8})$")]
	private static partial Regex OptionRegex();

	[GeneratedRegex(@"[^\d.\-]")]
	private static partial Regex NonNumericRegex();

	// Maps strategy name keywords to standardized strategy types
	// Ordered longest-first so "ironcondor" matches before "condor"
	private static readonly (string keyword, string kind)[] StrategyKeywords =
	[
		("butterfly", "Butterfly"),
		("calendar", "Calendar"),
		("ironcondor", "IronCondor"),
		("condor", "Condor"),
		("diagonal", "Diagonal"),
		("spread", "Spread"),
		("straddle", "Straddle"),
		("strangle", "Strangle"),
		("vertical", "Vertical"),
	];

	/// <summary>
	/// Tries to parse a decimal value from Webull exports.
	/// Handles @ prefixes, commas, and occasional non-numeric characters.
	/// </summary>
	public static bool TryParseWebullDecimal(string? value, out decimal result)
	{
		result = 0m;
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var text = NonNumericRegex().Replace(value.Trim(), "");

		return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
	}

	/// <summary>
	/// Parses an OCC-format option symbol into its components.
	/// Returns null if the symbol doesn't match the expected format.
	/// </summary>
	public static OptionParsed? ParseOptionSymbol(string symbol)
	{
		var match = OptionRegex().Match(symbol.Trim().ToUpperInvariant());
		if (!match.Success)
			return null;

		var root = match.Groups[1].Value;

		if (!DateTime.TryParseExact(match.Groups[2].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate))
			return null;

		var callPut = match.Groups[3].Value;

		if (!decimal.TryParse(match.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var strikeRaw))
			return null;

		return new OptionParsed(root, expiryDate, callPut, strikeRaw / 1000m);
	}

	/// <summary>
	/// Extracts the strategy type from a Webull strategy name.
	/// </summary>
	public static string StrategyKindFromName(string name)
	{
		var normalized = name.Replace(" ", "");

		return StrategyKeywords.Where(x => normalized.Contains(x.keyword, StringComparison.OrdinalIgnoreCase)).Select(x => x.kind).FirstOrDefault() ?? "Strategy";
	}
}

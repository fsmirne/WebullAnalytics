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

	// Matches trailing 3-letter timezone abbreviations like " EST", " EDT", " UTC"
	[GeneratedRegex(@"\s[A-Za-z]{3}$")]
	public static partial Regex TimezoneSuffixRegex();

	/// <summary>Datetime formats used in Webull CSV and JSONL exports.</summary>
	public static readonly string[] DateTimeFormats =
	[
		"MM/dd/yyyy HH:mm:ss",
		"M/d/yyyy H:mm:ss",
		"MM/dd/yyyy H:mm:ss",
		"M/d/yyyy HH:mm:ss"
	];

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
	/// Parses a Webull datetime string, stripping any trailing timezone suffix.
	/// Returns false if parsing fails.
	/// </summary>
	public static bool TryParseWebullDateTime(string? text, out DateTime result)
	{
		result = default;
		if (string.IsNullOrWhiteSpace(text))
			return false;

		var clean = TimezoneSuffixRegex().Replace(text.Trim(), "");
		return DateTime.TryParseExact(clean, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out result);
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
	/// Returns "Call" or "Put" for the given OCC call/put code ("C" or "P").
	/// </summary>
	public static string CallPutDisplayName(string callPut) => callPut == "C" ? "Call" : "Put";

	private static readonly string[] IndexRootPair = ["SPX", "SPXW"];
	private static readonly HashSet<string> IndexMonthlyFragmentedRoots = new(IndexRootPair, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The set of option-chain roots that should be treated as one book when aggregating by
	/// <paramref name="requestedRoot"/> at <paramref name="expiry"/> (GEX/max-pain/OI/strike-ladder lookups etc).
	/// Normally just <paramref name="requestedRoot"/> itself. On a standard-monthly (3rd Friday) expiry, SPX (legacy
	/// AM-settled) and SPXW (PM-settled) both count: CBOE lists real open interest under both roots on that date, so
	/// filtering to either alone silently drops most of it — verified live 2026-08-25 (SPXW-only gravity landed on
	/// the opposite side of spot from SPY and from SPX-only). Every other date (dailies, non-3rd-Friday weeklies)
	/// only ever lists SPXW, so this never widens the match outside monthlies.
	/// </summary>
	public static IReadOnlyList<string> AggregationRoots(string requestedRoot, DateTime expiry) =>
		IndexMonthlyFragmentedRoots.Contains(requestedRoot) && WebullAnalytics.AI.OpenerExpiryHelpers.IsMonthlyExpiry(expiry.Date)
			? IndexRootPair
			: [requestedRoot];

	/// <summary>True when <paramref name="parsedRoot"/> is in <see cref="AggregationRoots"/> for
	/// <paramref name="requestedRoot"/> at <paramref name="expiry"/>. See <see cref="AggregationRoots"/> for why.</summary>
	public static bool RootsMatchForAggregation(string parsedRoot, string requestedRoot, DateTime expiry) =>
		AggregationRoots(requestedRoot, expiry).Contains(parsedRoot, StringComparer.OrdinalIgnoreCase);

	/// <summary>True for SPX/SPXW — the pair that shares a book on standard-monthly expiries (see
	/// <see cref="AggregationRoots"/>). For presence/coverage checks that aren't tied to one specific expiry,
	/// where being permissive about which of the pair "counts" costs nothing.</summary>
	public static bool IsIndexMonthlyFragmentedRoot(string root) => IndexMonthlyFragmentedRoots.Contains(root);

	/// <summary>Like <see cref="AggregationRoots"/> but ungated by expiry — both SPX and SPXW whenever
	/// <paramref name="root"/> is either of them, on every date, not just monthlies. Only safe when the
	/// caller's OWN data source is inherently monthly-only for the sibling root already (e.g. a per-day file
	/// keyed by root that ThetaData never populates with non-monthly SPX rows to begin with), so merging in
	/// an empty/absent sibling file on a non-monthly day is a harmless no-op rather than a real widening.</summary>
	public static IReadOnlyList<string> RelatedRootsUnconditional(string root) =>
		IsIndexMonthlyFragmentedRoot(root) ? IndexRootPair : [root];

	/// <summary>Looks up <paramref name="root"/> in a by-root dictionary (underlying spot prices, historical vol,
	/// dividend schedules, etc.), falling back to the sibling SPX/SPXW root when <paramref name="root"/> itself
	/// isn't present. Data keyed by whichever of the pair was fetched (e.g. "$SPX" resolves live spot under one
	/// key) shouldn't leave the OTHER root's legs/contracts without a match just because they're the same
	/// underlying. Not expiry-gated — unlike <see cref="AggregationRoots"/>, a spot/HV/dividend value is the same
	/// number under either root on any date, so this is safe to use everywhere, not just on monthlies.</summary>
	public static bool TryResolveForRoot<T>(IReadOnlyDictionary<string, T> byRoot, string root, out T value)
	{
		if (byRoot.TryGetValue(root, out value!)) return true;
		if (IsIndexMonthlyFragmentedRoot(root))
			foreach (var sibling in IndexRootPair)
				if (!string.Equals(sibling, root, StringComparison.OrdinalIgnoreCase) && byRoot.TryGetValue(sibling, out value!))
					return true;
		value = default!;
		return false;
	}

	/// <summary>
	/// Extracts the strategy type from a Webull strategy name.
	/// </summary>
	public static string StrategyKindFromName(string name)
	{
		var normalized = name.Replace(" ", "");

		return StrategyKeywords.Where(x => normalized.Contains(x.keyword, StringComparison.OrdinalIgnoreCase)).Select(x => x.kind).FirstOrDefault() ?? "Strategy";
	}

	/// <summary>
	/// Classifies a multi-leg option strategy type based on leg counts.
	/// </summary>
	/// <param name="legCount">Total number of legs</param>
	/// <param name="distinctExpiries">Number of distinct expiration dates</param>
	/// <param name="distinctStrikes">Number of distinct strike prices</param>
	/// <param name="distinctCallPut">Number of distinct call/put types (1 or 2)</param>
	public static string ClassifyStrategyKind(int legCount, int distinctExpiries, int distinctStrikes, int distinctCallPut)
	{
		if (legCount >= 4 && distinctCallPut == 2)
		{
			// Multi-expiry 4-leggers are doubles, not irons. DoubleCalendar shares one strike on each side
			// (2 distinct strikes total); DoubleDiagonal offsets the long wing on each side (3+ strikes).
			if (distinctExpiries >= 2) return distinctStrikes <= 2 ? "DoubleCalendar" : "DoubleDiagonal";
			return distinctStrikes <= 3 ? "IronButterfly" : "IronCondor";
		}
		// Single-sided 4-leggers across two expiries are calendar/diagonal-verticals (a near short vertical +
		// a far long vertical on the same side). Same anchor+wing on both expiries (2 distinct strikes) = a
		// CalendarVertical; offset anchors (3-4 strikes) = a DiagonalVertical — the same split as
		// DoubleCalendar vs DoubleDiagonal above. Single-expiry 4-leggers are butterflies/condors by spread.
		if (legCount >= 4 && distinctExpiries >= 2) return distinctStrikes <= 2 ? "CalendarVertical" : "DiagonalVertical";
		if (legCount >= 4) return distinctStrikes <= 3 ? "Butterfly" : "Condor";

		return (distinctExpiries > 1, distinctStrikes > 1) switch
		{
			(true, false) => "Calendar",
			(false, true) => "Vertical",
			(true, true) => "Diagonal",
			_ => "Spread"
		};
	}
}

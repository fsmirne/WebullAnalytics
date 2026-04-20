namespace WebullAnalytics;

public static class MatchKeys
{
	public const string StockPrefix = "stock:";
	public const string OptionPrefix = "option:";

	public static string Stock(string symbol) => $"{StockPrefix}{symbol}";
	public static string Option(string symbol) => $"{OptionPrefix}{symbol}";

	public static bool TryGetOptionSymbol(string matchKey, out string symbol)
	{
		symbol = string.Empty;
		if (string.IsNullOrEmpty(matchKey) || !matchKey.StartsWith(OptionPrefix, StringComparison.Ordinal))
			return false;
		symbol = matchKey[OptionPrefix.Length..];
		return symbol.Length > 0;
	}

	/// <summary>Parses an option match key into (parsed, symbol), or null if it's not a valid option match key.</summary>
	public static (OptionParsed parsed, string symbol)? ParseOption(string matchKey)
	{
		if (!TryGetOptionSymbol(matchKey, out var symbol)) return null;
		var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
		return parsed == null ? null : (parsed, symbol);
	}

	/// <summary>
	/// Returns the trailing OCC suffix "{C|P}{strike*1000 as 8-digit}" used to match option
	/// match keys by strike and right. Example: strike=25, callPut="C" → "C00025000".
	/// </summary>
	public static string OccSuffix(decimal strike, string callPut) => $"{callPut}{(long)(strike * 1000m):D8}";

	/// <summary>Builds a full OCC option symbol from components. Example: root="GME", expiry=2026-02-13, strike=25, callPut="C" → "GME260213C00025000".</summary>
	public static string OccSymbol(string root, DateTime expiry, decimal strike, string callPut) =>
		$"{root}{expiry:yyMMdd}{OccSuffix(strike, callPut)}";

	private const string StrategyPrefix = "strategy:";

	/// <summary>
	/// Extracts the root ticker symbol from any MatchKey format.
	/// Stock: "stock:GME" → "GME". Option: "option:GME260213C00025000" → "GME". Strategy: "strategy:Vertical:GME:..." → "GME".
	/// </summary>
	public static string? GetTicker(string matchKey)
	{
		if (matchKey.StartsWith(StockPrefix, StringComparison.Ordinal))
			return matchKey[StockPrefix.Length..];

		if (matchKey.StartsWith(OptionPrefix, StringComparison.Ordinal))
			return ParsingHelpers.ParseOptionSymbol(matchKey[OptionPrefix.Length..])?.Root;

		if (matchKey.StartsWith(StrategyPrefix, StringComparison.Ordinal))
		{
			// strategy:kind:ROOT:date:legs — ROOT is the third colon-delimited segment
			var parts = matchKey.Split(':');
			return parts.Length >= 3 ? parts[2] : null;
		}

		return null;
	}
}

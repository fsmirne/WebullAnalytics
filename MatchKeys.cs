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

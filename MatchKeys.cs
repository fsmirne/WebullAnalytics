namespace WebullAnalytics;

public static class MatchKeys
{
	public const string StockPrefix = "stock:";
	public const string OptionPrefix = "option:";
	public const string StrategyPrefix = "strategy:";

	public static string Stock(string symbol) => $"{StockPrefix}{symbol}";
	public static string Option(string symbol) => $"{OptionPrefix}{symbol}";
	public static string Strategy(string keyRemainder) => $"{StrategyPrefix}{keyRemainder}";

	public static bool TryGetOptionSymbol(string matchKey, out string symbol)
	{
		symbol = string.Empty;
		if (string.IsNullOrEmpty(matchKey) || !matchKey.StartsWith(OptionPrefix, StringComparison.Ordinal))
			return false;
		symbol = matchKey[OptionPrefix.Length..];
		return symbol.Length > 0;
	}
}

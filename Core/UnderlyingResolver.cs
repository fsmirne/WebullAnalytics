namespace WebullAnalytics;

/// <summary>Maps an option root to the underlying tradable symbol used for market-data lookups.
/// SPXW options track SPX (same price action); the option root is a Webull/CBOE convention for the
/// weekly-expiry-only flavor of the chain. For ETFs and single names the root and underlying are
/// the same and the resolver returns the input unchanged.
///
/// Used wherever code needs to fetch underlying price action (intraday bars, daily closes) for a
/// ticker that's configured as an option root. Yahoo-specific symbol decoration (e.g. <c>^SPX</c>)
/// is layered on top by <see cref="Api.YahooOptionsClient"/>; this resolver returns the logical
/// underlying symbol only.</summary>
public static class UnderlyingResolver
{
	private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
	{
		{ "SPXW", "SPX" },
	};

	public static string ResolveUnderlying(string root) => Map.TryGetValue(root, out var mapped) ? mapped : root;
}

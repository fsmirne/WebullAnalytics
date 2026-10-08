namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Provides current open positions, filtered to the configured tickers.
/// Implementations: LivePositionSource (Webull OpenAPI) and ReplayPositionSource (orders.jsonl).
/// </summary>
internal interface IPositionSource
{
	/// <summary>Returns the open positions at the given logical time.</summary>
	/// <param name="asOf">Logical timestamp. Live implementations ignore this. Replay uses it to rebuild state.</param>
	/// <param name="tickers">Filter: only return positions whose underlying is in this set. Case-insensitive.</param>
	/// <param name="cycleToken">Identity of the live tick this call belongs to. The live source shares its broker order pull with
	/// every other caller holding the same token (see <see cref="TodayOrdersFeed"/>); null always pulls fresh. Offline sources ignore it.</param>
	Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation, object? cycleToken = null);

	/// <summary>Returns total free cash and account value at the given logical time.</summary>
	Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation);
}

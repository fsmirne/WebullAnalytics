using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Open interest for specific contracts as reported on one day: the daily OI snapshot store (<see cref="ChainSnapshotOiCache"/>)
/// first, then ThetaData for the contracts it lacks (the store only holds the backfilled roots and non-zero OI) when a ThetaData login is
/// configured. A contract neither covers is absent — OI is never carried over from another day.</summary>
internal static class AsOfOpenInterest
{
	internal static async Task<IReadOnlyDictionary<string, long>> LoadAsync(IReadOnlyCollection<string> symbols, DateTime date, ThetaFallback? theta, CancellationToken cancellation)
	{
		var snapshots = new ChainSnapshotOiCache();
		var openInterest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		foreach (var symbol in symbols)
			if (ParsingHelpers.ParseOptionSymbol(symbol) is { } parsed && snapshots.ForDay(parsed.Root, date).TryGetValue(symbol, out var snapshot))
				openInterest[symbol] = snapshot.Oi;

		var missing = symbols.Where(s => !openInterest.ContainsKey(s) && ParsingHelpers.ParseOptionSymbol(s) != null).ToList();
		if (missing.Count > 0 && theta != null)
			foreach (var (symbol, oi) in (await theta.OpenInterestAsync(missing, date, cancellation)).OpenInterest)
				openInterest[symbol] = oi;
		return openInterest;
	}

	/// <summary>Every contract's open interest in one expiry as reported on <paramref name="date"/>: the day's snapshot when one exists
	/// (it holds the whole chain), else one expiry-wide ThetaData request. Returns the map plus, when neither source has the expiry, why.</summary>
	internal static async Task<(IReadOnlyDictionary<string, long> OpenInterest, string? Miss)> LoadExpiryAsync(string root, DateTime expiry, DateTime date, ThetaFallback? theta, CancellationToken cancellation)
	{
		if (new ChainSnapshotOiCache().ByExpiry(root, date).TryGetValue(expiry.Date, out var contracts) && contracts.Count > 0)
			return (contracts.ToDictionary(c => c.Symbol, c => c.Oi, StringComparer.OrdinalIgnoreCase), null);
		if (theta == null) return (new Dictionary<string, long>(), $"no data/oi snapshot for {date:yyyy-MM-dd} and no ThetaData login configured");
		var (openInterest, error) = await theta.ExpiryOpenInterestAsync(root, expiry, date, cancellation);
		return openInterest.Count > 0 ? (openInterest, null) : (openInterest, $"no data/oi snapshot for {date:yyyy-MM-dd}; ThetaData: {error ?? "no open interest reported"}");
	}
}

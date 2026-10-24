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
}

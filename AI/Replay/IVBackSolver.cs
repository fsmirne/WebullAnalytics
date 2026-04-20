namespace WebullAnalytics.AI.Replay;

/// <summary>
/// Back-solves implied volatility from a historical option fill price + contemporaneous
/// underlying price, using Black-Scholes via OptionMath. Between registered fills the
/// back-solved vol is held constant (piecewise-constant assumption).
/// </summary>
internal sealed class IVBackSolver
{
	private readonly Dictionary<string, List<(DateTime ts, decimal price, decimal underlying)>> _fillsBySymbol = new();

	/// <summary>Registers a historical fill for symbol at timestamp, paired with the day's underlying close.</summary>
	public void RegisterFill(string symbol, DateTime ts, decimal fillPrice, decimal underlyingAtTs)
	{
		if (!_fillsBySymbol.TryGetValue(symbol, out var list))
			_fillsBySymbol[symbol] = list = new();
		list.Add((ts, fillPrice, underlyingAtTs));
	}

	/// <summary>Returns a back-solved IV for the given symbol at asOf, using the nearest fill within 30 days.
	/// Returns null when no suitable fill exists.</summary>
	public decimal? ResolveIV(string symbol, DateTime asOf, DateTime expiry, decimal strike, string callPut)
	{
		if (!_fillsBySymbol.TryGetValue(symbol, out var list) || list.Count == 0) return null;

		var anchor = list.OrderBy(f => Math.Abs((f.ts - asOf).TotalSeconds)).First();
		if (Math.Abs((anchor.ts - asOf).TotalDays) > 30) return null;

		var dte = Math.Max(1, (expiry.Date - anchor.ts.Date).Days);
		var timeYears = dte / 365.0;
		const double riskFreeRate = 0.036;

		return OptionMath.ImpliedVol(
			spot: anchor.underlying,
			strike: strike,
			timeYears: timeYears,
			riskFreeRate: riskFreeRate,
			marketPrice: anchor.price,
			callPut: callPut
		);
	}
}

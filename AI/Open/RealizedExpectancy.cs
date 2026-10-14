namespace WebullAnalytics.AI;

/// <summary>Inputs the scorer needs to price a trade the way it will actually be managed: round-trip friction and the
/// take-profit / stop-loss levels the exit rules will act on. The path-dependent expectation itself lives in
/// <see cref="PathExitModel"/>; all functions here are pure.</summary>
internal static class RealizedExpectancy
{
	/// <summary>Round-trip per-contract friction in dollars:
	/// <c>slippagePerSharePerOrder × ordersForStructure × 100 × roundTrips</c>. Right shape for
	/// combo execution where the broker fills each structure at one net price — multi-leg combos
	/// pay one cross of the spread, not one per leg. Returns 0 when the feature is disabled or
	/// the slippage knob is at 0. Used by the scorer for whole-lifecycle EV estimation; the
	/// backtest's per-fill deduction uses <see cref="ComputeFrictionPerOrderPerContract"/>.</summary>
	public static decimal ComputeFrictionPerContract(OpenerRealizedExpectancyConfig cfg, OpenStructureKind structureKind)
	{
		if (!cfg.Enabled || cfg.RoundTrips <= 0 || cfg.SlippagePerSharePerOrder <= 0m) return 0m;
		return cfg.SlippagePerSharePerOrder * OrdersForStructure(structureKind) * 100m * cfg.RoundTrips;
	}

	/// <summary>Per-broker-execution friction in dollars per contract: <c>slippagePerSharePerOrder ×
	/// ordersForStructure × 100</c>. The backtest deducts this on each fill (open / close / roll) so
	/// realized cash flow matches the friction-aware EV the scorer used to rank the trade.
	/// Roundtrips is irrelevant here — each fill represents exactly one execution.</summary>
	public static decimal ComputeFrictionPerOrderPerContract(OpenerRealizedExpectancyConfig cfg, OpenStructureKind structureKind)
	{
		if (!cfg.Enabled || cfg.SlippagePerSharePerOrder <= 0m) return 0m;
		return cfg.SlippagePerSharePerOrder * OrdersForStructure(structureKind) * 100m;
	}

	/// <summary>Number of independent broker orders required to enter <paramref name="kind"/>.
	/// Double calendar/diagonal and the diagonal-from-verticals need 2 orders on Webull (the broker
	/// doesn't combo them as a single net-price trade — and the diagonal-vertical is two separate
	/// verticals by construction); every other supported structure fills in 1.</summary>
	internal static int OrdersForStructure(OpenStructureKind kind) => StructureKindInfo.OrderCount(kind);

	/// <summary>P&amp;L per contract (&gt; 0) at which TakeProfitRule closes: <c>pct × |entry premium|</c> (debit paid or credit
	/// received). Null when realized-EV scoring is off or no take-profit is armed.</summary>
	public static decimal? TakeProfitPerContract(decimal entryValuePerContract, OpenerRealizedExpectancyConfig cfg) =>
		cfg.Enabled && cfg.TakeProfitPctOfPremium > 0m && entryValuePerContract != 0m ? cfg.TakeProfitPctOfPremium * Math.Abs(entryValuePerContract) : null;

	/// <summary>Loss per contract (&gt; 0) at which StopLossRule closes: the nearer of <c>pctOfMaxLoss × |maxLoss|</c> and
	/// <c>pctOfMaxProfit × maxProfit</c>. Null when realized-EV scoring is off or no stop is armed (a disabled stop is
	/// populated as pct-of-max-loss 1.0, which never fires before max loss).</summary>
	public static decimal? StopLossPerContract(decimal maxProfit, decimal maxLoss, OpenerRealizedExpectancyConfig cfg)
	{
		if (!cfg.Enabled) return null;
		decimal? stop = null;
		if (cfg.StopLossPctOfMaxLoss < 1m && maxLoss < 0m) stop = cfg.StopLossPctOfMaxLoss * Math.Abs(maxLoss);
		if (cfg.StopLossPctOfMaxProfit > 0m && maxProfit > 0m)
		{
			var byProfit = cfg.StopLossPctOfMaxProfit * maxProfit;
			stop = stop.HasValue ? Math.Min(stop.Value, byProfit) : byProfit;
		}
		return stop is > 0m ? stop : null;
	}
}

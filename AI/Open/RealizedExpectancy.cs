namespace WebullAnalytics.AI;

/// <summary>Adjusts the theoretical EV/PnL view with two corrections pros always apply:
/// <list type="number">
///   <item><description><b>Managed stop</b>: scenarios are floored at the stop-loss level rather than
///   carried to their full theoretical loss (−50% max loss by default, the "2× credit stop" on typical
///   short verticals). Winners ride to their theoretical max — the profit-target cap was removed with
///   Target B, so only the downside is clamped.</description></item>
///   <item><description><b>Slippage</b>: charges per-leg half-spread × round-trip count per
///   contract. The flooring is path-conservative — it credits the managed stop only at terminal
///   scenario points, ignoring the optionality of stopping intra-life when the path crosses the
///   floor. Errs in the safe direction.</description></item>
/// </list>
/// All functions are pure. Caller decides whether the feature is on by passing
/// <see cref="OpenerRealizedExpectancyConfig.Enabled"/>; when off, clamping no-ops and the caller
/// should also pass <c>friction = 0</c> so the realized EV collapses to the theoretical EV.</summary>
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

	/// <summary>Stop-loss dollar floor (≤ 0). Returns the raw <paramref name="maxLoss"/> when the
	/// feature is disabled. <paramref name="maxLoss"/> is expected to be ≤ 0 (signed loss); the
	/// floor is constructed as <c>-stopPct × |maxLoss|</c>.</summary>
	public static decimal StopLossPerContract(decimal maxLoss, OpenerRealizedExpectancyConfig cfg) =>
		cfg.Enabled ? -cfg.StopLossPctOfMaxLoss * Math.Abs(maxLoss) : maxLoss;

	/// <summary>Floors one scenario's theoretical PnL at the stop-loss level and subtracts friction. Winners
	/// ride to their theoretical max (the profit-target cap was removed with Target B), so only the downside
	/// is clamped. When the feature is disabled, returns the input minus friction unchanged — the caller is
	/// responsible for passing <c>friction = 0</c> in that case (see <see cref="ComputeFrictionPerContract"/>).</summary>
	public static decimal RealizePnl(decimal theoreticalPnl, decimal maxLoss, decimal frictionPerContract, OpenerRealizedExpectancyConfig cfg)
	{
		if (!cfg.Enabled) return theoreticalPnl - frictionPerContract;

		var stopFloor = StopLossPerContract(maxLoss, cfg);
		// Degenerate (maxLoss == 0 → no meaningful floor): just subtract friction.
		if (stopFloor >= 0m) return theoreticalPnl - frictionPerContract;

		return Math.Max(theoreticalPnl, stopFloor) - frictionPerContract;
	}

	/// <summary>Realized EV across a scenario grid. Each grid point's PnL is run through
	/// <see cref="RealizePnl"/> before being weighted. When the feature is disabled, this equals
	/// the theoretical EV minus friction (which is 0 if the caller wired the gate correctly).</summary>
	public static decimal RealizeEv(IReadOnlyList<CandidateScorer.ScenarioPoint> grid, Func<decimal, decimal> theoreticalPnlAt, decimal maxLoss, decimal frictionPerContract, OpenerRealizedExpectancyConfig cfg)
	{
		decimal ev = 0m;
		foreach (var pt in grid)
		{
			var theo = theoreticalPnlAt(pt.SpotAtExpiry);
			ev += pt.Weight * RealizePnl(theo, maxLoss, frictionPerContract, cfg);
		}
		return ev;
	}
}

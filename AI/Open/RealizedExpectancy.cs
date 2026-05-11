namespace WebullAnalytics.AI;

/// <summary>Adjusts the theoretical EV/PnL view with two corrections pros always apply:
/// <list type="number">
///   <item><description><b>Managed exits</b>: scenarios are clamped to a profit-target/stop window
///   rather than carried to expiry. Closes credit spreads at 50% max profit / -50% max loss by
///   default (tastytrade convention) — captures roughly the "2× credit stop" rule on typical
///   short verticals while staying structure-agnostic.</description></item>
///   <item><description><b>Slippage</b>: charges per-leg half-spread × round-trip count per
///   contract. The clamping is path-conservative — it credits the managed exit only at terminal
///   scenario points, ignoring the optionality of closing intra-life when the path crosses the
///   target. Errs in the safe direction (under-estimates managed-exit value).</description></item>
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
	/// the slippage knob is at 0.</summary>
	public static decimal ComputeFrictionPerContract(OpenerRealizedExpectancyConfig cfg, OpenStructureKind structureKind)
	{
		if (!cfg.Enabled || cfg.RoundTrips <= 0 || cfg.SlippagePerSharePerOrder <= 0m) return 0m;
		return cfg.SlippagePerSharePerOrder * OrdersForStructure(structureKind) * 100m * cfg.RoundTrips;
	}

	/// <summary>Number of independent broker orders required to enter <paramref name="kind"/>.
	/// Only double calendar and double diagonal need 2 orders on Webull (the broker doesn't combo
	/// them as a single net-price trade); every other supported structure fills in 1.</summary>
	internal static int OrdersForStructure(OpenStructureKind kind) => kind switch
	{
		OpenStructureKind.DoubleCalendar or OpenStructureKind.DoubleDiagonal => 2,
		_ => 1,
	};

	/// <summary>Profit-target dollar cap (≥ 0). Returns the raw <paramref name="maxProfit"/> when
	/// the feature is disabled so callers can blindly clamp without branching.</summary>
	public static decimal ProfitTargetPerContract(decimal maxProfit, OpenerRealizedExpectancyConfig cfg) =>
		cfg.Enabled ? Math.Max(0m, cfg.ProfitTargetPctOfMaxProfit * maxProfit) : maxProfit;

	/// <summary>Stop-loss dollar floor (≤ 0). Returns the raw <paramref name="maxLoss"/> when the
	/// feature is disabled. <paramref name="maxLoss"/> is expected to be ≤ 0 (signed loss); the
	/// floor is constructed as <c>-stopPct × |maxLoss|</c>.</summary>
	public static decimal StopLossPerContract(decimal maxLoss, OpenerRealizedExpectancyConfig cfg) =>
		cfg.Enabled ? -cfg.StopLossPctOfMaxLoss * Math.Abs(maxLoss) : maxLoss;

	/// <summary>Clamps one scenario's theoretical PnL into the managed-exit window and subtracts
	/// friction. When the feature is disabled, returns the input minus friction unchanged — the
	/// caller is responsible for passing <c>friction = 0</c> in that case (see
	/// <see cref="ComputeFrictionPerContract"/>).</summary>
	public static decimal RealizePnl(decimal theoreticalPnl, decimal maxProfit, decimal maxLoss, decimal frictionPerContract, OpenerRealizedExpectancyConfig cfg)
	{
		if (!cfg.Enabled) return theoreticalPnl - frictionPerContract;

		var profitCap = ProfitTargetPerContract(maxProfit, cfg);
		var stopFloor = StopLossPerContract(maxLoss, cfg);
		// Defensive: if maxProfit and maxLoss are degenerate (both 0), clamp degenerates too; just
		// subtract friction to keep behavior monotonic in the slippage knob.
		if (profitCap <= 0m && stopFloor >= 0m)
			return theoreticalPnl - frictionPerContract;

		var clamped = Math.Clamp(theoreticalPnl, stopFloor, Math.Max(stopFloor, profitCap));
		return clamped - frictionPerContract;
	}

	/// <summary>Realized EV across a scenario grid. Each grid point's PnL is run through
	/// <see cref="RealizePnl"/> before being weighted. When the feature is disabled, this equals
	/// the theoretical EV minus friction (which is 0 if the caller wired the gate correctly).</summary>
	public static decimal RealizeEv(IReadOnlyList<CandidateScorer.ScenarioPoint> grid, Func<decimal, decimal> theoreticalPnlAt, decimal maxProfit, decimal maxLoss, decimal frictionPerContract, OpenerRealizedExpectancyConfig cfg)
	{
		decimal ev = 0m;
		foreach (var pt in grid)
		{
			var theo = theoreticalPnlAt(pt.SpotAtExpiry);
			ev += pt.Weight * RealizePnl(theo, maxProfit, maxLoss, frictionPerContract, cfg);
		}
		return ev;
	}
}

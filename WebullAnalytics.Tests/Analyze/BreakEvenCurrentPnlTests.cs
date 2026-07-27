using WebullAnalytics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>Locks the grid-refactor guarantee: a position's panel "Current P&L" (BreakEvenResult.CurrentPnl)
/// is computed from the SAME per-leg mark (OptionMath.LegMarkPerShare) as the portfolio aggregate, so the
/// two always reconcile — CurrentPnl == (aggregate market value of the position's legs) − its entry basis —
/// including under a future --date. Joined to the EvaluationDate collection so a parallel test can't leak a
/// stale override into ComputeOpenPositionsMarketValue's past-date short-circuit.</summary>
[Collection("EvaluationDate")]
public class BreakEvenCurrentPnlTests : IDisposable
{
	public BreakEvenCurrentPnlTests() => EvaluationDate.Set(DateTime.Today);
	public void Dispose() => EvaluationDate.Reset();

	private static PositionRow Leg(string occ, Side side, int qty, decimal price, DateTime expiry, string callPut)
		=> new(Instrument: occ, Asset: Asset.Option, OptionKind: callPut == "C" ? "Call" : "Put",
			   Side: side, Qty: qty, AvgPrice: price, Expiry: expiry, IsStrategyLeg: true, MatchKey: MatchKeys.Option(occ));

	[Theory]
	[InlineData(0)]    // same-day
	[InlineData(15)]   // future --date: CurrentPnl must still track the aggregate (decay + spot)
	public void PerPosition_CurrentPnl_ReconcilesWithAggregate(int forwardDays)
	{
		var near = DateTime.Today.AddDays(20);
		var far = DateTime.Today.AddDays(50);
		var shortOcc = MatchKeys.OccSymbol("GME", near, 25m, "C");
		var longOcc = MatchKeys.OccSymbol("GME", far, 25m, "C");

		// A valid strategy group: an OptionStrategy PARENT (net debit 1.80, qty 10) + the two Option legs.
		// AnalyzeGroup routes on parent.Asset — this is what production builds for a multi-leg position.
		var parent = new PositionRow(Instrument: "GME Diagonal", Asset: Asset.OptionStrategy, OptionKind: "Diagonal",
			Side: Side.Buy, Qty: 10, AvgPrice: 1.80m, Expiry: near, IsStrategyLeg: false, MatchKey: "GME-DIAG");
		var group = new List<PositionRow>
		{
			parent,
			Leg(longOcc, Side.Buy, 10, 3.00m, far, "C"),
			Leg(shortOcc, Side.Sell, 10, 1.20m, near, "C"),
		};
		var opts = new AnalysisOptions
		{
			UnderlyingPrices = new Dictionary<string, decimal> { ["GME"] = 24.50m },
			UnderlyingPriceOverrides = new Dictionary<string, decimal> { ["GME"] = 26.00m },
			IvOverrides = new Dictionary<string, decimal> { [shortOcc] = 0.60m, [longOcc] = 0.55m },
			Theoretical = true,
		};

		if (forwardDays > 0) EvaluationDate.Set(DateTime.Today.AddDays(forwardDays));

		var res = BreakEvenAnalyzer.Analyze(group, opts).Single(r => r.CurrentPnl.HasValue);

		// Independent aggregate over the same legs, via the shared valuation path.
		var lots = new Dictionary<string, List<Lot>>
		{
			[MatchKeys.Option(longOcc)] = new() { new Lot(Side.Buy, 10, 3.00m) },
			[MatchKeys.Option(shortOcc)] = new() { new Lot(Side.Sell, 10, 1.20m) },
		};
		var aggValue = TableBuilder.ComputeOpenPositionsMarketValue(lots, opts);

		Assert.NotNull(aggValue);
		Assert.True(res.EntryBasis.HasValue);
		// Reconciliation identity: position P&L == its legs' market value − what was paid/received to open.
		Assert.Equal(aggValue!.Value - res.EntryBasis!.Value, res.CurrentPnl!.Value, 2);
	}

	/// <summary>Default (non-theoretical) mode: the grid's eval-date column at the spot row must equal the
	/// headline Current P&L — i.e. the mid anchor is carried across the grid, not just the today column.
	/// A deliberate model-vs-market basis (IV overrides that don't match the quoted mids) makes the OLD
	/// col-0-only grid disagree by ~$1-2/share×qty; the carry-forward makes them reconcile (to within the
	/// grid column's 09:30 vs eval-now intraday-theta, kept tiny here via a far-dated short).</summary>
	[Fact]
	public void DefaultMode_GridEvalColumn_MatchesHeadline()
	{
		var near = DateTime.Today.AddDays(45);
		var far = DateTime.Today.AddDays(90);
		var shortOcc = MatchKeys.OccSymbol("GME", near, 25m, "C");
		var longOcc = MatchKeys.OccSymbol("GME", far, 25m, "C");
		var parent = new PositionRow(Instrument: "GME Diagonal", Asset: Asset.OptionStrategy, OptionKind: "Diagonal",
			Side: Side.Buy, Qty: 10, AvgPrice: 1.80m, Expiry: near, IsStrategyLeg: false, MatchKey: "GME-DIAG");
		var group = new List<PositionRow> { parent, Leg(longOcc, Side.Buy, 10, 3.00m, far, "C"), Leg(shortOcc, Side.Sell, 10, 1.20m, near, "C") };
		var opts = new AnalysisOptions
		{
			OptionQuotes = new Dictionary<string, OptionContractQuote> { [longOcc] = TestQuote.Q(2.95m, 3.05m, iv: 0.55m), [shortOcc] = TestQuote.Q(1.15m, 1.25m, iv: 0.60m) },
			UnderlyingPrices = new Dictionary<string, decimal> { ["GME"] = 24.50m },
			UnderlyingPriceOverrides = new Dictionary<string, decimal> { ["GME"] = 26.00m },
			IvOverrides = new Dictionary<string, decimal> { [shortOcc] = 0.60m, [longOcc] = 0.55m },  // NOT the mid-implied IV → real basis
			Theoretical = false,
		};

		EvaluationDate.Set(DateTime.Today.AddDays(15));  // future eval; short still 30 DTE so intraday theta is negligible

		var res = BreakEvenAnalyzer.Analyze(group, opts).Single(r => r.Grid != null && r.CurrentPnl.HasValue);
		var grid = res.Grid!;

		int col = -1;
		for (int c = 0; c < grid.DateColumns.Count; c++)
			if (grid.DateColumns[c].Date == EvaluationDate.Today.Date) col = c;
		Assert.True(col >= 0, "grid has an eval-date column");

		int row = 0; var best = decimal.MaxValue;
		for (int r = 0; r < grid.PriceRows.Count; r++) { var d = Math.Abs(grid.PriceRows[r] - 26.00m); if (d < best) { best = d; row = r; } }

		Assert.True(Math.Abs(grid.PnLs[row, col] - res.CurrentPnl!.Value) < 25m,
			$"grid eval cell {grid.PnLs[row, col]} vs headline {res.CurrentPnl} — carry-forward should reconcile them (old col-0-only differs by the ~$1000+ basis)");
	}
}

using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Guards the realized/unrealized lineage classification: a lineage that booked a terminal fill but is STILL
// open at the window end (a diagonal whose short leg Expired while the surviving long couldn't be closed for
// lack of quotes — e.g. --until at today's data frontier before the evening pull) is UNREALIZED. The pre-fix
// classification keyed "realized" off the mere presence of a Close/Expire fill, booking the half-settled
// lifecycle as a realized loss (the long's exit proceeds still outstanding) while excluding its MTM from
// unrealized — poisoning P&L, PF, expectancy and win rate. Companion guard: SimulatedBook re-keys a
// partial-expiry survivor without booking a fill under the new key, so the runner must resolve lineage via
// the book's own map (LineageFor), not a fills-derived PositionKey join.
public class BacktestResultClassificationTests
{
	private static BacktestFill Fill(long lineage, BacktestFillKind kind, decimal netCash, DateTime date) =>
		new(date, "SPY_LongDiagonal_745.00P_20260806", "SPY", "LongDiagonal", 1, kind, lineage, Array.Empty<BacktestLegFill>(), netCash, Fees: 0m, RuleName: null, Spot: 750m);

	private static BacktestResult Result(IReadOnlyList<BacktestFill> fills, IReadOnlyDictionary<long, decimal> endMtm) =>
		new(StartingCash: 50_000m, EndingCash: 50_000m, TotalFees: 0m, OpenFills: 0, CloseFills: 0, RollFills: 0, LegInFills: 0, ExpireFills: 0, MaxDrawdown: 0m, MaxDrawdownPct: 0m, PeakEquity: 50_000m,
			EquityCurve: Array.Empty<(DateTime, decimal)>(), Fills: fills, EndMtmByLineage: endMtm, Provenance: default, Cleanliness: default);

	[Fact]
	public void HalfSettledLineage_StillOpenAtEnd_IsUnrealizedNotRealized()
	{
		// Open a diagonal for a $1,000 debit; the short Expires worthless (cash 0) but the long survives at the window end, marked $800.
		var fills = new[] { Fill(1, BacktestFillKind.Open, -1_000m, new DateTime(2026, 7, 31, 9, 30, 0)), Fill(1, BacktestFillKind.Expire, 0m, new DateTime(2026, 8, 6, 16, 0, 0)) };
		var result = Result(fills, new Dictionary<long, decimal> { [1] = 800m });

		Assert.Equal(0m, result.RealizedPnL);            // the outstanding long means nothing is realized yet — pre-fix this booked -$1,000
		Assert.Equal(-200m, result.UnrealizedPnL);       // net cash -1000 + survivor MTM 800 — pre-fix this lineage was excluded entirely
		Assert.Empty(result.LifecyclePnLs());            // no per-trade stats contribution (win rate / PF / expectancy)
	}

	[Fact]
	public void SplitFillLineage_FullyClosed_IsRealizedOnce()
	{
		// Expire (short leg, cash 0) + Close (surviving long, +$900) finalize the lineage: one realized trade of -$100.
		var fills = new[] { Fill(1, BacktestFillKind.Open, -1_000m, new DateTime(2026, 7, 16, 9, 30, 0)), Fill(1, BacktestFillKind.Expire, 0m, new DateTime(2026, 7, 22, 16, 0, 0)), Fill(1, BacktestFillKind.Close, 900m, new DateTime(2026, 7, 22, 16, 0, 0)) };
		var result = Result(fills, new Dictionary<long, decimal>());

		Assert.Equal(-100m, result.RealizedPnL);
		Assert.Equal(0m, result.UnrealizedPnL);
		Assert.Equal(-100m, Assert.Single(result.LifecyclePnLs()));
	}

	[Fact]
	public void PartialExpiry_SurvivorKeepsLineageUnderNewKey()
	{
		var book = new SimulatedBook(10_000m, feePerContract: 0.05m, new OpenerRealizedExpectancyConfig());
		var opened = book.Open(new DateTime(2025, 9, 12, 9, 30, 0), "SPY", OpenStructureKind.LongDiagonal, new[]
		{
			new BacktestLegFill("SPY250916P00660000", Side.Sell, 1, 2.95m),
			new BacktestLegFill("SPY251010P00670000", Side.Buy, 1, 14.74m),
		}, qty: 1, spot: 660.0m);
		Assert.True(opened);
		var lineage = book.Fills[0].LineageId;

		// Short expires OTM (put 660 vs spot 670); the long survives under a NEW position key with NO fill of its own.
		var survivorKey = book.Expire(new DateTime(2025, 9, 16, 16, 0, 0), book.OpenPositions.Keys.Single(), spotAtExpiry: 670m);

		Assert.NotNull(survivorKey);
		Assert.Equal(lineage, book.LineageFor(survivorKey!));   // the book's map is the only way to recover the survivor's lineage
	}
}

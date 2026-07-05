using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Guards the Roll delta semantics: a roll's fills are the DELTA (close old short + open new short), and
// the post-roll position must be the old legs with that delta applied. The pre-fix implementation rebuilt
// the position from the delta fills alone — the untouched far-dated long vanished from the book (its
// entire remaining value silently lost) and the bought-back short survived as a phantom long. That hole
// manufactured the "0/10 rolled trades, −$800 avg" artifact that nearly convicted DefensiveRollRule.
public class SimulatedBookRollTests
{
	private const string LongSym = "SPY251010P00670000";   // far-dated long (the leg a roll never touches)
	private const string OldShortSym = "SPY250916P00660000";
	private const string NewShortSym = "SPY250919P00659000";

	private static SimulatedBook OpenDiagonal()
	{
		var book = new SimulatedBook(10_000m, feePerContract: 0.05m, new OpenerRealizedExpectancyConfig());
		var opened = book.Open(new DateTime(2025, 9, 12, 15, 49, 0), "SPY", OpenStructureKind.LongDiagonal, new[]
		{
			new BacktestLegFill(OldShortSym, Side.Sell, 1, 2.95m),
			new BacktestLegFill(LongSym, Side.Buy, 1, 14.74m),
		}, qty: 1, spot: 660.0m);
		Assert.True(opened);
		return book;
	}

	private static bool RollShort(SimulatedBook book) =>
		book.Roll(new DateTime(2025, 9, 15, 9, 30, 0), book.OpenPositions.Keys.Single(), new[]
		{
			new BacktestLegFill(OldShortSym, Side.Buy, 1, 1.475m),   // buy-to-close the threatened short
			new BacktestLegFill(NewShortSym, Side.Sell, 1, 3.625m),  // sell-to-open the replacement
		}, "DefensiveRollRule", spot: 659.5m);

	[Fact]
	public void Roll_ReplacesShortAndPreservesUntouchedLong()
	{
		var book = OpenDiagonal();
		Assert.True(RollShort(book));

		var pos = book.OpenPositions.Values.Single();
		Assert.Equal(2, pos.Legs.Count);
		var longLeg = Assert.Single(pos.Legs, l => l.Symbol == LongSym);
		Assert.Equal(Side.Buy, longLeg.Side);                                     // the far long survives, still long
		var shortLeg = Assert.Single(pos.Legs, l => l.Symbol == NewShortSym);
		Assert.Equal(Side.Sell, shortLeg.Side);                                   // the new short is short
		Assert.DoesNotContain(pos.Legs, l => l.Symbol == OldShortSym);            // no phantom leg from the buyback
		Assert.Equal(nameof(OpenStructureKind.LongDiagonal), pos.StrategyKind);
	}

	[Fact]
	public void Roll_PreservesLineageAndAdjustsBasisByRollCash()
	{
		var book = OpenDiagonal();
		var openDebitPerShare = book.OpenPositions.Values.Single().AdjustedNetDebit;   // 14.74 − 2.95 = 11.79
		Assert.True(RollShort(book));

		var pos = book.OpenPositions.Values.Single();
		// Roll delta: −1.475 (buyback) + 3.625 (new short) = +2.15/share credit → basis drops by 2.15.
		Assert.Equal(openDebitPerShare - 2.15m, pos.AdjustedNetDebit);
		var lineages = book.Fills.Select(f => f.LineageId).Distinct().ToList();
		Assert.Single(lineages);                                                   // one lifecycle across Open+Roll
		Assert.Equal(2, book.Fills.Count);
	}

	[Fact]
	public void Roll_ExpireAfterRoll_SettlesOnlyTheLegsActuallyHeld()
	{
		var book = OpenDiagonal();
		Assert.True(RollShort(book));

		// 09-16: the OLD short's expiry date. The position no longer holds it — nothing expires today
		// (survivors: 09/19 short + 10/10 long), so Expire must be a no-op for this position.
		var survivorKey = book.Expire(new DateTime(2025, 9, 16, 16, 0, 0), book.OpenPositions.Keys.Single(), spotAtExpiry: 661m);
		Assert.Null(survivorKey);
		var pos = book.OpenPositions.Values.Single();
		Assert.Equal(2, pos.Legs.Count);                                           // untouched — old short never settles
		Assert.DoesNotContain(book.Fills, f => f.Kind == BacktestFillKind.Expire);
	}
}

using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class AIVerifyCommandTests
{
	private static LiveWatchProposal MakeProposal(
		string structure,
		IReadOnlyList<VerifyLeg> legs,
		decimal spot = 730m,
		decimal? finalScore = 0.05000m,
		DateTime? timestamp = null) =>
		new(
			TimestampEt: timestamp ?? new DateTime(2026, 11, 18, 9, 30, 0),
			Structure: structure,
			Qty: 7,
			Legs: legs,
			Spot: spot,
			NetPerShare: -8.77m,
			RawScore: finalScore,
			FinalScore: finalScore,
			RepIv: 0.16m,
			CascadeSetup: null,
			CascadeScore: null,
			Breakevens: [],
			LegQuotes: new Dictionary<string, LiveLegQuote>());

	private static BacktestFill MakeBtOpen(
		string structure,
		IReadOnlyList<BacktestLegFill> legs,
		decimal spot = 730m,
		decimal? finalScore = 0.05000m,
		DateTime? timestamp = null) =>
		new(
			Date: timestamp ?? new DateTime(2026, 11, 18, 9, 30, 0),
			PositionKey: "SPY|1",
			Ticker: "SPY",
			StrategyKind: structure,
			Qty: 7,
			Kind: BacktestFillKind.Open,
			LineageId: 1,
			Legs: legs,
			NetCashFlow: -6139m,
			Fees: 2.24m,
			RuleName: null,
			Spot: spot,
			RawScore: finalScore,
			FinalScore: finalScore,
			RepIv: 0.16m);

	[Fact]
	public void ClassifyScoredComparison_ExactMatch_ReturnsMatch()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		]);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261123C00732000", Side.Sell, 7, 4.93m),
			new BacktestLegFill("SPY261211C00731000", Side.Buy, 7, 13.70m)
		]);

		var (verdict, note) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.Match, verdict);
		Assert.Equal("", note);
	}

	[Fact]
	public void ClassifyScoredComparison_UniformStrikeOffset_ReturnsStrikeShift()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], spot: 730.61m, finalScore: 0.05000m);
		// Reversed leg order + uniform +2 strike shift tracking spot Δ+1.39
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261123C00734000", Side.Sell, 11, 4.59m),
			new BacktestLegFill("SPY261211C00733000", Side.Buy, 11, 13.20m)
		], spot: 732.00m, finalScore: 0.05200m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.StrikeShift, verdict);
		Assert.Equal("strike +2", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_ShortStrikeOneStepAtScoreTie_ReturnsStrikeShift()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], spot: 730.00m, finalScore: 0.05000m);
		// Short leg moved one strike at a score tie. The tie rules only forgive LONG-leg strike drift, so this is a strike shift.
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211C00731000", Side.Buy, 7, 13.70m),
			new BacktestLegFill("SPY261123C00733000", Side.Sell, 7, 4.50m)
		], spot: 730.40m, finalScore: 0.05040m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.StrikeShift, verdict);
		Assert.Equal("buy 0, sell +1", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_SameTypeStrikeShiftBeyondTieTolerance_ReturnsStrikeShiftNotLegFlip()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], spot: 730.00m, finalScore: 0.05000m);
		// Same calls, same expiries; short leg moved 3 strikes, long leg 1, score gap well above tie tolerance.
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211C00732000", Side.Buy, 7, 13.10m),
			new BacktestLegFill("SPY261123C00735000", Side.Sell, 7, 3.90m)
		], spot: 730.20m, finalScore: 0.06000m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.StrikeShift, verdict);
		Assert.Equal("buy +1, sell +3", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_CallPutFlipOnSameExpiries_ReturnsLegFlip()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], spot: 730.61m, finalScore: 0.05000m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211P00731000", Side.Buy, 7, 13.20m),
			new BacktestLegFill("SPY261123P00730000", Side.Sell, 7, 4.59m)
		], spot: 730.61m, finalScore: 0.05200m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.LegFlip, verdict);
		Assert.Equal("call→put", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_DifferentStructure_ReturnsMismatch()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		]);
		var bt = MakeBtOpen("LongCalendar", [
			new BacktestLegFill("SPY261211C00731000", Side.Buy, 7, 13.70m),
			new BacktestLegFill("SPY261123C00731000", Side.Sell, 7, 5.20m)
		]);

		var (verdict, _) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.Mismatch, verdict);
	}

	[Fact]
	public void ClassifyScoredComparison_DifferentExpiriesOutsideTolerance_ReturnsMismatch()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], finalScore: 0.05000m);
		// Short leg 9 days later (beyond the 3-day neighboring-listing band) and long leg 35 days later.
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY270115C00731000", Side.Buy, 7, 14.50m),
			new BacktestLegFill("SPY261202C00732000", Side.Sell, 7, 5.50m)
		], finalScore: 0.06000m);

		var (verdict, _) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.Mismatch, verdict);
	}

	[Fact]
	public void ClassifyScoredComparison_OppositeSideAtShortStrikeAtScoreTie_ReturnsAtmTie()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00730000", 4.93m)
		], spot: 730.40m, finalScore: 0.05000m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211P00729000", Side.Buy, 7, 12.10m),
			new BacktestLegFill("SPY261123P00730000", Side.Sell, 7, 4.40m)
		], spot: 730.40m, finalScore: 0.05010m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.NearTie, verdict);
		Assert.Equal("ATM side tie", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_OppositeSideAwayFromShortStrike_IsNotAtmTie()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00730000", 4.93m)
		], spot: 733.00m, finalScore: 0.05000m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211P00729000", Side.Buy, 7, 12.10m),
			new BacktestLegFill("SPY261123P00730000", Side.Sell, 7, 4.40m)
		], spot: 733.00m, finalScore: 0.05010m);

		var (verdict, _) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.LegFlip, verdict);
	}

	[Fact]
	public void ClassifyScoredComparison_LongStrikeOneStepAtScoreTie_ReturnsLongStrikeTie()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], finalScore: 0.05000m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211C00730000", Side.Buy, 7, 14.30m),
			new BacktestLegFill("SPY261123C00732000", Side.Sell, 7, 4.93m)
		], finalScore: 0.05002m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.NearTie, verdict);
		Assert.Equal("adj. long strike", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_LongStrikeOneStepWithoutScoreTie_ReturnsStrikeShift()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], finalScore: 0.00200m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211C00730000", Side.Buy, 7, 14.30m),
			new BacktestLegFill("SPY261123C00732000", Side.Sell, 7, 4.93m)
		], finalScore: 0.00400m);

		var (verdict, _) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.StrikeShift, verdict);
	}

	[Fact]
	public void ClassifyScoredComparison_LongExpiryNeighborAtScoreTie_ReturnsLongExpiryTie()
	{
		// 07-21 case: live long 8/31 748P vs bt 8/21 749P, same short.
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY260831P00748000", 9.10m),
			new VerifyLeg("sell", "SPY260727P00746000", 3.20m)
		], spot: 747m, finalScore: 0.05000m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY260821P00749000", Side.Buy, 7, 8.90m),
			new BacktestLegFill("SPY260727P00746000", Side.Sell, 7, 3.20m)
		], spot: 747m, finalScore: 0.05004m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.NearTie, verdict);
		Assert.Equal("adj. long expiry", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_ShortExpiryNeighborAtScoreTie_ReturnsShortExpiryTie()
	{
		// 07-22 case: live sell 7/27 746P vs bt sell 7/28 746P, same long.
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY260831P00748000", 9.10m),
			new VerifyLeg("sell", "SPY260727P00746000", 3.20m)
		], spot: 747m, finalScore: 0.05000m);
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY260831P00748000", Side.Buy, 7, 9.10m),
			new BacktestLegFill("SPY260728P00746000", Side.Sell, 7, 3.45m)
		], spot: 747m, finalScore: 0.05049m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.NearTie, verdict);
		Assert.Equal("adj. short expiry", detail);
	}

	[Fact]
	public void FindEarlyOpen_FlagsOnlyWhenLiveEvaluatedTheBacktestsEarlierMinute()
	{
		var live = MakeProposal("LongDiagonal", [new VerifyLeg("buy", "SPY261211C00731000", 13.70m)], timestamp: new DateTime(2026, 10, 9, 10, 0, 12));
		var btEarly = MakeBtOpen("LongDiagonal", [new BacktestLegFill("SPY261211C00731000", Side.Buy, 1, 13.70m)], timestamp: new DateTime(2026, 10, 9, 9, 44, 0));

		var watchedThrough = new SortedSet<TimeSpan> { new(9, 30, 0), new(9, 44, 0), new(9, 50, 0), new(10, 0, 0) };
		var startedLate = new SortedSet<TimeSpan> { new(10, 0, 0) }; // watch first ticked at its open minute: never saw 09:44

		Assert.Same(btEarly, AIVerifyCommand.FindEarlyOpen(live, btEarly, watchedThrough));
		Assert.Null(AIVerifyCommand.FindEarlyOpen(live, btEarly, startedLate));
		Assert.Null(AIVerifyCommand.FindEarlyOpen(live, null, watchedThrough));
		var btLater = btEarly with { Date = new DateTime(2026, 10, 9, 10, 5, 0) };
		Assert.Null(AIVerifyCommand.FindEarlyOpen(live, btLater, watchedThrough));
	}

	[Fact]
	public void ScoresTie_IsRelativeOnly()
	{
		Assert.True(AIVerifyCommand.ScoresTie(0.00076m, 0.00070m));   // same legs, feed noise (~8%)
		Assert.False(AIVerifyCommand.ScoresTie(0.00070m, 0.00110m));  // 36% apart: no absolute floor rescues it
		Assert.False(AIVerifyCommand.ScoresTie(null, 0.001m));
	}

	[Fact]
	public void FromOpenFill_MapsTheBacktestPickToASlippedReplayOpen()
	{
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261123C00734000", Side.Sell, 1, 4.59m),
			new BacktestLegFill("SPY261211C00733000", Side.Buy, 1, 13.20m)
		], spot: 732.00m, finalScore: 0.05200m);

		var open = FillsReplayLoader.FromOpenFill(bt);

		Assert.Equal(bt.Date, open.OpenEt);
		Assert.Equal(OpenStructureKind.LongDiagonal, open.StructureKind);
		Assert.True(open.ApplySlippage);   // booked like the normal backtest's own open
		Assert.Equal([("sell", "SPY261123C00734000", 4.59m), ("buy", "SPY261211C00733000", 13.20m)], open.Legs.Select(l => (l.Action, l.Symbol, l.PricePerShare!.Value)));
		Assert.Throws<ArgumentException>(() => FillsReplayLoader.FromOpenFill(bt with { Kind = BacktestFillKind.Close }));
	}

	[Fact]
	public void PathDelta_IsBtMinusLiveOnlyWhenBothPicksWereFollowed()
	{
		var row = new VerifyRow(new DateOnly(2026, 11, 18), null, null, null, null, VerifyVerdict.StrikeShift, "",
			BacktestPickPnl: new PathPnl(67.90m, true, null, null), LivePickPnl: new PathPnl(49.90m, true, null, null));

		Assert.Equal(18.00m, AIVerifyRenderer.PathDelta(row));
		Assert.Null(AIVerifyRenderer.PathDelta(row with { LivePickPnl = null }));
	}

	[Fact]
	public void ExitCodeFor_FollowsSeverityLadder()
	{
		Assert.Equal(0, AIVerifyCommand.ExitCodeFor(VerifyVerdict.Match));
		Assert.Equal(3, AIVerifyCommand.ExitCodeFor(VerifyVerdict.NearTie));
		Assert.Equal(3, AIVerifyCommand.ExitCodeFor(VerifyVerdict.CadenceOffset));
		Assert.Equal(5, AIVerifyCommand.ExitCodeFor(VerifyVerdict.StrikeShift));
		Assert.Equal(4, AIVerifyCommand.ExitCodeFor(VerifyVerdict.LegFlip));
		Assert.Equal(2, AIVerifyCommand.ExitCodeFor(VerifyVerdict.NoBackfill));
		Assert.Equal(1, AIVerifyCommand.ExitCodeFor(VerifyVerdict.Mismatch));
		Assert.True(VerifyVerdict.Mismatch > VerifyVerdict.NoBackfill && VerifyVerdict.NoBackfill > VerifyVerdict.LegFlip && VerifyVerdict.LegFlip > VerifyVerdict.StrikeShift && VerifyVerdict.StrikeShift > VerifyVerdict.NearTie);
	}

	[Fact]
	public void MarkCommand_PricesBothTradesAtTheGivenSizeSellFirst()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		]) with { LegQuotes = new Dictionary<string, LiveLegQuote> { ["SPY261211C00731000"] = new(13.60m, 13.80m), ["SPY261123C00732000"] = new(4.90m, 4.96m) } };
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211C00733000", Side.Buy, 1, 13.20m),
			new BacktestLegFill("SPY261123C00734000", Side.Sell, 1, 4.59m)
		]);

		Assert.Equal("wa analyze trade \"sell:SPY261123C00732000:7@4.93,buy:SPY261211C00731000:7@13.70;sell:SPY261123C00734000:7@4.59,buy:SPY261211C00733000:7@13.20\" --compare", AIVerifyRenderer.MarkCommand(live, bt, lots: 7));
	}

	[Fact]
	public void SameLegSet_IsOrderAndCaseIndependentButSideSensitive()
	{
		var live = new[]
		{
			new VerifyLeg("BUY", "spy261211c00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		};
		var btReordered = new[] { new BacktestLegFill("SPY261123C00732000", Side.Sell, 1, 4.90m), new BacktestLegFill("SPY261211C00731000", Side.Buy, 1, 13.65m) };
		var btOpposite = new[] { new BacktestLegFill("SPY261123C00732000", Side.Buy, 1, 4.80m), new BacktestLegFill("SPY261211C00731000", Side.Sell, 1, 14.00m) };

		Assert.True(AIVerifyCommand.SameLegSet(live, btReordered));
		Assert.False(AIVerifyCommand.SameLegSet(live, btOpposite));
	}

	[Fact]
	public void SettingsValidate_ChecksTickerDatesCadenceAndOutput()
	{
		Assert.True(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Date = "2026-11-18", Detail = true }.Validate().Successful);
		Assert.False(new AIVerifySettings { Strategy = "DC2", Date = "2026-11-18" }.Validate().Successful);
		Assert.True(new AIVerifySettings { Ticker = "SPY", Date = "2026-11-18" }.Validate().Successful); // strategy falls back to the ticker's defaultStrategy at run time
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Date = "not-a-date" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Since = "2026-11-19", Until = "2026-11-18" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Cadence = 0 }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Output = "json" }.Validate().Successful);
	}
}

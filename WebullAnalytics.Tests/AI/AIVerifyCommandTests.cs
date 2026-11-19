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
		decimal? finalScore = 0.05000m) =>
		new(
			TimestampEt: new DateTime(2026, 11, 18, 9, 30, 0),
			Ticker: "SPY",
			Strategy: "DC2",
			Structure: structure,
			Qty: 7,
			Legs: legs,
			Spot: spot,
			NetPerShare: -8.77m,
			RawScore: finalScore,
			FinalScore: finalScore,
			RepIv: 0.16m,
			CascadeSetup: null,
			CascadeScore: null);

	private static BacktestFill MakeBtOpen(
		string structure,
		IReadOnlyList<BacktestLegFill> legs,
		decimal spot = 730m,
		decimal? finalScore = 0.05000m) =>
		new(
			Date: new DateTime(2026, 11, 18, 9, 30, 0),
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

		var (verdict, _) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.Match, verdict);
	}

	[Fact]
	public void ClassifyScoredComparison_UniformStrikeOffset_ReturnsMatch()
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

		Assert.Equal(VerifyVerdict.Match, verdict);
		Assert.Contains("strike Δ+2", detail);
		Assert.Contains("spot Δ+1.39", detail);
	}

	[Fact]
	public void ClassifyScoredComparison_NonUniformAdjacentStrikeWithinScoreTolerance_ReturnsNearTie()
	{
		var live = MakeProposal("LongDiagonal", [
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		], spot: 730.00m, finalScore: 0.05000m);
		// Only one leg shifted by 1 strike (spread width changed), within score tolerance
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261211C00731000", Side.Buy, 7, 13.70m),
			new BacktestLegFill("SPY261123C00733000", Side.Sell, 7, 4.50m)
		], spot: 730.40m, finalScore: 0.05040m);

		var (verdict, detail) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.NearTie, verdict);
		Assert.Contains("adjacent strike tie", detail);
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
		Assert.Contains("call/put flip", detail);
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
		var bt = MakeBtOpen("LongDiagonal", [
			new BacktestLegFill("SPY261218C00731000", Side.Buy, 7, 14.50m),
			new BacktestLegFill("SPY261125C00732000", Side.Sell, 7, 5.50m)
		], finalScore: 0.06000m);

		var (verdict, _) = AIVerifyCommand.ClassifyScoredComparison(live, bt, strikeStep: 1m);

		Assert.Equal(VerifyVerdict.Mismatch, verdict);
	}

	[Fact]
	public void SameAndOppositeLegSet_OrderIndependent()
	{
		var a = new[]
		{
			new VerifyLeg("buy", "SPY261211C00731000", 13.70m),
			new VerifyLeg("sell", "SPY261123C00732000", 4.93m)
		};
		var sameReordered = new[]
		{
			new VerifyLeg("SELL", "spy261123c00732000", 4.90m),
			new VerifyLeg("BUY", "spy261211c00731000", 13.65m)
		};
		var opposite = new[]
		{
			new VerifyLeg("sell", "SPY261211C00731000", 14.00m),
			new VerifyLeg("buy", "SPY261123C00732000", 4.80m)
		};

		Assert.True(AIVerifyCommand.SameLegSet(a, sameReordered));
		Assert.False(AIVerifyCommand.SameLegSet(a, opposite));
		Assert.True(AIVerifyCommand.IsOppositeLegSet(a, opposite));
		Assert.False(AIVerifyCommand.IsOppositeLegSet(a, sameReordered));
	}

	[Fact]
	public void SettingsValidate_ChecksTickerStrategyDatesCadenceSourceAndOutput()
	{
		Assert.True(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Date = "2026-11-18" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Strategy = "DC2", Date = "2026-11-18" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Date = "2026-11-18" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Date = "not-a-date" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Since = "2026-11-19", Until = "2026-11-18" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Cadence = 0 }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Source = "bogus" }.Validate().Successful);
		Assert.False(new AIVerifySettings { Ticker = "SPY", Strategy = "DC2", Output = "json" }.Validate().Successful);
	}
}

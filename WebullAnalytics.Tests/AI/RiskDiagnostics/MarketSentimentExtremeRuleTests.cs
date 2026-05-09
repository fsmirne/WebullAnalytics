using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class MarketSentimentExtremeRuleTests
{
	private static RiskDiagnosticFacts FactsWithSentiment(string bias, decimal score, decimal? delta1w = null) =>
		new RiskDiagnosticFacts(
			StructureLabel: "test",
			DirectionalBias: bias,
			NetDelta: 0m,
			NetThetaPerDay: 0m,
			NetVega: 0m,
			ShortLegDteMin: 0,
			LongLegDteMax: 0,
			DteGapDays: 0,
			HasShortLeg: false,
			HasLongLeg: true,
			LongPremiumPaid: 0m,
			ShortPremiumReceived: 0m,
			NetCashPerShare: 0m,
			PremiumRatio: null,
			Spot: 25m,
			ShortLegOtm: false,
			ShortLegExtrinsic: 0m,
			LongLegStrike: 25m,
			ShortLegStrike: 0m,
			NetDeltaPostShort: 0m,
			Trend: null,
			MarketSentimentScore: score,
			MarketSentimentRating: score >= 75m ? "extreme greed" : score <= 24m ? "extreme fear" : "neutral",
			MarketSentimentDelta1Week: delta1w);

	[Fact]
	public void DoesNotFireWhenNoSentiment()
	{
		var f = FactsWithSentiment("bullish", 60m) with { MarketSentimentScore = null };
		Assert.Null(new MarketSentimentExtremeRule().TryEvaluate(f));
	}

	[Fact]
	public void DoesNotFireWhenScoreIsNeutralAndDeltaIsSmall()
	{
		var f = FactsWithSentiment("bullish", 55m, delta1w: 5m);
		Assert.Null(new MarketSentimentExtremeRule().TryEvaluate(f));
	}

	[Fact]
	public void FiresOnExtremeGreedAlignedWithBullishPosition()
	{
		var f = FactsWithSentiment("bullish", 84m);
		var hit = new MarketSentimentExtremeRule().TryEvaluate(f);
		Assert.NotNull(hit);
		Assert.Equal("market_sentiment_extreme", hit!.Id);
		Assert.Contains("84", hit.Message);
		Assert.Contains("bullish", hit.Message);
	}

	[Fact]
	public void FiresOnExtremeFearAlignedWithBearishPosition()
	{
		var f = FactsWithSentiment("bearish", 12m);
		var hit = new MarketSentimentExtremeRule().TryEvaluate(f);
		Assert.NotNull(hit);
		Assert.Contains("12", hit!.Message);
		Assert.Contains("bearish", hit.Message);
	}

	[Fact]
	public void DoesNotFireOnExtremeGreedWithBearishPositionAlone()
	{
		// Contrarian alignment — no warning; position is correctly leaning against the crowd.
		var f = FactsWithSentiment("bearish", 84m);
		Assert.Null(new MarketSentimentExtremeRule().TryEvaluate(f));
	}

	[Fact]
	public void DoesNotFireOnExtremeFearWithBullishPositionAlone()
	{
		var f = FactsWithSentiment("bullish", 12m);
		Assert.Null(new MarketSentimentExtremeRule().TryEvaluate(f));
	}

	[Fact]
	public void FiresOnRegimeShiftEvenAtMidScore()
	{
		var f = FactsWithSentiment("neutral", 55m, delta1w: 35m);
		var hit = new MarketSentimentExtremeRule().TryEvaluate(f);
		Assert.NotNull(hit);
		Assert.Contains("regime change", hit!.Message);
	}

	[Fact]
	public void DoesNotFireForNeutralBiasAtModerateExtreme()
	{
		// Score is at the edge of extreme greed but bias is neutral and delta is small — nothing to warn.
		var f = FactsWithSentiment("neutral", 76m, delta1w: 5m);
		Assert.Null(new MarketSentimentExtremeRule().TryEvaluate(f));
	}
}

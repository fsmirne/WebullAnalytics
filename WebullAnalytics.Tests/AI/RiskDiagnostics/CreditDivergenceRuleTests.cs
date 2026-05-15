using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class CreditDivergenceRuleTests
{
	private static RiskDiagnosticFacts FactsWith(string bias, decimal? composite, decimal? junk) =>
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
			Spot: 100m,
			ShortLegOtm: false,
			ShortLegExtrinsic: 0m,
			LongLegStrike: 100m,
			ShortLegStrike: 0m,
			NetDeltaPostShort: 0m,
			Trend: null,
			MarketSentimentScore: composite,
			MarketSentimentRating: null,
			MarketSentimentDelta1Week: null,
			JunkBondDemandScore: junk);

	[Fact]
	public void DoesNotFireWhenCompositeMissing()
	{
		var f = FactsWith("bullish", null, 25m);
		Assert.Null(new CreditDivergenceRule().TryEvaluate(f));
	}

	[Fact]
	public void DoesNotFireWhenJunkScoreMissing()
	{
		var f = FactsWith("bullish", 70m, null);
		Assert.Null(new CreditDivergenceRule().TryEvaluate(f));
	}

	[Fact]
	public void DoesNotFireBelowDivergenceThreshold()
	{
		var f = FactsWith("bullish", 60m, 35m);
		Assert.Null(new CreditDivergenceRule().TryEvaluate(f));
	}

	[Fact]
	public void FiresOnEquityGreedCreditFearAgainstBullishPosition()
	{
		var f = FactsWith("bullish", 66m, 26m);
		var hit = new CreditDivergenceRule().TryEvaluate(f);
		Assert.NotNull(hit);
		Assert.Equal("credit_divergence", hit!.Id);
		Assert.Contains("66", hit.Message);
		Assert.Contains("26", hit.Message);
		Assert.Contains("lead equity drawdowns", hit.Message);
	}

	[Fact]
	public void FiresOnEquityGreedCreditFearAgainstNeutralPosition()
	{
		var f = FactsWith("neutral", 70m, 30m);
		Assert.NotNull(new CreditDivergenceRule().TryEvaluate(f));
	}

	[Fact]
	public void DoesNotFireOnEquityGreedCreditFearAgainstBearishPosition()
	{
		var f = FactsWith("bearish", 70m, 30m);
		Assert.Null(new CreditDivergenceRule().TryEvaluate(f));
	}

	[Fact]
	public void FiresOnEquityFearCreditGreedAgainstBearishPosition()
	{
		var f = FactsWith("bearish", 30m, 70m);
		var hit = new CreditDivergenceRule().TryEvaluate(f);
		Assert.NotNull(hit);
		Assert.Contains("recovering ahead of equities", hit!.Message);
	}

	[Fact]
	public void DoesNotFireOnEquityFearCreditGreedAgainstBullishPosition()
	{
		var f = FactsWith("bullish", 30m, 70m);
		Assert.Null(new CreditDivergenceRule().TryEvaluate(f));
	}
}

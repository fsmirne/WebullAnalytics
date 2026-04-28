using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerShortVerticalTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m };

	private static (CandidateSkeleton skel, Dictionary<string, OptionContractQuote> quotes) PutCreditSpread()
	{
		// SPY put credit: sell 495P / buy 494P, 4 DTE (Mon → Fri)
		var exp = new DateTime(2026, 4, 24);
		var shortSym = MatchKeys.OccSymbol("SPY", exp, 495m, "P");
		var longSym = MatchKeys.OccSymbol("SPY", exp, 494m, "P");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.ShortPutVertical, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(0.60m, 0.65m, 0.40m),
			[longSym] = TestQuote.Q(0.18m, 0.22m, 0.40m)
		};
		return (skel, quotes);
	}

	[Fact]
	public void PutCreditSpreadCreditUsesMidByDefault()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
		// credit = ((0.625) − (0.20)) × 100 = 42.5
		Assert.Equal(42.5m, p.DebitOrCreditPerContract);
	}

	[Fact]
	public void PutCreditSpreadCapitalAtRiskIsWidthMinusCredit()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
		// width × 100 − credit = 1.0 × 100 − 42.5 = 57.5
		Assert.Equal(57.5m, p.CapitalAtRiskPerContract);
	}

	[Fact]
	public void PutCreditSpreadMaxProfitIsCredit()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
		Assert.Equal(42.5m, p.MaxProfitPerContract);
	}

	[Fact]
	public void PutCreditSpreadMaxLossIsNegativeRisk()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
		Assert.Equal(-57.5m, p.MaxLossPerContract);
	}

	[Fact]
	public void PutCreditSpreadBreakevenIsShortStrikeMinusCreditPerShare()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
		Assert.Equal(494.575m, p.Breakevens[0]); // 495 - 0.425
	}

	[Fact]
	public void PutCreditSpreadDirectionalFitIsPositiveOne()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
		Assert.Equal(1, p.DirectionalFit);
	}

	[Fact]
	public void PutCreditSpreadCanStillUseBidAskWhenExplicitlyRequested()
	{
		var (skel, quotes) = PutCreditSpread();
		var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg(), pricingMode: "bidask")!;
		Assert.Equal(38m, p.DebitOrCreditPerContract);
		Assert.Equal(62m, p.CapitalAtRiskPerContract);
	}
}

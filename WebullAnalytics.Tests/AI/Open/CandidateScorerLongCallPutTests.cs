using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerLongCallPutTests
{
	private const decimal Alpha = 0.5m;

	private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = Alpha, ProfitBandPct = 5m, StructureWeight = new() };

	[Fact]
	public void LongCallBreakevenIsStrikePlusDebit()
	{
		var asOf = new DateTime(2026, 4, 1);
		var exp = new DateTime(2026, 5, 15);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);

		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
		};

		var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;

		Assert.Single(p.Breakevens);
		// Breakeven = strike + mid by default
		Assert.Equal(505.00m, p.Breakevens[0]);
	}

	[Fact]
	public void LongCallCapitalAtRiskEqualsDebit()
	{
		var asOf = new DateTime(2026, 4, 1);
		var exp = new DateTime(2026, 5, 15);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
		};
		var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
		Assert.Equal(500m, p.CapitalAtRiskPerContract);   // 5.00 × 100
		Assert.Equal(-500m, p.DebitOrCreditPerContract);  // negative = debit
	}

	[Fact]
	public void LongCallPopIsProbSpotGreaterThanBreakeven()
	{
		var asOf = new DateTime(2026, 4, 1);
		var exp = new DateTime(2026, 5, 1); // 30 DTE
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(5.00m, 5.00m, 0.40m)
		};
		var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
		// Breakeven 505, spot 500 at 30 DTE with 40% IV: POP < 0.5, positive, bounded
		Assert.InRange((double)p.ProbabilityOfProfit, 0.30, 0.50);
	}

	[Fact]
	public void LongCallDirectionalFitIsPositiveOne()
	{
		var asOf = new DateTime(2026, 4, 1);
		var exp = new DateTime(2026, 5, 1);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(5.00m, 5.00m, 0.40m)
		};
		var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0.40m, Cfg())!;
		Assert.Equal(1, p.DirectionalFit);
		Assert.NotEqual(p.RawScore, p.BiasAdjustedScore);
	}

	[Fact]
	public void LongPutBreakevenIsStrikeMinusDebit()
	{
		var asOf = new DateTime(2026, 4, 1);
		var exp = new DateTime(2026, 5, 1);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "P");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongPut, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(3.90m, 4.10m, 0.40m)
		};
		var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
		Assert.Equal(496.00m, p.Breakevens[0]);
		Assert.Equal(-1, p.DirectionalFit);
	}

	[Fact]
	public void MissingQuoteReturnsNull()
	{
		var asOf = new DateTime(2026, 4, 1);
		var exp = new DateTime(2026, 5, 1);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>(); // empty
		var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg());
		Assert.Null(p);
	}
}

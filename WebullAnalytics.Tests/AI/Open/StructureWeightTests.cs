using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class StructureWeightTests
{
	private static OpenerConfig CfgWithWeight(string kind, decimal weight) => new()
	{
		IvDefaultPct = 40m,
		DirectionalFitWeight = 0.5m,
		ProfitBandPct = 5m,
		StructureWeight = new() { [kind] = weight },
	};

	[Fact]
	public void LongCallScoreScalesWithStructureWeight()
	{
		var exp = new DateTime(2026, 5, 15);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote> { [sym] = TestQuote.Q(5m, 5.10m, 0.40m) };

		var baseline = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, CfgWithWeight("LongCall", 1.0m))!;
		var halved = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, CfgWithWeight("LongCall", 0.5m))!;

		// RawScore unchanged; only BiasAdjustedScore reflects the weight.
		Assert.Equal(baseline.RawScore, halved.RawScore);
		Assert.Equal(baseline.BiasAdjustedScore * 0.5m, halved.BiasAdjustedScore);
	}

	[Fact]
	public void MissingKindDefaultsToOne()
	{
		var exp = new DateTime(2026, 5, 15);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote> { [sym] = TestQuote.Q(5m, 5.10m, 0.40m) };

		// Empty StructureWeight dict → LongCall kind not listed → weight defaults to 1.0. Verify by
		// comparing against an explicit weight=1.0 config: both should produce the same score.
		var emptyCfg = new OpenerConfig { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m, StructureWeight = new() };
		var explicitCfg = CfgWithWeight("LongCall", 1.0m);
		var pEmpty = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, emptyCfg)!;
		var pExplicit = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, explicitCfg)!;
		Assert.Equal(pExplicit.BiasAdjustedScore, pEmpty.BiasAdjustedScore);
	}
}

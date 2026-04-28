using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerDispatchTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m };

	[Fact]
	public void ScoreDispatchesLongCall()
	{
		var exp = new DateTime(2026, 5, 15);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(4.90m, 5.00m, 0.40m)
		};
		var p = CandidateScorer.Score(skel, spot: 500m, asOf: new DateTime(2026, 4, 1), quotes, bias: 0m, Cfg());
		Assert.NotNull(p);
		Assert.Equal(OpenStructureKind.LongCall, p!.StructureKind);
	}

	[Fact]
	public void RationaleMentionsStructureAndScore()
	{
		var exp = new DateTime(2026, 5, 15);
		var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[sym] = TestQuote.Q(4.90m, 5.00m, 0.40m)
		};
		var p = CandidateScorer.Score(skel, spot: 500m, asOf: new DateTime(2026, 4, 1), quotes, bias: 0.40m, Cfg());
		var rationale = CandidateScorer.BuildRationale(p!, bias: 0.40m, cfg: Cfg());
		// Rationale no longer prefixes the structure name; structure is already shown elsewhere in output.
		Assert.Contains("POP", rationale);
		Assert.Contains("+20", rationale); // 0.5 × 0.4 × 1 = 0.20 → +20% boost
	}
}

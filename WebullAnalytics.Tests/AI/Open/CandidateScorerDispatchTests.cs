using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerDispatchTests
{
	private static OpenerConfig Cfg() => new()
	{
		Indicators = new() { IvDefaultPct = 40m, StrikeStep = 1.0m },
		Weights = new() { DirectionalFit = 0.5m },
		ProfitBandPct = 5m,
	};

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

	[Fact]
	public void ScoreDispatchesIronButterfly()
	{
		var exp = new DateTime(2026, 5, 15);
		var longPut = MatchKeys.OccSymbol("SPY", exp, 495m, "P");
		var shortPut = MatchKeys.OccSymbol("SPY", exp, 500m, "P");
		var shortCall = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
		var longCall = MatchKeys.OccSymbol("SPY", exp, 505m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.IronButterfly, new[]
		{
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		}, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[longPut] = TestQuote.Q(0.80m, 0.90m, 0.40m),
			[shortPut] = TestQuote.Q(2.70m, 2.90m, 0.40m),
			[shortCall] = TestQuote.Q(2.80m, 3.00m, 0.40m),
			[longCall] = TestQuote.Q(0.75m, 0.85m, 0.40m)
		};

		var p = CandidateScorer.Score(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg());
		Assert.NotNull(p);
		Assert.Equal(OpenStructureKind.IronButterfly, p!.StructureKind);
	}

	[Fact]
	public void ScoreDispatchesDoubleDiagonal()
	{
		var shortExp = new DateTime(2026, 5, 15);
		var longExp = new DateTime(2026, 6, 19);
		var shortPut = MatchKeys.OccSymbol("SPY", shortExp, 495m, "P");
		var longPut = MatchKeys.OccSymbol("SPY", longExp, 494m, "P");
		var shortCall = MatchKeys.OccSymbol("SPY", shortExp, 505m, "C");
		var longCall = MatchKeys.OccSymbol("SPY", longExp, 506m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.DoubleDiagonal, new[]
		{
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortPut] = TestQuote.Q(1.00m, 1.10m, 0.40m),
			[longPut] = TestQuote.Q(1.50m, 1.60m, 0.40m),
			[shortCall] = TestQuote.Q(1.00m, 1.10m, 0.40m),
			[longCall] = TestQuote.Q(1.50m, 1.60m, 0.40m)
		};

		var p = CandidateScorer.Score(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg());
		Assert.NotNull(p);
		Assert.Equal(OpenStructureKind.DoubleDiagonal, p!.StructureKind);
	}

	[Fact]
	public void ScoreDispatchesIronCondor()
	{
		var exp = new DateTime(2026, 5, 15);
		var longPut = MatchKeys.OccSymbol("SPY", exp, 495m, "P");
		var shortPut = MatchKeys.OccSymbol("SPY", exp, 500m, "P");
		var shortCall = MatchKeys.OccSymbol("SPY", exp, 505m, "C");
		var longCall = MatchKeys.OccSymbol("SPY", exp, 510m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.IronCondor, new[]
		{
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		}, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[longPut] = TestQuote.Q(0.70m, 0.80m, 0.40m),
			[shortPut] = TestQuote.Q(1.70m, 1.90m, 0.40m),
			[shortCall] = TestQuote.Q(1.60m, 1.80m, 0.40m),
			[longCall] = TestQuote.Q(0.65m, 0.75m, 0.40m)
		};

		var p = CandidateScorer.Score(skel, spot: 502m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg());
		Assert.NotNull(p);
		Assert.Equal(OpenStructureKind.IronCondor, p!.StructureKind);
	}

	[Fact]
	public void ScoreDispatchesCondor_PopulatesExpectedMoveAndBreakevens()
	{
		// All-put long condor: long wings (80, 110), short body (90, 100). The opener never builds this
		// skeleton; this exercises the analyze-position path that scores an already-open condor.
		var exp = new DateTime(2026, 7, 17);
		var longLowWing = MatchKeys.OccSymbol("USO", exp, 80m, "P");
		var shortLowBody = MatchKeys.OccSymbol("USO", exp, 90m, "P");
		var shortHighBody = MatchKeys.OccSymbol("USO", exp, 100m, "P");
		var longHighWing = MatchKeys.OccSymbol("USO", exp, 110m, "P");
		var skel = new CandidateSkeleton("USO", OpenStructureKind.Condor, new[]
		{
			new ProposalLeg("buy", longLowWing, 1),
			new ProposalLeg("sell", shortLowBody, 1),
			new ProposalLeg("sell", shortHighBody, 1),
			new ProposalLeg("buy", longHighWing, 1)
		}, TargetExpiry: exp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[longLowWing] = TestQuote.Q(0.65m, 0.75m, 0.45m),
			[shortLowBody] = TestQuote.Q(2.40m, 2.60m, 0.45m),
			[shortHighBody] = TestQuote.Q(6.90m, 7.10m, 0.45m),
			[longHighWing] = TestQuote.Q(15.40m, 15.60m, 0.45m)
		};

		var p = CandidateScorer.Score(skel, spot: 95m, asOf: new DateTime(2026, 6, 15), quotes, bias: 0m, Cfg());
		Assert.NotNull(p);
		Assert.Equal(OpenStructureKind.Condor, p!.StructureKind);
		// The reported gap: EM must be populated so the risk diagnostic can show it.
		Assert.True(p.ExpectedMoveLower.HasValue && p.ExpectedMoveUpper.HasValue);
		Assert.True(p.ExpectedMoveLower!.Value < 95m && p.ExpectedMoveUpper!.Value > 95m);
		Assert.Equal(2, p.Breakevens.Count); // a long condor has two breakevens bounding the profit plateau
		Assert.InRange(p.ProbabilityOfProfit, 0m, 1m);

		var rationale = CandidateScorer.BuildRationale(p, bias: 0m, cfg: Cfg());
		Assert.Contains("EM ", rationale);
		Assert.Contains("BE ", rationale);
		Assert.Contains("POP", rationale);
	}
}

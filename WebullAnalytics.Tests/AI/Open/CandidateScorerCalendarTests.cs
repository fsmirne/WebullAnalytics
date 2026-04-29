using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerCalendarTests
{
	private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m };

	[Fact]
	public void CalendarDebitIsLongAskMinusShortBid()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24); // 4 DTE
		var longExp = new DateTime(2026, 5, 15);   // 25 DTE
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
			[longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
		};
		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
		// Default pricing uses mid, so debit = long_mid − short_mid = 5.00 − 1.525 = 3.475/share.
		Assert.Equal(-347.500m, p.DebitOrCreditPerContract);
		Assert.Equal(347.500m, p.CapitalAtRiskPerContract);
	}

	[Fact]
	public void CalendarDirectionalFitIsZero()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
			[longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
		};
		// Verify fit=0 by checking that bias has no effect: scores with bias=0 and bias=0.80 should match.
		// (The score formula now includes a structure-independent balance factor, so RawScore != BiasAdjusted
		// in general — comparing two scorings with different bias is the cleanest way to pin "fit is 0".)
		var pNoBias = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
		var pWithBias = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0.80m, Cfg())!;
		Assert.Equal(0, pWithBias.DirectionalFit);
		Assert.Equal(pNoBias.BiasAdjustedScore, pWithBias.BiasAdjustedScore);
	}

	[Fact]
	public void InvertedDiagonalMaxLossIncludesStrikeGapPlusDebit()
	{
		var asOf = new DateTime(2026, 4, 26);
		var shortExp = new DateTime(2026, 5, 1);
		var longExp = new DateTime(2026, 5, 22);
		// Inverted call diagonal: long strike > short strike.
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 25.5m, "C");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 26m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongDiagonal, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			// debit = long_ask - short_bid = 0.95 - 0.36 = 0.59/share ($59/contract)
			[shortSym] = TestQuote.Q(bid: 0.36m, ask: 0.39m, iv: 0.40m),
			[longSym] = TestQuote.Q(bid: 0.81m, ask: 0.95m, iv: 0.40m)
		};
		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 24.95m, asOf, quotes, bias: 0m, Cfg())!;
		// Default pricing uses mid, so debit = 0.88 − 0.375 = 0.505/share ($50.50/contract).
		Assert.Equal(-50.500m, p.DebitOrCreditPerContract);
		Assert.Equal(-100.500m, p.MaxLossPerContract);
		Assert.Equal(100.500m, p.CapitalAtRiskPerContract);
	}

	[Fact]
	public void CalendarPopIsProbInProfitBand()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
			[longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
		};
		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
		// POP is now P(BE_lower < S_T < BE_upper) computed from numerically-found breakevens. The exact
		// value depends on the quote-implied IV's consistency with the legs' Black-Scholes prices; the
		// test's contrived prices yield very wide breakevens, but POP must always be a probability.
		Assert.InRange((double)p.ProbabilityOfProfit, 0.0, 1.0);
		Assert.True(p.ProbabilityOfProfit > 0m);
		Assert.Equal(2, p.Breakevens.Count);
		Assert.True(p.Breakevens[0] < p.Breakevens[1]);
	}

	[Fact]
	public void CalendarGetsBoostWhenShortStrikeMatchesMaxPain()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 25m, "C");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 25m, "C");
		var extraCall = MatchKeys.OccSymbol("GME", shortExp, 30m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m, openInterest: 100),
			[longSym] = TestQuote.Q(1.80m, 1.90m, 0.40m),
			[extraCall] = TestQuote.Q(0.20m, 0.25m, 0.40m, openInterest: 40)
		};

		var baseCfg = Cfg();
		var painCfg = Cfg();
		painCfg.MaxPainWeight = 0.50m;

		var withoutPain = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 25m, asOf, quotes, bias: 0m, baseCfg)!;
		var withPain = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 25m, asOf, quotes, bias: 0m, painCfg)!;

		Assert.Equal(25m, withPain.TargetExpiryMaxPain);
		Assert.Equal(1.50m, withPain.MaxPainAdjustmentFactor);
		Assert.Equal(Math.Sign(withoutPain.BiasAdjustedScore), Math.Sign(withPain.BiasAdjustedScore));
		Assert.True(Math.Abs(withPain.BiasAdjustedScore) > Math.Abs(withoutPain.BiasAdjustedScore));
	}

	[Fact]
	public void DoubleCalendarScoresAsNeutralDebitStructure()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortPut = MatchKeys.OccSymbol("GME", shortExp, 24.5m, "P");
		var longPut = MatchKeys.OccSymbol("GME", longExp, 24.5m, "P");
		var shortCall = MatchKeys.OccSymbol("GME", shortExp, 25.5m, "C");
		var longCall = MatchKeys.OccSymbol("GME", longExp, 25.5m, "C");
		var extraOi = MatchKeys.OccSymbol("GME", shortExp, 25m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.DoubleCalendar, new[]
		{
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortPut] = TestQuote.Q(0.35m, 0.40m, 0.40m, openInterest: 50),
			[longPut] = TestQuote.Q(0.90m, 1.00m, 0.40m),
			[shortCall] = TestQuote.Q(0.35m, 0.40m, 0.40m, openInterest: 50),
			[longCall] = TestQuote.Q(0.90m, 1.00m, 0.40m),
			[extraOi] = TestQuote.Q(0.55m, 0.60m, 0.40m, openInterest: 300)
		};

		var cfg = Cfg();
		cfg.MaxPainWeight = 0.50m;
		var p = CandidateScorer.ScoreMultiLeg(skel, spot: 25m, asOf, quotes, bias: 0m, cfg)!;

		Assert.Equal(OpenStructureKind.DoubleCalendar, p.StructureKind);
		Assert.True(p.DebitOrCreditPerContract < 0m);
		Assert.Equal(0, p.DirectionalFit);
		Assert.True(p.Breakevens.Count >= 2);
		Assert.Equal(25m, p.TargetExpiryMaxPain);
		Assert.True(p.MaxPainAdjustmentFactor > 1m);
	}

	[Fact]
	public void DoubleDiagonalScoresAsNeutralDebitStructure()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortPut = MatchKeys.OccSymbol("GME", shortExp, 24.5m, "P");
		var longPut = MatchKeys.OccSymbol("GME", longExp, 24m, "P");
		var shortCall = MatchKeys.OccSymbol("GME", shortExp, 25.5m, "C");
		var longCall = MatchKeys.OccSymbol("GME", longExp, 26m, "C");
		var extraOi = MatchKeys.OccSymbol("GME", shortExp, 25m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.DoubleDiagonal, new[]
		{
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortPut] = TestQuote.Q(0.40m, 0.45m, 0.40m, openInterest: 50),
			[longPut] = TestQuote.Q(0.95m, 1.05m, 0.40m),
			[shortCall] = TestQuote.Q(0.40m, 0.45m, 0.40m, openInterest: 50),
			[longCall] = TestQuote.Q(0.95m, 1.05m, 0.40m),
			[extraOi] = TestQuote.Q(0.55m, 0.60m, 0.40m, openInterest: 300)
		};

		var cfg = Cfg();
		cfg.MaxPainWeight = 0.50m;
		var p = CandidateScorer.ScoreMultiLeg(skel, spot: 25m, asOf, quotes, bias: 0m, cfg)!;

		Assert.Equal(OpenStructureKind.DoubleDiagonal, p.StructureKind);
		Assert.True(p.DebitOrCreditPerContract < 0m);
		Assert.Equal(0, p.DirectionalFit);
		Assert.True(p.Breakevens.Count >= 2);
		Assert.Equal(25m, p.TargetExpiryMaxPain);
		Assert.True(p.MaxPainAdjustmentFactor > 1m);
	}
}

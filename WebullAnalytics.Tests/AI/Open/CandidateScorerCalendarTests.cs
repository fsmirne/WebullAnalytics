using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerCalendarTests
{
	private static OpenerConfig Cfg() => new()
	{
		Indicators = new() { IvDefaultPct = 40m, StrikeStep = 1.0m },
		Weights = new() { DirectionalFit = 0.5m },
		ProfitBandPct = 5m,
	};

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
	public void CoveredDiagonalOpenEndedUpside_HasNonZeroPop()
	{
		// Wide covered call diagonal: long $748 < short $768 (gap $20 > debit ~$15) stays profitable past
		// the short strike all the way to +∞ → only a LOWER breakeven exists. The old closed-band POP
		// returned 0 (no upper BE) and dropped the breakeven; the fix must give a real one-sided POP and
		// keep the breakeven.
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 29); // 9 DTE
		var longExp = new DateTime(2026, 5, 15);   // 25 DTE
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 768m, "C");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 748m, "C");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongDiagonal, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(3.60m, 3.80m, 0.13m),
			[longSym] = TestQuote.Q(18.90m, 19.10m, 0.13m)
		};
		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 760m, asOf, quotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;
		Assert.True(p.ProbabilityOfProfit > 0m, $"open-ended covered diagonal POP should be > 0, got {p.ProbabilityOfProfit}");
		Assert.NotEmpty(p.Breakevens);
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
	public void CalendarFallsBackToBlackScholesPricingWithWarningWhenBidAskMissing()
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
			[shortSym] = new OptionContractQuote(shortSym, LastPrice: null, Bid: null, Ask: null, Change: null, PercentChange: null, Volume: null, OpenInterest: null, ImpliedVolatility: 0.40m),
			[longSym] = new OptionContractQuote(longSym, LastPrice: null, Bid: null, Ask: null, Change: null, PercentChange: null, Volume: null, OpenInterest: null, ImpliedVolatility: 0.40m)
		};

		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;

		Assert.NotNull(p);
		Assert.NotNull(p.PricingWarning);
		Assert.Contains("fallback Black-Scholes pricing", p.PricingWarning);
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
			[shortPut] = TestQuote.Q(0.35m, 0.40m, 0.40m, openInterest: 100),
			[longPut] = TestQuote.Q(0.90m, 1.00m, 0.40m, openInterest: 100),
			[shortCall] = TestQuote.Q(0.35m, 0.40m, 0.40m, openInterest: 100),
			[longCall] = TestQuote.Q(0.90m, 1.00m, 0.40m, openInterest: 100),
			[extraOi] = TestQuote.Q(0.55m, 0.60m, 0.40m, openInterest: 300)
		};

		var cfg = Cfg();
		var p = CandidateScorer.ScoreMultiLeg(skel, spot: 25m, asOf, quotes, bias: 0m, cfg)!;

		Assert.Equal(OpenStructureKind.DoubleCalendar, p.StructureKind);
		Assert.True(p.DebitOrCreditPerContract < 0m);
		Assert.Equal(0, p.DirectionalFit);
		Assert.True(p.Breakevens.Count >= 2);
		Assert.Equal(25m, p.TargetExpiryMaxPain);
	}

	// ── Cost-basis / --spot POP correctness ──────────────────────────────────────────────────────

	// When analyze-position re-scores an existing calendar it collapses each leg's bid/ask to the
	// entry price (OverrideBidAskWithCostBasis).  If useMarketImpliedIv is left TRUE, MarketImpliedIv
	// back-solves ivLong from the entry price instead of the current market mid, which can inflate it
	// dramatically (e.g. $1.92 entry → IV≈1.04 vs $0.84 market → IV≈0.628).  That pushes the lower
	// breakeven well below current spot and inflates POP from ~40% to ~81%.  The fix sets
	// effectiveUseMarketImpliedIv=false in cost-basis mode so ivLong always comes from the broker's
	// reported Iv field (unchanged by the bid/ask override).
	//
	// Numbers match the GME x400 call calendar reported on 2026-05-04:
	//   spot=$23.84, short GME260508C00026500 (4 DTE, IV=1.059), long GME260605C00026500 (32 DTE, IV=0.628)
	//   entry: long@$1.92, short@$1.00 → net debit $0.92.

	[Fact]
	public void CalendarCostBasisMode_MarketImpliedIvTrue_InflatesPopVsCorrectIv()
	{
		var asOf = new DateTime(2026, 5, 4);
		var shortExp = new DateTime(2026, 5, 8);
		var longExp = new DateTime(2026, 6, 5);
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 26.50m, "C");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 26.50m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, [new ProposalLeg("sell", shortSym, 1), new ProposalLeg("buy", longSym, 1)], TargetExpiry: shortExp);

		// Simulates OverrideBidAskWithCostBasis: bid=ask=entry price; Iv field is UNCHANGED.
		var costBasisQuotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(bid: 1.00m, ask: 1.00m, iv: 1.059m),
			[longSym]  = TestQuote.Q(bid: 1.92m, ask: 1.92m, iv: 0.628m)
		};

		// BUG path (useMarketImpliedIv=true): MarketImpliedIv back-solves ivLong from mid=$1.92 at spot=$23.84.
		// The option is OTM so there's no intrinsic guard; the solver returns IV≈1.04 >> broker 0.628.
		// This inflates residual time value in the breakeven scan and drops the lower BE well below spot.
		var pBug = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 23.84m, asOf, costBasisQuotes, bias: 0m, Cfg(), useMarketImpliedIv: true)!;

		// FIX path (useMarketImpliedIv=false): ivLong = ResolveIv = broker Iv field = 0.628.
		// OverrideBidAskWithCostBasis does NOT touch Iv, so this is always the current market IV.
		var pFix = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 23.84m, asOf, costBasisQuotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;

		// Both paths compute the same cost-basis debit.
		Assert.Equal(pBug.DebitOrCreditPerContract, pFix.DebitOrCreditPerContract);

		// Bug path: inflated ivLong → lower BE ≈ $21.49 (well below spot $23.84) → POP ≈ 81%.
		Assert.True(pBug.ProbabilityOfProfit > 0.70m, $"Bug path POP {pBug.ProbabilityOfProfit:P1} should exceed 70%");
		Assert.True(pBug.Breakevens.Count == 2 && pBug.Breakevens[0] < 23.84m, $"Bug path lower BE {pBug.Breakevens[0]:F2} should be below spot $23.84");

		// Fix path: correct ivLong → lower BE ≈ $24+ (above spot $23.84) → POP ≈ 40%.
		Assert.True(pFix.ProbabilityOfProfit < 0.50m, $"Fix path POP {pFix.ProbabilityOfProfit:P1} should be below 50%");
		Assert.True(pFix.Breakevens.Count == 2 && pFix.Breakevens[0] > 23.84m, $"Fix path lower BE {pFix.Breakevens[0]:F2} should be above spot $23.84 (position below cost-basis breakeven)");

		// The inflation is material — not a rounding difference.
		Assert.True(pBug.ProbabilityOfProfit > pFix.ProbabilityOfProfit + 0.30m, $"Bug path POP should exceed fix path by >30pp; got {pBug.ProbabilityOfProfit:P1} vs {pFix.ProbabilityOfProfit:P1}");
	}

	[Fact]
	public void CalendarCostBasisMode_WhenEntryEqualsCurrentPrice_PopIsUnchangedByIvFlag()
	{
		// When the entry price equals the current market mid, back-solving IV from entry price
		// produces the same IV as ResolveIv (the broker reports IV consistent with its own mid).
		// Both useMarketImpliedIv paths should give essentially the same POP.
		var asOf = new DateTime(2026, 5, 4);
		var shortExp = new DateTime(2026, 5, 8);
		var longExp = new DateTime(2026, 6, 5);
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 26.50m, "C");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 26.50m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, [new ProposalLeg("sell", shortSym, 1), new ProposalLeg("buy", longSym, 1)], TargetExpiry: shortExp);

		// Entry price == current market price: bid=ask=mid of the current market.
		var atMarketQuotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(bid: 0.225m, ask: 0.225m, iv: 1.059m),
			[longSym]  = TestQuote.Q(bid: 0.835m, ask: 0.835m, iv: 0.628m)
		};

		var pTrue  = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 23.84m, asOf, atMarketQuotes, bias: 0m, Cfg(), useMarketImpliedIv: true)!;
		var pFalse = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 23.84m, asOf, atMarketQuotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;

		// When entry == market, back-solving from mid ≈ broker IV, so POP should be very close.
		Assert.InRange((double)Math.Abs(pTrue.ProbabilityOfProfit - pFalse.ProbabilityOfProfit), 0.0, 0.05);
	}

	[Fact]
	public void CalendarCostBasisMode_HypotheticalSpotAboveUpperBreakeven_PopLowerThanAtAtmSpot()
	{
		// Simulates "analyze position --spot 30" from the weekend when GME shot up.
		// With correct IV (useMarketImpliedIv=false, which --spot always uses), the upper breakeven
		// sits around $29–30.  A hypothetical spot at $30 is at or above the profit zone, so POP
		// should be lower than at ATM spot ($26.50 = peak profit point for the calendar).
		var asOf = new DateTime(2026, 5, 4);
		var shortExp = new DateTime(2026, 5, 8);
		var longExp = new DateTime(2026, 6, 5);
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 26.50m, "C");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 26.50m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, [new ProposalLeg("sell", shortSym, 1), new ProposalLeg("buy", longSym, 1)], TargetExpiry: shortExp);

		var costBasisQuotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(bid: 1.00m, ask: 1.00m, iv: 1.059m),
			[longSym]  = TestQuote.Q(bid: 1.92m, ask: 1.92m, iv: 0.628m)
		};

		// --spot always uses useMarketImpliedIv=false (stale market mids no longer reflect the hypothetical spot).
		var pAtm     = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 26.50m, asOf, costBasisQuotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;
		var pHighSpot = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 30.00m, asOf, costBasisQuotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;

		// At ATM spot ($26.50 = strike), the calendar is at peak profit — POP should be well above 50%.
		Assert.True(pAtm.ProbabilityOfProfit > 0.55m, $"ATM POP {pAtm.ProbabilityOfProfit:P1} should exceed 55%");

		// At spot=$30 the position is at or above the upper breakeven — POP should be < 50%.
		Assert.True(pHighSpot.ProbabilityOfProfit < 0.50m, $"High-spot POP {pHighSpot.ProbabilityOfProfit:P1} should be below 50%");

		// ATM is meaningfully better than the high-spot scenario.
		Assert.True(pAtm.ProbabilityOfProfit > pHighSpot.ProbabilityOfProfit + 0.10m,
			$"ATM POP {pAtm.ProbabilityOfProfit:P1} should exceed high-spot POP {pHighSpot.ProbabilityOfProfit:P1} by >10pp");

		// Upper breakeven should exist and be below the hypothetical $30 spot.
		Assert.True(pAtm.Breakevens.Count == 2);
		Assert.True(pAtm.Breakevens[1] < 30.00m, $"Upper BE {pAtm.Breakevens[1]:F2} should be below hypothetical spot $30");
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
			[shortPut] = TestQuote.Q(0.40m, 0.45m, 0.40m, openInterest: 100),
			[longPut] = TestQuote.Q(0.95m, 1.05m, 0.40m, openInterest: 100),
			[shortCall] = TestQuote.Q(0.40m, 0.45m, 0.40m, openInterest: 100),
			[longCall] = TestQuote.Q(0.95m, 1.05m, 0.40m, openInterest: 100),
			[extraOi] = TestQuote.Q(0.55m, 0.60m, 0.40m, openInterest: 300)
		};

		var cfg = Cfg();
		var p = CandidateScorer.ScoreMultiLeg(skel, spot: 25m, asOf, quotes, bias: 0m, cfg)!;

		Assert.Equal(OpenStructureKind.DoubleDiagonal, p.StructureKind);
		Assert.True(p.DebitOrCreditPerContract < 0m);
		Assert.Equal(0, p.DirectionalFit);
		Assert.Equal(25m, p.TargetExpiryMaxPain);
	}

	[Fact]
	public void CalendarVerticalIsRoutedThroughScoreDispatchAsNeutralDebit()
	{
		// Calls Score (the dispatch), not ScoreMultiLeg directly — this is the regression guard for the
		// dispatch gap: if CalendarVertical isn't routed to ScoreMultiLeg, Score returns null and the
		// structure is enumerated but never produces a proposal.
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		// Single-sided calls, same anchor (25) + wing (26) on both expiries — a calendar vertical.
		var farAnchor = MatchKeys.OccSymbol("GME", longExp, 25m, "C");
		var farWing = MatchKeys.OccSymbol("GME", longExp, 26m, "C");
		var nearAnchor = MatchKeys.OccSymbol("GME", shortExp, 25m, "C");
		var nearWing = MatchKeys.OccSymbol("GME", shortExp, 26m, "C");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.CalendarVertical, new[]
		{
			new ProposalLeg("buy", farAnchor, 1),
			new ProposalLeg("sell", farWing, 1),
			new ProposalLeg("sell", nearAnchor, 1),
			new ProposalLeg("buy", nearWing, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[farAnchor] = TestQuote.Q(1.80m, 1.90m, 0.40m, openInterest: 100),
			[farWing] = TestQuote.Q(1.30m, 1.40m, 0.40m, openInterest: 100),
			[nearAnchor] = TestQuote.Q(1.50m, 1.55m, 0.40m, openInterest: 100),
			[nearWing] = TestQuote.Q(1.05m, 1.15m, 0.40m, openInterest: 100)
		};

		var p = CandidateScorer.Score(skel, spot: 25m, asOf, quotes, bias: 0m, Cfg());

		Assert.NotNull(p);
		Assert.Equal(OpenStructureKind.CalendarVertical, p!.StructureKind);
		Assert.Equal(0, p.DirectionalFit);
	}
}

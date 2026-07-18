using WebullAnalytics.AI.Events;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerCalendarTests
{
	private static OpenerConfig Cfg() => new()
	{
		Indicators = new() { IvDefaultPct = 0.4m, StrikeStep = 1.0m },
		Weights = new() { DirectionalFit = 0.5m },
		ProfitBandPct = 0.05m,
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
		// Inverted shapes are rejected by the covered-only default; this test pins the risk math, so opt in.
		var cfg = Cfg();
		cfg.Structures.LongDiagonal.AllowInverted = true;
		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 24.95m, asOf, quotes, bias: 0m, cfg)!;
		// Default pricing uses mid, so debit = 0.88 − 0.375 = 0.505/share ($50.50/contract).
		Assert.Equal(-50.500m, p.DebitOrCreditPerContract);
		Assert.Equal(-100.500m, p.MaxLossPerContract);
		Assert.Equal(100.500m, p.CapitalAtRiskPerContract);
	}

	[Fact]
	public void CalendarMaxProfitCapturesPeakAtStrike()
	{
		// Regression: the peak of a calendar's payoff at short expiry is a sharp cusp AT the short strike
		// (short intrinsic = 0, long extrinsic maximal). The old uniform ±60% / 240-point scan stepped by
		// ~$3.74 for an SPY-class spot and straddled the strike (samples at ~748.81 / ~752.55, never 751),
		// understating max_profit by ~30%. FindCalendarOrDiagonalPeakPnl now seeds the scan with the strikes.
		// Setup mirrors the live SPY 751 calendar: at spot 748.82 the strike sits ~$1.79 from the nearest
		// old-grid point, the worst-case miss. ivLong is pinned to the quote IV (useMarketImpliedIv: false)
		// so the peak value is deterministic: long 751P @ 21 DTE, 12.5% ≈ $8.31/sh, debit = 9.32 − 3.30 =
		// $6.02/sh → true peak ≈ $229/contract. The old straddle reported ≈ $152; guard well above it.
		var asOf = new DateTime(2026, 7, 7);
		var shortExp = new DateTime(2026, 7, 10); // 3 DTE
		var longExp = new DateTime(2026, 7, 31);   // 24 DTE
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 751m, "P");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 751m, "P");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(3.27m, 3.33m, 0.107m), // mid 3.30
			[longSym] = TestQuote.Q(9.28m, 9.36m, 0.125m)   // mid 9.32
		};
		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 748.82m, asOf, quotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;
		Assert.True(p.MaxProfitPerContract > 200m, $"peak-at-strike should give ≈$229/contract, got {p.MaxProfitPerContract} (old cusp-miss reported ≈$152)");
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

	[Fact]
	public void DeepItmCalendarWithNoiseDominatedDebitIsRejected()
	{
		// Regression: the real 2026-01-15 SPY phantom. Spot ~694, 713P calendar 01/23→02/06 priced
		// $0.02 at mid-of-mids while the front leg's book was $2.34 wide — the "debit" was quote noise,
		// and the backtest booked +1690% overnight on first-minute spread wobble. Both new gates must
		// reject it: entry-to-noise ($0.02 vs ~$1.19 RSS) and short-leg extrinsic (~$0.1 vs $1.17 half-spread).
		var asOf = new DateTime(2026, 1, 15);
		var shortExp = new DateTime(2026, 1, 23);
		var longExp = new DateTime(2026, 2, 6);
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 713m, "P");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 713m, "P");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(17.62m, 19.96m, 0.30m),
			[longSym] = TestQuote.Q(18.60m, 19.02m, 0.30m)
		};

		Assert.Null(CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 694.3m, asOf, quotes, bias: 0m, Cfg()));

		// Same candidate with the gates disabled scores — proving the rejection came from the gates.
		var cfgOff = Cfg();
		cfgOff.MinEntryToNoiseRatio = 0m;
		cfgOff.MinShortExtrinsicToNoiseRatio = 0m;
		Assert.NotNull(CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 694.3m, asOf, quotes, bias: 0m, cfgOff));
	}

	[Fact]
	public void NearAtmCalendarWithHonestDebitPassesNoiseGates()
	{
		// The live SPY DC shape: near-ATM calendar, tight book, debit ~50× the combined half-spreads.
		// The gates must be far from interfering with what the live scorer actually trades.
		var asOf = new DateTime(2026, 1, 15);
		var shortExp = new DateTime(2026, 1, 23);
		var longExp = new DateTime(2026, 2, 6);
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 694m, "P");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 694m, "P");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(3.10m, 3.16m, 0.13m),
			[longSym] = TestQuote.Q(4.55m, 4.63m, 0.13m)
		};

		var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 694.3m, asOf, quotes, bias: 0m, Cfg());
		Assert.NotNull(p);
		Assert.Equal(-146m, p!.DebitOrCreditPerContract); // mid 4.59 − 3.13 = 1.46/share — untouched by the gates
	}

	[Fact]
	public void InvertedDiagonalIsRejectedWhenAllowInvertedIsFalse()
	{
		// Reverse call diagonal: short 500C ITM-of-long 505C — the $5 strike gap sits on the loss side.
		// The default (covered-only) must reject it; allowInverted=true must still score it, and a
		// covered diagonal (short 505 / long 500) must score under the covered-only default.
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var lo = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
		var hiLong = MatchKeys.OccSymbol("SPY", longExp, 505m, "C");
		var hiShort = MatchKeys.OccSymbol("SPY", shortExp, 505m, "C");
		var loLong = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
		var reverse = new CandidateSkeleton("SPY", OpenStructureKind.LongDiagonal, new[] { new ProposalLeg("sell", lo, 1), new ProposalLeg("buy", hiLong, 1) }, TargetExpiry: shortExp);
		var covered = new CandidateSkeleton("SPY", OpenStructureKind.LongDiagonal, new[] { new ProposalLeg("sell", hiShort, 1), new ProposalLeg("buy", loLong, 1) }, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[lo] = TestQuote.Q(3.95m, 4.05m, 0.40m),
			[hiLong] = TestQuote.Q(4.40m, 4.50m, 0.40m),
			[hiShort] = TestQuote.Q(1.95m, 2.05m, 0.40m),
			[loLong] = TestQuote.Q(6.40m, 6.50m, 0.40m)
		};

		var allowing = Cfg();
		allowing.Structures.LongDiagonal.AllowInverted = true;

		Assert.Null(CandidateScorer.ScoreCalendarOrDiagonal(reverse, spot: 502m, asOf, quotes, bias: 0m, Cfg()));        // covered-only default rejects
		Assert.NotNull(CandidateScorer.ScoreCalendarOrDiagonal(reverse, spot: 502m, asOf, quotes, bias: 0m, allowing));  // opt-in allows
		Assert.NotNull(CandidateScorer.ScoreCalendarOrDiagonal(covered, spot: 502m, asOf, quotes, bias: 0m, Cfg()));     // covered unaffected by default
	}

	[Fact]
	public void MultiLegFallbackPricingChargesTheoreticalEntryNotZero()
	{
		// The off-hours scan bug: snapshot quotes carry IV/OI but a null book (market closed). ScoreMultiLeg
		// priced the payoff curve from BS-resolved legs but read the ENTRY from raw quotes with a $0
		// default — every 4-leg structure looked free (credit $0.00, huge maxProfit, POP ~1) and degenerate
		// double calendars topped the board. The entry must now come from the same BS-resolved prices.
		var asOf = new DateTime(2026, 6, 10);
		var shortExp = new DateTime(2026, 6, 15);
		var longExp = new DateTime(2026, 7, 2);
		static OptionContractQuote NullBook(decimal iv) => new(ContractSymbol: "", LastPrice: null, Bid: null, Ask: null, Change: null, PercentChange: null, Volume: 0, OpenInterest: 1000, ImpliedVolatility: iv);
		var legs = new (string Action, string Sym, decimal Iv)[]
		{
			("sell", MatchKeys.OccSymbol("SPY", shortExp, 735m, "P"), 0.179m),
			("buy",  MatchKeys.OccSymbol("SPY", longExp, 735m, "P"), 0.166m),
			("sell", MatchKeys.OccSymbol("SPY", shortExp, 743m, "C"), 0.161m),
			("buy",  MatchKeys.OccSymbol("SPY", longExp, 743m, "C"), 0.155m)
		};
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.DoubleCalendar, legs.Select(l => new ProposalLeg(l.Action, l.Sym, 1)).ToArray(), TargetExpiry: shortExp);
		var quotes = legs.ToDictionary(l => l.Sym, l => NullBook(l.Iv));

		var p = CandidateScorer.ScoreMultiLeg(skel, spot: 737.05m, asOf, quotes, bias: 0m, Cfg());

		Assert.NotNull(p);
		Assert.NotNull(p!.PricingWarning); // fallback pricing is flagged
		// A double calendar's far legs are worth more than its near legs at any IV — the BS entry is a
		// real debit, not the $0 "free structure" the raw-quote read produced.
		Assert.True(p.DebitOrCreditPerContract < -10m, $"expected a material BS debit, got {p.DebitOrCreditPerContract}");
	}

	[Fact]
	public void EntryNoiseGateCombinesLegSpreadsInQuadrature()
	{
		var legs = new[] { new ProposalLeg("buy", "A", 1), new ProposalLeg("sell", "B", 1), new ProposalLeg("buy", "C", 1), new ProposalLeg("sell", "D", 1) };
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["A"] = TestQuote.Q(1.80m, 1.90m), // half-spread 0.05
			["B"] = TestQuote.Q(1.30m, 1.40m), // 0.05
			["C"] = TestQuote.Q(1.50m, 1.55m), // 0.025
			["D"] = TestQuote.Q(1.05m, 1.15m)  // 0.05
		};
		// RSS noise = sqrt(0.05² + 0.05² + 0.025² + 0.05²) ≈ 0.0901; linear sum would be 0.175.
		Assert.True(CandidateScorer.PassesEntryNoiseGate(legs, quotes, netEntryPerShare: 0.075m, minRatio: 0.5m));  // 0.075 ≥ 0.0451 — passes RSS, would fail a linear sum
		Assert.False(CandidateScorer.PassesEntryNoiseGate(legs, quotes, netEntryPerShare: 0.04m, minRatio: 0.5m));  // 0.04 < 0.0451
		Assert.True(CandidateScorer.PassesEntryNoiseGate(legs, quotes, netEntryPerShare: 0.01m, minRatio: 0m));     // ratio 0 disables
		// Legs without a real book contribute no noise: an all-synthetic candidate always passes.
		Assert.True(CandidateScorer.PassesEntryNoiseGate(legs, new Dictionary<string, OptionContractQuote>(), netEntryPerShare: 0.001m, minRatio: 0.5m));
	}

	[Fact]
	public void ExDivBetweenExpiries_WidensPutCalendarBreakevens()
	{
		// Put calendar straddling an ex-dividend: short expires 06-18, ex-div 06-22 (Monday, open), long
		// expires 07-02. The surviving long must price the coming drop — its put is worth MORE at the short
		// expiry, so both breakevens widen vs the no-dividend model. An ex-div on/before the short expiry
		// is absorbed by the spot path (both legs cross it) and must be a no-op.
		var asOf = new DateTime(2026, 6, 10);
		var shortExp = new DateTime(2026, 6, 18);
		var longExp = new DateTime(2026, 7, 2);
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 740m, "P");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 740m, "P");
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(18.50m, 18.70m, 0.17m),
			[longSym] = TestQuote.Q(21.30m, 21.50m, 0.17m)
		};
		TickerEvents Ev(DateTime exDate) => new("SPY", NextEarningsDate: null, EarningsTime: null, NextExDividendDate: exDate, DividendAmount: 1.80m);

		var noDiv = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 725m, asOf, quotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;
		var straddled = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 725m, asOf, quotes, bias: 0m, Cfg(), useMarketImpliedIv: false, events: Ev(new DateTime(2026, 6, 22)))!;
		var absorbed = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 725m, asOf, quotes, bias: 0m, Cfg(), useMarketImpliedIv: false, events: Ev(new DateTime(2026, 6, 18)))!;

		Assert.Equal(2, noDiv.Breakevens.Count);
		Assert.Equal(2, straddled.Breakevens.Count);
		Assert.True(straddled.Breakevens[0] < noDiv.Breakevens[0], $"lower BE should widen down: {straddled.Breakevens[0]} vs {noDiv.Breakevens[0]}");
		Assert.True(straddled.Breakevens[1] > noDiv.Breakevens[1], $"upper BE should widen up: {straddled.Breakevens[1]} vs {noDiv.Breakevens[1]}");
		Assert.Equal(noDiv.Breakevens, absorbed.Breakevens); // ex-div on the short expiry: no surviving dividend, no adjustment
	}
}

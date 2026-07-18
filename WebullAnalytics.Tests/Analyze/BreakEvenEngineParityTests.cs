using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>
/// Pins agreement between the TWO break-even engines on the same calendar:
///   • the opener scorer (<see cref="CandidateScorer.ScoreCalendarOrDiagonal"/>) — what `analyze position`'s
///     risk-diagnostic probe renders, and
///   • the position analyzer (<see cref="BreakEvenAnalyzer"/>) — what `wa report` and the analyze break-even
///     table render.
/// Fed identical inputs (same spot, same strikes/expiries, same long-leg IV via the quote field, same
/// per-share debit via the leg cost basis, no dividends), the two engines must produce the same break-evens.
/// Before the bisection unification the only residual gap was the analyzer's rounded 50-iteration bisection
/// vs the scorer's unrounded 60-iteration one (~$0.05–0.12 on SPX-class strikes); this guards that it stays
/// closed.
/// </summary>
public class BreakEvenEngineParityTests
{
	private static OpenerConfig Cfg() => new()
	{
		Indicators = new() { IvDefaultPct = 0.4m, StrikeStep = 1.0m },
		Weights = new() { DirectionalFit = 0.5m },
		ProfitBandPct = 0.05m,
	};

	[Fact]
	public void ScorerAndAnalyzer_AgreeOnCalendarBreakevens()
	{
		var asOf = new DateTime(2026, 6, 12);
		var shortExp = new DateTime(2026, 6, 18);
		var longExp = new DateTime(2026, 7, 2);
		// Note: deliberately does NOT touch the global EvaluationDate — the analyzer computes a calendar's
		// break-evens at the short expiry's close (independent of "today"), and the scorer takes asOf as a
		// parameter. Mutating the static would race with parallel test collections.

		const decimal strike = 755m, spot = 755m, iv = 0.15m;
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, strike, "P");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, strike, "P");

		// short 755P mid 4.50, long 755P mid 8.00 → net debit 3.50/share.
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(4.40m, 4.60m, iv),
			[longSym] = TestQuote.Q(7.90m, 8.10m, iv),
		};

		// --- Engine A: opener scorer. useMarketImpliedIv:false → long IV = the quote IV field (0.15),
		// matching what the analyzer reads, so the comparison isolates engine math (not the IV anchor). ---
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar,
			[new ProposalLeg("sell", shortSym, 1), new ProposalLeg("buy", longSym, 1)], TargetExpiry: shortExp);
		var scorerProposal = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot, asOf, quotes, bias: 0m, Cfg(), useMarketImpliedIv: false)!;
		var scorerBe = scorerProposal.Breakevens.OrderBy(x => x).ToList();

		// --- Engine B: position analyzer. Leg cost basis = the quote mids (matching the scorer's mid debit);
		// long-leg IV resolved from the same quote field via GetLegIv (no override, no calibration). ---
		var parent = new PositionRow("SPY Calendar", Asset.OptionStrategy, "Calendar", Side.Buy, 1, 3.50m, shortExp, IsStrategyLeg: false);
		var legShort = new PositionRow(shortSym, Asset.Option, "Put", Side.Sell, 1, 4.50m, shortExp, IsStrategyLeg: true, MatchKey: MatchKeys.Option(shortSym));
		var legLong = new PositionRow(longSym, Asset.Option, "Put", Side.Buy, 1, 8.00m, longExp, IsStrategyLeg: true, MatchKey: MatchKeys.Option(longSym));
		var opts = new AnalysisOptions(
			OptionQuotes: quotes,
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = spot },
			Theoretical: true);
		var analyzerResult = Assert.Single(BreakEvenAnalyzer.Analyze([parent, legShort, legLong], opts));
		var analyzerBe = analyzerResult.BreakEvens.OrderBy(x => x).ToList();

		Assert.Equal(2, scorerBe.Count);
		Assert.Equal(2, analyzerBe.Count);
		// Sub-cent agreement: both bisect the SAME continuous P&L curve to a 0.005 interval. The residual is
		// only the difference in where each engine's scan brackets the root before bisecting — well under $0.01.
		Assert.True(Math.Abs(scorerBe[0] - analyzerBe[0]) < 0.01m, $"lower BE: scorer {scorerBe[0]} vs analyzer {analyzerBe[0]}");
		Assert.True(Math.Abs(scorerBe[1] - analyzerBe[1]) < 0.01m, $"upper BE: scorer {scorerBe[1]} vs analyzer {analyzerBe[1]}");
	}

	[Fact]
	public void LegValueAt_InjectedEvaluator_MatchesDirectBlackScholes()
	{
		// The scorer injects a memoized Black-Scholes into LegValueAt to keep its per-tick long-leg cache.
		// That injection must be numerically transparent — identical to the direct path, including the
		// dividend-adjusted forward — or the cache would silently shift the opener's calendar pricing.
		var longLeg = new OptionParsed("SPY", new DateTime(2026, 7, 2), "P", 755m);
		var evalDate = new DateTime(2026, 6, 18).Date + OptionMath.MarketClose; // short expiry close: long still has time
		var divs = new[] { new DividendEvent(new DateTime(2026, 6, 22), 1.80m) };
		var calls = 0;
		OptionMath.BlackScholesEvaluator forwarding = (s, k, t, iv, cp) => { calls++; return OptionMath.BlackScholes(s, k, t, OptionMath.RiskFreeRate, iv, cp); };

		foreach (var sT in new[] { 600m, 700m, 740m, 755m, 800m, 900m })
		{
			var direct = OptionMath.LegValueAt(sT, evalDate, longLeg, 0.17m, divs);
			var injected = OptionMath.LegValueAt(sT, evalDate, longLeg, 0.17m, divs, forwarding);
			Assert.Equal(direct, injected);
		}
		Assert.Equal(6, calls); // the BS branch (not intrinsic) was actually exercised for every point
	}
}

using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>
/// Spot-vs-book consistency guard for the cash-index tickers (SPX/SPXW/XSP). The official index print is composed of constituent trades, so at the 09:30 bell on a gap day it lags the level the option market is
/// pricing until every constituent has opened — live 2026-08-03 the first RTH minute's midpoint said 7511.72 while put-call parity on the same minute's NBBO implied ~7520.7, so a 7505P/7515P debit spread looked
/// priced at pure intrinsic (a phantom 2:1 coin flip) and the opener bought a bearish vertical into a +0.25 bullish bias. The premarket SPY-synthetic tape already knew better (09:29 close 7517.44) but only feeds
/// the bias, never the spot. This guard back-solves the spot from put-call parity on the ATM straddle of the very chain being scored (strict NBBO mids, no last-price echo) and replaces the reported spot when the
/// two disagree by more than <see cref="Tolerance"/> — fills price off those quotes, so on disagreement the book is the authority. Both the opener (after its chain bootstrap) and the management paths (via
/// <see cref="AIPipelineHelper.FetchQuotesWithHypotheticals"/> and the backtest 0DTE minute walk) run it, so entry scoring, LegInShort/CompleteCondor moneyness, and force-close checks all see the same spot.
/// Deliberately scoped:
/// <list type="bullet">
/// <item>RTH only — premarket books can be FROZEN at the prior session's two-sided NBBO (Schwab until the bell, Webull GTH after 09:15 ET), which passes the strict-mids test while reproducing YESTERDAY's spot.
/// Detecting that requires the extended-hours-bar cross-check that <see cref="PremarketSpotOverride"/> owns; this guard stays out of its jurisdiction.</item>
/// <item>Index roots only — an equity/ETF spot is a real traded print, and a pending dividend legitimately holds parity below spot by the dividend amount (~24 bp for SPY on an ex-div-morning 0DTE), which would
/// false-trigger the guard.</item>
/// </list>
/// </summary>
internal static class ParitySpotGuard
{
	private static readonly TimeSpan RthOpen = new(9, 30, 0);
	private static readonly TimeSpan RthClose = new(16, 0, 0);

	/// <summary>Max relative spot-vs-parity disagreement before the guard fires. Calibrated on 2026-08-03/04 (two fast trend days): routine disagreement — ATM-straddle mid noise plus the bar-anchor vs
	/// quote-timestamp convention skew on fast minutes — reached 8.2 bp with alternating sign across the management minute walk, while the one-sided composed-open lag this guard exists for measured
	/// 11.8–12.1 bp (SPXW/XSP). 10 bp separates the two: below it the "error" is within-minute jitter that the ~50 bp-granularity moneyness gates don't care about (and correcting it would just inject
	/// quote noise into the spot path), above it the bar print and the book genuinely disagree.</summary>
	private const decimal Tolerance = 0.001m;

	/// <summary>Returns the parity-corrected spot when the guard fires (logging the correction), null when the reported spot stands (outside RTH, non-index root, no strike with two-sided call+put NBBO, or
	/// agreement within tolerance). <paramref name="asOf"/> is ET wall-clock, the convention every caller (live ticks, backtest minute/day steps) already uses.</summary>
	public static decimal? Correct(string ticker, decimal reportedSpot, IReadOnlyDictionary<string, OptionContractQuote> quotes, DateTime asOf)
	{
		if (reportedSpot <= 0m || asOf.TimeOfDay < RthOpen || asOf.TimeOfDay >= RthClose) return null;
		if (!WebullIntradayBars.SpxFamilyTickers.Contains(ticker)) return null;
		var parity = PremarketSpotOverride.DeriveSpotFromParity(ticker, quotes, OptionMath.RiskFreeRate, asOf, allowLastPrice: false);
		if (parity is not { Spot: > 0m } p) return null;
		if (Math.Abs(p.Spot - reportedSpot) / reportedSpot <= Tolerance) return null;
		Console.Error.WriteLine($"[parity-spot] {asOf:yyyy-MM-dd HH:mm} ET {ticker} reported spot {reportedSpot:F2} disagrees with the option book (parity {p.Spot:F2}, {(p.Spot - reportedSpot) / reportedSpot * 10000m:+0.0;-0.0} bp) — using the parity spot.");
		return p.Spot;
	}

	/// <summary>ATM straddle probe symbols (call+put at the three strikes bracketing <paramref name="spot"/>) for <paramref name="expiry"/>. Management fetches only carry position legs — verticals are
	/// single-right, so parity has no same-strike pair to solve from without these. Empty for non-index roots (the guard would never fire) and non-positive spots.</summary>
	public static IEnumerable<string> ProbeSymbols(string ticker, DateTime expiry, decimal spot, decimal strikeStep)
	{
		if (spot <= 0m || !WebullIntradayBars.SpxFamilyTickers.Contains(ticker)) yield break;
		var step = strikeStep > 0m ? strikeStep : 5m;
		var atm = Math.Round(spot / step, MidpointRounding.AwayFromZero) * step;
		for (var k = atm - step; k <= atm + step; k += step)
		{
			if (k <= 0m) continue;
			yield return MatchKeys.OccSymbol(ticker, expiry, k, "C");
			yield return MatchKeys.OccSymbol(ticker, expiry, k, "P");
		}
	}
}

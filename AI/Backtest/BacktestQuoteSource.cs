using System.Collections.Concurrent;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Backtest;

/// <summary>How a leg with no captured bar of its own got its synthetic price. Diagnostic only — used by
/// the post-run provenance breakdown to show whether synthetic legs were anchored to the real same-day
/// smile of captured neighbors (<see cref="SurfaceIv"/>) or fell through to the parametric fallback
/// (<see cref="VixSmile"/>) / raw intrinsic (<see cref="Intrinsic"/>). The split decides whether building
/// cross-expiry interpolation is worth it: lots of VixSmile → neighbors are missing → worth it.</summary>
internal enum SyntheticPricingSource { SurfaceIv, CrossExpiry, VixSmile, Intrinsic }

/// <summary>Cross-expiry anchor available to a VIX-fallback leg: <see cref="Bracketed"/> = captured
/// neighbor expiries on both sides (genuine total-variance interpolation), <see cref="OneSided"/> = only
/// one side (extrapolation across the gap), <see cref="None"/> = no neighbor anchor (irreducible).</summary>
internal enum NeighborAnchor { None, OneSided, Bracketed }

/// <summary>
/// Black-Scholes-priced option quote source for backtests. Underlying spot comes from each ticker's
/// daily close (<see cref="HistoricalBarCache"/>); IV comes from <see cref="BacktestIVProvider"/>.
/// Synthesizes a symmetric bid/ask around the theoretical mid using a per-ticker spread model so
/// the candidate scorer's liquidity/realized-EV checks see friction that resembles the live tape:
/// SPY/QQQ/IWM are penny-pilot tight; everything else gets the wider per-share floor typical of
/// single-stock options (matching e.g. the GME quotes seen in production — bid 0.30 / ask 0.35 on
/// a $0.325 mid).
/// </summary>
internal sealed class BacktestQuoteSource : IQuoteSource
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	/// <summary>Moneyness half-width for the bootstrap chain expansion: only captured strikes within ±15% of
	/// spot are surfaced to the opener's ladder. The enumerator never reaches further (delta bands + a
	/// ±24-strike grid), so expanding the whole captured chain just priced thousands of unused legs per tick.</summary>
	private const decimal ExpansionWindowPct = 0.15m;

	/// <summary>Converts an ET wall-clock <see cref="DateTime"/> to the UTC instant for cache lookup,
	/// preserving the time-of-day. The historical option bars are keyed by UTC minute, and callers
	/// pass <paramref name="asOf"/> at the minute they're evaluating — 09:30 ET for the daily-step
	/// open pass, but any minute (e.g. 10:32 ET) for the intraday opener's per-minute walk. Both
	/// paths must end up looking up the bar at the SAME wall-clock minute, not always the day's
	/// open. The previous implementation forced 09:30 regardless of <paramref name="asOf"/>'s time
	/// component, which meant the intraday opener was repeatedly fetching the 09:30 bar at every
	/// minute it evaluated — a silent miss that always fell through to synthetic.</summary>
	private static DateTimeOffset ToUtcMinute(DateTime asOf)
	{
		var et = DateTime.SpecifyKind(asOf, DateTimeKind.Unspecified);
		return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(et, NyTz), TimeSpan.Zero);
	}

	/// <summary>Builds a real-IV value for <paramref name="targetStrike"/> from the captured strikes of
	/// the same root+expiry+right at the minute, then interpolates/extrapolates to the target. For each
	/// captured strike: use its reported IV if present (Webull-sourced), else back-solve IV from its
	/// real open price via Black-Scholes inverse (massive-sourced contracts ship no IV). Linear
	/// interpolation between bracketing strikes; flat extrapolation (nearest endpoint) beyond the
	/// captured range — so a leg the opener reaches *outside* the band still prices on the real implied
	/// vol rather than the ~10-points-too-low VIX1D anchor. Returns null when no captured strikes exist
	/// for this expiry+right at the minute (caller falls back to the VIX1D-anchored synthetic).</summary>
	private decimal? BuildSurfaceIv(string root, DateTime expiry, string callPut, decimal targetStrike, decimal spot, double timeYears, DateTimeOffset asOfUtc)
	{
		if (_optionBars == null) return null;
		var points = _optionBars.GetCapturedQuotePoints(root, expiry, callPut, asOfUtc);
		if (points.Count == 0) return null;

		var ivByStrike = new List<(decimal Strike, decimal Iv)>(points.Count);
		foreach (var p in points)
		{
			// Back-solve IV from the bar's time-midpoint price (returned as p.Price by the cache).
			// Always solve fresh rather than trusting the captured iv column: the column is
			// back-solved from bar.Open by Webull's chart endpoint, and using it here would carry a
			// moment-in-minute mismatch into the surface (Open-anchored IV interpolated between
			// Mid-anchored leg pricing). ImpliedVol clamps to [0.01, 5.0]; pinned bounds indicate
			// vega-flat regimes (deep ITM/OTM or near-zero time) — skip those.
			decimal? iv = null;
			var solved = OptionMath.ImpliedVol(spot, p.Strike, timeYears, _riskFreeRate, p.Price, callPut);
			if (solved > 0.011m && solved < 4.99m) iv = solved;
			if (iv.HasValue && iv.Value > 0m) ivByStrike.Add((p.Strike, iv.Value));
		}
		if (ivByStrike.Count == 0) return null;
		ivByStrike.Sort((a, b) => a.Strike.CompareTo(b.Strike));

		if (ivByStrike.Count == 1 || targetStrike <= ivByStrike[0].Strike) return ivByStrike[0].Iv;
		if (targetStrike >= ivByStrike[^1].Strike) return ivByStrike[^1].Iv;
		for (var i = 1; i < ivByStrike.Count; i++)
		{
			if (targetStrike > ivByStrike[i].Strike) continue;
			var (s0, iv0) = ivByStrike[i - 1];
			var (s1, iv1) = ivByStrike[i];
			if (s1 == s0) return iv0;
			var w = (targetStrike - s0) / (s1 - s0);
			return iv0 + w * (iv1 - iv0);
		}
		return ivByStrike[^1].Iv;
	}

	/// <summary>IV for <paramref name="targetStrike"/> when the leg's OWN expiry has no captured strikes at
	/// the minute, derived from NEARBY captured expiries (same root+right, within ±<see cref="NeighborExpiryDayGap"/>
	/// days). For each neighbor expiry, take its own smile's IV at the target strike (via <see cref="BuildSurfaceIv"/>
	/// at that expiry's TTE), then interpolate across expiry in TOTAL-VARIANCE space (w = σ²·T, linear in T) —
	/// the standard term-structure interpolation that keeps the forward variance non-negative and matches how
	/// the vol surface actually evolves with maturity. With anchors on both sides this is true interpolation;
	/// with only one side it flat-extrapolates that neighbor's IV across the gap (less reliable, but still real
	/// same-day vol rather than the ~10-points-too-low VIX1D parametric guess). Returns null when no neighbor
	/// expiry yields a usable back-solved IV at the target strike — the caller then falls through to the
	/// VIX-anchored synthetic. Only meaningful for >0DTE legs (0DTE has its own regime); the caller gates on that.</summary>
	private decimal? BuildCrossExpiryIv(string root, DateTime targetExpiry, string callPut, decimal targetStrike, decimal spot, double targetTimeYears, DateTime asOf, DateTimeOffset asOfUtc)
	{
		if (_optionBars == null || targetTimeYears <= 0) return null;

		// Collect every neighbor expiry that yields a usable IV at the target strike, tagged by signed
		// calendar gap (negative = earlier) and its own time-to-expiry.
		var below = new List<(int Gap, double T, decimal Iv)>();
		var above = new List<(int Gap, double T, decimal Iv)>();
		foreach (var exp in _optionBars.NeighborExpiriesWithin(root, targetExpiry, NeighborExpiryDayGap))
		{
			var gap = (exp.Date - targetExpiry.Date).Days;
			var tE = (exp.Date - asOf.Date).Days / 365.0;
			if (tE <= 0) continue;
			var ivE = BuildSurfaceIv(root, exp, callPut, targetStrike, spot, tE, asOfUtc);
			if (!ivE.HasValue || ivE.Value <= 0m) continue;
			(gap < 0 ? below : above).Add((gap, tE, ivE.Value));
		}

		// Nearest anchor on each side (gap closest to 0).
		var lo = below.Count > 0 ? below.OrderByDescending(a => a.Gap).First() : ((int, double, decimal)?)null;
		var hi = above.Count > 0 ? above.OrderBy(a => a.Gap).First() : ((int, double, decimal)?)null;

		if (lo.HasValue && hi.HasValue && hi.Value.Item2 > lo.Value.Item2)
		{
			// Linear total variance in time between the bracketing expiries, then back out σ at target TTE.
			var wLo = (double)lo.Value.Item3 * (double)lo.Value.Item3 * lo.Value.Item2;
			var wHi = (double)hi.Value.Item3 * (double)hi.Value.Item3 * hi.Value.Item2;
			var w = wLo + (wHi - wLo) * (targetTimeYears - lo.Value.Item2) / (hi.Value.Item2 - lo.Value.Item2);
			return w > 0 ? (decimal)Math.Sqrt(w / targetTimeYears) : (decimal?)null;
		}
		// One-sided: flat-σ extrapolation from the single nearest neighbor.
		if (lo.HasValue) return lo.Value.Item3;
		if (hi.HasValue) return hi.Value.Item3;
		return null;
	}

	// Penny-pilot index ETFs: minimum half-spread $0.005 ($0.01 total), plus 0.5% of mid.
	private const decimal IndexHalfSpreadFloor = 0.005m;
	private const decimal IndexHalfSpreadPct = 0.005m;
	// Single-stock single-name options: minimum half-spread $0.01 ($0.02 total), plus 5% of mid.
	// This roughly reproduces real GME-style quotes where mid-priced contracts trade one
	// nickel-tick wide and very cheap (sub-$0.10) contracts trade one penny-tick wide.
	private const decimal SingleStockHalfSpreadFloor = 0.01m;
	private const decimal SingleStockHalfSpreadPct = 0.05m;

	// Penny-pilot ETFs + cash-settled index options (SPX/SPXW/XSP/NDX). Index options aren't formally
	// "penny pilot" but they trade in tight $0.05 ticks under $3 / $0.10 above, which is closer to the
	// SPY/QQQ regime than to single-stock single-name option spreads.
	private static readonly HashSet<string> PennyPilotIndexEtfs = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPY", "QQQ", "IWM", "DIA", "SPX", "SPXW", "XSP", "NDX"
	};

	// One trading day = 6.5h. Used to give 0DTE a non-zero time component so the open captures
	// a full day's theta before the same-step expiry settles at intrinsic. Without this, BlackScholes
	// returns intrinsic at both ends and the position can never collect time premium.
	private const double ZeroDteTimeYears = 6.5 / 24.0 / 365.0;
	// Half-session TTE — used as the approximation for "current moment is the middle of the session"
	// when re-pricing positions at the day's High/Low for intraday stop-loss / take-profit checks.
	// Without minute-bar history we can't know when the extreme actually occurred; mid-session is the
	// expected midpoint and avoids systematically biasing either toward open (rich extrinsic) or close
	// (zero extrinsic). Exposed as a constant so the same value is used by the intraday-trigger pass
	// in <see cref="BacktestRunner"/>.
	internal const double IntradayHalfSessionTimeYears = 3.25 / 24.0 / 365.0;

	private readonly HistoricalBarCache _bars;
	private readonly BacktestIVProvider _iv;
	private readonly double _riskFreeRate;
	private readonly IReadOnlyDictionary<string, decimal>? _spotOverrides;
	private readonly HistoricalOptionBarCache? _optionBars;

	// Diagnostic: records which synthetic branch priced each (contract, ET date) the last time it was
	// synthesized (no captured bar of its own). Keyed at day granularity to match the day-level provenance
	// metric (HasBarOnDate); the source for a given contract on a given day is stable enough that the
	// last-write across the day's minutes is representative. ConcurrentDictionary because the intraday
	// opener prices minutes in parallel batches. Read post-run by BacktestRunner.ComputeProvenance.
	private readonly ConcurrentDictionary<(string Occ, DateTime DateEt), SyntheticPricingSource> _syntheticSource = new();

	/// <summary>The synthetic-pricing branch taken for <paramref name="occ"/> on the ET trading day of
	/// <paramref name="dateEt"/>, or null if it was never synthesized (e.g. always captured). Diagnostic
	/// only — see <see cref="SyntheticPricingSource"/>.</summary>
	internal SyntheticPricingSource? GetSyntheticSource(string occ, DateTime dateEt)
		=> _syntheticSource.TryGetValue((occ, dateEt.Date), out var s) ? s : null;

	// Expiry-day window for the cross-expiry recoverability probe: how far (calendar days) a neighboring
	// expiry can be from the target and still serve as an interpolation anchor. ±14d spans the 1-2 adjacent
	// weekly/monthly expiries on each side that a total-variance interpolation would bracket between.
	private const int NeighborExpiryDayGap = 14;

	/// <summary>Diagnostic: for a leg that fell through to the parametric (VIX-smile) synthetic — its own
	/// expiry had no captured strikes at the minute — classify the cross-expiry anchor available within
	/// ±<see cref="NeighborExpiryDayGap"/> days (same root+right): <see cref="NeighborAnchor.Bracketed"/>
	/// (both sides → genuine interpolation), <see cref="NeighborAnchor.OneSided"/> (extrapolation only), or
	/// <see cref="NeighborAnchor.None"/> (irreducible). <paramref name="asOfEtMinute"/> is the fill's ET
	/// timestamp (minute-precise for opens).</summary>
	internal NeighborAnchor ClassifyNeighborExpiry(string occ, DateTime asOfEtMinute)
	{
		if (_optionBars == null) return NeighborAnchor.None;
		var parsed = ParsingHelpers.ParseOptionSymbol(occ);
		if (parsed?.CallPut == null) return NeighborAnchor.None;
		var (below, above) = _optionBars.NeighborExpiryAnchors(parsed.Root, parsed.ExpiryDate, parsed.CallPut, ToUtcMinute(asOfEtMinute), NeighborExpiryDayGap);
		return below && above ? NeighborAnchor.Bracketed : (below || above ? NeighborAnchor.OneSided : NeighborAnchor.None);
	}

	/// <param name="spotOverrides">When supplied for a ticker, replaces the bar.open lookup for that
	/// ticker. Used by <c>ai scan --theoretical</c> to evaluate a hypothetical spot at an asOf for which
	/// no historical bar exists (next-business-day previews) or a stress scenario at any spot level.</param>
	/// <param name="optionBars">Optional cache of captured per-contract minute bars from
	/// <c>data/options/&lt;root&gt;/&lt;expiry&gt;/&lt;occ&gt;.csv</c>. When supplied and a bar exists for the
	/// leg's minute, the bar's close is used as the theoretical mid and the bar's IV (when present)
	/// replaces the VIX-anchored synthetic IV. Bid/ask is still derived from the half-spread model
	/// — the option chart endpoint reports trade prints, not NBBO. Legs without a captured bar fall
	/// through to the Black-Scholes path unchanged, so partial coverage is fine.</param>
	public BacktestQuoteSource(HistoricalBarCache bars, BacktestIVProvider iv, double riskFreeRate, IReadOnlyDictionary<string, decimal>? spotOverrides = null, HistoricalOptionBarCache? optionBars = null)
	{
		_bars = bars;
		_iv = iv;
		_riskFreeRate = riskFreeRate;
		_spotOverrides = spotOverrides;
		_optionBars = optionBars;
	}

	/// <summary>Diagnostic: did <paramref name="occ"/> have a real captured bar on the ET trading day of
	/// <paramref name="dateEt"/>? When false, any backtest price for that contract on that day came from
	/// the synthetic Black-Scholes fallback. Used by the post-run pricing-provenance report.</summary>
	internal bool HasCapturedBarOnDate(string occ, DateTime dateEt) => _optionBars?.HasBarOnDate(occ, dateEt) ?? false;

	/// <summary>True when the contract was captured (has ≥1 real bar) on ANY day — it really traded at some
	/// point. A synthetic-priced leg whose contract was NEVER captured is a likely phantom strike (a grid the
	/// enumerator invented that the chain never listed); the provenance report flags these separately.</summary>
	internal bool HasAnyCapturedBar(string occ) => _optionBars?.HasAnyBar(occ) ?? false;

	public async Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation, QuoteOverrides overrides = default)
	{
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		// Use the day's OPEN as the spot price the opener sees: the daily simulator step is
		// conceptually "morning of day X" — strikes get picked relative to the open, the position
		// holds across the session, and SettleExpirationsAsync settles 0DTE expiries at bar.Close.
		// Without this, strike selection and settlement both use Close, so a delta-X% OTM strike
		// is OTM at picking-time AND at settle-time, producing an unrealistic 100%-win-rate backtest.
		foreach (var ticker in tickers)
		{
			// Precedence: per-call overrides.Spots (set by BacktestRunner's intraday opener loop)
			// wins over the construction-time _spotOverrides (--theoretical), which wins over bar.Open.
			if (overrides.Spots != null && overrides.Spots.TryGetValue(ticker, out var minuteSpot))
			{
				underlyings[ticker] = minuteSpot;
				continue;
			}
			if (_spotOverrides != null && _spotOverrides.TryGetValue(ticker, out var spotOverride))
			{
				underlyings[ticker] = spotOverride;
				continue;
			}
			var bar = await _bars.GetBarAsync(ticker, asOf.Date, cancellation);
			if (bar != null) underlyings[ticker] = bar.Open;
		}

		// Parse once, collect all (sym, parsed) pairs, and resolve any root spots not yet in underlyings.
		// Pre-parsing avoids reparsing inside the parallel loop and lets us load all root spots and ATM
		// IVs serially up front — both are async + cache-backed, and parallelizing those would compete
		// for the same in-memory dictionary on first miss.
		// Mirror the live chain probe so the opener's StrikeLadder sees the real (non-uniform) captured strike
		// grid. The opener bootstraps a chain by requesting ONE placeholder per (root, expiry) at strike $1;
		// the live quote source answers with the entire listed chain. The backtest must do the same — without
		// it, live snaps candidate strikes to listed strikes while the backtest enumerates a uniform
		// strikeStep grid, and the two diverge. Detect the $1 placeholder and expand it to every captured
		// strike for that (root, expiry); real candidate strikes for SPX-family tickers are never $1, so this
		// only fires for the bootstrap probe, not for Phase-B leg refetches or position pricing.
		var effectiveSymbols = optionSymbols;
		if (_optionBars != null)
		{
			var probeExpiries = optionSymbols
				.Select(ParsingHelpers.ParseOptionSymbol)
				.Where(p => p != null && p.Strike == 1m)
				.Select(p => (p!.Root, Expiry: p.ExpiryDate.Date))
				.Distinct()
				.ToList();
			if (probeExpiries.Count > 0)
			{
				var probeUtc = ToUtcMinute(asOf);
				var expanded = new HashSet<string>(optionSymbols, StringComparer.OrdinalIgnoreCase);
				foreach (var (root, expiry) in probeExpiries)
				{
					// Only expand near-money strikes. The opener's enumerator never reaches beyond a few
					// percent of spot (delta bands + ±24-strike grid), so expanding the entire captured chain
					// — which for densely-backfilled roots like SPXW is thousands of strikes per expiry —
					// priced thousands of unused legs per tick and made the backtest crawl. ±15% covers every
					// delta band at the widest DTE with headroom.
					underlyings.TryGetValue(root, out var probeSpot);
					var window = probeSpot > 0m ? probeSpot * ExpansionWindowPct : decimal.MaxValue;
					foreach (var cp in new[] { "C", "P" })
						foreach (var pt in _optionBars.GetCapturedQuotePoints(root, expiry, cp, probeUtc))
							if (probeSpot <= 0m || Math.Abs(pt.Strike - probeSpot) <= window)
								expanded.Add(MatchKeys.OccSymbol(root, expiry, pt.Strike, cp));
				}
				effectiveSymbols = expanded;
			}
		}

		var parsedSymbols = new List<(string Symbol, OptionParsed Parsed)>(effectiveSymbols.Count);
		foreach (var sym in effectiveSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p != null) parsedSymbols.Add((sym, p));
		}

		var roots = parsedSymbols.Select(t => t.Parsed.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (var root in roots)
		{
			if (underlyings.ContainsKey(root)) continue;
			if (overrides.Spots != null && overrides.Spots.TryGetValue(root, out var minuteSpot))
			{
				underlyings[root] = minuteSpot;
				continue;
			}
			if (_spotOverrides != null && _spotOverrides.TryGetValue(root, out var spotOverride))
			{
				underlyings[root] = spotOverride;
				continue;
			}
			var bar = await _bars.GetBarAsync(root, asOf.Date, cancellation);
			if (bar != null) underlyings[root] = bar.Open;
		}

		// One smile-scale lookup per (root, asOf), and one ATM IV lookup per (root, dte). ATM IV is now
		// DTE-aware (VIX1D / VIX9D / VIX per term), so a chain that mixes 0DTE and 7DTE legs resolves
		// to different ATM anchors. Pre-collect unique (root, dte) pairs so the per-strike fan-out
		// below doesn't re-hit the VIX/HV caches.
		var smileScaleByRoot = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			smileScaleByRoot[root] = await _iv.GetSmileScaleAsync(root, asOf, cancellation);
		}
		var atmByRootDte = new Dictionary<(string Root, int Dte), decimal?>();
		foreach (var (_, parsed) in parsedSymbols)
		{
			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
			var key = (parsed.Root, dte);
			if (atmByRootDte.ContainsKey(key)) continue;
			atmByRootDte[key] = await _iv.GetAtmIVAsync(parsed.Root, asOf, dte, cancellation);
		}

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var asOfUtc = ToUtcMinute(asOf);
		foreach (var (sym, parsed) in parsedSymbols)
		{
			if (!underlyings.TryGetValue(parsed.Root, out var spot)) continue;
			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
			atmByRootDte.TryGetValue((parsed.Root, dte), out var atm);
			smileScaleByRoot.TryGetValue(parsed.Root, out var smileScale);

			// Prefer captured per-minute bar when available. Price the leg at the bar's TIME-MIDPOINT
			// (Open+Close)/2 — that's the price a live tick fired anywhere in the middle of the minute
			// would see under linear price evolution, which is approximately where live's tick-interval
			// scheduler (e.g. wa ai watch at :30 each minute) actually samples. Using bar.Open
			// systematically under-samples by ~30 seconds on trending minutes — empirically the
			// difference between backtest scoring LongCall at -0.082 and live's +0.067 at the same
			// minute (verified against an ai-proposals.jsonl record on 2026-05-28). The IV is
			// back-solved from this midpoint price; the captured IV column is *not* used because it
			// was solved from bar.Open and is anchored to the wrong moment-in-minute. Vega-too-small
			// (deep ITM/OTM 0DTE) falls back to the parametric surface so the leg still gets a
			// usable IV. This is the same flavor of within-minute lookahead the tape boundary bar
			// discussion already accepted — bounded to "what live would have seen 30s into the bar."
			var capturedBar = _optionBars?.GetBar(sym, asOfUtc);
			decimal price;
			decimal? iv = null;
			long? volume = null;
			if (capturedBar != null && capturedBar.Open > 0m && capturedBar.Close > 0m)
			{
				price = (capturedBar.Open + capturedBar.Close) / 2m;
				var timeYears = dte <= 0
					? (overrides.ZeroDteTimeYears ?? ZeroDteTimeYears)
					: dte / 365.0;
				if (parsed.CallPut != null && timeYears > 0)
				{
					var solved = OptionMath.ImpliedVol(spot, parsed.Strike, timeYears, _riskFreeRate, price, parsed.CallPut);
					// Reject the bounds (0.01 / 5.0) — those indicate non-convergent vega-flat regimes
					// (deep ITM/OTM or zero-time). Fall through to the parametric surface instead so
					// the leg's IV still tracks the smile rather than getting pinned to the back-solver
					// bound.
					if (solved > 0.011m && solved < 4.99m)
						iv = solved;
				}
				if (!iv.HasValue && atm.HasValue)
					iv = _iv.ApplySmile(atm.Value, parsed.Root, parsed.Strike, spot, smileScale);
				if (capturedBar.Volume > 0) volume = capturedBar.Volume;
			}
			else
			{
				// No captured bar for this leg. 0DTE TTE: per-call override (remaining session) for the
				// intraday loop, else the day-step constant. Used for both the missing-leg BS price AND
				// the back-solve of captured neighbors so the surface and the leg share one TTE.
				var timeYears = dte <= 0
					? (overrides.ZeroDteTimeYears ?? ZeroDteTimeYears)
					: dte / 365.0;

				// Prefer an IV from the captured strikes of the SAME expiry+right (the real-IV surface)
				// over the VIX1D-anchored synthetic. Captured contracts from massive carry no IV, so we
				// back-solve it from their real open price (Black-Scholes inverse) — this is what keeps a
				// spread's missing/out-of-band leg on the same real-IV basis as its captured legs. Pricing
				// the missing leg with VIX1D (~10 IV points below the real 0DTE surface) is what fabricated
				// spread credit and inflated the backtest. Falls back to VIX1D-anchored ATM+smile only when
				// no captured strikes exist for this expiry+right at this minute.
				var surfaceIv = parsed.CallPut != null
					? BuildSurfaceIv(parsed.Root, parsed.ExpiryDate, parsed.CallPut, parsed.Strike, spot, timeYears, asOfUtc)
					: null;
				SyntheticPricingSource src;
				if (surfaceIv.HasValue && surfaceIv.Value > 0m)
				{
					iv = surfaceIv.Value;
					src = SyntheticPricingSource.SurfaceIv;
				}
				else
				{
					// No same-expiry surface: try interpolating real IV across NEARBY expiries before
					// falling back to the parametric VIX-anchored guess. >0DTE only — 0DTE vol regime
					// doesn't interpolate from weekly/monthly neighbors, and 0DTE is densely captured anyway.
					var crossIv = (parsed.CallPut != null && dte > 0)
						? BuildCrossExpiryIv(parsed.Root, parsed.ExpiryDate, parsed.CallPut, parsed.Strike, spot, timeYears, asOf, asOfUtc)
						: null;
					if (crossIv.HasValue && crossIv.Value > 0m)
					{
						iv = crossIv.Value;
						src = SyntheticPricingSource.CrossExpiry;
					}
					else if (atm.HasValue)
					{
						iv = _iv.ApplySmile(atm.Value, parsed.Root, parsed.Strike, spot, smileScale);
						src = SyntheticPricingSource.VixSmile;
					}
					else
						src = SyntheticPricingSource.Intrinsic;
				}

				if (iv.HasValue)
					price = OptionMath.BlackScholes(spot, parsed.Strike, timeYears, _riskFreeRate, iv.Value, parsed.CallPut!);
				else
				{
					price = parsed.CallPut == "C" ? Math.Max(0m, spot - parsed.Strike) : Math.Max(0m, parsed.Strike - spot);
					src = SyntheticPricingSource.Intrinsic; // ApplySmile returned null → no usable IV, priced at intrinsic
				}
				_syntheticSource[(sym, asOf.Date)] = src;
			}

			var halfSpread = HalfSpreadFor(parsed.Root, price);
			var bid = Math.Max(0m, price - halfSpread);
			var ask = price + halfSpread;
			options[sym] = new OptionContractQuote(
				ContractSymbol: sym,
				LastPrice: price,
				Bid: bid,
				Ask: ask,
				Change: null,
				PercentChange: null,
				Volume: volume,
				OpenInterest: null,
				ImpliedVolatility: iv,
				HistoricalVolatility: null,
				ImpliedVolatility5Day: null
			);
		}

		return new QuoteSnapshot(options, underlyings);
	}

	/// <summary>Convenience for the intraday-trigger pass: pre-resolves the smile scale + a per-DTE
	/// map of ATM IV for the legs being re-priced, then calls <see cref="PriceAtSpot"/>. Returns an
	/// empty map if no leg's ATM IV is available for the date.</summary>
	internal async Task<IReadOnlyDictionary<string, OptionContractQuote>> GetIntradayQuotesAsync(
		DateTime asOf, string ticker, decimal spot, IEnumerable<string> optionSymbols,
		double zeroDteTimeYears, CancellationToken cancellation)
	{
		var parsed = new List<(string Symbol, OptionParsed Parsed)>();
		foreach (var sym in optionSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p != null) parsed.Add((sym, p));
		}
		var atmByDte = new Dictionary<int, decimal>();
		foreach (var (_, p) in parsed)
		{
			var dte = (p.ExpiryDate.Date - asOf.Date).Days;
			if (atmByDte.ContainsKey(dte)) continue;
			var atm = await _iv.GetAtmIVAsync(ticker, asOf, dte, cancellation);
			if (atm.HasValue) atmByDte[dte] = atm.Value;
		}
		if (atmByDte.Count == 0) return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var smileScale = await _iv.GetSmileScaleAsync(ticker, asOf, cancellation);
		return PriceAtSpot(asOf, optionSymbols, spot, atmByDte, smileScale, zeroDteTimeYears);
	}

	/// <summary>Re-prices a set of option legs at an explicit spot for a single ticker, with an
	/// override for 0DTE time-to-expiry. Used by the intraday-trigger pass in
	/// <see cref="BacktestRunner"/> to compute position MTM at the day's bar.High and bar.Low
	/// without disturbing the cache or refetching ATM IV (passed in by the caller). Non-0DTE legs
	/// use the standard <c>dte/365</c> TTE — a few hours of intraday adjustment is negligible at
	/// 7+ DTE. <paramref name="atmByDte"/> maps each leg's DTE to the ATM IV at that term; missing
	/// keys fall back to the longest-DTE entry (defensive — caller should pre-populate).
	///
	/// <para>Does not consult <see cref="_optionBars"/>: the intraday-trigger pass evaluates at the
	/// day's bar.High / bar.Low extremes, and the daily-step simulator doesn't track which minute
	/// those extremes occurred at. Without a minute timestamp we can't pick the right captured bar,
	/// so this path stays on the parametric (Black-Scholes + smile) model. The open-pass in
	/// <see cref="GetQuotesAsync"/> does use captured bars where available.</para></summary>
	internal IReadOnlyDictionary<string, OptionContractQuote> PriceAtSpot(
		DateTime asOf, IEnumerable<string> optionSymbols, decimal spot,
		IReadOnlyDictionary<int, decimal> atmByDte, decimal smileScale, double zeroDteTimeYears)
	{
		if (atmByDte.Count == 0) return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var fallbackAtm = atmByDte.OrderByDescending(kv => kv.Key).First().Value;

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var sym in optionSymbols)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;

			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
			var atmIv = atmByDte.TryGetValue(dte, out var hit) ? hit : fallbackAtm;
			var iv = _iv.ApplySmile(atmIv, parsed.Root, parsed.Strike, spot, smileScale) ?? atmIv;
			var timeYears = dte <= 0 ? zeroDteTimeYears : dte / 365.0;
			var price = OptionMath.BlackScholes(spot, parsed.Strike, timeYears, _riskFreeRate, iv, parsed.CallPut);

			var halfSpread = HalfSpreadFor(parsed.Root, price);
			var bid = Math.Max(0m, price - halfSpread);
			var ask = price + halfSpread;
			options[sym] = new OptionContractQuote(
				ContractSymbol: sym,
				LastPrice: price,
				Bid: bid,
				Ask: ask,
				Change: null,
				PercentChange: null,
				Volume: null,
				OpenInterest: null,
				ImpliedVolatility: iv,
				HistoricalVolatility: null,
				ImpliedVolatility5Day: null);
		}
		return options;
	}

	private static decimal HalfSpreadFor(string ticker, decimal mid)
	{
		var (floor, pct) = PennyPilotIndexEtfs.Contains(ticker)
			? (IndexHalfSpreadFloor, IndexHalfSpreadPct)
			: (SingleStockHalfSpreadFloor, SingleStockHalfSpreadPct);
		return Math.Max(floor, mid * pct);
	}
}

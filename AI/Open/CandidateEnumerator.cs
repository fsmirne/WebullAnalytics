using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

internal static class CandidateEnumerator
{

	/// <summary>Enumerates candidate skeletons for a ticker. <paramref name="availableExpirations"/>, when
	/// non-null, is the set of real expirations from the chain — using these (rather than computed
	/// 3rd-Friday/Friday helpers) is what makes holiday-shifted monthlies (e.g. Juneteenth pushes June
	/// monthly to Thursday) match the OCC symbols Webull actually returns. Pass null to fall back to the
	/// computed Friday helpers, used by tests that don't model a chain.
	///
	/// <paramref name="quotes"/>, when non-null, provides the live chain quotes. The delta-band
	/// filters that select short / long strikes pull each strike's actual <c>ImpliedVolatility</c> from
	/// the matching <see cref="OptionContractQuote"/> instead of using the static
	/// <c>cfg.Indicators.IvDefaultPct</c>. In regimes where the smile lifts wing IV materially above
	/// the static default (e.g. SPXW today: default 18%, live 28% at delta-0.15 strikes), strike
	/// picks using the static IV land in deltas the live market reads outside the band. Live IV
	/// closes that gap. Strikes without a quote fall back to the static default.</summary>
	public static IEnumerable<CandidateSkeleton> Enumerate(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations = null, IReadOnlyDictionary<string, OptionContractQuote>? quotes = null)
	{
		if (cfg.Structures.LongCalendar.Enabled)
			foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongCalendar, OpenStructureKind.LongCalendar, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.DoubleCalendar.Enabled)
			foreach (var sk in EnumerateDoubleCalendars(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.LongDiagonal.Enabled)
			foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongDiagonal, OpenStructureKind.LongDiagonal, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.DoubleDiagonal.Enabled)
			foreach (var sk in EnumerateDoubleDiagonals(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.IronButterfly.Enabled)
			foreach (var sk in EnumerateIronButterflies(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.IronCondor.Enabled)
			foreach (var sk in EnumerateIronCondors(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.Condor.Enabled)
			foreach (var sk in EnumerateCondors(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.ShortVertical.Enabled)
			foreach (var sk in EnumerateShortVerticals(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.LongVertical.Enabled)
			foreach (var sk in EnumerateLongVerticals(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.LongCallPut.Enabled)
			foreach (var sk in EnumerateLongCallPut(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.DiagonalVertical.Enabled)
			foreach (var sk in EnumerateDiagonalVerticals(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;

		if (cfg.Structures.CalendarVertical.Enabled)
			foreach (var sk in EnumerateCalendarVerticals(ticker, spot, asOf, cfg, availableExpirations, quotes))
				yield return sk;
	}

	/// <summary>Resolves the implied volatility to use for delta-band filtering on a specific strike.
	/// Prefers the live chain's per-strike IV; falls back to the configured static default if no
	/// quote is available or the quote lacks an IV. Returns a fraction (e.g. 0.18 for 18%).</summary>
	private static decimal ResolveIv(string ticker, DateTime expiry, decimal strike, string callPut, IReadOnlyDictionary<string, OptionContractQuote>? quotes, decimal defaultIv)
	{
		if (quotes != null)
		{
			var symbol = MatchKeys.OccSymbol(ticker, expiry, strike, callPut);
			if (quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility is decimal liveIv && liveIv > 0m)
				return liveIv;
		}
		return defaultIv;
	}

	/// <summary>When the chain is known, take its real expirations within the DTE window; otherwise fall
	/// back to the ticker-aware computed-expiry enumerator (daily for SPX/SPY/QQQ/NDX/XSP/SPXW, Mon-Wed-Fri
	/// for the mega-cap multi-weekly list, weekly Fridays for everyone else).</summary>
	private static IEnumerable<DateTime> WeeklyExpiriesInRange(string ticker, IReadOnlySet<DateTime>? available, DateTime asOf, int minDte, int maxDte)
	{
		if (available == null) return OpenerExpiryHelpers.NextExpiriesForTicker(ticker, asOf, minDte, maxDte);
		var start = asOf.Date.AddDays(minDte);
		var end = asOf.Date.AddDays(maxDte);
		// Snap market-closed dates to the day the contract actually expires (e.g. a Good Friday weekly →
		// the preceding Thursday). Data providers list the holiday-Friday symbol but it never trades, so
		// using it verbatim makes the backtest fabricate a fill (phantom strike) and would propose a
		// non-tradeable contract live. Snapping (vs dropping) keeps the real expiry: if its strikes are in
		// the chain the candidate prices off real bars, else the per-expiry ladder is empty and it drops
		// naturally. Distinct collapses a snap that collides with an already-listed prior-day expiry.
		return available.Select(MarketCalendar.PreviousOpenOnOrBefore).Where(d => d >= start && d <= end).Distinct().OrderBy(d => d);
	}

	/// <summary>Same as <see cref="WeeklyExpiriesInRange"/> for chain-known expirations — the chain's real
	/// dates are intrinsically holiday-adjusted, so there's no separate "monthly" carve-out to make. The
	/// fallback path still uses the computed 3rd-Friday helper for tests that don't supply a chain.</summary>
	private static IEnumerable<DateTime> MonthlyExpiriesInRange(IReadOnlySet<DateTime>? available, DateTime asOf, int minDte, int maxDte)
	{
		if (available == null) return OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, minDte, maxDte);
		var start = asOf.Date.AddDays(minDte);
		var end = asOf.Date.AddDays(maxDte);
		// Snap market-closed dates to the real expiry — see WeeklyExpiriesInRange. The April monthly third
		// Friday is Good Friday in some years (2025-04-18, 2026-04-03), which settles the preceding Thursday.
		return available.Select(MarketCalendar.PreviousOpenOnOrBefore).Where(d => d >= start && d <= end).Distinct().OrderBy(d => d);
	}

	private static IEnumerable<DateTime> FilterShortExpiries(IEnumerable<DateTime> exps, OpenerConfig cfg)
	{
		if (cfg.AllowShortDailies && cfg.AllowShortWeeklies && cfg.AllowShortMonthlies) return exps;
		return exps.Where(d => {
			bool isMonthly = OpenerExpiryHelpers.IsMonthlyExpiry(d);
			bool isWeekly = !isMonthly && OpenerExpiryHelpers.IsWeekEndingExpiry(d);
			bool isDaily = !isWeekly && !isMonthly;
			return (isDaily && cfg.AllowShortDailies) || (isWeekly && cfg.AllowShortWeeklies) || (isMonthly && cfg.AllowShortMonthlies);
		});
	}

	private static IEnumerable<DateTime> FilterLongExpiries(IEnumerable<DateTime> exps, OpenerConfig cfg)
	{
		if (cfg.AllowLongDailies && cfg.AllowLongWeeklies && cfg.AllowLongMonthlies) return exps;
		return exps.Where(d => {
			bool isMonthly = OpenerExpiryHelpers.IsMonthlyExpiry(d);
			bool isWeekly = !isMonthly && OpenerExpiryHelpers.IsWeekEndingExpiry(d);
			bool isDaily = !isWeekly && !isMonthly;
			return (isDaily && cfg.AllowLongDailies) || (isWeekly && cfg.AllowLongWeeklies) || (isMonthly && cfg.AllowLongMonthlies);
		});
	}

	private static IEnumerable<CandidateSkeleton> EnumerateCalendarLike(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, OpenerCalendarLikeConfig sCfg, OpenStructureKind kind, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		// Bound the expiry cross-product (same cap as DiagonalVertical): on daily-expiry chains (SPXW) the
		// short window holds ~7 expiries and the long window ~20, and the full product × strikes × sides feeds
		// thousands of candidates per tick into the numerical breakeven scan — the per-minute scoring grinds.
		// Nearest 2 short × nearest 3 long keeps it tractable and matches the DiagonalVertical breadth.
		var shortExps = FilterShortExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax), cfg).OrderBy(e => e).Take(2).ToList();
		var longExps = FilterLongExpiries(MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax), cfg).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = FallbackStep(spot);
		var defaultIv = cfg.Indicators.IvDefaultPct;
		var useDelta = sCfg.DeltaMax > 0m; // delta-band placement vs legacy ATM grid
		foreach (var callPut in new[] { "C", "P" })
			foreach (var shortExp in shortExps)
				foreach (var longExp in longExps)
				{
					if (longExp <= shortExp) continue;
					// Per-expiry ladders: the short strike comes from the short expiry's listed strikes; the long
					// leg shares it (calendar) or sits at its own strike (diagonal) in the LONG expiry's ladder.
					var shortLadder = StrikeLadder.Build(ticker, shortExp, callPut, quotes);
					var longLadder = StrikeLadder.Build(ticker, longExp, callPut, quotes);

					if (useDelta)
					{
						// Anchor (calendar shared strike / diagonal long leg) in the delta band on the FAR leg —
						// the directional anchor, same convention as DiagonalVertical / CalendarVertical.
						var longYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, longExp);
						var dir = callPut == "C" ? 1 : -1;
						// Anchors SPAN the whole [DeltaMin, DeltaMax] band (sorted OTM→ITM), not just the midpoint —
						// so a band like 0.40–0.70 yields OTM, ATM, and ITM long legs as distinct candidates and the
						// scorer picks per regime (e.g. ITM call diagonals in a bull tape). The band is the user's
						// delta control; MaxLongAnchors caps the per-minute scan by downsampling a wide band evenly.
						var bandStrikes = StrikesAround(spot, step, 24, longLadder)
							.Select(k => (k, d: Math.Abs(OptionMath.Delta(spot, k, longYears, OptionMath.RiskFreeRate, ResolveIv(ticker, longExp, k, callPut, quotes, defaultIv), callPut))))
							.Where(x => x.d >= sCfg.DeltaMin && x.d <= sCfg.DeltaMax)
							.OrderBy(x => x.d).Select(x => x.k).ToList();
						var anchors = SpanEvenly(bandStrikes, cfg.MaxLongAnchors);

						foreach (var anchor in anchors)
						{
							if (kind == OpenStructureKind.LongCalendar)
							{
								// The anchor is picked from the LONG ladder; a calendar shares the strike across expiries,
								// so it must ALSO be listed at the SHORT expiry, else the near leg is unpriceable. (Short
								// and long ladders differ on real SPXW chains — this is what produced zero calendar opens.)
								if (shortLadder.ChainPresent && WingStrike(anchor, 0, 1, step, shortLadder) != anchor) continue;
								yield return BuildSpread(ticker, kind, shortExp, longExp, anchor, anchor, callPut);
							}
							else
							{
								// Diagonal: short leg from the ShortDelta band OR a tight gap off the long anchor, on
								// either side of the long (covered + reverse).
								var shortYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, shortExp);
								// Span the short delta band too (sorted, downsampled) — not the 2 nearest its midpoint.
								// Clustering at shortMid pinned the short near delta 0.40 and never reached the ATM
								// (~0.50) end, so the tight near-ATM covered diagonals (rich short premium, balanced BE)
								// were never enumerated. Now the scorer can choose them.
								var shortInBand = StrikesAround(spot, step, 24, shortLadder)
									.Select(k => (k, d: Math.Abs(OptionMath.Delta(spot, k, shortYears, OptionMath.RiskFreeRate, ResolveIv(ticker, shortExp, k, callPut, quotes, defaultIv), callPut))))
									.Where(x => x.d >= sCfg.ShortDeltaMin && x.d <= sCfg.ShortDeltaMax)
									.OrderBy(x => x.d).Select(x => x.k).ToList();
								// Plus TIGHT 1–2 strike gaps off the long anchor. The delta grid is too coarse to land an
								// adjacent-strike pairing (a near-ATM short one strike above a near-ATM long), so without
								// these the balanced tight covered diagonals the scorer rates highest are never built.
								var tightShorts = Enumerable.Range(1, Math.Max(0, cfg.MaxTightGapStrikes))
									.SelectMany(w => new[] { WingStrike(anchor, w, dir, step, shortLadder), WingStrike(anchor, w, -dir, step, shortLadder) })
									.Where(s => s is > 0m).Select(s => s!.Value);
								var shortStrikes = SpanEvenly(shortInBand, cfg.MaxShortAnchors).Concat(tightShorts).Distinct();
								foreach (var shortStrike in shortStrikes)
								{
									// Same strike = calendar, not a diagonal. Either side of the long is allowed: short
									// OTM-of-long = covered (bullish) diagonal, short ITM-of-long = reverse diagonal. The
									// scorer's debit>0 gate drops net-credit (reverse) spreads that don't make sense.
									if (shortStrike == anchor) continue;
									yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, anchor, callPut);
								}
							}
						}
						continue;
					}

					foreach (var shortStrike in StrikeGrid(spot, step, shortLadder))
					{
						// Legacy ATM grid. Skip strikes that are ITM by more than one step on either side (bad debit-calendar entry).
						if (callPut == "C" && shortStrike < spot - step) continue;
						if (callPut == "P" && shortStrike > spot + step) continue;

						if (kind == OpenStructureKind.LongCalendar)
						{
							// Calendar shares one strike across expiries — require it listed at the long expiry too.
							if (longLadder.ChainPresent && WingStrike(shortStrike, 0, 1, step, longLadder) != shortStrike) continue;
							yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, shortStrike, callPut);
						}
						else
						{
							// Diagonal: long strike one strike above or below the short strike, along the long ladder.
							foreach (var dir in new[] { -1, 1 })
							{
								if (WingStrike(shortStrike, 1, dir, step, longLadder) is not { } longStrike || longStrike <= 0m) continue;
								yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
							}
						}
					}
				}
	}

	/// <summary>Five distinct strikes centered on spot: floor(spot/step)*step, ceil(spot/step)*step, and ±1 step around each.</summary>
	internal static IReadOnlyList<decimal> StrikeGrid(decimal spot, decimal step)
	{
		var atmBelow = Math.Floor(spot / step) * step;
		var atmAbove = Math.Ceiling(spot / step) * step;
		var set = new HashSet<decimal>
		{
			atmBelow,
			atmAbove,
			atmBelow - step,
			atmAbove + step,
			atmBelow - 2m * step
		};
		return set.Where(s => s > 0m).OrderBy(s => s).ToList();
	}

	/// <summary>Fallback strike increment for the NO-CHAIN path only (<c>--theoretical</c> / unit tests, where
	/// the ladder is empty). When a chain is present the ladder is authoritative and this is unused. A spot-
	/// magnitude heuristic so the opener needs no <c>indicators.strikeStep</c> config — that knob now governs
	/// only roll/scenario/assignment stepping, not opener strike selection.</summary>
	private static decimal FallbackStep(decimal spot) => spot switch
	{
		< 25m => 0.5m,
		< 250m => 1m,
		< 2500m => 5m,
		_ => 25m,
	};

	// ── Ladder-aware strike sourcing ──────────────────────────────────────────────────────────────────
	// SPX-family chains list strikes on a non-uniform grid that varies by moneyness and expiry, so a single
	// uniform `strikeStep` generates strikes the venue never lists (their legs come back unpriced and the
	// candidate is dropped). When the chain is known (live, or backtest once it exposes captured strikes) we
	// source strikes from the per-expiry ladder and express widths as a count of strikes along it. When no
	// chain is supplied (tests / --theoretical), the ladder is empty and we fall back to the uniform grid so
	// behaviour is byte-for-byte unchanged.

	// Each helper is AUTHORITATIVE when the chain is present (live snapshot or backtest captured): it returns
	// only real listed strikes, and an empty chain-present ladder yields nothing (no phantom strikes). Only
	// when no chain was supplied at all (tests / --theoretical) do they fall back to the uniform step grid.

	/// <summary>~ATM strike band: the chain's listed strikes bracketing spot when a chain is present, else the uniform grid.</summary>
	private static IReadOnlyList<decimal> StrikeGrid(decimal spot, decimal step, StrikeLadder ladder)
		=> ladder.ChainPresent ? ladder.Around(spot, 3).ToList() : StrikeGrid(spot, step);

	/// <summary>Listed strikes below spot when a chain is present, else the uniform step grid.</summary>
	private static IReadOnlyList<decimal> StrikesBelow(decimal spot, decimal step, int count, StrikeLadder ladder)
		=> ladder.ChainPresent ? ladder.Below(spot, count).ToList() : StrikesBelowSpot(spot, step, count).ToList();

	/// <summary>Listed strikes above spot when a chain is present, else the uniform step grid.</summary>
	private static IReadOnlyList<decimal> StrikesAbove(decimal spot, decimal step, int count, StrikeLadder ladder)
		=> ladder.ChainPresent ? ladder.Above(spot, count).ToList() : StrikesAboveSpot(spot, step, count).ToList();

	/// <summary>Listed strikes bracketing spot when a chain is present, else the uniform step grid.</summary>
	private static IReadOnlyList<decimal> StrikesAround(decimal spot, decimal step, int count, StrikeLadder ladder)
		=> ladder.ChainPresent ? ladder.Around(spot, count).ToList() : StrikesAroundSpot(spot, step, count).ToList();

	/// <summary>Up to <paramref name="max"/> items evenly spaced across <paramref name="sorted"/> (a
	/// delta-sorted strike list), always including both ends, so anchors span the configured band instead
	/// of clustering. Returns the whole list unchanged when it already fits within <paramref name="max"/>.</summary>
	private static List<decimal> SpanEvenly(IReadOnlyList<decimal> sorted, int max)
	{
		if (max <= 1 || sorted.Count <= max) return sorted.ToList();
		var result = new List<decimal>(max);
		for (var i = 0; i < max; i++)
			result.Add(sorted[(int)Math.Round((double)i * (sorted.Count - 1) / (max - 1))]);
		return result.Distinct().ToList();
	}

	/// <summary>Wing strike <paramref name="w"/> positions from <paramref name="anchor"/> in direction
	/// <paramref name="dir"/> (+1 up / -1 down): a count of strikes along the real ladder when a chain is
	/// present (null when it runs off the ladder OR no strike is there — the caller drops that candidate), else
	/// a uniform <c>w × step</c> dollar offset.</summary>
	private static decimal? WingStrike(decimal anchor, int w, int dir, decimal step, StrikeLadder ladder)
		=> ladder.ChainPresent ? ladder.Offset(anchor, dir * w) : anchor + dir * w * step;

	private static CandidateSkeleton BuildSpread(string ticker, OpenStructureKind kind, DateTime shortExp, DateTime longExp, decimal shortStrike, decimal longStrike, string callPut)
	{
		var shortSym = MatchKeys.OccSymbol(ticker, shortExp, shortStrike, callPut);
		var longSym = MatchKeys.OccSymbol(ticker, longExp, longStrike, callPut);
		var legs = new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		};
		return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: shortExp);
	}

	private static IEnumerable<CandidateSkeleton> EnumerateDoubleCalendars(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.DoubleCalendar;
		// Bound the expiry cross-product (same cap as DiagonalVertical): on daily-expiry chains (SPXW) the
		// short window holds ~7 expiries and the long window ~20, and the full product × strikes × sides feeds
		// thousands of candidates per tick into the numerical breakeven scan — the per-minute scoring grinds.
		// Nearest 2 short × nearest 3 long keeps it tractable and matches the DiagonalVertical breadth.
		var shortExps = FilterShortExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax), cfg).OrderBy(e => e).Take(2).ToList();
		var longExps = FilterLongExpiries(MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax), cfg).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = FallbackStep(spot);
		foreach (var shortExp in shortExps)
			foreach (var longExp in longExps)
			{
				if (longExp <= shortExp) continue;
				// Strangle across spot (put lower / call upper). Width = strikes apart along the combined
				// both-sides ladder; the legs reference these strikes at both expiries (calendar) and the
				// scorer/Phase-B drops any leg not listed at one of them.
				var combined = StrikeLadder.Build(ticker, shortExp, null, quotes);
				foreach (var widthSteps in sCfg.WidthSteps.Distinct().OrderBy(w => w))
					foreach (var lowerStrike in StrikeGrid(spot, step, combined))
					{
						if (WingStrike(lowerStrike, widthSteps, 1, step, combined) is not { } upperStrike || upperStrike <= lowerStrike) continue;
						if (spot < lowerStrike || spot > upperStrike) continue;
						yield return BuildDoubleCalendar(ticker, shortExp, longExp, lowerStrike, upperStrike);
					}
			}
	}

	private static CandidateSkeleton BuildDoubleCalendar(string ticker, DateTime shortExp, DateTime longExp, decimal putStrike, decimal callStrike)
	{
		var shortPut = MatchKeys.OccSymbol(ticker, shortExp, putStrike, "P");
		var longPut = MatchKeys.OccSymbol(ticker, longExp, putStrike, "P");
		var shortCall = MatchKeys.OccSymbol(ticker, shortExp, callStrike, "C");
		var longCall = MatchKeys.OccSymbol(ticker, longExp, callStrike, "C");
		var legs = new[]
		{
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		};
		return new CandidateSkeleton(ticker, OpenStructureKind.DoubleCalendar, legs, TargetExpiry: shortExp);
	}

	private static IEnumerable<CandidateSkeleton> EnumerateDoubleDiagonals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.DoubleDiagonal;
		// Bound the expiry cross-product (same cap as DiagonalVertical): on daily-expiry chains (SPXW) the
		// short window holds ~7 expiries and the long window ~20, and the full product × strikes × sides feeds
		// thousands of candidates per tick into the numerical breakeven scan — the per-minute scoring grinds.
		// Nearest 2 short × nearest 3 long keeps it tractable and matches the DiagonalVertical breadth.
		var shortExps = FilterShortExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax), cfg).OrderBy(e => e).Take(2).ToList();
		var longExps = FilterLongExpiries(MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax), cfg).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = FallbackStep(spot);
		foreach (var shortExp in shortExps)
			foreach (var longExp in longExps)
			{
				if (longExp <= shortExp) continue;
				// Strangle across spot with long wings further out. Body width and wing offsets are counts of
				// strikes along the combined both-sides ladder; the put long is below the call structure and
				// the call long above. Unlisted legs at either expiry are dropped downstream.
				var combined = StrikeLadder.Build(ticker, shortExp, null, quotes);
				foreach (var widthSteps in sCfg.WidthSteps.Distinct().OrderBy(w => w))
					foreach (var longWingSteps in sCfg.LongWingSteps.Distinct().OrderBy(w => w))
						foreach (var lowerShortStrike in StrikeGrid(spot, step, combined))
						{
							if (WingStrike(lowerShortStrike, widthSteps, 1, step, combined) is not { } upperShortStrike || upperShortStrike <= lowerShortStrike) continue;
							if (spot < lowerShortStrike || spot > upperShortStrike) continue;
							if (WingStrike(lowerShortStrike, longWingSteps, -1, step, combined) is not { } lowerLongStrike || lowerLongStrike <= 0m) continue;
							if (WingStrike(upperShortStrike, longWingSteps, 1, step, combined) is not { } upperLongStrike) continue;
							yield return BuildDoubleDiagonal(ticker, shortExp, longExp, lowerShortStrike, lowerLongStrike, upperShortStrike, upperLongStrike);
						}
			}
	}

	private static CandidateSkeleton BuildDoubleDiagonal(string ticker, DateTime shortExp, DateTime longExp, decimal putShortStrike, decimal putLongStrike, decimal callShortStrike, decimal callLongStrike)
	{
		var shortPut = MatchKeys.OccSymbol(ticker, shortExp, putShortStrike, "P");
		var longPut = MatchKeys.OccSymbol(ticker, longExp, putLongStrike, "P");
		var shortCall = MatchKeys.OccSymbol(ticker, shortExp, callShortStrike, "C");
		var longCall = MatchKeys.OccSymbol(ticker, longExp, callLongStrike, "C");
		var legs = new[]
		{
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		};
		return new CandidateSkeleton(ticker, OpenStructureKind.DoubleDiagonal, legs, TargetExpiry: shortExp);
	}

	/// <summary>Diagonal-from-verticals: a far-dated LONG vertical (debit, long leg in the LongDelta band —
	/// the directional anchor) + a near-dated SHORT vertical (credit, short leg in the ShortDelta band,
	/// further OTM — theta financing), all on one side. Enumerated for both calls (bullish) and puts
	/// (bearish). Every leg is bounded, so nothing is naked. TargetExpiry is the near (short) expiry — the
	/// structure's first decision point, where the short vertical settles and the long vertical carries on.
	/// WidthSteps sizes the near short vertical; the far long vertical's protective wing is pinned to the near
	/// vertical's protective wing (<see cref="SharedProtectiveWing"/>) so the far leg hedges the near vertical's
	/// entire loss zone and the payoff is a diagonal tent, not a zigzag with an unhedged downside cliff.</summary>
	private static IEnumerable<CandidateSkeleton> EnumerateDiagonalVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.DiagonalVertical;
		// Bound the expiry cross-product: a diagonal uses the nearest short expiry and a small set of long
		// expiries, not every pair. Without this the candidate count explodes on daily-expiry chains (SPXW)
		// and the per-minute scoring grinds. Nearest 2 short × nearest 3 long keeps it tractable.
		var shortExps = FilterShortExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax), cfg).OrderBy(e => e).Take(2).ToList();
		var longExps = FilterLongExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax), cfg).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);

		foreach (var side in new[] { "C", "P" })
			foreach (var shortExp in shortExps)
				foreach (var longExp in longExps)
				{
					if (longExp <= shortExp) continue;
					var longYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, longExp);
					var shortYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, shortExp);

					// Per-expiry ladders: the back-month grid is coarser than the front week on SPX-family
					// chains, so the long and short legs each snap to their own expiry's listed strikes and
					// widths count strikes along that ladder. Wide both-sided band (±24): the delta filter must
					// reach the OTM short anchor (delta ~0.20–0.35), well outside ATM.
					var longLadder = StrikeLadder.Build(ticker, longExp, side, quotes);
					var shortLadder = StrikeLadder.Build(ticker, shortExp, side, quotes);
					var longGrid = StrikesAround(spot, step, 24, longLadder);
					var shortGrid = StrikesAround(spot, step, 24, shortLadder);

					// Far long-vertical anchor: the long leg, directional, in the LongDelta band at the far expiry.
					// Cap to the 2 strikes nearest the band centre (delta-filtered strikes are few, but cap defensively).
					var longMid = (sCfg.LongDeltaMin + sCfg.LongDeltaMax) / 2m;
					var longAnchors = longGrid
						.Select(k => (k, d: Math.Abs(OptionMath.Delta(spot, k, longYears, OptionMath.RiskFreeRate, ResolveIv(ticker, longExp, k, side, quotes, defaultIv), side))))
						.Where(x => x.d >= sCfg.LongDeltaMin && x.d <= sCfg.LongDeltaMax)
						.OrderBy(x => Math.Abs(x.d - longMid)).Take(2).Select(x => x.k).ToList();
					// Near short-vertical anchor: the short leg, further OTM, in the ShortDelta band at the near expiry.
					var shortMid = (sCfg.ShortDeltaMin + sCfg.ShortDeltaMax) / 2m;
					var shortAnchors = shortGrid
						.Select(k => (k, d: Math.Abs(OptionMath.Delta(spot, k, shortYears, OptionMath.RiskFreeRate, ResolveIv(ticker, shortExp, k, side, quotes, defaultIv), side))))
						.Where(x => x.d >= sCfg.ShortDeltaMin && x.d <= sCfg.ShortDeltaMax)
						.OrderBy(x => Math.Abs(x.d - shortMid)).Take(2).Select(x => x.k).ToList();

					var dir = side == "C" ? 1 : -1;
					foreach (var longStrike in longAnchors)
						foreach (var shortStrike in shortAnchors)
						{
							// Directional consistency: short leg further OTM than the long anchor (calls: higher; puts: lower).
							if (side == "C" && shortStrike <= longStrike) continue;
							if (side == "P" && shortStrike >= longStrike) continue;

							foreach (var w in sCfg.WidthSteps.Distinct().OrderBy(x => x))
							{
								// Near short vertical (theta financing): WidthSteps strikes further OTM from the short anchor.
								if (WingStrike(shortStrike, w, dir, step, shortLadder) is not { } shortWing || shortWing <= 0m) continue;
								// Far long vertical's protective wing is PINNED to the near vertical's protective wing (snapped
								// onto the far ladder), not sized independently by WidthSteps. This keeps the far long leg
								// gaining through the near short vertical's whole loss zone: with an independent narrow far wing
								// the far vertical caps out ABOVE the near short strikes, leaving that max-loss zone unhedged and
								// producing a sharp downside cliff / max-loss valley (a third breakeven, a zigzag P&L) instead of
								// a true diagonal's tent. Sharing the protective strike makes the downside monotonic.
								if (SharedProtectiveWing(shortWing, dir, longLadder) is not { } longWing || longWing <= 0m) continue;
								// Far wing must stay strictly OTM of the far anchor, else it isn't a valid long vertical.
								if (dir * longWing <= dir * longStrike) continue;
								yield return BuildDiagonalVertical(ticker, side, shortExp, longExp, shortStrike, shortWing, longStrike, longWing);
							}
						}
				}
	}

	private static CandidateSkeleton BuildDiagonalVertical(string ticker, string side, DateTime shortExp, DateTime longExp, decimal shortStrike, decimal shortWingStrike, decimal longStrike, decimal longWingStrike)
		=> BuildTwoVerticalStructure(ticker, OpenStructureKind.DiagonalVertical, side, shortExp, longExp, shortStrike, shortWingStrike, longStrike, longWingStrike);

	/// <summary>The far long vertical's protective wing for a diagonal-vertical: the near vertical's protective
	/// wing <paramref name="nearWing"/> snapped onto the far expiry's ladder, nudged one strike further OTM if the
	/// snap landed short of it. Pinning the far wing to the near wing (rather than offsetting it independently by
	/// WidthSteps) makes the far long leg's gaining range cover the near short vertical's whole loss zone, so the
	/// payoff is a diagonal tent rather than a zigzag with an unhedged downside cliff. On a uniform grid (no
	/// chain) the two ladders coincide, so the near wing strike is used directly.</summary>
	private static decimal? SharedProtectiveWing(decimal nearWing, int dir, StrikeLadder longLadder)
	{
		if (!longLadder.ChainPresent) return nearWing;
		if (longLadder.Offset(nearWing, 0) is not { } snapped || snapped <= 0m) return null;
		// snapped at/beyond (OTM of) the near wing already covers the loss zone; if the coarser far ladder
		// snapped it to the in-side of the near wing, step one strike further OTM so coverage isn't lost.
		return dir * snapped >= dir * nearWing ? snapped : longLadder.Offset(snapped, dir);
	}

	/// <summary>Builds the shared "two defined-risk verticals on one side" leg set used by both
	/// DiagonalVertical (different anchor strikes per expiry) and CalendarVertical (same anchor strike
	/// across expiries). Far long vertical (debit) + near short vertical (credit); TargetExpiry is the near
	/// (short) expiry — the structure's first decision point. The geometry distinction is the caller's: the
	/// kind is passed in, and classifiers recover it from the strike count.</summary>
	private static CandidateSkeleton BuildTwoVerticalStructure(string ticker, OpenStructureKind kind, string side, DateTime shortExp, DateTime longExp, decimal shortStrike, decimal shortWingStrike, decimal longStrike, decimal longWingStrike)
	{
		var legs = new[]
		{
			// Far long vertical (debit): buy the anchor, sell the wing further OTM.
			new ProposalLeg("buy", MatchKeys.OccSymbol(ticker, longExp, longStrike, side), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol(ticker, longExp, longWingStrike, side), 1),
			// Near short vertical (credit): sell the anchor, buy the wing further OTM.
			new ProposalLeg("sell", MatchKeys.OccSymbol(ticker, shortExp, shortStrike, side), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol(ticker, shortExp, shortWingStrike, side), 1)
		};
		return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: shortExp);
	}

	/// <summary>Calendar-from-verticals: a far-dated LONG vertical (debit) + a near-dated SHORT vertical
	/// (credit) on one side that SHARE one anchor strike (and one wing) across both expiries. Identical to
	/// <see cref="EnumerateDiagonalVerticals"/> except the near and far verticals use the SAME strikes — a
	/// calendar rather than a diagonal. Enumerated for both calls and puts. Every leg is bounded.</summary>
	private static IEnumerable<CandidateSkeleton> EnumerateCalendarVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.CalendarVertical;
		var shortExps = FilterShortExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax), cfg).OrderBy(e => e).Take(2).ToList();
		var longExps = FilterLongExpiries(WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax), cfg).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);
		var deltaMid = (sCfg.DeltaMin + sCfg.DeltaMax) / 2m;

		foreach (var side in new[] { "C", "P" })
			foreach (var shortExp in shortExps)
				foreach (var longExp in longExps)
				{
					if (longExp <= shortExp) continue;
					var longYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, longExp);
					// A calendar shares one anchor strike AND one wing across both expiries, so both must be
					// listed in BOTH expiries' ladders. Anchor candidates come from the long ladder ∩ short
					// ladder (intersection); the wing is offset along the long ladder and re-checked on the
					// short ladder. Empty ladders → uniform step grid (tests / --theoretical).
					var longLadder = StrikeLadder.Build(ticker, longExp, side, quotes);
					var shortLadder = StrikeLadder.Build(ticker, shortExp, side, quotes);
					var longGrid = StrikesAround(spot, step, 24, longLadder);
					// Single shared anchor: the calendar strike, picked in the delta band on the far (long) leg —
					// the directional anchor, same convention as the diagonal-vertical's long anchor.
					var anchors = longGrid
						.Select(k => (k, d: Math.Abs(OptionMath.Delta(spot, k, longYears, OptionMath.RiskFreeRate, ResolveIv(ticker, longExp, k, side, quotes, defaultIv), side))))
						.Where(x => x.d >= sCfg.DeltaMin && x.d <= sCfg.DeltaMax)
						.OrderBy(x => Math.Abs(x.d - deltaMid)).Take(2).Select(x => x.k).ToList();

					var dir = side == "C" ? 1 : -1;
					foreach (var anchor in anchors)
						foreach (var w in sCfg.WidthSteps.Distinct().OrderBy(x => x))
						{
							// Wing must be the SAME listed strike on both expiries. Offset along the long ladder,
							// then confirm that exact strike is also listed in the short ladder (snap-and-verify).
							if (WingStrike(anchor, w, dir, step, longLadder) is not { } wing || wing <= 0m) continue;
							if (shortLadder.ChainPresent && WingStrike(anchor, w, dir, step, shortLadder) != wing) continue;
							// Same anchor + wing on both expiries → a calendar pair, not a diagonal.
							yield return BuildTwoVerticalStructure(ticker, OpenStructureKind.CalendarVertical, side, shortExp, longExp, anchor, wing, anchor, wing);
						}
				}
	}

	private static IEnumerable<CandidateSkeleton> EnumerateIronButterflies(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.IronButterfly;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var step = FallbackStep(spot);
		foreach (var exp in exps)
		{
			// Body straddle sits on a strike listed for both sides; wings are wingSteps strikes along each
			// side's own ladder. Combined ladder picks the ATM body strikes; per-side ladders place the wings.
			var putLadder = StrikeLadder.Build(ticker, exp, "P", quotes);
			var callLadder = StrikeLadder.Build(ticker, exp, "C", quotes);
			var bodyStrikes = StrikeGrid(spot, step, StrikeLadder.Build(ticker, exp, null, quotes));
			foreach (var bodyStrike in bodyStrikes)
				foreach (var wingSteps in sCfg.WingSteps.Distinct().OrderBy(w => w))
				{
					if (WingStrike(bodyStrike, wingSteps, -1, step, putLadder) is not { } putWing || putWing <= 0m) continue;
					if (WingStrike(bodyStrike, wingSteps, 1, step, callLadder) is not { } callWing || callWing <= 0m) continue;
					yield return BuildIronButterfly(ticker, exp, putWing, bodyStrike, callWing);
				}
		}
	}

	private static CandidateSkeleton BuildIronButterfly(string ticker, DateTime exp, decimal putWingStrike, decimal bodyStrike, decimal callWingStrike)
	{
		var longPut = MatchKeys.OccSymbol(ticker, exp, putWingStrike, "P");
		var shortPut = MatchKeys.OccSymbol(ticker, exp, bodyStrike, "P");
		var shortCall = MatchKeys.OccSymbol(ticker, exp, bodyStrike, "C");
		var longCall = MatchKeys.OccSymbol(ticker, exp, callWingStrike, "C");
		var legs = new[]
		{
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		};
		return new CandidateSkeleton(ticker, OpenStructureKind.IronButterfly, legs, TargetExpiry: exp);
	}

	private static IEnumerable<CandidateSkeleton> EnumerateIronCondors(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.IronCondor;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);

		foreach (var exp in exps)
		{
			var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, exp);
			var putLadder = StrikeLadder.Build(ticker, exp, "P", quotes);
			var callLadder = StrikeLadder.Build(ticker, exp, "C", quotes);
			// Combined both-sides ladder to measure the body width as a count of listed strikes across spot.
			var bodyLadder = StrikeLadder.Build(ticker, exp, null, quotes);
			var putShorts = StrikesBelow(spot, step, 8, putLadder)
				.Where(shortStrike =>
				{
					var iv = ResolveIv(ticker, exp, shortStrike, "P", quotes, defaultIv);
					var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "P"));
					return delta >= sCfg.ShortDeltaMin && delta <= sCfg.ShortDeltaMax;
				})
				.ToList();
			var callShorts = StrikesAbove(spot, step, 8, callLadder)
				.Where(shortStrike =>
				{
					var iv = ResolveIv(ticker, exp, shortStrike, "C", quotes, defaultIv);
					var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "C"));
					return delta >= sCfg.ShortDeltaMin && delta <= sCfg.ShortDeltaMax;
				})
				.ToList();

			foreach (var putShortStrike in putShorts)
				foreach (var callShortStrike in callShorts)
				{
					if (callShortStrike <= putShortStrike) continue;
					// Body width = number of listed strikes between the two shorts along the real ladder
					// (when a chain is known); falls back to the uniform step count for tests/--theoretical.
					var bodyWidthSteps = !bodyLadder.ChainPresent
						? (int?)((callShortStrike - putShortStrike) / step is var bw && bw == decimal.Truncate(bw) ? (int)bw : -1)
						: bodyLadder.StepsBetween(putShortStrike, callShortStrike);
					if (bodyWidthSteps is not int bws || !sCfg.BodyWidthSteps.Contains(bws)) continue;

					foreach (var widthSteps in sCfg.WidthSteps.Distinct().OrderBy(w => w))
					{
						if (WingStrike(putShortStrike, widthSteps, -1, step, putLadder) is not { } putLongStrike || putLongStrike <= 0m) continue;
						if (WingStrike(callShortStrike, widthSteps, 1, step, callLadder) is not { } callLongStrike || callLongStrike <= 0m) continue;
						yield return BuildIronCondor(ticker, exp, putLongStrike, putShortStrike, callShortStrike, callLongStrike);
					}
				}
		}
	}

	private static CandidateSkeleton BuildIronCondor(string ticker, DateTime exp, decimal putLongStrike, decimal putShortStrike, decimal callShortStrike, decimal callLongStrike)
	{
		var longPut = MatchKeys.OccSymbol(ticker, exp, putLongStrike, "P");
		var shortPut = MatchKeys.OccSymbol(ticker, exp, putShortStrike, "P");
		var shortCall = MatchKeys.OccSymbol(ticker, exp, callShortStrike, "C");
		var longCall = MatchKeys.OccSymbol(ticker, exp, callLongStrike, "C");
		var legs = new[]
		{
			new ProposalLeg("buy", longPut, 1),
			new ProposalLeg("sell", shortPut, 1),
			new ProposalLeg("sell", shortCall, 1),
			new ProposalLeg("buy", longCall, 1)
		};
		return new CandidateSkeleton(ticker, OpenStructureKind.IronCondor, legs, TargetExpiry: exp);
	}

	// How far below/above spot the anchor (near-spot inner short) search reaches, in listed strikes. The
	// short-delta band trims this down; the count only has to be generous enough to reach the OTM tail
	// on fine ($1) strike ladders before the band cuts it off.
	private const int CondorAnchorSearchStrikes = 30;

	/// <summary>Single-sided (all-put OR all-call) LONG condor. Unlike the iron condor — which brackets
	/// spot and thus always has ITM legs on one side — a condor is placed to ONE side of spot so every
	/// leg stays OTM (no early-assignment risk): puts below spot express a bearish profit zone, calls
	/// above a bullish one. Enumeration anchors the body's inner short nearest spot (kept OTM via the
	/// short-delta band), extends the body <c>bodyWidthSteps</c> further from spot to the second inner
	/// short, then places the two long wings <c>widthSteps</c> beyond each inner short. <c>side</c> picks
	/// put / call / both.</summary>
	private static IEnumerable<CandidateSkeleton> EnumerateCondors(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.Condor;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);
		var sides = sCfg.Side.Trim().ToLowerInvariant() switch
		{
			"put" => new[] { "P" },
			"call" => new[] { "C" },
			_ => new[] { "P", "C" },
		};

		foreach (var exp in exps)
		{
			var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, exp);
			foreach (var type in sides)
			{
				var ladder = StrikeLadder.Build(ticker, exp, type, quotes);
				var dir = type == "P" ? -1 : 1;   // body/wings extend away from spot: down for puts, up for calls
				var anchors = (type == "P" ? StrikesBelow(spot, step, CondorAnchorSearchStrikes, ladder) : StrikesAbove(spot, step, CondorAnchorSearchStrikes, ladder))
					.Where(k =>
					{
						var iv = ResolveIv(ticker, exp, k, type, quotes, defaultIv);
						var delta = Math.Abs(OptionMath.Delta(spot, k, years, OptionMath.RiskFreeRate, iv, type));
						return delta >= sCfg.ShortDeltaMin && delta <= sCfg.ShortDeltaMax;
					})
					.ToList();

				foreach (var anchor in anchors)
					foreach (var bodyWidth in sCfg.BodyWidthSteps.Distinct().OrderBy(w => w))
						foreach (var wing in sCfg.WidthSteps.Distinct().OrderBy(w => w))
						{
							if (WingStrike(anchor, bodyWidth, dir, step, ladder) is not { } farInner || farInner <= 0m) continue;
							if (WingStrike(anchor, wing, -dir, step, ladder) is not { } nearWing || nearWing <= 0m) continue;   // wing toward spot
							if (WingStrike(farInner, wing, dir, step, ladder) is not { } farWing || farWing <= 0m) continue;    // wing away from spot

							var strikes = new[] { anchor, farInner, nearWing, farWing }.OrderBy(s => s).ToArray();
							if (strikes.Distinct().Count() != 4) continue;
							yield return BuildCondor(ticker, exp, type, strikes[0], strikes[1], strikes[2], strikes[3]);
						}
			}
		}
	}

	private static CandidateSkeleton BuildCondor(string ticker, DateTime exp, string type, decimal outerLow, decimal innerLow, decimal innerHigh, decimal outerHigh)
	{
		// Long (debit) condor: buy the outer wings, sell the two inner body strikes — all one option type.
		var legs = new[]
		{
			new ProposalLeg("buy", MatchKeys.OccSymbol(ticker, exp, outerLow, type), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol(ticker, exp, innerLow, type), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol(ticker, exp, innerHigh, type), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol(ticker, exp, outerHigh, type), 1)
		};
		return new CandidateSkeleton(ticker, OpenStructureKind.Condor, legs, TargetExpiry: exp);
	}

	private static IEnumerable<CandidateSkeleton> EnumerateShortVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.ShortVertical;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);

		foreach (var exp in exps)
		{
			var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, exp);
			var putLadder = StrikeLadder.Build(ticker, exp, "P", quotes);
			var callLadder = StrikeLadder.Build(ticker, exp, "C", quotes);

			// Put credit side (bullish): short strike below spot.
			foreach (var shortStrike in StrikesBelow(spot, step, 8, putLadder))
			{
				var iv = ResolveIv(ticker, exp, shortStrike, "P", quotes, defaultIv);
				var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "P"));
				if (delta < sCfg.ShortDeltaMin || delta > sCfg.ShortDeltaMax) continue;
				foreach (var widthSteps in sCfg.WidthSteps)
				{
					if (WingStrike(shortStrike, widthSteps, -1, step, putLadder) is not { } longStrike || longStrike <= 0m) continue;
					yield return BuildVertical(ticker, OpenStructureKind.ShortPutVertical, exp, shortStrike, longStrike, "P");
				}
			}

			// Call credit side (bearish): short strike above spot.
			foreach (var shortStrike in StrikesAbove(spot, step, 8, callLadder))
			{
				var iv = ResolveIv(ticker, exp, shortStrike, "C", quotes, defaultIv);
				var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "C"));
				if (delta < sCfg.ShortDeltaMin || delta > sCfg.ShortDeltaMax) continue;
				foreach (var widthSteps in sCfg.WidthSteps)
				{
					if (WingStrike(shortStrike, widthSteps, 1, step, callLadder) is not { } longStrike || longStrike <= 0m) continue;
					yield return BuildVertical(ticker, OpenStructureKind.ShortCallVertical, exp, shortStrike, longStrike, "C");
				}
			}
		}
	}

	/// <summary>Long (debit) vertical = bull call spread or bear put spread. Long leg sits near/ATM
	/// (delta filter on the long leg), short leg <c>widthSteps × step</c> further OTM. Cheaper than
	/// a naked long call/put (the short leg offsets premium) but caps the upside at <c>width − debit</c>.
	/// Both sides bounded: max loss = debit paid, max profit = width − debit. Directional with
	/// defined risk both ways.</summary>
	private static IEnumerable<CandidateSkeleton> EnumerateLongVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.LongVertical;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);

		foreach (var exp in exps)
		{
			var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, exp);
			var callLadder = StrikeLadder.Build(ticker, exp, "C", quotes);
			var putLadder = StrikeLadder.Build(ticker, exp, "P", quotes);

			// Bull call spread (LongCallVertical): buy a near-ATM call, sell a further-OTM call.
			// Filter long-leg by delta in [longDeltaMin, longDeltaMax]. Iterate strikes both above
			// and below spot since delta-0.55 can be slightly ITM (strike below spot) and
			// delta-0.30 can be OTM (strike above).
			foreach (var longStrike in StrikesAround(spot, step, 8, callLadder))
			{
				var iv = ResolveIv(ticker, exp, longStrike, "C", quotes, defaultIv);
				var longDelta = Math.Abs(OptionMath.Delta(spot, longStrike, years, OptionMath.RiskFreeRate, iv, "C"));
				if (longDelta < sCfg.LongDeltaMin || longDelta > sCfg.LongDeltaMax) continue;
				foreach (var widthSteps in sCfg.WidthSteps)
				{
					if (WingStrike(longStrike, widthSteps, 1, step, callLadder) is not { } shortStrike || shortStrike <= 0m) continue;
					yield return BuildVertical(ticker, OpenStructureKind.LongCallVertical, exp, shortStrike, longStrike, "C");
				}
			}

			// Bear put spread (LongPutVertical): buy a near-ATM put, sell a further-OTM put.
			foreach (var longStrike in StrikesAround(spot, step, 8, putLadder))
			{
				var iv = ResolveIv(ticker, exp, longStrike, "P", quotes, defaultIv);
				var longDelta = Math.Abs(OptionMath.Delta(spot, longStrike, years, OptionMath.RiskFreeRate, iv, "P"));
				if (longDelta < sCfg.LongDeltaMin || longDelta > sCfg.LongDeltaMax) continue;
				foreach (var widthSteps in sCfg.WidthSteps)
				{
					if (WingStrike(longStrike, widthSteps, -1, step, putLadder) is not { } shortStrike || shortStrike <= 0m) continue;
					yield return BuildVertical(ticker, OpenStructureKind.LongPutVertical, exp, shortStrike, longStrike, "P");
				}
			}
		}
	}

	private static IEnumerable<decimal> StrikesBelowSpot(decimal spot, decimal step, int count)
	{
		var k = Math.Floor(spot / step) * step;
		if (k == spot) k -= step;
		for (int i = 0; i < count; i++)
		{
			if (k <= 0m) yield break;
			yield return k;
			k -= step;
		}
	}

	private static IEnumerable<decimal> StrikesAboveSpot(decimal spot, decimal step, int count)
	{
		var k = Math.Ceiling(spot / step) * step;
		if (k == spot) k += step;
		for (int i = 0; i < count; i++)
		{
			yield return k;
			k += step;
		}
	}

	private static CandidateSkeleton BuildVertical(string ticker, OpenStructureKind kind, DateTime exp, decimal shortStrike, decimal longStrike, string callPut)
	{
		var shortSym = MatchKeys.OccSymbol(ticker, exp, shortStrike, callPut);
		var longSym = MatchKeys.OccSymbol(ticker, exp, longStrike, callPut);
		var legs = new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		};
		return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: exp);
	}

	private static IEnumerable<CandidateSkeleton> EnumerateLongCallPut(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.LongCallPut;
		// Use the ticker-aware dispatch so daily-expiry tickers (SPX/SPXW/QQQ/etc.) can enumerate
		// 0DTE long calls/puts. Monthly-only enumeration was correct when the structure was constrained
		// to 21–60 DTE but blocks every shorter-term setup on tickers with daily chains.
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).Take(2).ToList();
		if (exps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct;
		var step = FallbackStep(spot);

		foreach (var exp in exps)
		{
			var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, exp);

			foreach (var callPut in new[] { "C", "P" })
			{
				var ladder = StrikeLadder.Build(ticker, exp, callPut, quotes);
				foreach (var strike in StrikesAround(spot, step, 10, ladder))
				{
					var iv = ResolveIv(ticker, exp, strike, callPut, quotes, defaultIv);
					var delta = Math.Abs(OptionMath.Delta(spot, strike, years, OptionMath.RiskFreeRate, iv, callPut));
					if (delta < sCfg.DeltaMin || delta > sCfg.DeltaMax) continue;

					var sym = MatchKeys.OccSymbol(ticker, exp, strike, callPut);
					var legs = new[] { new ProposalLeg("buy", sym, 1) };
					var kind = callPut == "C" ? OpenStructureKind.LongCall : OpenStructureKind.LongPut;
					yield return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: exp);
				}
			}
		}
	}

	private static IEnumerable<decimal> StrikesAroundSpot(decimal spot, decimal step, int count)
	{
		var below = StrikesBelowSpot(spot, step, count).ToList();
		var above = StrikesAboveSpot(spot, step, count).ToList();
		return below.Concat(above).Distinct();
	}
}

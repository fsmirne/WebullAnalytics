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
			foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongCalendar, OpenStructureKind.LongCalendar, availableExpirations))
				yield return sk;

		if (cfg.Structures.DoubleCalendar.Enabled)
			foreach (var sk in EnumerateDoubleCalendars(ticker, spot, asOf, cfg, availableExpirations))
				yield return sk;

		if (cfg.Structures.LongDiagonal.Enabled)
			foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongDiagonal, OpenStructureKind.LongDiagonal, availableExpirations))
				yield return sk;

		if (cfg.Structures.DoubleDiagonal.Enabled)
			foreach (var sk in EnumerateDoubleDiagonals(ticker, spot, asOf, cfg, availableExpirations))
				yield return sk;

		if (cfg.Structures.IronButterfly.Enabled)
			foreach (var sk in EnumerateIronButterflies(ticker, spot, asOf, cfg, availableExpirations))
				yield return sk;

		if (cfg.Structures.IronCondor.Enabled)
			foreach (var sk in EnumerateIronCondors(ticker, spot, asOf, cfg, availableExpirations, quotes))
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
		return available.Where(d => d >= start && d <= end).OrderBy(d => d);
	}

	/// <summary>Same as <see cref="WeeklyExpiriesInRange"/> for chain-known expirations — the chain's real
	/// dates are intrinsically holiday-adjusted, so there's no separate "monthly" carve-out to make. The
	/// fallback path still uses the computed 3rd-Friday helper for tests that don't supply a chain.</summary>
	private static IEnumerable<DateTime> MonthlyExpiriesInRange(IReadOnlySet<DateTime>? available, DateTime asOf, int minDte, int maxDte)
	{
		if (available == null) return OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, minDte, maxDte);
		var start = asOf.Date.AddDays(minDte);
		var end = asOf.Date.AddDays(maxDte);
		return available.Where(d => d >= start && d <= end).OrderBy(d => d);
	}

	private static IEnumerable<CandidateSkeleton> EnumerateCalendarLike(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, OpenerCalendarLikeConfig sCfg, OpenStructureKind kind, IReadOnlySet<DateTime>? availableExpirations)
	{
		var shortExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
		var longExps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = cfg.Indicators.StrikeStep;
		foreach (var shortStrike in StrikeGrid(spot, step))
		{
			// Skip strikes that are ITM by more than one step on either side (bad entry for a debit calendar).
			foreach (var callPut in new[] { "C", "P" })
			{
				if (callPut == "C" && shortStrike < spot - step) continue;
				if (callPut == "P" && shortStrike > spot + step) continue;

				foreach (var shortExp in shortExps)
					foreach (var longExp in longExps)
					{
						if (longExp <= shortExp) continue;

						if (kind == OpenStructureKind.LongCalendar)
						{
							var longStrike = shortStrike;
							yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
						}
						else
						{
							// Diagonal: long strike one step above or below short strike.
							foreach (var longStrike in new[] { shortStrike - step, shortStrike + step })
							{
								if (longStrike <= 0m) continue;
								yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
							}
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

	// ── Ladder-aware strike sourcing ──────────────────────────────────────────────────────────────────
	// SPX-family chains list strikes on a non-uniform grid that varies by moneyness and expiry, so a single
	// uniform `strikeStep` generates strikes the venue never lists (their legs come back unpriced and the
	// candidate is dropped). When the chain is known (live, or backtest once it exposes captured strikes) we
	// source strikes from the per-expiry ladder and express widths as a count of strikes along it. When no
	// chain is supplied (tests / --theoretical), the ladder is empty and we fall back to the uniform grid so
	// behaviour is byte-for-byte unchanged.

	/// <summary>~ATM strike band: the chain's listed strikes bracketing spot when known, else the uniform grid.</summary>
	private static IReadOnlyList<decimal> StrikeGrid(decimal spot, decimal step, StrikeLadder ladder)
		=> ladder.IsEmpty ? StrikeGrid(spot, step) : ladder.Around(spot, 3).ToList();

	/// <summary>Listed strikes below spot when the chain is known, else the uniform step grid.</summary>
	private static IReadOnlyList<decimal> StrikesBelow(decimal spot, decimal step, int count, StrikeLadder ladder)
		=> ladder.IsEmpty ? StrikesBelowSpot(spot, step, count).ToList() : ladder.Below(spot, count).ToList();

	/// <summary>Listed strikes above spot when the chain is known, else the uniform step grid.</summary>
	private static IReadOnlyList<decimal> StrikesAbove(decimal spot, decimal step, int count, StrikeLadder ladder)
		=> ladder.IsEmpty ? StrikesAboveSpot(spot, step, count).ToList() : ladder.Above(spot, count).ToList();

	/// <summary>Listed strikes bracketing spot when the chain is known, else the uniform step grid.</summary>
	private static IReadOnlyList<decimal> StrikesAround(decimal spot, decimal step, int count, StrikeLadder ladder)
		=> ladder.IsEmpty ? StrikesAroundSpot(spot, step, count).ToList() : ladder.Around(spot, count).ToList();

	/// <summary>Wing strike <paramref name="w"/> positions from <paramref name="anchor"/> in direction
	/// <paramref name="dir"/> (+1 up / -1 down): a count of strikes along the real ladder when known, else a
	/// uniform <c>w × step</c> dollar offset. Null when the offset runs off the ladder — the caller drops that
	/// candidate, exactly as it would live when the wing strike isn't listed.</summary>
	private static decimal? WingStrike(decimal anchor, int w, int dir, decimal step, StrikeLadder ladder)
		=> ladder.IsEmpty ? anchor + dir * w * step : ladder.Offset(anchor, dir * w);

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

	private static IEnumerable<CandidateSkeleton> EnumerateDoubleCalendars(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.DoubleCalendar;
		var shortExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
		var longExps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = cfg.Indicators.StrikeStep;
		var strikes = StrikeGrid(spot, step);
		foreach (var shortExp in shortExps)
			foreach (var longExp in longExps)
			{
				if (longExp <= shortExp) continue;

				foreach (var widthSteps in sCfg.WidthSteps.Distinct().OrderBy(w => w))
				{
					var width = widthSteps * step;
					foreach (var lowerStrike in strikes)
					{
						var upperStrike = lowerStrike + width;
						if (upperStrike <= lowerStrike) continue;
						if (spot < lowerStrike || spot > upperStrike) continue;
						yield return BuildDoubleCalendar(ticker, shortExp, longExp, lowerStrike, upperStrike);
					}
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

	private static IEnumerable<CandidateSkeleton> EnumerateDoubleDiagonals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.DoubleDiagonal;
		var shortExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
		var longExps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = cfg.Indicators.StrikeStep;
		var strikes = StrikeGrid(spot, step);
		foreach (var shortExp in shortExps)
			foreach (var longExp in longExps)
			{
				if (longExp <= shortExp) continue;

				foreach (var widthSteps in sCfg.WidthSteps.Distinct().OrderBy(w => w))
					foreach (var longWingSteps in sCfg.LongWingSteps.Distinct().OrderBy(w => w))
					{
						var width = widthSteps * step;
						var wing = longWingSteps * step;
						foreach (var lowerShortStrike in strikes)
						{
							var upperShortStrike = lowerShortStrike + width;
							if (upperShortStrike <= lowerShortStrike) continue;
							if (spot < lowerShortStrike || spot > upperShortStrike) continue;

							var lowerLongStrike = lowerShortStrike - wing;
							var upperLongStrike = upperShortStrike + wing;
							if (lowerLongStrike <= 0m) continue;

							yield return BuildDoubleDiagonal(ticker, shortExp, longExp, lowerShortStrike, lowerLongStrike, upperShortStrike, upperLongStrike);
						}
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
	/// structure's first decision point, where the short vertical settles and the long vertical carries on.</summary>
	private static IEnumerable<CandidateSkeleton> EnumerateDiagonalVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.DiagonalVertical;
		// Bound the expiry cross-product: a diagonal uses the nearest short expiry and a small set of long
		// expiries, not every pair. Without this the candidate count explodes on daily-expiry chains (SPXW)
		// and the per-minute scoring grinds. Nearest 2 short × nearest 3 long keeps it tractable.
		var shortExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).OrderBy(e => e).Take(2).ToList();
		var longExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct / 100m;
		var step = cfg.Indicators.StrikeStep;

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
								if (WingStrike(longStrike, w, dir, step, longLadder) is not { } longWing || longWing <= 0m) continue;
								if (WingStrike(shortStrike, w, dir, step, shortLadder) is not { } shortWing || shortWing <= 0m) continue;
								yield return BuildDiagonalVertical(ticker, side, shortExp, longExp, shortStrike, shortWing, longStrike, longWing);
							}
						}
				}
	}

	private static CandidateSkeleton BuildDiagonalVertical(string ticker, string side, DateTime shortExp, DateTime longExp, decimal shortStrike, decimal shortWingStrike, decimal longStrike, decimal longWingStrike)
		=> BuildTwoVerticalStructure(ticker, OpenStructureKind.DiagonalVertical, side, shortExp, longExp, shortStrike, shortWingStrike, longStrike, longWingStrike);

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
		var shortExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).OrderBy(e => e).Take(2).ToList();
		var longExps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).OrderBy(e => e).Take(3).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct / 100m;
		var step = cfg.Indicators.StrikeStep;
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
							if (!shortLadder.IsEmpty && WingStrike(anchor, w, dir, step, shortLadder) != wing) continue;
							// Same anchor + wing on both expiries → a calendar pair, not a diagonal.
							yield return BuildTwoVerticalStructure(ticker, OpenStructureKind.CalendarVertical, side, shortExp, longExp, anchor, wing, anchor, wing);
						}
				}
	}

	private static IEnumerable<CandidateSkeleton> EnumerateIronButterflies(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.IronButterfly;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var step = cfg.Indicators.StrikeStep;
		var bodyStrikes = StrikeGrid(spot, step);
		foreach (var exp in exps)
			foreach (var bodyStrike in bodyStrikes)
				foreach (var wingSteps in sCfg.WingSteps.Distinct().OrderBy(w => w))
				{
					var putWing = bodyStrike - wingSteps * step;
					var callWing = bodyStrike + wingSteps * step;
					if (putWing <= 0m) continue;
					yield return BuildIronButterfly(ticker, exp, putWing, bodyStrike, callWing);
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

		var defaultIv = cfg.Indicators.IvDefaultPct / 100m;
		var step = cfg.Indicators.StrikeStep;

		foreach (var exp in exps)
		{
			var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, exp);
			var putShorts = StrikesBelowSpot(spot, step, count: 8)
				.Where(shortStrike =>
				{
					var iv = ResolveIv(ticker, exp, shortStrike, "P", quotes, defaultIv);
					var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "P"));
					return delta >= sCfg.ShortDeltaMin && delta <= sCfg.ShortDeltaMax;
				})
				.ToList();
			var callShorts = StrikesAboveSpot(spot, step, count: 8)
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
					var bodyWidth = callShortStrike - putShortStrike;
					var bodyWidthSteps = bodyWidth / step;
					if (callShortStrike <= putShortStrike) continue;
					if (bodyWidthSteps != decimal.Truncate(bodyWidthSteps)) continue;
					if (!sCfg.BodyWidthSteps.Contains((int)bodyWidthSteps)) continue;

					foreach (var widthSteps in sCfg.WidthSteps.Distinct().OrderBy(w => w))
					{
						var putLongStrike = putShortStrike - widthSteps * step;
						var callLongStrike = callShortStrike + widthSteps * step;
						if (putLongStrike <= 0m) continue;
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

	private static IEnumerable<CandidateSkeleton> EnumerateShortVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var sCfg = cfg.Structures.ShortVertical;
		var exps = WeeklyExpiriesInRange(ticker, availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var defaultIv = cfg.Indicators.IvDefaultPct / 100m;
		var step = cfg.Indicators.StrikeStep;

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

		var defaultIv = cfg.Indicators.IvDefaultPct / 100m;
		var step = cfg.Indicators.StrikeStep;

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

		var defaultIv = cfg.Indicators.IvDefaultPct / 100m;
		var step = cfg.Indicators.StrikeStep;

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

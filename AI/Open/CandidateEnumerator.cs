using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

internal static class CandidateEnumerator
{
	/// <summary>Enumerates candidate skeletons for a ticker. <paramref name="availableExpirations"/>, when
	/// non-null, is the set of real expirations from the chain — using these (rather than computed
	/// 3rd-Friday/Friday helpers) is what makes holiday-shifted monthlies (e.g. Juneteenth pushes June
	/// monthly to Thursday) match the OCC symbols Webull actually returns. Pass null to fall back to the
	/// computed Friday helpers, used by tests that don't model a chain.</summary>
	public static IEnumerable<CandidateSkeleton> Enumerate(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations = null)
	{
		if (cfg.Structures.LongCalendar.Enabled)
			foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongCalendar, OpenStructureKind.LongCalendar, availableExpirations))
				yield return sk;

		if (cfg.Structures.LongDiagonal.Enabled)
			foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongDiagonal, OpenStructureKind.LongDiagonal, availableExpirations))
				yield return sk;

		if (cfg.Structures.ShortVertical.Enabled)
			foreach (var sk in EnumerateShortVerticals(ticker, spot, asOf, cfg, availableExpirations))
				yield return sk;

		if (cfg.Structures.LongCallPut.Enabled)
			foreach (var sk in EnumerateLongCallPut(ticker, spot, asOf, cfg, availableExpirations))
				yield return sk;
	}

	/// <summary>When the chain is known, take its real expirations within the DTE window; otherwise fall
	/// back to the computed weekly Fridays in the same window.</summary>
	private static IEnumerable<DateTime> WeeklyExpiriesInRange(IReadOnlySet<DateTime>? available, DateTime asOf, int minDte, int maxDte)
	{
		if (available == null) return OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte, maxDte);
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
		var shortExps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
		var longExps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = cfg.StrikeStep;
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

	private static IEnumerable<CandidateSkeleton> EnumerateShortVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.ShortVertical;
		var exps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var iv = cfg.IvDefaultPct / 100m;
		var step = cfg.StrikeStep;

		foreach (var exp in exps)
		{
			var years = Math.Max(1, (exp.Date - asOf.Date).Days) / 365.0;

			// Put credit side (bullish): short strike below spot.
			foreach (var shortStrike in StrikesBelowSpot(spot, step, count: 8))
			{
				var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "P"));
				if (delta < sCfg.ShortDeltaMin || delta > sCfg.ShortDeltaMax) continue;
				foreach (var widthSteps in sCfg.WidthSteps)
				{
					var longStrike = shortStrike - widthSteps * step;
					if (longStrike <= 0m) continue;
					yield return BuildVertical(ticker, OpenStructureKind.ShortPutVertical, exp, shortStrike, longStrike, "P");
				}
			}

			// Call credit side (bearish): short strike above spot.
			foreach (var shortStrike in StrikesAboveSpot(spot, step, count: 8))
			{
				var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "C"));
				if (delta < sCfg.ShortDeltaMin || delta > sCfg.ShortDeltaMax) continue;
				foreach (var widthSteps in sCfg.WidthSteps)
				{
					var longStrike = shortStrike + widthSteps * step;
					yield return BuildVertical(ticker, OpenStructureKind.ShortCallVertical, exp, shortStrike, longStrike, "C");
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

	private static IEnumerable<CandidateSkeleton> EnumerateLongCallPut(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.LongCallPut;
		var exps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).Take(2).ToList();
		if (exps.Count == 0) yield break;

		var iv = cfg.IvDefaultPct / 100m;
		var step = cfg.StrikeStep;

		foreach (var exp in exps)
		{
			var years = Math.Max(1, (exp.Date - asOf.Date).Days) / 365.0;

			foreach (var callPut in new[] { "C", "P" })
			{
				foreach (var strike in StrikesAroundSpot(spot, step, count: 10))
				{
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

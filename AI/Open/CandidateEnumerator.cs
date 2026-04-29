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
			foreach (var sk in EnumerateIronCondors(ticker, spot, asOf, cfg, availableExpirations))
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

		var step = cfg.StrikeStepFor(ticker);
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

	private static IEnumerable<CandidateSkeleton> EnumerateDoubleCalendars(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.DoubleCalendar;
		var shortExps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
		var longExps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = cfg.StrikeStepFor(ticker);
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
		var shortExps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
		var longExps = MonthlyExpiriesInRange(availableExpirations, asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
		if (shortExps.Count == 0 || longExps.Count == 0) yield break;

		var step = cfg.StrikeStepFor(ticker);
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

	private static IEnumerable<CandidateSkeleton> EnumerateIronButterflies(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.IronButterfly;
		var exps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var step = cfg.StrikeStepFor(ticker);
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

	private static IEnumerable<CandidateSkeleton> EnumerateIronCondors(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.IronCondor;
		var exps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var iv = cfg.IvDefaultPct / 100m;
		var step = cfg.StrikeStepFor(ticker);

		foreach (var exp in exps)
		{
			var years = Math.Max(1, (exp.Date - asOf.Date).Days) / 365.0;
			var putShorts = StrikesBelowSpot(spot, step, count: 8)
				.Where(shortStrike =>
				{
					var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "P"));
					return delta >= sCfg.ShortDeltaMin && delta <= sCfg.ShortDeltaMax;
				})
				.ToList();
			var callShorts = StrikesAboveSpot(spot, step, count: 8)
				.Where(shortStrike =>
				{
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

	private static IEnumerable<CandidateSkeleton> EnumerateShortVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, IReadOnlySet<DateTime>? availableExpirations)
	{
		var sCfg = cfg.Structures.ShortVertical;
		var exps = WeeklyExpiriesInRange(availableExpirations, asOf, sCfg.DteMin, sCfg.DteMax).ToList();
		if (exps.Count == 0) yield break;

		var iv = cfg.IvDefaultPct / 100m;
		var step = cfg.StrikeStepFor(ticker);

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
		var step = cfg.StrikeStepFor(ticker);

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

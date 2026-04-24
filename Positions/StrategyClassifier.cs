using WebullAnalytics.Trading;

namespace WebullAnalytics.Positions;

internal static class StrategyClassifier
{
	/// <summary>
	/// Classifies a list of parsed legs into a strategy kind. Returns null if the legs
	/// do not form a recognizable strategy (caller should surface "pass --strategy explicitly").
	/// Possible values: "Stock", "Single", "Vertical", "Calendar", "Diagonal",
	/// "IronCondor", "IronButterfly", "Butterfly", "Condor", "Spread",
	/// "CoveredCall", "ProtectivePut", "Collar", "Straddle", "Strangle".
	/// </summary>
	internal static string? Classify(IReadOnlyList<ParsedLeg> legs)
	{
		if (legs.Count == 0) return null;

		var stockLegs = legs.Where(l => l.Option == null).ToList();
		var optionLegs = legs.Where(l => l.Option != null).ToList();

		// Equity-only cases.
		if (optionLegs.Count == 0)
		{
			if (stockLegs.Count == 1) return "Stock";
			return null;
		}

		// Single option.
		if (optionLegs.Count == 1 && stockLegs.Count == 0) return "Single";

		// All option legs must share a root.
		var roots = optionLegs.Select(l => l.Option!.Root).Distinct().ToList();
		if (roots.Count > 1) return null;

		// Stock + option combos (common brokerage strategies).
		if (stockLegs.Count == 1)
		{
			// Root must match the stock symbol.
			if (!string.Equals(stockLegs[0].Symbol, roots[0], StringComparison.OrdinalIgnoreCase))
				return null;

			var stockIsLong = stockLegs[0].Action == LegAction.Buy;

			if (optionLegs.Count == 1)
			{
				var o = optionLegs[0];
				var isShortCall = o.Action == LegAction.Sell && o.Option!.CallPut == "C";
				var isLongPut = o.Action == LegAction.Buy && o.Option!.CallPut == "P";
				if (stockIsLong && isShortCall) return "CoveredCall";
				if (stockIsLong && isLongPut) return "ProtectivePut";
				return null;
			}

			if (optionLegs.Count == 2 && stockIsLong)
			{
				var hasLongPut = optionLegs.Any(l => l.Action == LegAction.Buy && l.Option!.CallPut == "P");
				var hasShortCall = optionLegs.Any(l => l.Action == LegAction.Sell && l.Option!.CallPut == "C");
				if (hasLongPut && hasShortCall) return "Collar";
			}

			return null;
		}

		// Option-only multi-leg: delegate to existing classifier.
		if (stockLegs.Count == 0 && optionLegs.Count >= 2)
		{
			var distinctExpiries = optionLegs.Select(l => l.Option!.ExpiryDate).Distinct().Count();
			var distinctStrikes = optionLegs.Select(l => l.Option!.Strike).Distinct().Count();
			var distinctCallPut = optionLegs.Select(l => l.Option!.CallPut).Distinct().Count();

			// Straddle: 2 legs, same strike, same expiry, one call + one put.
			if (optionLegs.Count == 2 && distinctStrikes == 1 && distinctExpiries == 1 && distinctCallPut == 2)
				return "Straddle";

			// Strangle: 2 legs, different strikes, same expiry, one call + one put.
			if (optionLegs.Count == 2 && distinctStrikes == 2 && distinctExpiries == 1 && distinctCallPut == 2)
				return "Strangle";

			var kind = ParsingHelpers.ClassifyStrategyKind(optionLegs.Count, distinctExpiries, distinctStrikes, distinctCallPut);
			// "Spread" means legs are structurally degenerate (same contract on all legs).
			// Return null so the caller prompts the user to pass --strategy explicitly.
			return kind == "Spread" ? null : kind;
		}

		return null;
	}
}

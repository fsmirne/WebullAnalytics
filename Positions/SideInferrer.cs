using WebullAnalytics.Trading;

namespace WebullAnalytics.Positions;

/// <summary>
/// Infers the combo `side` (BUY for net-debit, SELL for net-credit) from the leg structure.
/// The action/strike/expiry pattern determines direction unambiguously for every standard strategy;
/// `--side` is only needed as an override for unusual constructions.
/// </summary>
internal static class SideInferrer
{
	internal static string? Infer(IReadOnlyList<ParsedLeg> legs, string strategy)
	{
		switch (strategy)
		{
			case "Stock":
			case "Single":
				return legs[0].Action == LegAction.Buy ? "BUY" : "SELL";

			case "Calendar":
			case "Diagonal":
			{
				var buy = legs.First(l => l.Action == LegAction.Buy);
				var sell = legs.First(l => l.Action == LegAction.Sell);
				// Long calendar/diagonal: buy back-month (later expiry), sell front-month — net debit → BUY.
				return buy.Option!.ExpiryDate > sell.Option!.ExpiryDate ? "BUY" : "SELL";
			}

			case "Vertical":
			{
				var buy = legs.First(l => l.Action == LegAction.Buy);
				var sell = legs.First(l => l.Action == LegAction.Sell);
				// Call vertical: buy-lower + sell-higher = debit (bull call / BUY). Reversed = credit.
				// Put vertical:  buy-higher + sell-lower = debit (bear put  / BUY). Reversed = credit.
				if (buy.Option!.CallPut == "C")
					return buy.Option.Strike < sell.Option!.Strike ? "BUY" : "SELL";
				return buy.Option.Strike > sell.Option!.Strike ? "BUY" : "SELL";
			}

			case "Straddle":
			case "Strangle":
				if (legs.All(l => l.Action == LegAction.Buy)) return "BUY";
				if (legs.All(l => l.Action == LegAction.Sell)) return "SELL";
				return null;

			case "CoveredCall":
			case "ProtectivePut":
			case "Collar":
			{
				// Opening: long stock + short call (CC) / long put (PP) / collar combo = net debit → BUY.
				// Closing is rare; user overrides with explicit --side sell.
				var stock = legs.First(l => l.Option == null);
				return stock.Action == LegAction.Buy ? "BUY" : "SELL";
			}

			case "Butterfly":
			case "Condor":
			{
				// Single CP across all legs. Long (debit): wings BUY, body SELL. Short (credit): reversed.
				// The lowest-strike leg is always a wing, so its action reveals the direction.
				var lowest = legs.OrderBy(l => l.Option!.Strike).First();
				return lowest.Action == LegAction.Buy ? "BUY" : "SELL";
			}

			case "IronButterfly":
			case "IronCondor":
			{
				// Mixed calls + puts. Short (credit, typical): outer strike wings BUY, inner strikes SELL.
				// Long (debit): reversed. Sign flips vs Butterfly/Condor because the wings are out-of-the-money
				// on opposite sides (put wing below, call wing above), and the body premium dominates.
				var lowest = legs.OrderBy(l => l.Option!.Strike).First();
				return lowest.Action == LegAction.Buy ? "SELL" : "BUY";
			}

			default:
				return null;
		}
	}
}

using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Backtest fill-price model for multi-leg orders. Two parts:
/// <list type="bullet">
/// <item><description><see cref="LegPrice"/>: each leg gives up a fraction (<c>cross</c>) of its half-spread — 0 = mid, 1 = the
/// crossing side (ask to buy, bid to sell). Summed over legs this is the combo's net mid moved <c>cross</c> of the way toward its
/// natural price, the usual way a combo limit between mid and natural is modeled.</description></item>
/// <item><description><see cref="ClampToNoArb"/>: summing independent per-leg mids can price a same-expiry combo outside the range of
/// its own expiry payoff — e.g. a $10-wide iron fly "collecting" $10.50 at mid, a credit no counterparty would ever pay because it is
/// a riskless profit. Such fills made tight credit structures look loss-proof (SPY iron fly PF 4.55 at mid vs 0.15 at bid/ask,
/// 2026-07-27). The clamp holds the net price strictly inside [min payoff, max payoff].</description></item>
/// </list>
/// </summary>
internal static class ComboFill
{
	/// <summary>Distance kept inside the no-arbitrage bound: a fill exactly at the bound is still a free option (no downside, some upside).</summary>
	internal const decimal NoArbMargin = 0.01m;

	/// <summary>Leg fill price that gives up <paramref name="cross"/> of the half-spread between <paramref name="mid"/> and
	/// <paramref name="crossingPrice"/> (the ask for a buy, the bid for a sell).</summary>
	internal static decimal LegPrice(decimal mid, decimal crossingPrice, decimal cross) => mid + cross * (crossingPrice - mid);

	/// <summary>Returns <paramref name="fills"/> with the order's net price clamped strictly inside the range of its expiry payoff.
	/// Applies only to orders of two or more option legs that share one expiry — the payoff then bounds the fair price exactly
	/// (rates ignored). Cross-expiry orders (calendars, diagonals, rolls across expiries) and single legs are returned unchanged.
	/// Only the net matters for P&L, so the whole correction goes onto one leg: a leg is raised when possible (a buy leg to
	/// add debit, a sell leg to remove it), otherwise the highest-priced leg on the other side is lowered, floored at 0.</summary>
	internal static IReadOnlyList<BacktestLegFill> ClampToNoArb(IReadOnlyList<BacktestLegFill> fills)
	{
		if (fills.Count < 2) return fills;
		var legs = new List<(BacktestLegFill Fill, OptionParsed Parsed, decimal Weight)>(fills.Count);
		var unit = fills.Min(f => f.Qty);
		if (unit <= 0) return fills;
		foreach (var f in fills)
		{
			var p = ParsingHelpers.ParseOptionSymbol(f.Symbol);
			if (p is null || p.CallPut is not ("C" or "P")) return fills;
			if (legs.Count > 0 && p.ExpiryDate.Date != legs[0].Parsed.ExpiryDate.Date) return fills;
			legs.Add((f, p, (decimal)f.Qty / unit * (f.Side == Side.Buy ? 1m : -1m)));
		}

		var (minPayoff, maxPayoff) = PayoffRange(legs.Select(l => (l.Parsed.Strike, l.Parsed.CallPut, l.Weight)).ToList());
		var lo = minPayoff.HasValue ? minPayoff.Value + NoArbMargin : decimal.MinValue;
		var hi = maxPayoff.HasValue ? maxPayoff.Value - NoArbMargin : decimal.MaxValue;
		if (lo > hi) return fills;

		var net = legs.Sum(l => l.Weight * l.Fill.PricePerShare);   // debit paid per unit (negative = credit)
		var delta = Math.Clamp(net, lo, hi) - net;
		if (delta == 0m) return fills;

		// delta > 0: the order must pay more (or collect less). Raise a buy leg, else lower a sell leg. delta < 0: the reverse.
		var raiseSide = delta > 0m ? Side.Buy : Side.Sell;
		var index = IndexOfMaxPrice(legs, raiseSide);
		decimal change;
		if (index >= 0)
			change = Math.Abs(delta / legs[index].Weight);
		else
		{
			index = IndexOfMaxPrice(legs, raiseSide == Side.Buy ? Side.Sell : Side.Buy);
			change = -Math.Min(legs[index].Fill.PricePerShare, Math.Abs(delta / legs[index].Weight));
		}

		var adjusted = fills.ToList();
		adjusted[index] = adjusted[index] with { PricePerShare = adjusted[index].PricePerShare + change };
		return adjusted;
	}

	/// <summary>Min and max of a same-expiry option portfolio's expiry payoff per unit over spot ∈ [0, ∞). The payoff is piecewise linear
	/// with kinks at the strikes, so the extremes sit at spot 0, at a strike, or at infinity (null = unbounded, set by the call slope
	/// above the top strike).</summary>
	internal static (decimal? Min, decimal? Max) PayoffRange(IReadOnlyList<(decimal Strike, string CallPut, decimal Weight)> legs)
	{
		decimal Payoff(decimal spot) => legs.Sum(l => l.Weight * OptionMath.Intrinsic(spot, l.Strike, l.CallPut));
		var points = legs.Select(l => l.Strike).Append(0m).Distinct().ToList();
		var min = points.Min(Payoff);
		var max = points.Max(Payoff);
		var tailSlope = legs.Where(l => l.CallPut == "C").Sum(l => l.Weight);
		return (tailSlope < 0m ? null : min, tailSlope > 0m ? null : max);
	}

	private static int IndexOfMaxPrice(List<(BacktestLegFill Fill, OptionParsed Parsed, decimal Weight)> legs, Side side)
	{
		var index = -1;
		for (var i = 0; i < legs.Count; i++)
			if (legs[i].Fill.Side == side && (index < 0 || legs[i].Fill.PricePerShare > legs[index].Fill.PricePerShare))
				index = i;
		return index;
	}
}

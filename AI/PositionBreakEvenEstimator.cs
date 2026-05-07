using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>
/// Estimates the break-even spot prices of a multi-leg open position. For time spreads
/// (calendar/diagonal) it numerically locates the spots where, at the short leg's expiry, the
/// long leg's Black-Scholes value equals the short leg's intrinsic value plus the strategy's
/// adjusted net debit — i.e. where the position has zero P&amp;L. Uses the long leg's live IV from
/// <see cref="EvaluationContext.Quotes"/>.
///
/// Falls back to a coarse <c>shortStrike ± debit</c> heuristic when IV or spot is unavailable.
/// The heuristic is intentionally tight (1× debit, not 3×): callers treat the band as a "trigger
/// floor" and the heuristic should not understate how close spot is to the no-IV-known boundary.
///
/// Single-leg, vertical, and other structures fall through to the heuristic; the rules using this
/// helper currently target calendar/diagonal positions only.
/// </summary>
internal static class PositionBreakEvenEstimator
{
	public enum BreakEvenSource { BlackScholes, Heuristic, None }

	public static (decimal? Low, decimal? High, BreakEvenSource Source) Estimate(OpenPosition position, EvaluationContext ctx)
	{
		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null && l.Expiry.HasValue);
		var longLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Buy && l.CallPut != null && l.Expiry.HasValue);
		if (shortLeg == null || longLeg == null) return (null, null, BreakEvenSource.None);

		var debit = Math.Abs(position.AdjustedNetDebit);
		if (debit <= 0m) return (null, null, BreakEvenSource.None);

		if (longLeg.Expiry!.Value.Date > shortLeg.Expiry!.Value.Date
			&& ctx.Quotes.TryGetValue(longLeg.Symbol, out var longQuote)
			&& longQuote.ImpliedVolatility is decimal iv && iv > 0m
			&& ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) && spot > 0m)
		{
			var (low, high) = SolveTimeSpreadBreakevens(shortLeg, longLeg, iv, debit);
			if (low.HasValue || high.HasValue)
				return (low, high, BreakEvenSource.BlackScholes);
		}

		return (shortLeg.Strike - debit, shortLeg.Strike + debit, BreakEvenSource.Heuristic);
	}

	/// <summary>Walks a price grid from 0.3× to 2.0× the short strike, evaluating
	/// <c>longBS − shortIntrinsic − debit</c> at the short expiry. Linearly interpolates the first
	/// two zero crossings (low, then high). For diagonals one side may extend unbounded; in that
	/// case only the bounded crossing is returned.</summary>
	private static (decimal? Low, decimal? High) SolveTimeSpreadBreakevens(PositionLeg shortLeg, PositionLeg longLeg, decimal longIv, decimal debit)
	{
		var residualDays = Math.Max(1, (longLeg.Expiry!.Value.Date - shortLeg.Expiry!.Value.Date).Days);
		var t = residualDays / 365.0;
		var r = OptionMath.RiskFreeRate;
		var longCp = longLeg.CallPut!;
		var shortCp = shortLeg.CallPut!;

		decimal Pnl(decimal s)
		{
			var longBs = OptionMath.BlackScholes(s, longLeg.Strike, t, r, longIv, longCp);
			var shortIntrinsic = OptionMath.Intrinsic(s, shortLeg.Strike, shortCp);
			return longBs - shortIntrinsic - debit;
		}

		var refStrike = shortLeg.Strike;
		var lower = refStrike * 0.30m;
		var upper = refStrike * 2.00m;
		const int steps = 400;
		var step = (upper - lower) / steps;

		decimal? lowBe = null;
		decimal? highBe = null;

		var prevX = lower;
		var prev = Pnl(prevX);
		for (var i = 1; i <= steps; i++)
		{
			var x = lower + i * step;
			var curr = Pnl(x);
			if (Math.Sign(prev) != Math.Sign(curr) && prev != curr)
			{
				var crossing = prevX - prev * (x - prevX) / (curr - prev);
				var rounded = Math.Round(crossing, 2);
				if (lowBe == null) lowBe = rounded;
				else if (highBe == null) { highBe = rounded; break; }
			}
			prevX = x;
			prev = curr;
		}

		if (lowBe.HasValue && highBe.HasValue && lowBe.Value > highBe.Value)
			(lowBe, highBe) = (highBe, lowBe);

		return (lowBe, highBe);
	}
}

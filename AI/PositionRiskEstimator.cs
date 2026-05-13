namespace WebullAnalytics.AI;

/// <summary>
/// Derives the worst-case-loss-per-share for a position from its leg geometry. Used by management
/// rules (StopLossRule) to fire at the same threshold the opener's candidate scorer assumed when
/// it ranked the trade, so realized P&amp;L tracks the EV the scorer ranked.
///
/// For debit-paying structures (calendars, diagonals, long verticals, double calendars/diagonals)
/// max loss = the debit paid — you can't lose more than what you put in. For credit-receiving
/// structures (iron butterflies, iron condors, credit verticals) max loss = max wing width minus
/// the credit collected. "Wing width" is computed per option type (calls / puts) as the spread
/// between same-side strikes; the worse side governs.
/// </summary>
internal static class PositionRiskEstimator
{
	public static decimal? MaxLossPerShare(decimal initialNetDebit, IReadOnlyList<PositionLeg> legs)
	{
		if (legs.Count == 0) return null;

		if (initialNetDebit > 0m) return initialNetDebit;

		var credit = -initialNetDebit;
		var wingWidth = MaxWingWidth(legs);
		if (!wingWidth.HasValue || wingWidth.Value <= 0m) return null;
		return Math.Max(0m, wingWidth.Value - credit);
	}

	public static decimal? MaxLossPerShare(OpenPosition position) =>
		MaxLossPerShare(position.InitialNetDebit, position.Legs);

	private static decimal? MaxWingWidth(IReadOnlyList<PositionLeg> legs)
	{
		decimal? best = null;
		foreach (var callPut in new[] { "C", "P" })
		{
			decimal min = decimal.MaxValue, max = decimal.MinValue;
			var count = 0;
			foreach (var leg in legs)
			{
				if (leg.CallPut != callPut) continue;
				if (leg.Strike < min) min = leg.Strike;
				if (leg.Strike > max) max = leg.Strike;
				count++;
			}
			if (count < 2) continue;
			var width = max - min;
			if (!best.HasValue || width > best.Value) best = width;
		}
		return best;
	}
}

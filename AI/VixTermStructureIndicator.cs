namespace WebullAnalytics.AI;

/// <summary>
/// Computes a directional/regime score from the front of the VIX volatility term structure
/// (9-day VIX9D vs 30-day VIX).
///
/// Mechanics: <c>score = clamp((VIX / VIX9D − 1) × scale, −1, +1)</c>.
/// <list type="bullet">
///   <item><description><b>Contango</b> (VIX > VIX9D, the normal regime) → positive score.
///     Near-term realized vol expectations sit below the 30-day expectation; markets are calm.
///     Modest positive drift edge.</description></item>
///   <item><description><b>Backwardation</b> (VIX9D > VIX, the stress regime) → negative score.
///     Near-term vol > 30-day vol = "something bad is happening now." Historically associated
///     with continued downside and mean-reversion edge against rallies.</description></item>
/// </list>
///
/// <c>scale</c> controls saturation: at scale=10, a ~10% term-structure dislocation pegs the
/// score at ±1. That's roughly one standard deviation of the historical VIX9D/VIX ratio, so
/// "extreme" backwardation/contango maps to the indicator clamp.
/// </summary>
internal static class VixTermStructureIndicator
{
	/// <summary>Returns null when either input is missing or non-positive. Otherwise returns a
	/// directional score in [-1, +1]: positive = contango (calm) → bullish tilt; negative =
	/// backwardation (stress) → bearish tilt.</summary>
	public static decimal? Compute(decimal? vixSpot, decimal? vix9d, decimal scale = 10m)
	{
		if (!vixSpot.HasValue || !vix9d.HasValue) return null;
		if (vixSpot.Value <= 0m || vix9d.Value <= 0m) return null;
		var ratio = vixSpot.Value / vix9d.Value - 1m;
		return Math.Clamp(ratio * scale, -1m, 1m);
	}
}

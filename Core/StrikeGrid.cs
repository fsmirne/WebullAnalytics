namespace WebullAnalytics;

/// <summary>Strikes on a fixed-increment grid.</summary>
internal static class StrikeGrid
{
	/// <summary>The two strikes on the <paramref name="step"/> grid bracketing <paramref name="spot"/>, below first; one strike when spot sits on the grid.</summary>
	internal static IEnumerable<decimal> Bracket(decimal spot, decimal step)
	{
		var below = Math.Floor(spot / step) * step;
		var above = Math.Ceiling(spot / step) * step;
		yield return below;
		if (above != below) yield return above;
	}
}

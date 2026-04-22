namespace WebullAnalytics.AI;

internal sealed record TechnicalBias(
	decimal Score,
	decimal SmaScore,
	decimal RsiScore,
	decimal MomentumScore)
{
	/// <summary>Returns true when this signal conflicts with the position's directional risk.
	/// Calls are adverse when bullish (price likely to breach short call). Puts are adverse when bearish.</summary>
	public bool IsAdverse(string callPut, decimal bullishBlockThreshold, decimal bearishBlockThreshold) =>
		callPut == "C" ? Score >= bullishBlockThreshold : Score <= bearishBlockThreshold;
}

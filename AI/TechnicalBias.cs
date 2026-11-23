namespace WebullAnalytics.AI;

internal sealed record TechnicalBias(
	decimal Score,
	decimal SmaScore,
	decimal RsiScore,
	decimal MomentumScore,
	decimal Sma200Score = 0m);

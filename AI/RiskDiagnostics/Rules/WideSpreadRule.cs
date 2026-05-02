using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the worst leg's bid/ask spread exceeds 25% of mid. Wide spreads on any single leg
/// gate the whole structure's exit cost — even a tight short can't compensate for a long whose mid is
/// halfway between an unrealistic bid and a defensive ask.</summary>
internal sealed class WideSpreadRule : IRiskRule
{
	public string Id => "wide_spread";
	private const decimal Threshold = 0.25m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.WorstLegBidAskSpreadPct is not decimal s) return null;
		if (s <= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["worst_spread_pct"] = s,
			["threshold"] = Threshold,
		};
		var pct = (s * 100m).ToString("F0", CultureInfo.InvariantCulture);
		var thresholdPct = (Threshold * 100m).ToString("F0", CultureInfo.InvariantCulture);
		var message = $"Worst leg has a {pct}% bid/ask spread (>{thresholdPct}%); exit cost is dominated by liquidity friction, not fair value.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

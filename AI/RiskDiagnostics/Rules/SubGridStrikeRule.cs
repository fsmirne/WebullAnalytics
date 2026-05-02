using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when a leg's open interest is below 25% of the maximum OI among same-expiry strikes
/// within ±10% of spot. Catches "sub-grid" strikes — chain slots that exist (e.g., $0.50 increments
/// alongside the dominant $1.00 grid) but where most volume cluster on the round-number strikes.
/// Sub-grid strikes are systematically harder to exit at fair value regardless of their absolute OI.</summary>
internal sealed class SubGridStrikeRule : IRiskRule
{
	public string Id => "sub_grid_strike";
	private const decimal Threshold = 0.25m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.MinRelativeOpenInterest is not decimal rel) return null;
		if (rel >= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["min_relative_oi"] = rel,
			["threshold"] = Threshold,
		};
		var pct = (rel * 100m).ToString("F0", CultureInfo.InvariantCulture);
		var threshPct = (Threshold * 100m).ToString("F0", CultureInfo.InvariantCulture);
		var message = $"Worst leg's OI is {pct}% of the max OI among same-expiry near-spot strikes (<{threshPct}%); this strike is a sub-grid slot the chain has lit up but volume is clustering on round-number neighbors.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

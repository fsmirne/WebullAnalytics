using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when a leg's effective liquidity (<c>max(OI, intraday volume)</c>) is below 25% of
/// the maximum among same-expiry strikes within ±10% of spot. Catches "sub-grid" strikes — chain
/// slots that exist (e.g., $0.50 increments alongside the dominant $1.00 grid) but where most
/// activity clusters on the round-number strikes. Volume is folded into the metric so a recently-
/// active sub-grid strike isn't punished as harshly as a truly dead one.</summary>
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
		var message = $"Worst leg's OI/volume is {pct}% of the max among same-expiry near-spot strikes (<{threshPct}%); a sub-grid slot the chain lit up but activity is clustering on round-number neighbors.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when net vega is materially negative — the position loses on IV expansion.</summary>
internal sealed class VegaAdverseRule : IRiskRule
{
	public string Id => "vega_adverse";
	private const decimal Threshold = -5m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.NetVega >= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["net_vega"] = f.NetVega,
			["threshold"] = Threshold,
		};
		var message = $"Net vega ${f.NetVega.ToString("F2", CultureInfo.InvariantCulture)}/contract per IV point: position loses on IV expansion.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

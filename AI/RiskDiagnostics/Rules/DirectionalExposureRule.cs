using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the position carries material directional exposure — abs(net delta) exceeds the
/// neutral threshold. AI consumers correlate this with DirectionalBias to judge whether the structure
/// matches user intent.</summary>
internal sealed class DirectionalExposureRule : IRiskRule
{
	public string Id => "directional_exposure";
	private const decimal Threshold = 0.25m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (Math.Abs(f.NetDelta) <= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["net_delta"] = f.NetDelta,
			["threshold"] = Threshold,
		};
		var dollarMove = Math.Abs(f.NetDelta) * 100m;
		var message = $"Net delta is {f.NetDelta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} ({f.DirectionalBias}); position moves ~${dollarMove.ToString("F0", CultureInfo.InvariantCulture)}/contract per $1 underlying move.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

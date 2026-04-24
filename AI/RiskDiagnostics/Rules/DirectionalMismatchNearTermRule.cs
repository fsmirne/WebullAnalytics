using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when DirectionalBias contradicts the recent 5-day price move beyond the threshold.
/// Skips silently when trend is unavailable or bias is neutral.</summary>
internal sealed class DirectionalMismatchNearTermRule : IRiskRule
{
	public string Id => "directional_mismatch_near_term";
	private const decimal Threshold = 3.0m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.Trend is not TrendSnapshot t) return null;
		if (f.DirectionalBias == "neutral") return null;

		var move = t.ChangePct5Day;
		var fires = (f.DirectionalBias == "bullish" && move < -Threshold)
				 || (f.DirectionalBias == "bearish" && move > Threshold);
		if (!fires) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["change_5day"] = move,
			["threshold"] = Threshold,
		};
		var biasLabel = char.ToUpperInvariant(f.DirectionalBias[0]) + f.DirectionalBias.Substring(1);
		var message = $"{biasLabel} structure against recent {move.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}% 5-day move; delta exposure runs against the trend.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

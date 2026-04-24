using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the underlying's 20-day ATR-as-percent-of-spot is elevated — the position is
/// exposed to larger-than-usual underlying moves.</summary>
internal sealed class HighRealizedVolRule : IRiskRule
{
	public string Id => "high_realized_vol";
	private const decimal Threshold = 4.0m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.Trend is not TrendSnapshot t) return null;
		if (t.Spot20DayAtrPct <= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["atr_pct"] = t.Spot20DayAtrPct,
			["threshold"] = Threshold,
		};
		var message = $"Underlying has realized {t.Spot20DayAtrPct.ToString("F1", CultureInfo.InvariantCulture)}% ATR over 20 days; position is exposed to larger-than-usual moves.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

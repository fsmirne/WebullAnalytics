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
		if (t.Atr14Pct <= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["atr_pct"] = t.Atr14Pct,
			["threshold"] = Threshold,
		};
		var message = $"Underlying has realized {t.Atr14Pct.ToString("F1", CultureInfo.InvariantCulture)}% ATR(14); position is exposed to larger-than-usual moves.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

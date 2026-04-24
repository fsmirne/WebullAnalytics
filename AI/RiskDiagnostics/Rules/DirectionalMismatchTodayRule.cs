using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when net delta is material and runs opposite to today's intraday move. Skips silently
/// when trend is unavailable or intraday change is null (outside market hours).</summary>
internal sealed class DirectionalMismatchTodayRule : IRiskRule
{
	public string Id => "directional_mismatch_today";
	private const decimal DeltaThreshold = 0.25m;
	private const decimal IntradayThreshold = 1.0m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.Trend is not TrendSnapshot t) return null;
		if (t.ChangePctIntraday is not decimal intraday) return null;
		if (Math.Abs(f.NetDelta) <= DeltaThreshold) return null;
		if (Math.Abs(intraday) <= IntradayThreshold) return null;
		if (Math.Sign(f.NetDelta) == Math.Sign(intraday)) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["net_delta"] = f.NetDelta,
			["change_intraday"] = intraday,
			["threshold"] = IntradayThreshold,
		};
		var deltaLabel = f.NetDelta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
		var intraLabel = intraday.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
		var message = $"Net delta is {deltaLabel} but underlying is {intraLabel}% intraday; entered against today's direction.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

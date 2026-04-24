using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires on debit structures where the long premium paid is more than 3× the short premium
/// received — the short leg provides little cushion against adverse moves on the long.</summary>
internal sealed class PremiumRatioImbalancedRule : IRiskRule
{
	public string Id => "premium_ratio_imbalanced";
	private const decimal Threshold = 3.0m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.NetCashPerShare >= 0m) return null;
		if (f.PremiumRatio is not decimal ratio) return null;
		if (ratio <= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["long_paid"] = f.LongPremiumPaid,
			["short_received"] = f.ShortPremiumReceived,
			["ratio"] = ratio,
			["threshold"] = Threshold,
			["net_cash"] = f.NetCashPerShare,
		};
		var message = $"Paid ${f.LongPremiumPaid.ToString("F2", CultureInfo.InvariantCulture)} in long premium for ${f.ShortPremiumReceived.ToString("F2", CultureInfo.InvariantCulture)} of short-leg offset (ratio {ratio.ToString("F1", CultureInfo.InvariantCulture)}×); short provides limited cushion.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

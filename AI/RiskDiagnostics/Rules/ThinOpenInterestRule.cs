using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the leg with the smallest "effective liquidity" — <c>max(OI, intraday volume)</c>
/// — falls below 50. Using max(OI, volume) means an actively-traded contract isn't penalized by low
/// standing OI alone: today's volume signals real market-maker engagement even when the book is thin.
/// Thin liquidity raises spread cost, assignment risk on early exits, and slippage on multi-contract
/// fills.</summary>
internal sealed class ThinOpenInterestRule : IRiskRule
{
	public string Id => "thin_open_interest";
	private const long Threshold = 50;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.MinOpenInterest is not long liq) return null;
		if (liq >= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["min_open_interest"] = liq,
			["threshold"] = Threshold,
		};
		var message = $"Worst leg has only {liq.ToString(CultureInfo.InvariantCulture)} max(OI, volume) (<{Threshold}); thin liquidity raises spread cost, assignment risk, and slippage.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the leg with the smallest OI has fewer than 50 contracts open. Thin OI signals
/// poor market-maker engagement: quotes are wide, fills walk the book, and exiting a multi-contract
/// position can move the price against you.</summary>
internal sealed class ThinOpenInterestRule : IRiskRule
{
	public string Id => "thin_open_interest";
	private const long Threshold = 50;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.MinOpenInterest is not long oi) return null;
		if (oi >= Threshold) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["min_open_interest"] = oi,
			["threshold"] = Threshold,
		};
		var message = $"Worst leg has {oi.ToString(CultureInfo.InvariantCulture)} open interest (<{Threshold}); thin liquidity raises both spread cost and assignment risk on early exits.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

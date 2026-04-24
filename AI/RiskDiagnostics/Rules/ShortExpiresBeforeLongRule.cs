using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the earliest short-leg DTE is strictly less than the latest long-leg DTE.
/// Informational: after the short expires, the user holds a naked long leg with residual delta
/// captured in NetDeltaPostShort.</summary>
internal sealed class ShortExpiresBeforeLongRule : IRiskRule
{
	public string Id => "short_expires_before_long";

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (!f.HasShortLeg || !f.HasLongLeg) return null;
		if (f.ShortLegDteMin >= f.LongLegDteMax) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["short_dte"] = f.ShortLegDteMin,
			["long_dte"] = f.LongLegDteMax,
			["dte_gap"] = f.DteGapDays,
			["net_delta_post_short"] = f.NetDeltaPostShort,
		};
		var postSign = f.NetDeltaPostShort.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
		var message = $"Short expires in {f.ShortLegDteMin} days, long in {f.LongLegDteMax} days; after short expiry you hold a naked long leg at {postSign} delta.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

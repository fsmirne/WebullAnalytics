using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the earliest short-leg DTE is strictly less than the latest long-leg DTE.
/// Informational: after the short expires, the user is left with the longer-dated legs, carrying the
/// residual delta captured in NetDeltaPostShort. Whether that residual is a NAKED long or a DEFINED-RISK
/// spread depends on whether a short leg also survives (ShortLegSurvivesPostShort) — e.g. a single-leg
/// LongDiagonal leaves a naked long, but a DiagonalVertical leaves its far vertical (defined risk).</summary>
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
			["short_survives_post_short"] = f.ShortLegSurvivesPostShort ? 1m : 0m,
		};
		var postSign = f.NetDeltaPostShort.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
		var residual = f.ShortLegSurvivesPostShort
			? $"a defined-risk spread at {postSign} net delta"
			: $"a naked long leg at {postSign} delta";
		var message = $"Short expires in {f.ShortLegDteMin} days, long in {f.LongLegDteMax} days; after short expiry you hold {residual}.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

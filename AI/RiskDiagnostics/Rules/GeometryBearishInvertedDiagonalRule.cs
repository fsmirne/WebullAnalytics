using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires for a bearish-biased inverted diagonal. Informational: notes that the structure gains
/// on a drop and loses on a rally. When trend is available, adds `trend_aligned` (1 = bias agrees with
/// 5d move, 0 = misaligned).</summary>
internal sealed class GeometryBearishInvertedDiagonalRule : IRiskRule
{
	public string Id => "geometry_bearish_inverted_diagonal";

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.StructureLabel != "inverted_diagonal") return null;
		if (f.DirectionalBias != "bearish") return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["short_strike"] = f.ShortLegStrike,
			["long_strike"] = f.LongLegStrike,
			["spot"] = f.Spot,
			["net_delta"] = f.NetDelta,
		};
		if (f.Trend is TrendSnapshot t)
			inputs["trend_aligned"] = t.ChangePct5Day < 0m ? 1m : 0m;

		var message = $"Inverted diagonal: long strike ${f.LongLegStrike.ToString("F2", CultureInfo.InvariantCulture)} > short strike ${f.ShortLegStrike.ToString("F2", CultureInfo.InvariantCulture)} gives bearish delta bias. Gains on drop, loses on rally.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

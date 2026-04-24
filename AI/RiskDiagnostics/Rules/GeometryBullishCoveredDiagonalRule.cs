using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires for a bullish-biased covered diagonal (long strike &lt; short strike for calls; opposite for puts).
/// Informational: notes that the structure gains on a rally and loses on a drop. When trend is available,
/// adds `trend_aligned` to inputs (1 = bias agrees with 5d move, 0 = misaligned).</summary>
internal sealed class GeometryBullishCoveredDiagonalRule : IRiskRule
{
	public string Id => "geometry_bullish_covered_diagonal";

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (f.StructureLabel != "covered_diagonal") return null;
		if (f.DirectionalBias != "bullish") return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["short_strike"] = f.ShortLegStrike,
			["long_strike"] = f.LongLegStrike,
			["spot"] = f.Spot,
			["net_delta"] = f.NetDelta,
		};
		if (f.Trend is TrendSnapshot t)
			inputs["trend_aligned"] = t.ChangePct5Day > 0m ? 1m : 0m;

		var message = $"Covered diagonal: long strike ${f.LongLegStrike.ToString("F2", CultureInfo.InvariantCulture)} < short strike ${f.ShortLegStrike.ToString("F2", CultureInfo.InvariantCulture)} gives bullish delta bias. Gains on rally, loses on drop.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

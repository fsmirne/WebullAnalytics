using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when the shortest short-leg DTE is low AND its extrinsic value is thin — the short can't
/// deliver meaningful theta because there's barely any time premium left to decay.</summary>
internal sealed class ShortLegLowExtrinsicRule : IRiskRule
{
	public string Id => "short_leg_low_extrinsic";
	private const int ThresholdDte = 2;
	private const decimal ThresholdExtrinsic = 0.30m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (!f.HasShortLeg) return null;
		if (f.ShortLegDteMin > ThresholdDte) return null;
		if (f.ShortLegExtrinsic >= ThresholdExtrinsic) return null;

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["short_dte"] = f.ShortLegDteMin,
			["short_extrinsic"] = f.ShortLegExtrinsic,
			["threshold_dte"] = ThresholdDte,
			["threshold_extrinsic"] = ThresholdExtrinsic,
		};
		var message = $"Short leg has ${f.ShortLegExtrinsic.ToString("F2", CultureInfo.InvariantCulture)} extrinsic with {f.ShortLegDteMin} DTE; little harvestable theta remains.";
		return new RiskRuleHit(Id, message, inputs);
	}
}

using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Surfaces upcoming earnings and ex-dividend catalysts for the position life. Fires when
/// either falls within the next <see cref="ProximityDays"/> of as-of:
/// <list type="bullet">
///   <item>Earnings within the window — always interesting (IV expansion + gap risk).</item>
///   <item>Ex-dividend before the latest short-call expiry — early-assignment risk.</item>
/// </list>
/// This rule has zero scoring impact (the scorer's veto handles that). It exists so the user sees the
/// catalyst on trades that survived the veto — long-only structures, or runs with the veto disabled.
/// </summary>
internal sealed class EarningsProximityRule : IRiskRule
{
	public string Id => "earnings_proximity";

	/// <summary>Surface the catalyst when within this many days of as-of. Larger than the veto's blackout
	/// because this is informational: traders want awareness, not just rejection.</summary>
	private const int ProximityDays = 14;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (!f.AsOf.HasValue) return null;
		var asOfDate = f.AsOf.Value.Date;

		var earningsHit = f.NextEarningsDate.HasValue
			&& f.NextEarningsDate.Value.Date >= asOfDate
			&& (f.NextEarningsDate.Value.Date - asOfDate).TotalDays <= ProximityDays;

		var exDivHit = f.HasShortCallLeg
			&& f.NextExDividendDate.HasValue
			&& f.NextExDividendDate.Value.Date >= asOfDate
			&& (f.NextExDividendDate.Value.Date - asOfDate).TotalDays <= ProximityDays;

		if (!earningsHit && !exDivHit) return null;

		var parts = new List<string>();
		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal);

		if (earningsHit)
		{
			var earnings = f.NextEarningsDate!.Value.Date;
			var daysOut = (decimal)(earnings - asOfDate).TotalDays;
			inputs["earnings_days_out"] = daysOut;
			var timing = string.IsNullOrEmpty(f.EarningsTime) ? "" : $" ({f.EarningsTime})";
			parts.Add($"earnings on {earnings.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{timing} in {daysOut.ToString("F0", CultureInfo.InvariantCulture)} day(s)");
		}

		if (exDivHit)
		{
			var exDiv = f.NextExDividendDate!.Value.Date;
			var daysOut = (decimal)(exDiv - asOfDate).TotalDays;
			inputs["ex_div_days_out"] = daysOut;
			parts.Add($"ex-div on {exDiv.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} in {daysOut.ToString("F0", CultureInfo.InvariantCulture)} day(s); short-call leg present (early-assignment risk on ITM strikes)");
		}

		var message = "Scheduled catalyst near position life: " + string.Join("; ", parts) + ".";
		return new RiskRuleHit(Id, message, inputs);
	}
}

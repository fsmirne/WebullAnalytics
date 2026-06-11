using System.Globalization;
using WebullAnalytics.AI;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Surfaces upcoming earnings and ex-dividend catalysts for the position life:
/// <list type="bullet">
///   <item>Earnings within the next <see cref="ProximityDays"/> — always interesting (IV expansion + gap risk).</item>
///   <item>A SHORT CALL crossing the next ex-dividend — early-assignment risk (HIGH when the short's
///     time value is below the dividend, the classic dividend-capture exercise trigger).</item>
///   <item>A LONG leg crossing the next ex-dividend — informational only (no assignment risk, but the leg
///     prices on the ex-dividend forward, which is why a calendar's theoretical can exceed the long mid).</item>
/// </list>
/// Ex-dividend crossings are keyed on actual leg expiries (not 14-day proximity) and are suppressed for
/// cash-settled European index roots, which carry no early-assignment risk. Zero scoring impact (the
/// scorer's veto handles gating); this exists so the user sees the catalyst on trades that survived it.
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

		// Ex-dividend "crossing": a leg whose expiry is on/after the upcoming ex-date trades through the
		// dividend. Unlike the proximity check this is keyed on the actual leg life. Cash-settled index
		// options (SPX/XSP/…) are European → no early assignment and the dividend is already in the
		// forward, so we suppress the note entirely for those roots.
		var exDivDate = f.NextExDividendDate;
		var exDivIsFuture = exDivDate.HasValue && exDivDate.Value.Date >= asOfDate;
		var isAmerican = !OptionSettlement.IsCashSettledIndex(f.Ticker);
		var exDivDaysOut = exDivIsFuture ? (exDivDate!.Value.Date - asOfDate).TotalDays : double.MaxValue;

		// Short call crossing → early-assignment risk (rational dividend-capture exercise on ITM strikes).
		var shortCallCrosses = isAmerican && exDivIsFuture && f.HasShortCallLeg && f.HasShortLeg && exDivDaysOut <= f.ShortLegDteMin;
		// Any long leg crossing → informational (no assignment risk on a long, but the leg prices on the
		// ex-dividend forward — which is also why a calendar's theoretical can sit above the long mid).
		var longCrosses = isAmerican && exDivIsFuture && f.HasLongLeg && exDivDaysOut <= f.LongLegDteMax;

		if (!earningsHit && !shortCallCrosses && !longCrosses) return null;

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

		if (shortCallCrosses || longCrosses)
		{
			var exDiv = exDivDate!.Value.Date;
			inputs["ex_div_days_out"] = (decimal)exDivDaysOut;
			var amount = f.NextDividendAmount.HasValue ? $" (${f.NextDividendAmount.Value.ToString("0.##", CultureInfo.InvariantCulture)}/sh)" : "";
			var on = $"ex-div on {exDiv.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{amount} in {((decimal)exDivDaysOut).ToString("F0", CultureInfo.InvariantCulture)} day(s)";
			if (shortCallCrosses)
			{
				// Highest risk when the short call's time value is below the dividend — the holder profits
				// by exercising early to capture it.
				var extrinsicLtDiv = f.NextDividendAmount.HasValue && f.ShortLegExtrinsic < f.NextDividendAmount.Value;
				var sev = extrinsicLtDiv ? " — short-call extrinsic below the dividend: HIGH early-assignment risk" : " — short call crosses it (early-assignment risk on ITM strikes)";
				parts.Add(on + sev);
			}
			else
			{
				parts.Add(on + " — long leg trades through it (priced on the ex-dividend forward; the long itself can't be assigned — a short leg expiring ITM still assigns at expiry)");
			}
		}

		var message = "Scheduled catalyst near position life: " + string.Join("; ", parts) + ".";
		return new RiskRuleHit(Id, message, inputs);
	}
}

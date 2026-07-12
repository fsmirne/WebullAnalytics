namespace WebullAnalytics.AI.Events;

/// <summary>Hard-veto rules for scheduled catalysts. Pure functions over (skeleton, asOf, events,
/// config). The opener calls <see cref="ShouldVeto"/> right before the liquidity gate; a true return
/// short-circuits scoring and the candidate is dropped without surfacing a proposal. The exact reason
/// is returned via <paramref name="reason"/> for debug logging.
///
/// Design: vetoes only fire on structures whose short legs are exposed to the event. Long-only
/// structures (LongCall/LongPut) and any structure with no short legs are never vetoed — they can
/// benefit from earnings vol expansion or gap moves, and the trader may explicitly want the catalyst.
/// Diagnostic surfacing of the event for non-vetoed trades lives in
/// <see cref="WebullAnalytics.AI.RiskDiagnostics.Rules.EarningsProximityRule"/>.</summary>
internal static class EventVeto
{
	/// <param name="vetoNow">The actual current date/time for "is this event still upcoming?" checks.
	/// Separate from <paramref name="asOf"/> because asOf is the quote anchor (ObservationInstant — the
	/// last session's close) while vetoNow is the real wall-clock date. Off-hours they differ: asOf=Friday,
	/// vetoNow=Sunday. Earnings on Friday are already past on Sunday so should not veto. When null, asOf
	/// is used for both purposes (backtest: asOf==ctx.Now anyway; tests: controlled fixed date).</param>
	public static bool ShouldVeto(CandidateSkeleton skel, DateTime asOf, TickerEvents? events, OpenerEventsConfig cfg, out string? reason, DateTime? vetoNow = null)
	{
		reason = null;
		if (!cfg.Enabled || events == null) return false;
		var vetoDate = (vetoNow ?? asOf).Date;

		if (events.NextEarningsDate.HasValue && HasShortLeg(skel.StructureKind))
		{
			var earnings = events.NextEarningsDate.Value.Date;
			var blackoutEnd = skel.TargetExpiry.Date.AddDays(Math.Max(0, cfg.EarningsBlackoutDaysAfter));
			if (earnings >= vetoDate && earnings <= blackoutEnd)
			{
				reason = $"earnings on {earnings:yyyy-MM-dd} ≤ target expiry {skel.TargetExpiry:yyyy-MM-dd}+{cfg.EarningsBlackoutDaysAfter}d";
				return true;
			}
		}

		if (cfg.RejectShortCallsThroughExDiv && events.NextExDividendDate.HasValue)
		{
			var exDiv = events.NextExDividendDate.Value.Date;
			if (exDiv >= vetoDate)
			{
				foreach (var leg in skel.Legs)
				{
					if (leg.Action != "sell") continue;
					var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
					if (parsed == null || parsed.CallPut != "C") continue;
					if (exDiv <= parsed.ExpiryDate.Date)
					{
						reason = $"ex-div {exDiv:yyyy-MM-dd} ≤ short-call expiry {parsed.ExpiryDate:yyyy-MM-dd} (early-assignment risk)";
						return true;
					}
				}
			}
		}

		return false;
	}

	/// <summary>True when the structure type contains at least one short leg whose payoff is exposed
	/// to a same-day gap on the catalyst. Long-only structures return false.</summary>
	internal static bool HasShortLeg(OpenStructureKind kind) => kind switch
	{
		OpenStructureKind.LongCall or OpenStructureKind.LongPut => false,
		_ => true,
	};
}

using System.Globalization;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Builds the copy-pasteable command lines that reproduce an open proposal: one or two
/// <c>wa trade place</c> lines (split structures — DiagonalVertical / CalendarVertical /
/// DoubleCalendar / DoubleDiagonal — emit two, since Webull rejects a single 4-leg cross-expiry
/// ticket) followed by a single <c>wa analyze trade</c> line covering the whole structure.
///
/// <para>Shared by the live proposal panel (<see cref="OpenProposalSink"/>) and the <c>--log-level debug</c>
/// best-candidate trace in the opener, so the two render identically and can never drift. The caller
/// owns presentation (Spectre markup + ↪ lead-in for proposals; plain arrowed stderr lines for debug).</para>
/// </summary>
internal static class ReproductionCommands
{
	/// <param name="explicitAnalyzePrices">When true, the analyze legs are rendered at their concrete
	/// per-share prices (the debug "exactly what the scorer saw, replayable offline" path) instead of the
	/// MID/BID/ASK placeholder keywords the live panel uses (which re-fetch the current market on run).</param>
	public static IReadOnlyList<string> Build(OpenProposal p, string suggestPricing, bool explicitAnalyzePrices = false)
	{
		var lines = new List<string>();

		var groups = StructureOrderSplit.Split(p.StructureKind, p.Legs);
		if (groups.Count > 1)
		{
			foreach (var g in groups)
			{
				var sideTrades = string.Join(",", g.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
				var sideLimit = (SuggestionPricing.TryGetLimitPerShare(g.Legs, suggestPricing) ?? 0m).ToString("F2", CultureInfo.InvariantCulture);
				lines.Add($"wa trade place --trade \"{sideTrades}\" --limit {sideLimit}  # {g.Label}");
			}
		}
		else
		{
			var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
			var limit = (SuggestionPricing.TryGetLimitPerShare(p.Legs, suggestPricing) ?? Math.Abs(p.DebitOrCreditPerContract / 100m)).ToString("F2", CultureInfo.InvariantCulture);
			lines.Add($"wa trade place --trade \"{tradesArg}\" --limit {limit}");
		}

		var analyzeArg = string.Join(",", p.Legs.Select(l =>
		{
			var price = explicitAnalyzePrices && l.PricePerShare.HasValue
				? l.PricePerShare.Value.ToString("F2", CultureInfo.InvariantCulture)
				: SuggestionPricing.AnalyzeKeywordFor(l, suggestPricing);
			return $"{l.Action}:{l.Symbol}:{l.Qty}@{price}";
		}));
		lines.Add($"wa analyze trade \"{analyzeArg}\"");

		return lines;
	}
}

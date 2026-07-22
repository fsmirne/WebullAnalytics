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
	/// <summary>Builds the same two command lines for legs recovered outside the opener (e.g. `wa clipboard
	/// order`'s OCR path): the net limit is the caller's own (the ticket's), analyze legs use the MID keyword,
	/// and the place line can be suppressed (<paramref name="emitPlace"/> false) when the caller's validation
	/// failed — an analyze line on suspect legs is harmless, an order line is not. Renders through the same
	/// private line-builders as the proposal overload, so the two can never drift.</summary>
	public static IReadOnlyList<string> Build(IReadOnlyList<ProposalLeg> legs, decimal? netLimitPerShare, string tif, bool emitPlace = true)
	{
		var lines = new List<string>();
		if (emitPlace && netLimitPerShare.HasValue)
			lines.Add(PlaceLine(legs, netLimitPerShare.Value, string.Equals(tif, "gtc", StringComparison.OrdinalIgnoreCase) ? " --tif gtc" : ""));
		lines.Add(AnalyzeLine(legs, _ => "MID"));
		return lines;
	}

	private static string PlaceLine(IEnumerable<ProposalLeg> legs, decimal limitPerShare, string suffix = "") =>
		$"wa trade place \"{TradesArg(legs)}\" --limit {limitPerShare.ToString("F2", CultureInfo.InvariantCulture)}{suffix}";

	private static string TradesArg(IEnumerable<ProposalLeg> legs) => string.Join(",", legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));

	private static string AnalyzeLine(IEnumerable<ProposalLeg> legs, Func<ProposalLeg, string> priceOf) =>
		$"wa analyze trade \"{string.Join(",", legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{priceOf(l)}"))}\"";

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
				lines.Add(PlaceLine(g.Legs, SuggestionPricing.TryGetLimitPerShare(g.Legs, suggestPricing) ?? 0m, $"  # {g.Label}"));
		}
		else
		{
			lines.Add(PlaceLine(p.Legs, SuggestionPricing.TryGetLimitPerShare(p.Legs, suggestPricing) ?? Math.Abs(p.DebitOrCreditPerContract / 100m)));
		}

		lines.Add(AnalyzeLine(p.Legs, l => explicitAnalyzePrices && l.PricePerShare.HasValue
			? l.PricePerShare.Value.ToString("F2", CultureInfo.InvariantCulture)
			: SuggestionPricing.AnalyzeKeywordFor(l, suggestPricing)));

		return lines;
	}
}

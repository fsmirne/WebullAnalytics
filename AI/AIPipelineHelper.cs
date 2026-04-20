using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>
/// Shared quote-fetch pipeline for AI subcommands. Two-phase fetch: first grabs quotes for current
/// position legs (to learn spot), then enumerates hypothetical-scenario symbols and fetches any
/// missing quotes so the OpportunisticRollRule can score bracket-strike and next-weekly variants.
/// </summary>
internal static class AIPipelineHelper
{
	public static async Task<QuoteSnapshot> FetchQuotesWithHypotheticals(
		IReadOnlyDictionary<string, OpenPosition> openPositions,
		IReadOnlySet<string> tickerSet,
		DateTime asOf,
		IQuoteSource quotes,
		AIConfig config,
		CancellationToken cancellation)
	{
		// Phase 1: current-leg symbols only.
		var phase1Symbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var phase1 = await quotes.GetQuotesAsync(asOf, phase1Symbols, tickerSet, cancellation);

		// Phase 2: enumerate hypotheticals using each position's spot.
		var phase2Symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var pos in openPositions.Values)
		{
			if (!phase1.Underlyings.TryGetValue(pos.Ticker, out var spot) || spot <= 0m) continue;

			// Convert to LegInfo and classify.
			var legInfos = new List<ScenarioEngine.LegInfo>();
			var bail = false;
			foreach (var leg in pos.Legs)
			{
				if (leg.CallPut == null || !leg.Expiry.HasValue) { bail = true; break; }
				var parsed = new OptionParsed(pos.Ticker, leg.Expiry.Value, leg.CallPut, leg.Strike);
				legInfos.Add(new ScenarioEngine.LegInfo(leg.Symbol, IsLong: leg.Side == Side.Buy, Qty: leg.Qty, parsed));
			}
			if (bail) continue;

			var kind = ScenarioEngine.Classify(legInfos);
			var strikeStep = config.Rules.OpportunisticRoll.StrikeStep;
			foreach (var sym in ScenarioEngine.EnumerateHypotheticalSymbols(legInfos, kind, spot, strikeStep, asOf))
			{
				if (!phase1.Options.ContainsKey(sym)) phase2Symbols.Add(sym);
			}
		}

		if (phase2Symbols.Count == 0) return phase1;

		var phase2 = await quotes.GetQuotesAsync(asOf, phase2Symbols, tickerSet, cancellation);

		// Merge phase2 option quotes into phase1. Underlyings already correct from phase1.
		var merged = new Dictionary<string, OptionContractQuote>(phase1.Options, StringComparer.OrdinalIgnoreCase);
		foreach (var (k, v) in phase2.Options) merged[k] = v;
		return new QuoteSnapshot(merged, phase1.Underlyings);
	}
}

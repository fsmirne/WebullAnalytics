using WebullAnalytics.AI.Replay;
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
		CancellationToken cancellation,
		QuoteOverrides overrides = default)
	{
		// Phase 1: current-leg symbols only.
		var phase1Symbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var phase1 = await quotes.GetQuotesAsync(asOf, phase1Symbols, tickerSet, cancellation, overrides);

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
			var strikeStep = config.Indicators.StrikeStep;
			foreach (var sym in ScenarioEngine.EnumerateHypotheticalSymbols(legInfos, kind, spot, strikeStep, asOf))
			{
				if (!phase1.Options.ContainsKey(sym)) phase2Symbols.Add(sym);
			}
		}

		// Parity probes: a cash-index spot can lag the option book (composed-open lag on gap days — see
		// ParitySpotGuard), and the position legs alone rarely carry the same-strike call+put pair that
		// parity needs (verticals are single-right). Add ATM straddle probes at each index position's
		// earliest live expiry so the guard below has a pair to solve from. Live quote clients return the
		// whole chain anyway, so the probes are usually already priced and add nothing to the fetch.
		foreach (var group in openPositions.Values.GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase))
		{
			if (!phase1.Underlyings.TryGetValue(group.Key, out var spot) || spot <= 0m) continue;
			var expiry = group.SelectMany(p => p.Legs).Where(l => l.Expiry.HasValue && l.Expiry.Value.Date >= asOf.Date).Select(l => l.Expiry!.Value.Date).DefaultIfEmpty().Min();
			if (expiry == default) continue;
			foreach (var sym in ParitySpotGuard.ProbeSymbols(group.Key, expiry, spot, config.Indicators.StrikeStep))
				if (!phase1.Options.ContainsKey(sym)) phase2Symbols.Add(sym);
		}

		var options = phase1.Options;
		if (phase2Symbols.Count > 0)
		{
			var phase2 = await quotes.GetQuotesAsync(asOf, phase2Symbols, tickerSet, cancellation, overrides);
			// Merge phase2 option quotes into phase1. Underlyings already correct from phase1.
			var merged = new Dictionary<string, OptionContractQuote>(phase1.Options, StringComparer.OrdinalIgnoreCase);
			foreach (var (k, v) in phase2.Options) merged[k] = v;
			options = merged;
		}

		// Parity spot guard: management rules gate on moneyness (LegInShort's minSpotPctITM, assignment-risk
		// force-close, roll strike selection), so they must see the same spot the option book prices —
		// exactly like the opener's post-bootstrap correction in OpenCandidateEvaluator.
		Dictionary<string, decimal>? correctedUnderlyings = null;
		foreach (var ticker in openPositions.Values.Select(p => p.Ticker).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!phase1.Underlyings.TryGetValue(ticker, out var spot)) continue;
			if (ParitySpotGuard.Correct(ticker, spot, options, asOf) is decimal paritySpot)
			{
				correctedUnderlyings ??= new Dictionary<string, decimal>(phase1.Underlyings, StringComparer.OrdinalIgnoreCase);
				correctedUnderlyings[ticker] = paritySpot;
			}
		}

		if (correctedUnderlyings == null && ReferenceEquals(options, phase1.Options)) return phase1;
		return new QuoteSnapshot(options, correctedUnderlyings ?? phase1.Underlyings);
	}

	/// <summary>Fetches recent daily closes per ticker and computes a composite technical bias.
	/// Returns an empty dict when filter is disabled. Missing tickers (insufficient data) are omitted —
	/// rules treat a missing entry as neutral.</summary>
	public static async Task<IReadOnlyDictionary<string, TechnicalBias>> ComputeTechnicalSignalsAsync(
		IReadOnlySet<string> tickers,
		HistoricalPriceCache priceCache,
		TechnicalFilterConfig filter,
		DateTime asOf,
		CancellationToken cancellation)
	{
		var result = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
		if (!filter.Enabled) return result;
		// The Sma200 component needs ≥ 200 daily closes; bump the lookback when it's enabled. Otherwise
		// preserve the configured lookback (legacy behavior — typically 20 days, matching SMA20).
		var effectiveLookback = filter.Sma200Weight > 0m ? Math.Max(filter.LookbackDays, 200) : filter.LookbackDays;
		foreach (var ticker in tickers)
		{
			var closes = await priceCache.GetRecentClosesAsync(ticker, effectiveLookback, asOf, cancellation);
			var bias = TechnicalIndicators.Compute(closes, filter);
			if (bias != null) result[ticker] = bias;
		}
		return result;
	}
}

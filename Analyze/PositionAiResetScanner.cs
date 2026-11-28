using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Integrates <see cref="OpenCandidateEvaluator"/> (the exact engine behind <c>wa ai scan</c>) and
/// <see cref="AnalyzeRiskCommand.BuildProbe"/> into <c>wa analyze position</c> so:
///   1. "Reset" scenarios come from what <c>wa ai scan</c> would actually open right now (honouring
///      liquidity/OI, regime bias, sentiment, GEX, event vetoes, and managed EV) — with <c>LongCalendar</c>
///      enabled alongside the strategy's own structures so calendars compete fairly against diagonals.
///   2. Hold and Roll scenarios are scored on the exact same opener scorer (POP, break-evens, managed EV),
///      putting Hold, Close, Roll, and Reset on a single apples-to-apples campaign-EV scale.
/// </summary>
internal static class PositionAiResetScanner
{
	private sealed class StaticQuoteSource(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		IReadOnlyDictionary<string, decimal> underlyings) : IQuoteSource
	{
		public Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation, QuoteOverrides overrides = default)
			=> Task.FromResult(new QuoteSnapshot(quotes, underlyings));
	}

	/// <summary>Deep-clones <paramref name="source"/> for an in-memory reset scan, ensuring both the strategy's
	/// configured structures and <c>LongCalendar</c> are enabled under the same DTE, liquidity, and regime rules.</summary>
	internal static AIConfig PrepareResetScanConfig(AIConfig source, string ticker)
	{
		var json = JsonSerializer.Serialize(source);
		var clone = JsonSerializer.Deserialize<AIConfig>(json) ?? new AIConfig();
		clone.Ticker = ticker.ToUpperInvariant();
		clone.Strategy = source.Strategy;
		clone.Opener.Enabled = true;
		clone.Opener.TopNPerTicker = Math.Max(clone.Opener.TopNPerTicker, 5);
		clone.Opener.Indicators = clone.Indicators;

		// Give LongCalendar a fair shot even on diagonal-only strategies (e.g. DC2), inheriting the
		// diagonal's DTE & expiry-type windows when LongCalendar was left disabled/unconfigured.
		var cal = clone.Opener.Structures.LongCalendar;
		var diag = clone.Opener.Structures.LongDiagonal;
		if (!cal.Enabled)
		{
			cal.Enabled = true;
			if (diag.Enabled)
			{
				if (cal.ShortDteMin <= 0) cal.ShortDteMin = diag.ShortDteMin;
				if (cal.ShortDteMax <= 0) cal.ShortDteMax = diag.ShortDteMax;
				if (cal.LongDteMin <= 0) cal.LongDteMin = diag.LongDteMin;
				if (cal.LongDteMax <= 0) cal.LongDteMax = diag.LongDteMax;
			}
		}

		AIConfigLoader.PopulateRealizedEv(clone);
		return clone;
	}

	/// <summary>Enriches <paramref name="scenarios"/> with unified opener-scorer POP/EV (for Hold and Rolls)
	/// and replaces naive hardcoded "Reset to $X calendar" scenarios with real <c>wa ai scan</c> reset proposals.</summary>
	internal static async Task<List<AnalyzePositionCommand.Scenario>> ApplyAsync(
		List<AnalyzePositionCommand.Scenario> scenarios,
		List<AnalyzePositionCommand.PositionSnapshot> positionLegs,
		AnalyzePositionCommand.StructureKind kind,
		AnalyzePositionSettings settings,
		AIConfig aiCfg,
		decimal spot,
		DateTime asOf,
		DateTime quoteAsOf,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		HistoricalPriceCache priceCache,
		IntradayBarCache? intradayCache,
		CancellationToken cancellation)
	{
		if (positionLegs.Count == 0 || !scenarios.Any(s => s.Book != null))
			return scenarios;

		var ticker = positionLegs[0].Parsed.Root;
		var origQty = positionLegs[0].Qty;
		var initialDebitPerShare = positionLegs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var closeNetPerShare = positionLegs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * (MidOf(quotes, l.Symbol) ?? l.CostBasis));
		var closePnLPerContract = (closeNetPerShare - initialDebitPerShare) * 100m;

		// 1. Every scenario that says what it leaves held (its Book) is re-valued on the unified opener scorer — the same one `analyze roll` and
		// `wa ai scan` use — so Hold, Close, Rolls and Adds are ranked on one model: the P&L of marking everything to mid now, plus the scorer's
		// forward EV of each structure still held. Without this the adds kept the lognormal EV while Hold and Rolls had the scorer's, and the
		// gap between the two models' value of the SAME existing lots ranked an add above Hold (2026-11-20: +$2.8k of model mismatch).
		var scanCfg = PrepareResetScanConfig(aiCfg, ticker);
		var perOrderFriction = RealizedExpectancy.FrictionPerOrderContract(scanCfg.Opener.RealizedExpectancy);
		var scoringInputs = await AnalyzeRiskCommand.LoadOpenerScoringInputsAsync(ticker, asOf, includeCurrentBar: !EvaluationDate.IsPast, intradayCache, cancellation);
		var scoreCache = new Dictionary<string, AI.RiskDiagnostics.RiskDiagnosticOpenerScore?>(StringComparer.Ordinal);
		var existingSymbols = positionLegs.Select(l => l.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var updated = new List<AnalyzePositionCommand.Scenario>(scenarios.Count);
		var oppositeSideAdds = new List<(int Index, decimal Return)>();
		foreach (var sc in scenarios)
		{
			// Drop naive hardcoded "Reset to $X calendar" — replaced by real AI scan reset proposals below.
			if (sc.Name.StartsWith("Reset to ", StringComparison.Ordinal))
				continue;
			if (sc.Book == null) { updated.Add(sc); continue; }
			var (revalued, addedReturn) = Revalue(sc, origQty, closePnLPerContract, perOrderFriction, s => ScoreCached(scoreCache, s.Legs, quotes, spot, asOf, quoteAsOf, scoringInputs, settings.IvDefault));
			// The scale-up adds the existing structure again: it is the baseline the opposite-side adds are judged against, not a candidate.
			if (addedReturn is decimal r && sc.Book.Any(b => b.Label == "added" && !b.Legs.All(l => existingSymbols.Contains(l.Symbol))))
				oppositeSideAdds.Add((updated.Count, r));
			updated.Add(revalued);
		}
		FlagBestAdd(updated, oppositeSideAdds);

		// 2. Run OpenCandidateEvaluator (in-memory, zero extra network calls) to find the best AI scan reset(s) — for a two-leg calendar/diagonal,
		// whose close order and margin the reset rows are written for.
		var shortLeg = positionLegs.FirstOrDefault(l => l.Action == LegAction.Sell);
		var longLeg = positionLegs.FirstOrDefault(l => l.Action == LegAction.Buy);
		if (kind is not (AnalyzePositionCommand.StructureKind.Calendar or AnalyzePositionCommand.StructureKind.Diagonal) || positionLegs.Count != 2 || shortLeg == null || longLeg == null)
			return updated;
		var resetScenarios = await BuildAiScanResetScenariosAsync(
			scanCfg, perOrderFriction, ticker, shortLeg, longLeg, MidOf(quotes, shortLeg.Symbol) ?? shortLeg.CostBasis, MidOf(quotes, longLeg.Symbol) ?? longLeg.CostBasis, closeNetPerShare,
			closePnLPerContract * origQty, closeNetPerShare * 100m * origQty, origQty, initialDebitPerShare, settings, spot, asOf, quotes, priceCache, cancellation);

		updated.AddRange(resetScenarios);
		return updated;
	}

	private static async Task<List<AnalyzePositionCommand.Scenario>> BuildAiScanResetScenariosAsync(
		AIConfig scanCfg,
		decimal perOrderFriction,
		string ticker,
		AnalyzePositionCommand.PositionSnapshot shortLeg,
		AnalyzePositionCommand.PositionSnapshot longLeg,
		decimal shortMidNow,
		decimal longMidNow,
		decimal closeNetPerShare,
		decimal closePnLTotal,
		decimal closeProceedsTotal,
		int origQty,
		decimal initialDebitPerShare,
		AnalyzePositionSettings settings,
		decimal spot,
		DateTime asOf,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		HistoricalPriceCache priceCache,
		CancellationToken cancellation)
	{
		var results = new List<AnalyzePositionCommand.Scenario>();
		try
		{
			var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spot };
			var quoteSource = new StaticQuoteSource(quotes, underlyings);

			var freeCashAfterClose = (settings.Cash ?? 0m) + Math.Max(0m, closeProceedsTotal);
			if (freeCashAfterClose <= 0m) freeCashAfterClose = 100_000m;
			var accountValue = freeCashAfterClose;

			var evalCtx = new EvaluationContext(
				Now: asOf,
				OpenPositions: new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase),
				UnderlyingPrices: underlyings,
				Quotes: quotes,
				AccountCash: freeCashAfterClose,
				AccountValue: accountValue,
				TechnicalSignals: new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase));

			var evaluator = new OpenCandidateEvaluator(
				scanCfg, quoteSource, SuggestionPricing.Mid, priceCache,
				backtestMode: EvaluationDate.IsPast, enableChainSnapshot: false, showAllScores: true);

			var proposals = await evaluator.EvaluateAsync(evalCtx, cancellation);
			var usable = proposals
				.Where(p => !p.Informational && p.Legs.Count > 0)
				.OrderByDescending(p => p.FinalScore ?? decimal.MinValue)
				.ToList();

			if (usable.Count == 0) return results;

			// Pick top proposal overall + top proposal of a different structure kind (e.g., best diagonal + best calendar).
			var picks = new List<OpenProposal> { usable[0] };
			var altKind = usable.FirstOrDefault(p => p.StructureKind != usable[0].StructureKind);
			if (altKind != null) picks.Add(altKind);

			var currentMarginPerContract = MarginCalculator.PairPerContract(shortLeg.Parsed, shortMidNow, longLeg.Parsed, spot);
			var currentMarginTotal = currentMarginPerContract * origQty;

			foreach (var prop in picks)
			{
				var propQty = prop.Qty > 0
					? prop.Qty
					: (prop.CapitalAtRiskPerContract > 0m ? Math.Max(1, (int)Math.Floor(freeCashAfterClose / prop.CapitalAtRiskPerContract)) : origQty);

				var openDebitOrCreditTotal = prop.DebitOrCreditPerContract * propQty; // negative = debit paid
				var netCashTotal = closeProceedsTotal + openDebitOrCreditTotal;
				var forwardEvPerContract = prop.RealizedExpectedValuePerContract ?? prop.ExpectedValuePerContract;
				// The scorer's EV already pays the new structure's own crossings; closing the existing one is a further order now.
				var campaignEvTotal = closePnLTotal + forwardEvPerContract * propQty - perOrderFriction * origQty;
				var newMarginTotal = EstimateProposalMarginTotal(prop, propQty, spot, quotes);
				var marginDeltaTotal = newMarginTotal - currentMarginTotal;

				var openLegsText = string.Join(", ", prop.Legs.Select(l =>
				{
					var price = l.PricePerShare ?? MidOf(quotes, l.Symbol) ?? 0m;
					return $"{l.Action.ToUpperInvariant()} {l.Symbol} x{propQty} @{price.ToString("0.###", CultureInfo.InvariantCulture)}";
				}));
				var closeLegsText = $"BUY {shortLeg.Symbol} x{origQty} @{shortMidNow.ToString("0.###", CultureInfo.InvariantCulture)}, SELL {longLeg.Symbol} x{origQty} @{longMidNow.ToString("0.###", CultureInfo.InvariantCulture)}";
				var actionSummary = $"{closeLegsText}, {openLegsText}";

				var shortDesc = DescribeProposalLegs(prop);
				var gateNote = prop.FinalScore.HasValue && scanCfg.Opener.MinScoreToOpen > 0m && prop.FinalScore.Value < scanCfg.Opener.MinScoreToOpen
					? " [below minScore]"
					: "";
				var name = $"Reset → {prop.StructureKind} {shortDesc} (x{propQty}, AI scan){gateNote}";
				var rationale = $"AI scan pick (score {prop.FinalScore ?? 0m:F4}, POP {prop.ProbabilityOfProfit * 100m:0.0}%, managed EV {(forwardEvPerContract * propQty >= 0m ? "+" : "-")}${Math.Abs(forwardEvPerContract * propQty):N0}); close net ${closeNetPerShare:+0.00;-0.00}/sh, open {propQty}x @ ${prop.DebitOrCreditPerContract / 100m:+0.00;-0.00}/sh";

				// ProjectedValuePerContract reflects the forward expected value at target (no pinned-spot illusion).
				var projectedTotal = (prop.ExpectedValuePerContract - prop.DebitOrCreditPerContract) * propQty;
				var totalPnLTotal = closePnLTotal + prop.ExpectedValuePerContract * propQty;

				results.Add(new AnalyzePositionCommand.Scenario(
					Name: name,
					ActionSummary: actionSummary,
					CashImpactPerContract: netCashTotal / origQty,
					ProjectedValuePerContract: projectedTotal / origQty,
					TotalPnLPerContract: totalPnLTotal / origQty,
					MarginDeltaPerContract: marginDeltaTotal / origQty,
					Qty: origQty,
					DaysToTarget: Math.Max(1, prop.DaysToTarget),
					Rationale: rationale,
					IsRoll: true,
					ExpectedPnLPerContract: campaignEvTotal / origQty,
					BreakEvens: prop.Breakevens,
					ProbabilityOfProfit: prop.ProbabilityOfProfit));
			}
		}
		catch
		{
			// Best-effort: if AI scan fails on an incomplete chain, the caller still has Hold, Close, and Rolls.
		}

		return results;
	}

	/// <summary><paramref name="sc"/> valued on the opener scorer: the P&L of closing everything at mid now plus each held structure's forward EV
	/// (managed when the scorer models exits) times its contracts. A structure the scorer cannot value leaves the scenario without an EV.</summary>
	/// <returns>The scenario, and for an add with margin, the added structure's expected profit per added dollar.</returns>
	/// <param name="perOrderFriction">One broker order's friction per contract (<see cref="RealizedExpectancy.FrictionPerOrderContract"/>). The scorer
	/// charges every structure it values the friction of its whole life, as if opened now, opening order included; a scenario instead pays for
	/// the orders it places now (<see cref="AnalyzePositionCommand.Scenario.OrderContractsNow"/>). So each held structure's opening order is refunded
	/// and the orders placed now are charged: Close all pays its closing order, Hold pays nothing now, and an add pays its one opening order.</param>
	internal static (AnalyzePositionCommand.Scenario Scenario, decimal? AddedReturn) Revalue(
		AnalyzePositionCommand.Scenario sc,
		int origQty,
		decimal closePnLPerContract,
		decimal perOrderFriction,
		Func<AnalyzePositionCommand.BookStructure, AI.RiskDiagnostics.RiskDiagnosticOpenerScore?> score)
	{
		var book = sc.Book!;
		var chargedNow = perOrderFriction * sc.OrderContractsNow;
		if (book.Count == 0) return (sc with { ExpectedPnLPerContract = closePnLPerContract - chargedNow / origQty, EvNote = chargedNow > 0m ? $"trading costs {Signed(-chargedNow)} (closing order)" : null }, null);

		var parts = new List<(AnalyzePositionCommand.BookStructure Structure, decimal EvPerContract, decimal? Pop, int Orders)>();
		foreach (var structure in book.Where(b => b.Qty > 0))
		{
			var scored = score(structure);
			if ((scored?.ManagedExpectedValuePerContract ?? scored?.ExpectedValuePerContract) is not decimal ev)
				return (sc with { ExpectedPnLPerContract = null, ProbabilityOfProfit = null, EvNote = $"the opener scorer could not value the {structure.Label} structure, so no EV" }, null);
			parts.Add((structure, ev, scored!.ProbabilityOfProfit, OrdersOf(scored)));
		}

		var refunded = perOrderFriction * parts.Sum(p => p.Orders * p.Structure.Qty);
		var forwardTotal = parts.Sum(p => p.EvPerContract * p.Structure.Qty) + refunded - chargedNow;
		var note = "opener scorer EV from now: " + string.Join(", ", parts.Select(p => $"{p.Structure.Label} {Signed(p.EvPerContract)}/contract × {p.Structure.Qty}"));
		if (refunded != chargedNow) note += $"; trading costs {Signed(refunded - chargedNow)} (orders now vs. the scorer's opening order per structure)";
		decimal? addedReturn = null;
		if (parts.FirstOrDefault(p => p.Structure.Label == "added") is { Structure: not null } added)
		{
			var addedTotal = added.EvPerContract * added.Structure.Qty;
			var addedMargin = sc.MarginDeltaPerContract * sc.Qty;
			if (addedMargin > 0m) addedReturn = addedTotal / addedMargin;
			note += $"; expected profit on the add {Signed(addedTotal)}" + (addedReturn is decimal r ? $" ({r * 100m:+0.0;-0.0}% on added ${addedMargin:N0})" : "");
		}
		// A probability of profit only describes the scenario when one structure carries every contract.
		var pop = parts.Count == 1 && parts[0].Structure.Qty == origQty ? parts[0].Pop : null;
		return (sc with { ExpectedPnLPerContract = closePnLPerContract + forwardTotal / origQty, ProbabilityOfProfit = pop ?? sc.ProbabilityOfProfit, EvNote = note }, addedReturn);
	}

	/// <summary>Marks the opposite-side add (double calendar/diagonal) expected to make the most per added dollar, when there is a choice and that
	/// return is positive. Full and partial sizes of one add share its return, so both carry the mark.</summary>
	internal static void FlagBestAdd(List<AnalyzePositionCommand.Scenario> scenarios, IReadOnlyList<(int Index, decimal Return)> adds)
	{
		if (adds.Count < 2) return;
		var best = adds.Max(a => a.Return);
		if (best <= 0m) return;
		foreach (var (index, _) in adds.Where(a => a.Return == best))
			scenarios[index] = scenarios[index] with { EvNote = $"{scenarios[index].EvNote} — best add (highest expected profit on added capital)" };
	}

	/// <summary>Broker orders the scored structure takes to open; 1 when its kind is not recognized.</summary>
	private static int OrdersOf(AI.RiskDiagnostics.RiskDiagnosticOpenerScore scored) =>
		Enum.TryParse<OpenStructureKind>(scored.Structure, out var kind) ? RealizedExpectancy.OrdersForStructure(kind) : 1;

	private static string Signed(decimal dollars) => dollars >= 0m ? $"+${dollars:N2}" : $"-${Math.Abs(dollars):N2}";

	/// <summary><see cref="ScoreLegs"/> once per distinct structure: Hold, the scale-up and every partial share the existing legs' score.</summary>
	private static AI.RiskDiagnostics.RiskDiagnosticOpenerScore? ScoreCached(
		Dictionary<string, AI.RiskDiagnostics.RiskDiagnosticOpenerScore?> cache,
		IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> legs,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		decimal spot,
		DateTime asOf,
		DateTime quoteAsOf,
		AnalyzeRiskCommand.OpenerScoringInputs scoringInputs,
		decimal ivDefault)
	{
		var key = string.Join("|", legs.Select(l => $"{l.Action}:{l.Symbol}").Order(StringComparer.Ordinal));
		if (!cache.TryGetValue(key, out var scored))
			cache[key] = scored = ScoreLegs(legs, quotes, spot, asOf, quoteAsOf, scoringInputs, ivDefault);
		return scored;
	}

	private static AI.RiskDiagnostics.RiskDiagnosticOpenerScore? ScoreLegs(
		IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> legs,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		decimal spot,
		DateTime asOf,
		DateTime quoteAsOf,
		AnalyzeRiskCommand.OpenerScoringInputs scoringInputs,
		decimal ivDefault)
	{
		var diagLegs = AnalyzeRiskCommand.BuildDiagnosticLegs(legs, quotes, spot, asOf, ivDefault);
		return AnalyzeRiskCommand.BuildProbe(diagLegs, spot, asOf, quoteAsOf, quotes, scoringInputs, ivDefault, scoreAtCostBasis: false, useMarketImpliedIv: true).OpenerScore;
	}

	private static decimal EstimateProposalMarginTotal(OpenProposal prop, int qty, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var marginLegs = new List<MarginLeg>();
		foreach (var l in prop.Legs)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(l.Symbol);
			if (parsed == null) continue;
			var isLong = l.Action.Equals("buy", StringComparison.OrdinalIgnoreCase);
			var premium = isLong ? 0m : (l.PricePerShare ?? MidOf(quotes, l.Symbol) ?? 0m);
			marginLegs.Add(new MarginLeg(parsed, qty, isLong, premium));
		}
		return marginLegs.Count > 0 ? MarginCalculator.Requirement(marginLegs, spot, 0).Total : 0m;
	}

	private static string DescribeProposalLegs(OpenProposal prop)
	{
		var parsed = prop.Legs
			.Select(l => (l.Action, Opt: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.Where(x => x.Opt != null)
			.ToList();
		if (parsed.Count == 0) return prop.Ticker;
		return string.Join("/", parsed.Select(x => $"{(x.Action.Equals("buy", StringComparison.OrdinalIgnoreCase) ? "+" : "−")}{Formatters.FormatQty(x.Opt!.Strike)}{x.Opt.CallPut} {x.Opt.ExpiryDate:MM-dd}"));
	}

	private static decimal? MidOf(IReadOnlyDictionary<string, OptionContractQuote> quotes, string sym)
	{
		if (!quotes.TryGetValue(sym, out var q)) return null;
		if (q.Bid is decimal b && q.Ask is decimal a) return (b + a) / 2m;
		return q.LastPrice;
	}
}

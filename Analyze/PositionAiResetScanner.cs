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
		bool useMarketImpliedIv,
		CancellationToken cancellation)
	{
		if (kind is not (AnalyzePositionCommand.StructureKind.Calendar or AnalyzePositionCommand.StructureKind.Diagonal))
			return scenarios;
		if (positionLegs.Count != 2)
			return scenarios;

		var shortLeg = positionLegs.FirstOrDefault(l => l.Action == LegAction.Sell);
		var longLeg = positionLegs.FirstOrDefault(l => l.Action == LegAction.Buy);
		if (shortLeg == null || longLeg == null) return scenarios;

		var ticker = shortLeg.Parsed.Root;
		var origQty = positionLegs[0].Qty;
		var initialDebitPerShare = positionLegs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);

		var shortMidNow = MidOf(quotes, shortLeg.Symbol) ?? shortLeg.CostBasis;
		var longMidNow = MidOf(quotes, longLeg.Symbol) ?? longLeg.CostBasis;
		var closeNetPerShare = longMidNow - shortMidNow;
		var closePnLPerContract = (closeNetPerShare - initialDebitPerShare) * 100m;
		var closePnLTotal = closePnLPerContract * origQty;
		var closeProceedsTotal = closeNetPerShare * 100m * origQty;

		// 1. Unified opener scoring for Hold and ALL Roll scenarios (both full and partial — same scorer as `analyze roll` / `analyze risk`).
		var scoringInputs = await AnalyzeRiskCommand.LoadOpenerScoringInputsAsync(ticker, asOf, includeCurrentBar: !EvaluationDate.IsPast, intradayCache, cancellation);
		var holdScored = ScoreLegs(positionLegs, quotes, spot, asOf, quoteAsOf, scoringInputs, settings.IvDefault, useMarketImpliedIv);
		decimal? holdForwardEvPerContract = holdScored?.ManagedExpectedValuePerContract ?? holdScored?.ExpectedValuePerContract;
		var updated = new List<AnalyzePositionCommand.Scenario>(scenarios.Count);

		foreach (var sc in scenarios)
		{
			// Drop naive hardcoded "Reset to $X calendar" — replaced by real AI scan reset proposals below.
			if (sc.Name.StartsWith("Reset to ", StringComparison.Ordinal))
				continue;

			if (sc.ActionSummary == "—")
			{
				if (holdForwardEvPerContract.HasValue)
				{
					updated.Add(sc with
					{
						ExpectedPnLPerContract = closePnLPerContract + holdForwardEvPerContract.Value,
						ProbabilityOfProfit = holdScored!.ProbabilityOfProfit
					});
					continue;
				}
			}
			else if (sc.IsRoll)
			{
				// 2-leg roll (full OR partial): reconstruct resulting legs (static long + new rolled short) and score.
				var (newShortSym, rolledQty) = ExtractRolledNewShort(sc.ActionSummary, shortLeg.Symbol, origQty);
				if (newShortSym != null && ParsingHelpers.ParseOptionSymbol(newShortSym) is { } newShortParsed && quotes.ContainsKey(newShortSym))
				{
					var newShortMid = MidOf(quotes, newShortSym) ?? 0m;
					var postRollLegs = new List<AnalyzePositionCommand.PositionSnapshot>
					{
						new(longLeg.Symbol, LegAction.Buy, rolledQty, longMidNow, longLeg.Parsed),
						new(newShortSym, LegAction.Sell, rolledQty, newShortMid, newShortParsed)
					};
					var scored = ScoreLegs(postRollLegs, quotes, spot, asOf, quoteAsOf, scoringInputs, settings.IvDefault, useMarketImpliedIv);
					if (scored != null && (scored.ManagedExpectedValuePerContract ?? scored.ExpectedValuePerContract) is decimal rollForwardEvPerContract)
					{
						var unchangedQty = Math.Max(0, origQty - rolledQty);
						var totalForwardEv = rollForwardEvPerContract * rolledQty + (holdForwardEvPerContract ?? 0m) * unchangedQty;
						var campaignEvPerContract = closePnLPerContract + totalForwardEv / origQty;
						updated.Add(sc with
						{
							ExpectedPnLPerContract = campaignEvPerContract,
							ProbabilityOfProfit = rolledQty == origQty ? scored.ProbabilityOfProfit : sc.ProbabilityOfProfit
						});
						continue;
					}
				}
			}

			updated.Add(sc);
		}

		// 2. Run OpenCandidateEvaluator (in-memory, zero extra network calls) to find the best AI scan reset(s).
		var resetScenarios = await BuildAiScanResetScenariosAsync(
			aiCfg, ticker, shortLeg, longLeg, shortMidNow, longMidNow, closeNetPerShare, closePnLTotal, closeProceedsTotal,
			origQty, initialDebitPerShare, settings, spot, asOf, quotes, priceCache, cancellation);

		updated.AddRange(resetScenarios);
		return updated;
	}

	private static async Task<List<AnalyzePositionCommand.Scenario>> BuildAiScanResetScenariosAsync(
		AIConfig aiCfg,
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
			var scanCfg = PrepareResetScanConfig(aiCfg, ticker);
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
				var campaignEvTotal = closePnLTotal + forwardEvPerContract * propQty;
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

	private static AI.RiskDiagnostics.RiskDiagnosticOpenerScore? ScoreLegs(
		IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> legs,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		decimal spot,
		DateTime asOf,
		DateTime quoteAsOf,
		AnalyzeRiskCommand.OpenerScoringInputs scoringInputs,
		decimal ivDefault,
		bool useMarketImpliedIv)
	{
		var diagLegs = AnalyzeRiskCommand.BuildDiagnosticLegs(legs, quotes, spot, asOf, ivDefault);
		return AnalyzeRiskCommand.BuildProbe(diagLegs, spot, asOf, quoteAsOf, quotes, scoringInputs, ivDefault, scoreAtCostBasis: false, useMarketImpliedIv).OpenerScore;
	}

	private static (string? Symbol, int Qty) ExtractRolledNewShort(string actionSummary, string oldShortSymbol, int defaultQty)
	{
		foreach (var part in actionSummary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length >= 3 && tokens[0].Equals("SELL", StringComparison.OrdinalIgnoreCase)
				&& !tokens[1].Equals(oldShortSymbol, StringComparison.OrdinalIgnoreCase))
			{
				var qty = int.TryParse(tokens[2].TrimStart('x'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQty) && parsedQty > 0
					? parsedQty
					: defaultQty;
				return (tokens[1], qty);
			}
		}
		return (null, defaultQty);
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

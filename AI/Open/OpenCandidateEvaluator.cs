using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>
/// Orchestrates the opener pipeline: enumerate skeletons → phase-3 quote fetch → score → rank per ticker.
/// Produces a flat, ranked list of OpenProposal across all configured tickers, capped at topNPerTicker each.
/// </summary>
internal sealed class OpenCandidateEvaluator
{
	private readonly AIConfig _config;
	private readonly IQuoteSource _quotes;
	private readonly string _pricingMode;

	public OpenCandidateEvaluator(AIConfig config, IQuoteSource quotes, string pricingMode = SuggestionPricing.Mid)
	{
		_config = config;
		_quotes = quotes;
		_pricingMode = SuggestionPricing.Normalize(pricingMode);
	}

	public async Task<IReadOnlyList<OpenProposal>> EvaluateAsync(EvaluationContext ctx, CancellationToken cancellation)
	{
		var cfg = _config.Opener;
		if (!cfg.Enabled) return Array.Empty<OpenProposal>();

		var debug = string.Equals(_config.Log.ConsoleVerbosity, "debug", StringComparison.OrdinalIgnoreCase);

		var tickerSet = new HashSet<string>(_config.Tickers, StringComparer.OrdinalIgnoreCase);
		var output = new List<OpenProposal>();

		// Phase A0: bootstrap spots + chains for tickers missing from ctx.UnderlyingPrices.
		// The live-quote clients return the full chain plus the underlying spot for any OCC symbol,
		// so one placeholder per root suffices. Without this, tickers without open positions have
		// no spot in ctx and we enumerate nothing.
		var bootstrapSpots = new Dictionary<string, decimal>(ctx.UnderlyingPrices, StringComparer.OrdinalIgnoreCase);
		var bootstrapOptions = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var missingTickers = _config.Tickers.Where(t => !bootstrapSpots.ContainsKey(t) || bootstrapSpots[t] <= 0m).ToList();
		if (missingTickers.Count > 0)
		{
			var placeholderExpiry = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(ctx.Now, 1, 14).FirstOrDefault();
			if (placeholderExpiry == default) placeholderExpiry = ctx.Now.Date.AddDays(7);
			var placeholders = missingTickers.Select(t => MatchKeys.OccSymbol(t, placeholderExpiry, 1m, "C")).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var boot = await _quotes.GetQuotesAsync(ctx.Now, placeholders, tickerSet, cancellation);
			foreach (var (k, v) in boot.Underlyings) bootstrapSpots[k] = v;
			foreach (var (k, v) in boot.Options) bootstrapOptions[k] = v;
		}

		// Index the chain expirations available per ticker so the enumerator can use real chain dates
		// (which carry holiday adjustments — e.g. Juneteenth shifts the June monthly to Thursday) instead
		// of computed 3rd-Friday/Friday dates that Webull's chain doesn't actually return. Without this,
		// calendar/diagonal long legs generate OCC symbols Webull never sends back and silently drop.
		// Only walk Keys when the dictionary actually enumerates — some test fakes (e.g. always-true
		// ContainsKey) expose empty Keys; those callers fall through to the OpenerExpiryHelpers path.
		var availableByTicker = new Dictionary<string, HashSet<DateTime>>(StringComparer.OrdinalIgnoreCase);
		foreach (var ticker in _config.Tickers)
			availableByTicker[ticker] = new HashSet<DateTime>();
		IndexExpirations(bootstrapOptions.Keys, availableByTicker);
		IndexExpirations(ctx.Quotes.Keys, availableByTicker);

		// Phase A: enumerate across all tickers.
		var allSkeletons = new List<CandidateSkeleton>();
		foreach (var ticker in _config.Tickers)
		{
			if (!bootstrapSpots.TryGetValue(ticker, out var spot) || spot <= 0m) continue;
			var available = availableByTicker[ticker];
			allSkeletons.AddRange(CandidateEnumerator.Enumerate(ticker, spot, ctx.Now, cfg, available.Count > 0 ? available : null));
		}
		if (allSkeletons.Count == 0) return Array.Empty<OpenProposal>();

		// Phase B (phase-3 quote fetch): pull any leg whose quote is missing OR whose bid/ask is null.
		// The latter happens routinely after-hours and on weekends — Webull's strategy/list inlines pricing
		// only for liquid front-month contracts; long-DTE entries come back symbol-only with null bid/ask.
		// Position legs survive because Phase-1 wantedSymbols triggers a queryBatch refresh; opener legs
		// need this Phase B pass to do the equivalent for them.
		var neededSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var skel in allSkeletons)
			foreach (var leg in skel.Legs)
			{
				if (HasUsableQuote(ctx.Quotes, leg.Symbol)) continue;
				if (HasUsableQuote(bootstrapOptions, leg.Symbol)) continue;
				neededSymbols.Add(leg.Symbol);
			}

		// Keep ctx.Quotes as the primary lookup; overlay fetched symbols only for the new ones.
		// Do NOT copy ctx.Quotes by enumeration — callers may supply non-enumerable fakes (tests) or large live dictionaries.
		IReadOnlyDictionary<string, OptionContractQuote> mergedQuotes = bootstrapOptions.Count > 0 ? new OverlayQuoteDictionary(ctx.Quotes, bootstrapOptions) : ctx.Quotes;
		if (neededSymbols.Count > 0)
		{
			var extra = await _quotes.GetQuotesAsync(ctx.Now, neededSymbols, tickerSet, cancellation);
			if (extra.Options.Count > 0)
			{
				var overlay = new Dictionary<string, OptionContractQuote>(extra.Options, StringComparer.OrdinalIgnoreCase);
				foreach (var (k, v) in bootstrapOptions) overlay.TryAdd(k, v);
				mergedQuotes = new OverlayQuoteDictionary(ctx.Quotes, overlay);
			}
		}

		// Phase C: score per ticker.
		var reserve = CashReserveHelper.ComputeReserve(_config.CashReserve.Mode, _config.CashReserve.Value, ctx.AccountValue);
		var freeCash = Math.Max(0m, ctx.AccountCash - reserve);
		var historicalVolByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		if (cfg.VolatilityFitWeight > 0m)
		{
			var priceCache = new HistoricalPriceCache();
			foreach (var ticker in _config.Tickers)
			{
				var closes = await priceCache.GetRecentClosesAsync(ticker, cfg.VolatilityLookbackDays + 1, ctx.Now, cancellation);
				var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
				if (hv.HasValue && hv.Value > 0m)
					historicalVolByTicker[ticker] = hv.Value;
			}
		}

		foreach (var tickerGroup in allSkeletons.GroupBy(s => s.Ticker))
		{
			if (!bootstrapSpots.TryGetValue(tickerGroup.Key, out var spot) || spot <= 0m) continue;
			ctx.TechnicalSignals.TryGetValue(tickerGroup.Key, out var biasSignal);
			var bias = biasSignal?.Score ?? 0m;
			historicalVolByTicker.TryGetValue(tickerGroup.Key, out var historicalVolAnnual);

			var shortVerticalRejects = debug
				? new Dictionary<CandidateScorer.ShortVerticalRejectReason, int>()
				: null;
			var debugRejectLinesLeft = 25;

			var scoredByStructure = new Dictionary<OpenStructureKind, List<OpenProposal>>();

			foreach (var skel in tickerGroup)
			{
				var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, bias, cfg, historicalVolAnnual > 0m ? historicalVolAnnual : null, _pricingMode);
				if (p == null)
				{
					if (debug && (skel.StructureKind == OpenStructureKind.ShortPutVertical || skel.StructureKind == OpenStructureKind.ShortCallVertical))
					{
						var reason = CandidateScorer.DiagnoseShortVerticalRejection(skel, mergedQuotes, out var detail);
						shortVerticalRejects![reason] = shortVerticalRejects.GetValueOrDefault(reason) + 1;
						if (debugRejectLinesLeft-- > 0)
							Console.Error.WriteLine($"[debug] {skel.Ticker} {skel.StructureKind} dropped: {reason} ({detail})");
					}
					continue;
				}
				if (!scoredByStructure.TryGetValue(p.StructureKind, out var list))
					scoredByStructure[p.StructureKind] = list = new List<OpenProposal>();
				list.Add(p);
			}

			if (debug && shortVerticalRejects != null && shortVerticalRejects.Count > 0)
			{
				var summary = string.Join(", ", shortVerticalRejects.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
				Console.Error.WriteLine($"[debug] {tickerGroup.Key} short-vertical rejections: {summary}");
			}

			// Per-structure top-N truncation.
			var survivors = new List<OpenProposal>();
			foreach (var list in scoredByStructure.Values)
				survivors.AddRange(RankForOutput(list).Take(cfg.MaxCandidatesPerStructurePerTicker));

			// Apply cash sizing.
			for (int i = 0; i < survivors.Count; i++)
				survivors[i] = ApplyCashSizing(survivors[i], freeCash, cfg, bias);

			// Per-ticker top-N.
			output.AddRange(RankForOutput(survivors).Take(cfg.TopNPerTicker));
		}

		// Risk diagnostic: build one per surviving proposal. Trend fetched once per ticker.
		var trendByTicker = new Dictionary<string, TrendSnapshot?>(StringComparer.OrdinalIgnoreCase);
		foreach (var ticker in output.Select(p => p.Ticker).Distinct(StringComparer.OrdinalIgnoreCase))
			trendByTicker[ticker] = await TrendFetcher.FetchAsync(ticker, ctx.Now, cancellation);

		var annotated = new List<OpenProposal>(output.Count);
		foreach (var p in output)
		{
			var diagLegs = p.Legs.Select(l =>
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(l.Symbol);
				return new DiagnosticLeg(
					Symbol: l.Symbol,
					Parsed: parsed!,
					IsLong: l.Action == "buy",
					Qty: l.Qty,
					PricePerShare: l.PricePerShare,
					CostBasisPerShare: null);
			}).Where(l => l.Parsed != null).ToList();

			if (diagLegs.Count != p.Legs.Count)
			{
				annotated.Add(p);
				continue;
			}

			var spotForDiag = bootstrapSpots.TryGetValue(p.Ticker, out var s) ? s : 0m;
			trendByTicker.TryGetValue(p.Ticker, out var trend);
			var diagnostic = RiskDiagnosticBuilder.Build(
				legs: diagLegs,
				spot: spotForDiag,
				asOf: ctx.Now,
				ivResolver: sym => mergedQuotes.TryGetValue(sym, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
					? q.ImpliedVolatility.Value
					: 0.40m,
				trend: trend);

			var openerScore = (
			 bias: ctx.TechnicalSignals.TryGetValue(p.Ticker, out var bs) ? (bs?.Score ?? 0m) : 0m,
				cfg: cfg,
				structure: p.StructureKind.ToString(),
				qty: p.Qty,
				rationale: p.Rationale,
				creditPerContract: p.DebitOrCreditPerContract,
				maxProfit: p.MaxProfitPerContract,
				maxLoss: p.MaxLossPerContract,
				risk: p.CapitalAtRiskPerContract,
				pop: p.ProbabilityOfProfit,
				ev: p.ExpectedValuePerContract,
				days: p.DaysToTarget,
				rawScore: p.RawScore,
                biasScore: p.BiasAdjustedScore,
				thetaPerDayPerContract: p.ThetaPerDayPerContract);

			var probe = RiskDiagnosticProbeBuilder.Build(
				legs: diagLegs,
				spot: spotForDiag,
				asOf: ctx.Now,
				ivResolver: sym => mergedQuotes.TryGetValue(sym, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
					? q.ImpliedVolatility.Value
					: 0.40m,
				quotes: mergedQuotes,
				opener: openerScore);

			annotated.Add(p with { Diagnostic = diagnostic with { Probe = probe } });
		}
		return annotated;
	}

	internal static IReadOnlyList<OpenProposal> RankForOutput(IEnumerable<OpenProposal> proposals)
	{
		var list = proposals.ToList();
		if (list.Count <= 1) return list;

		return list
			.OrderByDescending(RankingScoreForOutput)
			.ThenByDescending(p => p.ThetaPerDayPerContract ?? decimal.MinValue)
			.ThenByDescending(p => p.BiasAdjustedScore)
			.ThenBy(p => IsCalendarLike(p) ? p.DaysToTarget : int.MaxValue)
			.ToList();
	}

	private static OpenProposal ApplyCashSizing(OpenProposal p, decimal freeCash, OpenerConfig cfg, decimal bias)
	{
		if (p.CapitalAtRiskPerContract <= 0m)
			return p with { Rationale = CandidateScorer.BuildRationale(p, bias, cfg) };

		var rawMax = Math.Floor(freeCash / p.CapitalAtRiskPerContract);
		var maxQty = rawMax >= cfg.MaxQtyPerProposal ? cfg.MaxQtyPerProposal : (int)rawMax;
		OpenProposal updated;
		if (maxQty >= 1)
		{
			var qty = Math.Min(maxQty, cfg.MaxQtyPerProposal);
			updated = p with { Qty = qty, Legs = ScaleLegs(p.Legs, qty) };
		}
		else
		{
			updated = p with
			{
				Qty = 0,
				CashReserveBlocked = true,
				CashReserveDetail = $"free ${freeCash:F0}, requires ${p.CapitalAtRiskPerContract:F0} per contract"
			};
		}
		return updated with
		{
			Rationale = CandidateScorer.BuildRationale(updated, bias, cfg),
			Fingerprint = CandidateScorer.ComputeFingerprint(updated.Ticker, updated.StructureKind, updated.Legs, updated.Qty)
		};
	}

	private static IReadOnlyList<ProposalLeg> ScaleLegs(IReadOnlyList<ProposalLeg> legs, int qty) =>
		legs.Select(l => l with { Qty = qty }).ToList();

	private static decimal RankingScoreForOutput(OpenProposal proposal)
	{
		var thetaPerDay = proposal.ThetaPerDayPerContract ?? 0m;
		return thetaPerDay > 0m ? proposal.BiasAdjustedScore * thetaPerDay : proposal.BiasAdjustedScore;
	}

	private static bool IsCalendarLike(OpenProposal proposal) => proposal.StructureKind is OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal;

	/// <summary>Returns true when the symbol exists and has both a non-null bid and a positive ask.
	/// Mirrors <c>CandidateScorer.TryLiveBidAsk</c>'s acceptance criteria so Phase B's "needs refresh"
	/// decision matches what the scorer will actually accept downstream.</summary>
	private static bool HasUsableQuote(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol)
	{
		if (!quotes.TryGetValue(symbol, out var q)) return false;
		return q.Bid.HasValue && q.Ask.HasValue && q.Ask.Value > 0m;
	}

	/// <summary>Parses each OCC symbol and records its expiration date in the per-ticker bucket. Symbols
	/// whose root isn't in <paramref name="byTicker"/> are ignored.</summary>
	private static void IndexExpirations(IEnumerable<string> symbols, Dictionary<string, HashSet<DateTime>> byTicker)
	{
		foreach (var sym in symbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null) continue;
			if (byTicker.TryGetValue(p.Root, out var set)) set.Add(p.ExpiryDate.Date);
		}
	}
}

/// <summary>Read-only dictionary that layers a small overlay (phase-3 fetched quotes) over a base dictionary (ctx.Quotes).
/// Overlay wins on key collisions. Enumeration reflects only what's in the overlay, but TryGetValue checks both.</summary>
internal sealed class OverlayQuoteDictionary : IReadOnlyDictionary<string, OptionContractQuote>
{
	private readonly IReadOnlyDictionary<string, OptionContractQuote> _base;
	private readonly IReadOnlyDictionary<string, OptionContractQuote> _overlay;

	public OverlayQuoteDictionary(IReadOnlyDictionary<string, OptionContractQuote> baseDict, IReadOnlyDictionary<string, OptionContractQuote> overlay)
	{
		_base = baseDict;
		_overlay = overlay;
	}

	public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out OptionContractQuote value)
	{
		if (_overlay.TryGetValue(key, out value)) return true;
		return _base.TryGetValue(key, out value);
	}
	public bool ContainsKey(string key) => _overlay.ContainsKey(key) || _base.ContainsKey(key);
	public OptionContractQuote this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);
	public IEnumerable<string> Keys => _overlay.Keys.Concat(_base.Keys.Where(k => !_overlay.ContainsKey(k)));
	public IEnumerable<OptionContractQuote> Values => Keys.Select(k => this[k]);
	public int Count => _overlay.Count + _base.Keys.Count(k => !_overlay.ContainsKey(k));
	public IEnumerator<KeyValuePair<string, OptionContractQuote>> GetEnumerator() => Keys.Select(k => new KeyValuePair<string, OptionContractQuote>(k, this[k])).GetEnumerator();
	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

using System.Text.Json;
using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Sentiment;

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
	private readonly HistoricalPriceCache _priceCache;
	private readonly bool _backtestMode;
	private IntradayBarCache? _intradayCache;

	/// <param name="priceCache">Shared close-price cache. Pass the runner-owned instance so the in-memory
	/// per-ticker dictionary survives across ticks/steps instead of being rebuilt (and the CSV re-read) each call.</param>
	/// <param name="backtestMode">When true, skips network calls to <see cref="EventCalendarLoader"/> and
	/// <see cref="RiskDiagnostics.TrendFetcher"/>. Both fetch current Yahoo state regardless of <c>asOf</c>,
	/// which introduces lookahead bias in backtests and accounts for most of the per-step wall time.</param>
	public OpenCandidateEvaluator(AIConfig config, IQuoteSource quotes, string pricingMode = SuggestionPricing.Mid, HistoricalPriceCache? priceCache = null, bool backtestMode = false)
	{
		_config = config;
		_quotes = quotes;
		_pricingMode = SuggestionPricing.Normalize(pricingMode);
		_priceCache = priceCache ?? new HistoricalPriceCache();
		_backtestMode = backtestMode;
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
			// Probe one placeholder OCC symbol per (ticker, candidate-expiry). Webull's live quote source
			// returns the full chain for any one symbol — but the BacktestQuoteSource only prices what's
			// asked, so we must enumerate the actual cadence (Mon-Fri for SPX/SPY/QQQ/NDX/XSP, Mon-Wed-Fri
			// for mega-cap multi-weeklies, Fridays for everything else) up to the widest enabled DTE.
			var maxDte = MaxDteAcrossStructures(cfg);
			var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in missingTickers)
			{
				foreach (var exp in OpenerExpiryHelpers.NextExpiriesForTicker(t, ctx.Now, 0, maxDte))
					placeholders.Add(MatchKeys.OccSymbol(t, exp, 1m, "C"));
				// Calendar/diagonal long legs reach into monthly DTE; probe those 3rd-Fridays too.
				foreach (var exp in OpenerExpiryHelpers.MonthlyExpiriesInRange(ctx.Now, 0, maxDte))
					placeholders.Add(MatchKeys.OccSymbol(t, exp, 1m, "C"));
				// Guard against an empty cadence (e.g. minDte/maxDte yielding no days) — keep at least one
				// probe so the underlying spot still surfaces.
				if (placeholders.All(s => !s.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
					placeholders.Add(MatchKeys.OccSymbol(t, ctx.Now.Date.AddDays(7), 1m, "C"));
			}
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

		// Phase A: enumerate across all tickers. Feed the enumerator the merged chain quotes so the
		// delta-band filter on strike picks uses each strike's live IV rather than the static
		// cfg.Indicators.IvDefaultPct fallback. The chain ImpliedVolatility carries the actual smile
		// the market is pricing — for SPXW in particular, 0DTE wing IV runs 50–80% above ATM and the
		// static default (typically 18%) lands strike picks well outside the configured delta band.
		IReadOnlyDictionary<string, OptionContractQuote> ivLookupQuotes = bootstrapOptions.Count > 0
			? new OverlayQuoteDictionary(ctx.Quotes, bootstrapOptions)
			: ctx.Quotes;
		var allSkeletons = new List<CandidateSkeleton>();
		foreach (var ticker in _config.Tickers)
		{
			if (!bootstrapSpots.TryGetValue(ticker, out var spot) || spot <= 0m) continue;
			var available = availableByTicker[ticker];
			allSkeletons.AddRange(CandidateEnumerator.Enumerate(ticker, spot, ctx.Now, cfg, available.Count > 0 ? available : null, ivLookupQuotes));
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
				// Webull's strategy/list refetch returns the full chain, including symbols that
				// bootstrapOptions (or position-loaded ctx.Quotes) already priced. If the refetch
				// arrives with bid/ask stripped — common after-hours and on illiquid expiries — we
				// must NOT let those nulls overwrite the earlier usable quotes; the candidate scorer
				// rejects any leg without a real bid/ask, so a stripped overlay silently disqualifies
				// otherwise-priceable candidates and is the leading cause of "missing leg" reports.
				var overlay = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
				foreach (var (k, v) in extra.Options)
				{
					if (HasUsableQuote(extra.Options, k))
						overlay[k] = v;
					else if (HasUsableQuote(bootstrapOptions, k))
						overlay[k] = bootstrapOptions[k];
					else if (HasUsableQuote(ctx.Quotes, k))
						continue;
					else
						overlay[k] = v;
				}
				foreach (var (k, v) in bootstrapOptions) overlay.TryAdd(k, v);
				mergedQuotes = new OverlayQuoteDictionary(ctx.Quotes, overlay);
			}
		}

		// Phase C: score per ticker.
		var reserve = CashReserveHelper.ComputeReserve(_config.CashReserve.Mode, _config.CashReserve.Value, ctx.AccountValue);
		var freeCash = Math.Max(0m, ctx.AccountCash - reserve);
		// Fetch the contrarian Fear & Greed regime overlay once per scan. Same value applies to every
		// ticker — F&G is a market-wide sentiment composite, not per-ticker. Null on outage; the scorer
		// treats null as "skip the sentiment factor". Skipped in backtest for the same reason as the
		// event calendar / trend fetcher below: CNN's endpoint returns current state regardless of
		// asOf (lookahead bias) and the per-step file-cache read still adds up across hundreds of days.
		decimal? sentimentScore = null;
		if (!_backtestMode && cfg.Weights.Sentiment > 0m)
		{
			var snapshot = await FearGreedClient.FetchAsync(ctx.Now, cancellation);
			if (snapshot != null) sentimentScore = snapshot.Score;
		}

		// VIX term structure (VIX vs VIX9D): a market-wide regime signal shared across all tickers.
		// Pulled once per scan from the daily-close cache. Both legs use the most-recent settled close
		// strictly before ctx.Now, which keeps backtests lookahead-safe (same filter the technical-bias
		// pipeline uses). Null on missing data — the blend collapses to macroBias.
		decimal? vixTermScore = null;
		if (cfg.Weights.VixTermStructure > 0m)
		{
			try
			{
				var vixCloses = await _priceCache.GetRecentClosesAsync("VIX", 1, ctx.Now, cancellation);
				var vix9dCloses = await _priceCache.GetRecentClosesAsync("VIX9D", 1, ctx.Now, cancellation);
				if (vixCloses.Count > 0 && vix9dCloses.Count > 0)
					vixTermScore = VixTermStructureIndicator.Compute(vixCloses[^1], vix9dCloses[^1]);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Console.WriteLine($"VIX term structure: fetch failed: {ex.Message}");
			}
		}
		var historicalVolByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var shortHorizonHvByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		// Bias calibration inputs per ticker: directional move sign over the lookback, plus the
		// stability score (1 - chop_ratio) derived from sign-flips in daily returns. Both feed the
		// per-ticker confidence calculation in the structure-scoring loop below.
		var biasCalibByTicker = new Dictionary<string, (decimal MoveSign, decimal Stability)>(StringComparer.OrdinalIgnoreCase);
		// Yesterday's settled close per ticker, captured during the closes prefetch. Consumed by the
		// intraday tape signal's gap component below — null when intraday is disabled.
		var prevCloseByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		// Need closes whenever any of:
		//   - 30-day vol-fit factor (HV vs IV)
		//   - 3-day whipsaw factor (3d vs 30d realized vol)
		//   - Bias-calibration factor (recent N-day move vs technical bias direction)
		//   - Intraday tape signal's gap component (yesterday's close)
		// Pulling once and computing all three is cheap.
		var needCloses = cfg.Weights.VolatilityFit > 0m || cfg.Weights.Whipsaw > 0m || cfg.BiasCalibrationLookbackDays > 0 || (!_backtestMode && cfg.Weights.IntradayTape > 0m);
		if (needCloses)
		{
			var lookback = Math.Max(cfg.VolatilityLookbackDays + 1, Math.Max(4, cfg.BiasCalibrationLookbackDays + 2));
			foreach (var ticker in _config.Tickers)
			{
				var closes = await _priceCache.GetRecentClosesAsync(ticker, lookback, ctx.Now, cancellation);
				if (closes.Count > 0) prevCloseByTicker[ticker] = closes[^1];
				var hv30 = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
				if (hv30.HasValue && hv30.Value > 0m)
					historicalVolByTicker[ticker] = hv30.Value;
				if (closes.Count >= 4)
				{
					var recent = closes.Skip(closes.Count - 4).ToList();
					var hv3 = CandidateScorer.ComputeHistoricalVolatilityAnnualized(recent);
					if (hv3.HasValue && hv3.Value > 0m)
						shortHorizonHvByTicker[ticker] = hv3.Value;
				}
				// Bias calibration: combine three reliability components, derived from closes:
				//   1. Directional move sign over the lookback window (paired with bias sign in the loop)
				//   2. Stability — fraction of consecutive same-sign daily returns (1 = monotonic trend,
				//      0 = fully alternating chop). Stable trends → bias is more trustworthy.
				// (Vol-regime component is handled per-structure-loop using the already-computed HV ratio.)
				if (cfg.BiasCalibrationLookbackDays > 0 && closes.Count > cfg.BiasCalibrationLookbackDays)
				{
					var n = cfg.BiasCalibrationLookbackDays;
					var recentClose = closes[^1];
					var olderClose = closes[closes.Count - 1 - n];
					if (olderClose > 0m)
					{
						var moveSign = recentClose > olderClose ? 1m : (recentClose < olderClose ? -1m : 0m);

						// Sign-flip count over the lookback. Each consecutive same-sign daily return pair
						// counts toward stability; opposite-sign pairs subtract from it. Zero-return days
						// are ignored (don't double-penalize sideways days).
						var dailySigns = new List<int>();
						for (int i = closes.Count - n; i < closes.Count; i++)
						{
							var ret = closes[i] - closes[i - 1];
							dailySigns.Add(ret > 0m ? 1 : (ret < 0m ? -1 : 0));
						}
						int sameSign = 0, oppSign = 0;
						for (int i = 1; i < dailySigns.Count; i++)
						{
							if (dailySigns[i] == 0 || dailySigns[i - 1] == 0) continue;
							if (dailySigns[i] == dailySigns[i - 1]) sameSign++;
							else oppSign++;
						}
						var totalPairs = sameSign + oppSign;
						var stability = totalPairs > 0 ? (decimal)sameSign / totalPairs : 0.5m;

						biasCalibByTicker[ticker] = (moveSign, stability);
					}
				}
			}
		}

		// Scheduled-catalyst calendar (earnings + ex-div) per ticker. Built once per scan; resolved
		// per-ticker inside the per-ticker loop. The loader returns EventCalendar.Empty when the
		// feature is disabled or when Yahoo is unreachable — the scorer treats null events as
		// "no veto" so a calendar outage never silences the opener. In backtest mode we skip the
		// Yahoo fetch entirely: it returns current state regardless of asOf (lookahead bias) and
		// adds a sequential HTTP round-trip to every simulated trading day.
		var eventCalendar = _backtestMode
			? EventCalendar.Empty
			: await EventCalendarLoader.LoadAsync(_config.Tickers, cfg.Indicators.Events, ctx.Now, cancellation);

		foreach (var tickerGroup in allSkeletons.GroupBy(s => s.Ticker))
		{
			if (!bootstrapSpots.TryGetValue(tickerGroup.Key, out var spot) || spot <= 0m) continue;
			ctx.TechnicalSignals.TryGetValue(tickerGroup.Key, out var biasSignal);
			var macroBias = biasSignal?.Score ?? 0m;

			// VIX term-structure blend: layered on top of macroBias before the intraday blend.
			// Collapses to macroBias when vixTermScore is null (Yahoo outage, pre-coverage backtest dates).
			var biasedMacro = macroBias;
			if (vixTermScore.HasValue && cfg.Weights.VixTermStructure > 0m)
			{
				var wVix = Math.Clamp(cfg.Weights.VixTermStructure, 0m, 1m);
				biasedMacro = (1m - wVix) * macroBias + wVix * vixTermScore.Value;
			}
			var bias = biasedMacro;

			// Intraday tape signal: fetch minute bars, derive open-to-now / gap / VWAP-deviation
			// composite, blend into bias. Skipped in backtest mode (no minute-bar history) and when
			// the weight is 0 (existing behavior preserved bit-identically). Per-ticker fetch is
			// fronted by IntradayBarCache so adjacent scan ticks reuse disk-cached bars. The blend
			// outcome is shadow-logged for every attempt — set weight to 0.001 to record the would-be
			// bias without materially affecting scoring.
			IntradayBias? intradayBias = null;
			if (!_backtestMode && cfg.Weights.IntradayTape > 0m)
			{
				prevCloseByTicker.TryGetValue(tickerGroup.Key, out var prevClose);
				intradayBias = await ComputeIntradayBiasAsync(tickerGroup.Key, prevClose > 0m ? prevClose : (decimal?)null, cfg.Indicators.IntradayTape, cancellation);
				if (intradayBias != null)
				{
					var w = Math.Clamp(cfg.Weights.IntradayTape, 0m, 1m);
					bias = (1m - w) * biasedMacro + w * intradayBias.Score;
				}
				WriteBiasShadowLog(tickerGroup.Key, macroBias, intradayBias, cfg.Weights.IntradayTape, bias, vixTermScore, cfg.Weights.VixTermStructure);
			}
			historicalVolByTicker.TryGetValue(tickerGroup.Key, out var historicalVolAnnual);
			shortHorizonHvByTicker.TryGetValue(tickerGroup.Key, out var shortHorizonHv);
			// Bias-signal calibration via directional agreement: if the bias direction disagreed with
			// the recent N-day move, the signal is mis-pointing → dampen down to a 0.2× floor; on
			// agreement, leave at full strength. Scales bias at the source so BOTH the grid-shift
			// (BiasDriftWeight) and BiasAdjust see the calibrated value at once.
			//
			// Tested-and-rejected additions: stability (sign-flip count over the window) and
			// vol-regime (whipsaw ratio) components. Both fired too often in normal market noise and,
			// when AND'd / averaged with direction, dampened winners more than losers. Direction
			// alone proved to be the only component with positive net P&L impact.
			//
			// Disabled when biasCalibrationLookbackDays = 0.
			if (cfg.BiasCalibrationLookbackDays > 0 && bias != 0m
				&& biasCalibByTicker.TryGetValue(tickerGroup.Key, out var calib)
				&& calib.MoveSign != 0m)
			{
				var biasSign = bias > 0m ? 1m : -1m;
				var agreement = biasSign * calib.MoveSign;
				var reliability = Math.Clamp(0.5m + 0.5m * agreement, 0.2m, 1.0m);
				bias *= reliability;
			}
			// Whipsaw penalty: when 3-day realized vol is well above 30-day, both put- and call-credit
			// spreads get punished by counter-trend reversals (e.g. April 2025 SPX +$492 inside the crash
			// week wiped every bearish credit). Computed once per ticker; applied per-proposal to credit
			// structures only.
			var whipsawFactor = ComputeWhipsawFactor(shortHorizonHv, historicalVolAnnual, cfg.Weights.Whipsaw);
			var tickerEvents = eventCalendar.Get(tickerGroup.Key);

			var shortVerticalRejects = debug
				? new Dictionary<CandidateScorer.ShortVerticalRejectReason, int>()
				: null;
			var debugRejectLinesLeft = 25;

			var scoredByStructure = new Dictionary<OpenStructureKind, List<OpenProposal>>();

			if (debug)
			{
				// Debug path stays serial: the per-skeleton rejection counters and Console.Error writes
				// need ordered, single-threaded access. Scoring volume in debug mode is the cost of getting
				// the diagnostic output, which is the whole point of running with --debug.
				foreach (var skel in tickerGroup)
				{
					var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, bias, cfg, historicalVolAnnual > 0m ? historicalVolAnnual : null, _pricingMode, sentimentScore: sentimentScore, events: tickerEvents);
					if (p != null && whipsawFactor < 1m && IsCreditStructure(skel.StructureKind))
					{
						var adjusted = CandidateScorer.ApplyFactor(p.FinalScore ?? 0m, whipsawFactor);
						p = p with { FinalScore = adjusted };
					}
					if (p == null)
					{
						if (skel.StructureKind == OpenStructureKind.ShortPutVertical || skel.StructureKind == OpenStructureKind.ShortCallVertical)
						{
							var reason = CandidateScorer.DiagnoseShortVerticalRejection(skel, mergedQuotes, out var detail, ctx.Now, cfg, tickerEvents, spot);
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
			}
			else
			{
				// CandidateScorer.Score dominates per-day wall time once Yahoo I/O is out of the picture
				// (multiple BS calls, scenario-grid P&L, realized-expectancy with friction per candidate;
				// ~150–300 candidates per day on SPXW 0DTE). It's a pure function over its inputs, so fan
				// out across cores. Fixed-size array + Parallel.For preserves original ordering, keeping
				// scoredByStructure insertion order — and downstream tie-breaking in RankForOutput —
				// bit-identical to the serial path.
				var skels = tickerGroup.ToList();
				var results = new OpenProposal?[skels.Count];
				Parallel.For(0, skels.Count, new ParallelOptions { CancellationToken = cancellation }, i =>
				{
					var skel = skels[i];
					var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, bias, cfg, historicalVolAnnual > 0m ? historicalVolAnnual : null, _pricingMode, sentimentScore: sentimentScore, events: tickerEvents);
					if (p != null && whipsawFactor < 1m && IsCreditStructure(skel.StructureKind))
					{
						var adjusted = CandidateScorer.ApplyFactor(p.FinalScore ?? 0m, whipsawFactor);
						p = p with { FinalScore = adjusted };
					}
					results[i] = p;
				});
				foreach (var p in results)
				{
					if (p == null) continue;
					if (!scoredByStructure.TryGetValue(p.StructureKind, out var list))
						scoredByStructure[p.StructureKind] = list = new List<OpenProposal>();
					list.Add(p);
				}
			}

			if (debug && shortVerticalRejects != null && shortVerticalRejects.Count > 0)
			{
				var summary = string.Join(", ", shortVerticalRejects.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
				Console.Error.WriteLine($"[debug] {tickerGroup.Key} short-vertical rejections: {summary}");
			}
			// Per-structure scored/positive/negative breakdown surfaces why a scan returned fewer proposals
			// than expected: the FinalScore > 0 filter at the emit step drops everything ≤ 0, so when
			// only a handful of candidates clear that line the cause is almost always one or two factor
			// chains compounding their cuts past zero. This line answers "how close was each structure?"
			// without needing to instrument the scorer.
			if (debug)
			{
				foreach (var kv in scoredByStructure.OrderBy(k => k.Key.ToString()))
				{
					var scores = kv.Value.Select(p => p.FinalScore ?? 0m).ToList();
					var positive = scores.Count(s => s > 0m);
					var best = scores.Count > 0 ? scores.Max() : 0m;
					var worst = scores.Count > 0 ? scores.Min() : 0m;
					Console.Error.WriteLine($"[debug] {tickerGroup.Key} {kv.Key}: scored={scores.Count} positive={positive} negative={scores.Count - positive} best={best:F6} worst={worst:F6}");
				}
			}

			// Per-structure top-N truncation.
			var survivors = new List<OpenProposal>();
			foreach (var list in scoredByStructure.Values)
				survivors.AddRange(RankForOutput(list).Take(cfg.MaxCandidatesPerStructurePerTicker));

			// Apply cash sizing.
			for (int i = 0; i < survivors.Count; i++)
				survivors[i] = ApplyCashSizing(survivors[i], freeCash, ctx.AccountValue, cfg, bias);

			// Per-ticker top-N. DoubleCalendar/DoubleDiagonal stay as one 4-leg proposal here and render as a
			// unified two-side panel downstream, so each counts as a single suggestion against the cap.
			// Only emit candidates the score itself endorses (FinalScore > MinScoreToOpen). The chain
			// produces a low/negative final when penalties (low POP, narrow BE, whipsaw, etc.) outweigh
			// the raw EV — that's the model saying "do not trade this." The MinScoreToOpen knob raises the
			// bar above zero for users who want only high-conviction trades; default 0 preserves the
			// legacy behavior of emitting any positive-EV trade.
			var minScore = cfg.MinScoreToOpen;
			foreach (var proposal in RankForOutput(survivors).Where(p => (p.FinalScore ?? 0m) > minScore).Take(cfg.TopNPerTicker))
				output.Add(proposal);
		}

		// Risk diagnostic: build one per surviving proposal. Trend fetched once per ticker; sentiment is
		// market-wide so we reuse the snapshot fetched at the top of Phase C (if any). Skipped in
		// backtest for the same reasons as the event calendar above: Yahoo returns current state, and
		// each call adds an HTTP round-trip to every step. Diagnostic builders treat null trend as
		// "unknown" so the proposal still scores and renders.
		var trendByTicker = new Dictionary<string, TrendSnapshot?>(StringComparer.OrdinalIgnoreCase);
		if (!_backtestMode)
		{
			foreach (var ticker in output.Select(p => p.Ticker).Distinct(StringComparer.OrdinalIgnoreCase))
				trendByTicker[ticker] = await TrendFetcher.FetchAsync(ticker, ctx.Now, cancellation);
		}
		// sentimentScore is already gated by !_backtestMode above, but be explicit here for symmetry
		// with the trend-fetcher gate immediately above.
		SentimentSnapshot? diagnosticSentiment = null;
		if (!_backtestMode && sentimentScore.HasValue)
			diagnosticSentiment = await FearGreedClient.FetchAsync(ctx.Now, cancellation);

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
			var pTickerEvents = eventCalendar.Get(p.Ticker);
			var diagnostic = RiskDiagnosticBuilder.Build(
				legs: diagLegs,
				spot: spotForDiag,
				asOf: ctx.Now,
				ivResolver: sym => mergedQuotes.TryGetValue(sym, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
					? q.ImpliedVolatility.Value
					: 0.40m,
				trend: trend,
				quotes: mergedQuotes,
				sentiment: diagnosticSentiment,
				events: pTickerEvents,
				isTheoretical: _backtestMode);

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
				thetaPerDayPerContract: p.ThetaPerDayPerContract,
				finalScore: p.FinalScore);

			var probe = RiskDiagnosticProbeBuilder.Build(
				legs: diagLegs,
				spot: spotForDiag,
				asOf: ctx.Now,
				ivResolver: sym => mergedQuotes.TryGetValue(sym, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
					? q.ImpliedVolatility.Value
					: 0.40m,
				quotes: mergedQuotes,
				opener: openerScore,
				sentimentScore: sentimentScore);

			annotated.Add(p with { Diagnostic = diagnostic with { Probe = probe } });
		}
		return annotated;
	}

	internal static IReadOnlyList<OpenProposal> RankForOutput(IEnumerable<OpenProposal> proposals)
	{
		var list = proposals.ToList();
		if (list.Count <= 1) return list;

		return list
			.OrderByDescending(p => p.FinalScore ?? CandidateScorer.ComputeFinalScore(p.BiasAdjustedScore, p.ThetaPerDayPerContract, p.CapitalAtRiskPerContract))
			.ThenByDescending(p => p.BiasAdjustedScore)
			.ThenByDescending(p => p.ThetaPerDayPerContract ?? decimal.MinValue)
			.ThenBy(p => IsCalendarLike(p) ? p.DaysToTarget : int.MaxValue)
			.ToList();
	}

	private static OpenProposal ApplyCashSizing(OpenProposal p, decimal freeCash, decimal accountValue, OpenerConfig cfg, decimal bias)
	{
		if (p.CapitalAtRiskPerContract <= 0m)
			return p with { Rationale = CandidateScorer.BuildRationale(p, bias, cfg) };

		// Three caps on qty:
		//   1) free cash: can't risk more than we have
		//   2) hard contract count (MaxQtyPerProposal): protects against degenerate ultra-cheap structures
		//   3) per-trade risk budget (MaxRiskPctPerProposal × accountValue): prevents a single position from
		//      dominating account drawdown. Without this, a $200/ct loss on a $25k account could fill 50
		//      contracts (= 40% of equity at risk on one trade).
		var cashCap = (int)Math.Floor(freeCash / p.CapitalAtRiskPerContract);
		// Risk budget = the smaller of (account-pct cap) and (absolute dollar cap). The dollar cap
		// stops compounding from inflating position sizes into seven-figure single-trade bets after
		// a few good months. Zero on the dollar cap means "no absolute limit" (pct-only).
		var pctBudget = accountValue * cfg.MaxRiskPctPerProposal;
		var dollarBudget = cfg.MaxDollarRiskPerProposal > 0m ? cfg.MaxDollarRiskPerProposal : decimal.MaxValue;
		var riskBudget = Math.Min(pctBudget, dollarBudget);
		var riskCap = riskBudget > 0m ? (int)Math.Floor(riskBudget / p.CapitalAtRiskPerContract) : 0;
		var maxQty = Math.Min(Math.Min(cashCap, riskCap), cfg.MaxQtyPerProposal);

		OpenProposal updated;
		if (maxQty >= 1)
		{
			updated = p with { Qty = maxQty, Legs = ScaleLegs(p.Legs, maxQty) };
		}
		else
		{
			var binding = cashCap < 1 ? "cash" : (riskCap < 1 ? "risk-budget" : "qty-cap");
			var budgetSource = riskBudget == dollarBudget && cfg.MaxDollarRiskPerProposal > 0m
				? $"${cfg.MaxDollarRiskPerProposal:F0} dollar cap"
				: $"{cfg.MaxRiskPctPerProposal:P0} of ${accountValue:F0}";
			updated = p with
			{
				Qty = 0,
				CashReserveBlocked = true,
				CashReserveDetail = binding == "risk-budget"
					? $"risk budget ${riskBudget:F0} ({budgetSource}) below ${p.CapitalAtRiskPerContract:F0} per contract"
					: $"free ${freeCash:F0}, requires ${p.CapitalAtRiskPerContract:F0} per contract"
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

	private static bool IsCalendarLike(OpenProposal proposal) => proposal.StructureKind is OpenStructureKind.LongCalendar or OpenStructureKind.DoubleCalendar or OpenStructureKind.LongDiagonal or OpenStructureKind.DoubleDiagonal;

	/// <summary>Returns true when the symbol exists and has both a non-null bid and a positive ask.
	/// Mirrors <c>CandidateScorer.TryLiveBidAsk</c>'s acceptance criteria so Phase B's "needs refresh"
	/// decision matches what the scorer will actually accept downstream.</summary>
	private static bool HasUsableQuote(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol)
	{
		if (!quotes.TryGetValue(symbol, out var q)) return false;
		return q.Bid.HasValue && q.Ask.HasValue && q.Ask.Value > 0m;
	}

	/// <summary>Whipsaw penalty factor: when 3-day realized vol is well above 30-day, both sides of
	/// credit-spread space get crushed by counter-trend reversals. Returns 1 (no penalty) when weight
	/// is 0, when either input is missing, or when the ratio is at/below 1.5. Above that, penalty
	/// scales linearly: factor = max(0, 1 − weight × (ratio − 1.5)).</summary>
	private static decimal ComputeWhipsawFactor(decimal shortHv, decimal longHv, decimal weight)
	{
		if (weight <= 0m || shortHv <= 0m || longHv <= 0m) return 1m;
		var ratio = shortHv / longHv;
		if (ratio <= 1.5m) return 1m;
		var excess = ratio - 1.5m;
		return Math.Max(0m, 1m - weight * excess);
	}

	private static bool IsCreditStructure(OpenStructureKind kind) =>
		kind is OpenStructureKind.ShortPutVertical
			 or OpenStructureKind.ShortCallVertical
			 or OpenStructureKind.IronCondor
			 or OpenStructureKind.IronButterfly;

	/// <summary>Widest DteMax across every enabled structure. Used to size the bootstrap-probe horizon so
	/// the backtest discovers every expiry the enumerators could legitimately propose.</summary>
	private static int MaxDteAcrossStructures(OpenerConfig cfg)
	{
		var s = cfg.Structures;
		int max = 7;
		if (s.LongCalendar.Enabled) max = Math.Max(max, Math.Max(s.LongCalendar.ShortDteMax, s.LongCalendar.LongDteMax));
		if (s.DoubleCalendar.Enabled) max = Math.Max(max, Math.Max(s.DoubleCalendar.ShortDteMax, s.DoubleCalendar.LongDteMax));
		if (s.LongDiagonal.Enabled) max = Math.Max(max, Math.Max(s.LongDiagonal.ShortDteMax, s.LongDiagonal.LongDteMax));
		if (s.DoubleDiagonal.Enabled) max = Math.Max(max, Math.Max(s.DoubleDiagonal.ShortDteMax, s.DoubleDiagonal.LongDteMax));
		if (s.IronButterfly.Enabled) max = Math.Max(max, s.IronButterfly.DteMax);
		if (s.IronCondor.Enabled) max = Math.Max(max, s.IronCondor.DteMax);
		if (s.ShortVertical.Enabled) max = Math.Max(max, s.ShortVertical.DteMax);
		if (s.LongCallPut.Enabled) max = Math.Max(max, s.LongCallPut.DteMax);
		return max;
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

	/// <summary>Fetches the configured-lookback minute bars for the strategy ticker (mapped to its
	/// chart-source ticker if the config specifies one) and runs the indicator. Returns null on any
	/// failure path (no bars, fetcher error, insufficient bars) so the caller falls back to
	/// macro-only bias. Logs warnings rather than throwing — an intraday outage must never silence
	/// the rest of the scan.</summary>
	private async Task<IntradayBias?> ComputeIntradayBiasAsync(string strategyTicker, decimal? prevClose, OpenerIntradayTapeConfig tapeCfg, CancellationToken cancellation)
	{
		var cache = GetOrCreateIntradayCache();
		if (cache == null) return null;

		// Strategy ticker IS the cache key. The fetcher handles source-mapping internally — SPXW
		// transparently merges SPX RTH + SPY pre-market in SPX scale, for example — and writes to
		// data/intraday/<strategyTicker>/, keeping the on-disk layout aligned with the daily-close
		// cache at data/history/<strategyTicker>.csv.
		var chartTicker = strategyTicker;

		var interval = ParseBarInterval(tapeCfg.BarIntervalCode);
		var toUtc = DateTimeOffset.UtcNow;
		var fromUtc = toUtc.AddMinutes(-Math.Max(60, tapeCfg.LookbackMinutes));

		IReadOnlyList<MinuteBar> bars;
		try
		{
			bars = await cache.GetBarsAsync(chartTicker, fromUtc, toUtc, interval, tapeCfg.IncludeExtended, cancellation);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Console.WriteLine($"Intraday tape: bar fetch failed for {chartTicker}: {ex.Message}");
			return null;
		}

		if (bars.Count == 0) return null;

		var indicatorCfg = new IntradayTapeConfig
		{
			MinBars = tapeCfg.MinBars,
			GapWeight = tapeCfg.GapWeight,
			OpenToNowWeight = tapeCfg.OpenToNowWeight,
			VwapDeviationWeight = tapeCfg.VwapDeviationWeight,
		};
		return IntradayTapeIndicators.Compute(bars, prevClose, toUtc, indicatorCfg);
	}

	private IntradayBarCache? GetOrCreateIntradayCache()
	{
		if (_backtestMode) return null;
		if (_intradayCache != null) return _intradayCache;
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null) return null;
		_intradayCache = new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig));
		return _intradayCache;
	}

	private static ApiConfig? TryLoadApiConfig()
	{
		try
		{
			var path = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(path)) return null;
			var cfg = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(path));
			if (cfg == null || cfg.Headers.Count == 0) return null;
			return cfg;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Intraday tape: failed to load api-config.json: {ex.Message}");
			return null;
		}
	}

	private static BarInterval ParseBarInterval(string code) => code?.ToLowerInvariant() switch
	{
		"m1" => BarInterval.M1,
		"m5" => BarInterval.M5,
		"m15" => BarInterval.M15,
		"m30" => BarInterval.M30,
		"h1" => BarInterval.H1,
		"d1" => BarInterval.D1,
		_ => BarInterval.M1,
	};

	/// <summary>Appends one JSONL line per per-ticker bias-blend attempt to
	/// <c>data/ai-bias-shadow.jsonl</c>. Records both the macro-only and blended biases so the user
	/// can validate the intraday signal offline before committing to a non-trivial weight. Records
	/// null intraday entries too — those flag scan ticks where the indicator couldn't fire (cache
	/// miss, pre-open, too few bars). Failures here are swallowed: a log-write hiccup must never
	/// silence the rest of the scan.</summary>
	private static void WriteBiasShadowLog(string ticker, decimal macroBias, IntradayBias? intradayBias, decimal weight, decimal blended, decimal? vixTermScore, decimal vixTermWeight)
	{
		try
		{
			var path = Program.ResolvePath("data/ai-bias-shadow.jsonl");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
			string intradayJson = intradayBias == null
				? "null"
				: $"{{\"score\":{intradayBias.Score:F4},\"gap\":{intradayBias.GapScore:F4},\"openToNow\":{intradayBias.OpenToNowScore:F4},\"vwapDev\":{intradayBias.VwapDeviationScore:F4},\"barCount\":{intradayBias.BarCount}}}";
			string vixJson = vixTermScore.HasValue
				? $"{{\"score\":{vixTermScore.Value:F4},\"weight\":{vixTermWeight:F4}}}"
				: "null";
			var line = $"{{\"ts\":\"{ts}\",\"ticker\":\"{ticker}\",\"macroBias\":{macroBias:F4},\"vixTerm\":{vixJson},\"intraday\":{intradayJson},\"weight\":{weight:F4},\"blended\":{blended:F4}}}\n";
			File.AppendAllText(path, line);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Intraday tape: shadow log write failed: {ex.Message}");
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

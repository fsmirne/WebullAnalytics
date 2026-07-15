using System.Text.Json;
using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using WebullAnalytics.Sentiment;

namespace WebullAnalytics.AI;

/// <summary>
/// Orchestrates the opener pipeline: enumerate skeletons → phase-3 quote fetch → score → rank per ticker.
/// Produces a flat, ranked list of OpenProposal across all configured tickers, capped at topNPerTicker each.
/// </summary>
internal sealed class OpenCandidateEvaluator
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private readonly AIConfig _config;
	private readonly IQuoteSource _quotes;
	private readonly string _pricingMode;
	private readonly HistoricalPriceCache _priceCache;
	private readonly bool _backtestMode;
	private readonly bool _enableChainSnapshot;
	private readonly bool _showAllScores;
	private readonly IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? _dividendsByRoot;
	private IntradayBarCache? _intradayCache;
	// Far-expiry bid/ask from the daily chain sweep (keyed by OCC symbol). The sweep only runs once per
	// ET day; on subsequent ticks within the same session the registry returns candidateExps.Count==0 and
	// EnsureDailySnapshotAsync returns null. Caching here lets every tick merge the swept quotes back into
	// bootstrapOptions so the scorer can price far-leg candidates without re-hitting the network.
	private Dictionary<string, OptionContractQuote>? _snapshotQuoteCache;

	/// <param name="priceCache">Shared close-price cache. Pass the runner-owned instance so the in-memory
	/// per-ticker dictionary survives across ticks/steps instead of being rebuilt (and the CSV re-read) each call.</param>
	/// <param name="backtestMode">When true, skips network calls to <see cref="EventCalendarLoader"/> and
	/// <see cref="RiskDiagnostics.TrendFetcher"/>. Both fetch current Yahoo state regardless of <c>asOf</c>,
	/// which introduces lookahead bias in backtests and accounts for most of the per-step wall time.</param>
	/// <param name="enableChainSnapshot">Live-only: when true, the opener takes/reuses the daily chain
	/// snapshot (a once-per-day near-money bid/ask sweep persisted in the derivative registry) to build the
	/// real strike ladder. Off for the backtest (which sources real strikes from captured bars) and for unit
	/// tests (so they neither hit the network nor touch the shared registry). Set by the live watch/scan paths.</param>
	/// <param name="dividendsByRoot">Backtest-only per-root historical dividend schedules (from
	/// <see cref="WebullAnalytics.AI.Backtest.HistoricalDividendCache"/>). When supplied, the scorer's
	/// dividend adjustment uses the TRUE next ex-date as-of each eval date (drawn from this history)
	/// instead of the event-cache's single stale forward-projected date — so backtest candidate scoring,
	/// and therefore selection/fills, matches the dividend-aware live path and the quote source. Null in
	/// live mode (the event-cache's next ex-date IS the real upcoming one there).</param>
	/// <param name="showAllScores">Scan-only gate override (`wa ai scan --all`): emit the top-N ranked
	/// proposals even when their FinalScore is at or below <c>minScoreToOpen</c>. This is a real override, not a
	/// display filter — the emitted proposals are ordinary ranked picks, so with <c>--submit</c> the auto-executor
	/// will place the top one even if it scored below the threshold. Only the scan paths (live and theoretical)
	/// pass it; watch and the backtest runner leave it false.</param>
	public OpenCandidateEvaluator(AIConfig config, IQuoteSource quotes, string pricingMode = SuggestionPricing.Mid, HistoricalPriceCache? priceCache = null, bool backtestMode = false, bool enableChainSnapshot = false, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividendsByRoot = null, bool showAllScores = false)
	{
		_config = config;
		_quotes = quotes;
		_pricingMode = SuggestionPricing.Normalize(pricingMode);
		_priceCache = priceCache ?? new HistoricalPriceCache();
		_backtestMode = backtestMode;
		_enableChainSnapshot = enableChainSnapshot && !backtestMode;
		_showAllScores = showAllScores;
		_dividendsByRoot = dividendsByRoot;
	}

	/// <summary>Replaces a ticker's event-cache dividend fields with the TRUE next ex-dividend as-of
	/// <paramref name="asOf"/>, taken from the supplied historical schedule (<see cref="_dividendsByRoot"/>).
	/// For the ≤45-DTE structures the opener enumerates, the only ex-date that can fall inside any leg's
	/// life is the first one strictly after the eval date — so a single corrected (date, amount) reproduces
	/// the exact dividend the quote source windows per leg, while reusing the existing single-dividend
	/// scorer plumbing. No-op (returns <paramref name="ev"/> unchanged) when no schedule is supplied for the
	/// ticker — i.e. always in live mode, and for index roots in backtest. Earnings fields are preserved.</summary>
	private TickerEvents? ApplyHistoricalDividend(TickerEvents? ev, string ticker, DateTime asOf)
	{
		if (_dividendsByRoot == null || !_dividendsByRoot.TryGetValue(ticker, out var schedule) || schedule.Count == 0)
			return ev;
		var next = schedule.Where(d => d.ExDate.Date > asOf.Date).OrderBy(d => d.ExDate).FirstOrDefault();
		DateTime? exDate = next != null ? next.ExDate : null;
		decimal? amount = next?.Amount;
		return ev != null
			? ev with { NextExDividendDate = exDate, DividendAmount = amount }
			: (exDate != null ? new TickerEvents(ticker.ToUpperInvariant(), null, null, exDate, amount) : null);
	}

	/// <summary>Per-ticker underlying spots seen by the most recent <see cref="EvaluateAsync"/> call — the
	/// context's spots merged with the phase-A0 bootstrap probe. Lets the live watch heartbeat print a spot on
	/// flat ticks, where the management-side quote fetch has no position legs and therefore no underlying price.
	/// Empty until the first call completes phase A0; stale (last tick's value) if the opener is disabled.</summary>
	public IReadOnlyDictionary<string, decimal> LastUnderlyings { get; private set; } = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

	/// <summary>Per-ticker regime inputs (macro/VIX/intraday/move-sign) captured on the last
	/// <see cref="EvaluateAsync"/> call. Read-only side channel for <c>wa analyze regime</c>; nothing in the
	/// scoring path reads it back.</summary>
	public IReadOnlyDictionary<string, RegimeAnalyzer.RegimeComponents> LastRegimeComponents { get; private set; } = new Dictionary<string, RegimeAnalyzer.RegimeComponents>(StringComparer.OrdinalIgnoreCase);

	/// <summary>The option book the opener actually priced its candidates from on the most recent
	/// <see cref="EvaluateAsync"/> call — <c>ctx.Quotes</c> overlaid with the bootstrap/snapshot chain and any
	/// Phase-B refetch. The live quote-integrity guard must inspect THIS, not the management-side ctx book: on a
	/// no-position scan the ctx fetch spans only the same-day expiry window, so a ticker whose earliest expiry
	/// isn't today (e.g. GME, whose weeklies start Friday) leaves ctx.Quotes empty of every traded leg. The guard
	/// would then find no timestamped two-sided quote and mis-report "staleness unverifiable" while silently
	/// skipping the torn-NBBO check on legs it can't see. Reset to empty at the top of each call and populated
	/// once Phase B finalizes the priced book; stays empty when the opener enumerates nothing.</summary>
	public IReadOnlyDictionary<string, OptionContractQuote> LastPricedQuotes { get; private set; } = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

	public async Task<IReadOnlyList<OpenProposal>> EvaluateAsync(EvaluationContext ctx, CancellationToken cancellation, QuoteOverrides quoteOverrides = default)
	{
		var cfg = _config.Opener;
		var debug = string.Equals(_config.LogLevel, "debug", StringComparison.OrdinalIgnoreCase);
		if (!cfg.Enabled)
		{
			if (debug) Console.Error.WriteLine($"[debug] {_config.Ticker} opener disabled (opener.enabled=false); no candidates enumerated.");
			return Array.Empty<OpenProposal>();
		}

		var tickerSet = _config.TickerSet();
		var output = new List<OpenProposal>();
		// Reset so the live quote guard never inspects a prior tick's book; repopulated once Phase B finalizes it.
		LastPricedQuotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

		// Phase A0: bootstrap spots + chains for tickers missing from ctx.UnderlyingPrices.
		// The live-quote clients return the full chain plus the underlying spot for any OCC symbol,
		// so one placeholder per root suffices. Without this, tickers without open positions have
		// no spot in ctx and we enumerate nothing.
		var bootstrapSpots = new Dictionary<string, decimal>(ctx.UnderlyingPrices, StringComparer.OrdinalIgnoreCase);
		var bootstrapOptions = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		// Probe when the ticker is missing EITHER its spot OR its option chain. The chain check matters in the
		// backtest: the spot is always present (bar.Open), so a spot-only gate skipped the probe — and with it
		// the captured-chain expansion — leaving the enumerator on the uniform strikeStep grid (the source of
		// phantom $1 strikes). Live is unchanged: no-position ticks miss the spot (probe as before), and
		// with-position ticks already carry the chain via the position legs (no extra probe).
		var spotMissing = !bootstrapSpots.TryGetValue(_config.Ticker, out var bootSpot) || bootSpot <= 0m;
		var chainMissing = ChainMissesNeededExpiries(_config.Ticker, cfg, ctx);
		var missingTickers = (spotMissing || chainMissing) ? new List<string> { _config.Ticker } : new List<string>();
		if (missingTickers.Count > 0)
		{
			// Probe one placeholder OCC symbol per (ticker, candidate-expiry). Webull's live quote source
			// returns the full chain for any one symbol — but the BacktestQuoteSource only prices what's
			// asked AND expands each $1 probe into that expiry's captured chain, so we restrict the probe to
			// expiries that fall in an enabled structure's DTE band. Probing every day 0..maxDte made the
			// backtest expand (and price) the full captured chain for ~40 expiries per tick on dense roots
			// like SPXW — most of them outside any band and never enumerated. Live is unaffected: one
			// placeholder still returns the whole chain.
			var maxDte = MaxDteAcrossStructures(cfg);
			var dteRanges = DteRangesForStructures(cfg);
			bool InAnyBand(DateTime exp)
			{
				var d = (exp.Date - ctx.Now.Date).Days;
				return d >= 0 && dteRanges.Any(r => d >= r.Min && d <= r.Max);
			}
			var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in missingTickers)
			{
				foreach (var exp in OpenerExpiryHelpers.NextExpiriesForTicker(t, ctx.Now, 0, maxDte).Where(InAnyBand))
					placeholders.Add(MatchKeys.OccSymbol(t, exp, 1m, "C"));
				// Calendar/diagonal long legs reach into monthly DTE; probe those 3rd-Fridays too.
				foreach (var exp in OpenerExpiryHelpers.MonthlyExpiriesInRange(ctx.Now, 0, maxDte).Where(InAnyBand))
					placeholders.Add(MatchKeys.OccSymbol(t, exp, 1m, "C"));
				// Guard against an empty cadence (e.g. minDte/maxDte yielding no days) — keep at least one
				// probe so the underlying spot still surfaces.
				if (placeholders.All(s => !s.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
					placeholders.Add(MatchKeys.OccSymbol(t, ctx.Now.Date.AddDays(7), 1m, "C"));
			}
			var boot = await _quotes.GetQuotesAsync(ctx.Now, placeholders, tickerSet, cancellation, quoteOverrides);
			foreach (var (k, v) in boot.Underlyings) bootstrapSpots[k] = v;
			foreach (var (k, v) in boot.Options) bootstrapOptions[k] = v;
		}
		LastUnderlyings = bootstrapSpots;

		// Index the chain expirations available per ticker so the enumerator can use real chain dates
		// (which carry holiday adjustments — e.g. Juneteenth shifts the June monthly to Thursday) instead
		// of computed 3rd-Friday/Friday dates that Webull's chain doesn't actually return. Without this,
		// calendar/diagonal long legs generate OCC symbols Webull never sends back and silently drop.
		// Only walk Keys when the dictionary actually enumerates — some test fakes (e.g. always-true
		// ContainsKey) expose empty Keys; those callers fall through to the OpenerExpiryHelpers path.
		var availableByTicker = new Dictionary<string, HashSet<DateTime>>(StringComparer.OrdinalIgnoreCase);
		availableByTicker[_config.Ticker] = new HashSet<DateTime>();
		IndexExpirations(bootstrapOptions.Keys, availableByTicker);
		IndexExpirations(ctx.Quotes.Keys, availableByTicker);

		// Daily chain snapshot. The chain inlines bid/ask only for the front expiry, so the future-expiry
		// liquidity that diagonals/calendars depend on is invisible to a single probe (the dead near-the-
		// money strikes look identical to the tradeable ones). Once per ET day, sweep bid/ask across the
		// near-money strikes of the candidate expiries to learn which listed strikes actually trade, and
		// persist it in the derivative registry; the overlay below feeds today's tradeable strikes to the
		// StrikeLadder so it snaps to the real grid. Reused every tick thereafter — the sweep itself is a
		// no-op once today's snapshot exists.
		if (_enableChainSnapshot)
		{
			var chainOccs = bootstrapOptions.Keys.Concat(ctx.Quotes.Keys);
			var snapshotQuotes = await EnsureDailySnapshotAsync(ctx, bootstrapSpots, availableByTicker, chainOccs, cfg, debug, cancellation, quoteOverrides);
			if (snapshotQuotes != null)
			{
				// First sweep of the day: cache the far-expiry quotes for reuse on subsequent ticks.
				_snapshotQuoteCache ??= new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
				foreach (var (occ, q) in snapshotQuotes)
					if (q is { Bid: > 0m, Ask: > 0m })
						_snapshotQuoteCache[occ] = q;
			}
			// Merge cached far-expiry bid/ask into bootstrapOptions so the scorer can price far-leg
			// candidates. Skips any symbol the live chain fetch already quoted (don't overwrite fresher data).
			if (_snapshotQuoteCache != null)
				foreach (var (occ, q) in _snapshotQuoteCache)
					if (!bootstrapOptions.TryGetValue(occ, out var existing) || existing.Bid is null or <= 0m || existing.Ask is null or <= 0m)
						bootstrapOptions[occ] = q;
			foreach (var (occ, q) in BuildSnapshotOverlay(_config.Ticker, ctx.Now, bootstrapOptions, ctx.Quotes))
				bootstrapOptions[occ] = q;
		}

		// Phase A: enumerate across all tickers. Feed the enumerator the merged chain quotes so the
		// delta-band filter on strike picks uses each strike's live IV rather than the static
		// cfg.Indicators.IvDefaultPct fallback. The chain ImpliedVolatility carries the actual smile
		// the market is pricing — for SPXW in particular, 0DTE wing IV runs 50–80% above ATM and the
		// static default (typically 18%) lands strike picks well outside the configured delta band.
		IReadOnlyDictionary<string, OptionContractQuote> ivLookupQuotes = bootstrapOptions.Count > 0
			? new OverlayQuoteDictionary(ctx.Quotes, bootstrapOptions)
			: ctx.Quotes;
		var allSkeletons = new List<CandidateSkeleton>();
		foreach (var ticker in new[] { _config.Ticker })
		{
			if (!bootstrapSpots.TryGetValue(ticker, out var spot) || spot <= 0m)
			{
				if (debug) Console.Error.WriteLine($"[debug] {ticker} no live spot resolved (Webull returned no underlying price/chain); 0 candidates enumerated.");
				continue;
			}
			var available = availableByTicker[ticker];
			if (debug)
			{
				// Dump the chain the enumerator actually sees per expiry: how many call strikes are listed, how
				// many carry a usable bid/ask, and the near-ATM strikes (reveals the real grid spacing). A
				// scored=0 run is then unambiguous: listed=0 → chain didn't reach Phase A; quoted=0 with
				// listed>0 → strikes exist but don't quote; near-ATM spacing shows whether strikeStep matters.
				var byExp = new SortedDictionary<DateTime, (int listed, int quoted, int oi, SortedSet<decimal> nearTradeable)>();
				foreach (var kv in ivLookupQuotes)
				{
					var p = ParsingHelpers.ParseOptionSymbol(kv.Key);
					if (p == null || p.CallPut != "C" || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
					if (!byExp.TryGetValue(p.ExpiryDate.Date, out var agg)) byExp[p.ExpiryDate.Date] = agg = (0, 0, 0, new SortedSet<decimal>());
					var quoted = kv.Value.Bid is > 0m && kv.Value.Ask is > 0m;
					var tradeable = quoted || kv.Value.OpenInterest is > 0 || kv.Value.Volume is > 0;
					if (tradeable && Math.Abs(p.Strike - spot) <= 12m) agg.nearTradeable.Add(p.Strike);
					byExp[p.ExpiryDate.Date] = (agg.listed + 1, agg.quoted + (quoted ? 1 : 0), agg.oi + (kv.Value.OpenInterest is > 0 || kv.Value.Volume is > 0 ? 1 : 0), agg.nearTradeable);
				}
				foreach (var kv in byExp.Where(e => available.Contains(e.Key)).Take(6))
					Console.Error.WriteLine($"[debug] {ticker} chain {kv.Key:yyyy-MM-dd} calls: spot={spot:F2} listed={kv.Value.listed} quoted={kv.Value.quoted} tradeable(oi/vol)={kv.Value.oi} nearATM-tradeable=[{string.Join(",", kv.Value.nearTradeable)}]");
			}
			allSkeletons.AddRange(CandidateEnumerator.Enumerate(ticker, spot, ctx.Now, cfg, available.Count > 0 ? available : null, ivLookupQuotes));
		}
		if (allSkeletons.Count == 0)
		{
			if (debug) Console.Error.WriteLine($"[debug] {_config.Ticker} enumerator produced 0 candidate skeletons (available expiries={(availableByTicker.TryGetValue(_config.Ticker, out var av) ? av.Count : 0)}); check live chain availability and the structures' DTE bands.");
			return Array.Empty<OpenProposal>();
		}

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
			var extra = await _quotes.GetQuotesAsync(ctx.Now, neededSymbols, tickerSet, cancellation, quoteOverrides);
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

		// Expose the finalized priced book for the live quote-integrity guard (see LastPricedQuotes).
		LastPricedQuotes = mergedQuotes;

		// Snapshot-tradeable strikes (live only): the daily chain snapshot is the authoritative liquidity
		// signal. A leg the snapshot confirmed tradeable (real bid/ask) bypasses the scorer's open-interest
		// gate — thin index roots like XSP quote far-dated strikes with little/no open interest, and the
		// snapshot already vetted them. The liquidity *factor* still uses real OI, so they score honestly.
		var snapshotTradeable = _enableChainSnapshot
			? new HashSet<string>(DerivativeIdRegistry.TradeableOccs(_config.Ticker, SnapshotDate(ctx.Now)), StringComparer.OrdinalIgnoreCase)
			: null;

		// Phase C: score per ticker.
		var reserve = CashReserveHelper.ComputeReserve(_config.CashReserve.Mode, _config.CashReserve.Value, ctx.AccountValue);
		var freeCash = Math.Max(0m, ctx.AccountCash - reserve);
		// Fetch the contrarian Fear & Greed regime overlay once per scan. Same value applies to every
		// ticker — F&G is a market-wide sentiment composite, not per-ticker. Null on outage; the scorer
		// treats null as "skip the sentiment factor". In backtest we pass cacheOnly=true so the network
		// branch is bypassed — CNN's endpoint reads the current score, which would be lookahead in a
		// historical replay. The on-disk cache (data/sentiment-cache/<date>.json) is date-keyed and
		// only written for settled dates, so reading it in backtest is lookahead-safe by construction
		// and gives T+1 replays access to the same value live saw the day before.
		// Display is decoupled from scoring: fetch the snapshot and pass the score unconditionally so the
		// reading is available to the diagnostic even when the sentiment weight is 0. ComputeSentimentFactor
		// gates on weight (returns null at weight <= 0), so a zero weight leaves scoring untouched — the
		// reading is informational only. cacheOnly still applies in backtest, keeping the read lookahead-safe.
		var sentimentSnapshot = await FearGreedClient.FetchAsync(ctx.Now, cancellation, cacheOnly: _backtestMode);
		decimal? sentimentScore = sentimentSnapshot?.Score;

		var historicalVolByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var shortHorizonHvByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		// Load historical closes for the vol-fit and whipsaw factors. Bias-calibration (moveSign),
		// VIX term structure, and intraday tape are now computed inside ComputeRegimeComponentsAsync
		// so they're identical whether called from the scanner or wa analyze risk.
		var needCloses = cfg.Weights.VolatilityFit > 0m || cfg.Weights.Whipsaw > 0m;
		if (needCloses)
		{
			var lookback = Math.Max(cfg.VolatilityLookbackDays + 1, 4);
			foreach (var ticker in new[] { _config.Ticker })
			{
				var closes = await _priceCache.GetRecentClosesAsync(ticker, lookback, ctx.Now, cancellation);
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
			}
		}

		// Scheduled-catalyst calendar (earnings + ex-div) per ticker. Built once per scan; resolved
		// per-ticker inside the per-ticker loop. The loader returns EventCalendar.Empty when the
		// feature is disabled or when Yahoo is unreachable — the scorer treats null events as
		// "no veto" so a calendar outage never silences the opener. In backtest we pass cacheOnly=true
		// so the Yahoo branch is bypassed (quoteSummary returns current-state events regardless of
		// asOf; calling it would leak future earnings/ex-div into a historical replay). The cache's
		// symmetric TTL check in TryReadCache rejects entries fetched too far from asOf in either
		// direction, so the on-disk cache stays lookahead-safe for distant historical asOfs too.
		var eventCalendar = await EventCalendarLoader.LoadAsync(new[] { _config.Ticker }, cfg.Indicators.Events, ctx.Now, cancellation, cacheOnly: _backtestMode);

		var lastRegimeComponents = new Dictionary<string, RegimeAnalyzer.RegimeComponents>(StringComparer.OrdinalIgnoreCase);

		foreach (var tickerGroup in allSkeletons.GroupBy(s => s.Ticker))
		{
			if (!bootstrapSpots.TryGetValue(tickerGroup.Key, out var spot) || spot <= 0m) continue;
			ctx.TechnicalSignals.TryGetValue(tickerGroup.Key, out var biasSignal);
			var macroBias = biasSignal?.Score ?? 0m;

			// Compute the complete per-ticker regime components — VIX term structure, bias-calibration
			// moveSign, and intraday tape — via the shared static pipeline that wa analyze risk also calls.
			// This is the single source of truth; having one code path guarantees identical bias values
			// across the scanner and every inspector command.
			var regimeComponents = await ComputeRegimeComponentsAsync(tickerGroup.Key, cfg, macroBias, ctx.Now, _priceCache, GetOrCreateIntradayCache(), includeCurrentBar: !_backtestMode, cancellation);
			decimal BiasForDte(int dteCalendar) => RegimeAnalyzer.BlendBias(regimeComponents, cfg, dteCalendar);
			lastRegimeComponents[tickerGroup.Key] = regimeComponents;

			historicalVolByTicker.TryGetValue(tickerGroup.Key, out var historicalVolAnnual);
			shortHorizonHvByTicker.TryGetValue(tickerGroup.Key, out var shortHorizonHv);
			// Whipsaw penalty: when 3-day realized vol is well above 30-day, both put- and call-credit
			// spreads get punished by counter-trend reversals (e.g. April 2025 SPX +$492 inside the crash
			// week wiped every bearish credit). Computed once per ticker; applied per-proposal to credit
			// structures only.
			var whipsawFactor = ComputeWhipsawFactor(shortHorizonHv, historicalVolAnnual, cfg.Weights.Whipsaw);
			var tickerEvents = ApplyHistoricalDividend(eventCalendar.Get(tickerGroup.Key), tickerGroup.Key, ctx.Now);

			var shortVerticalRejects = debug
				? new Dictionary<CandidateScorer.ShortVerticalRejectReason, int>()
				: null;
			// Every skeleton that Score rejects (null) — counted per structure so debug mode is never silent.
			// Without this, a ticker whose strikeStep grid doesn't line up with the live chain (so every leg
			// comes back unpriced and every candidate scores null) produces an empty scoredByStructure and
			// thus zero debug lines, which reads as "debug isn't working" when the opener actually ran.
			var rejectedByStructure = debug ? new Dictionary<OpenStructureKind, int>() : null;
			var debugRejectLinesLeft = 25;

			var scoredByStructure = new Dictionary<OpenStructureKind, List<OpenProposal>>();
			// Two separate time anchors:
			// asOf (= ctx.Now) — how much time the trade has left as of RIGHT NOW: DTE, shortYears for the
			// EV/POP scenario grid, daysToTarget for the raw-score divisor. Uses the actual wall-clock time so
			// that a Sunday scan says "5 days to Friday expiry," not "7 calendar days from Friday close."
			// ivSolveAsOf — when the quotes were actually struck: ObservationInstant() = DateTime.Now during
			// RTH, the previous session's close off-hours/weekends. The long-leg IV is back-solved by
			// inverting its mid against this anchor so the solved IV matches what the market priced at close,
			// regardless of when the scan runs. During RTH both anchors equal DateTime.Now — no difference.
			var ivSolveAsOf = _backtestMode ? ctx.Now : OptionMath.ObservationInstant();

			if (debug)
			{
				// Debug path stays serial: the per-skeleton rejection counters and Console.Error writes
				// need ordered, single-threaded access. Scoring volume in debug mode is the cost of getting
				// the diagnostic output, which is the whole point of running with --debug.
				foreach (var skel in tickerGroup)
				{
					var skelBias = BiasForDte((skel.TargetExpiry.Date - ctx.Now.Date).Days);
					var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, skelBias, cfg, historicalVolAnnual > 0m ? historicalVolAnnual : null, _pricingMode, sentimentScore: sentimentScore, events: tickerEvents, snapshotTradeable: snapshotTradeable, ivSolveAsOf: ivSolveAsOf, vetoNow: ctx.Now);
					if (p != null && whipsawFactor < 1m && IsCreditStructure(skel.StructureKind))
					{
						var adjusted = CandidateScorer.ApplyFactor(p.FinalScore ?? 0m, whipsawFactor);
						p = p with { FinalScore = adjusted };
					}
					if (p == null)
					{
						rejectedByStructure![skel.StructureKind] = rejectedByStructure.GetValueOrDefault(skel.StructureKind) + 1;
						if (debugRejectLinesLeft-- > 0)
						{
							var legDump = string.Join(" ", skel.Legs.Select(l =>
								mergedQuotes.TryGetValue(l.Symbol, out var lq) && lq.Bid is > 0m && lq.Ask is > 0m
									? $"{l.Symbol}={lq.Bid}/{lq.Ask}@oi{lq.OpenInterest?.ToString() ?? "null"}/vol{lq.Volume?.ToString() ?? "null"}"
									: $"{l.Symbol}=NOQUOTE"));
							var liqFails = CandidateScorer.GetLiquidityFailures(skel.Legs, mergedQuotes, cfg.Liquidity, spot, snapshotTradeable);
							Console.Error.WriteLine($"[debug] {skel.Ticker} {skel.StructureKind} dropped legs: {legDump} | liq-gate: {(liqFails.Count > 0 ? string.Join(", ", liqFails) : "pass")}");
						}
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
				//
				// In backtest mode the caller (BacktestRunner) already runs ~ProcessorCount MINUTES in
				// parallel, so fanning scoring out here too is nested parallelism — it oversubscribes the
				// pool and the contention dominates. Cap to 1 (serial) under backtest; the minute-level
				// parallelism is what keeps all cores busy. Live (single evaluation at a time) still fans out.
				var skels = tickerGroup.ToList();
				var results = new OpenProposal?[skels.Count];
				var scoreOpts = new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = _backtestMode ? 1 : -1 };
				Parallel.For(0, skels.Count, scoreOpts, i =>
				{
					var skel = skels[i];
					var skelBias = BiasForDte((skel.TargetExpiry.Date - ctx.Now.Date).Days);
					var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, skelBias, cfg, historicalVolAnnual > 0m ? historicalVolAnnual : null, _pricingMode, sentimentScore: sentimentScore, events: tickerEvents, snapshotTradeable: snapshotTradeable, ivSolveAsOf: ivSolveAsOf, vetoNow: ctx.Now);
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

			if (debug && rejectedByStructure != null && rejectedByStructure.Count > 0)
			{
				var scoredCount = scoredByStructure.Values.Sum(l => l.Count);
				var summary = string.Join(", ", rejectedByStructure.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
				Console.Error.WriteLine($"[debug] {tickerGroup.Key} candidates dropped before scoring (unpriced legs / filtered out): {summary} — scored={scoredCount}. If scored=0, check that indicators.strikeStep matches the live chain's strike grid.");
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
				var asOfEt = TimeZoneInfo.ConvertTime(ctx.Now, NyTz);
				foreach (var kv in scoredByStructure.OrderBy(k => k.Key.ToString()))
				{
					var scores = kv.Value.Select(p => p.FinalScore ?? 0m).ToList();
					var positive = scores.Count(s => s > 0m);
					var best = scores.Count > 0 ? scores.Max() : 0m;
					var worst = scores.Count > 0 ? scores.Min() : 0m;
					// Show the best-scoring candidate's legs+prices so a divergent best (vs live or vs a
					// prior run) can be traced to the exact contract and the price the scorer saw —
					// without this you'd have to re-instrument the scorer or grep ai-proposals.jsonl.
					var bestCandidate = kv.Value.Count > 0
						? kv.Value.Aggregate((a, b) => (a.FinalScore ?? decimal.MinValue) >= (b.FinalScore ?? decimal.MinValue) ? a : b)
						: null;
					Console.Error.WriteLine($"[debug] {asOfEt:yyyy-MM-dd HH:mm:ss} ET {tickerGroup.Key} {kv.Key}: scored={scores.Count} positive={positive} negative={scores.Count - positive} best={best:F6} worst={worst:F6}");
					// Reproduction commands for the best candidate, one per ↪ line (split structures emit two
					// trade-place lines), mirroring the proposal panel. Explicit per-share prices so the analyze
					// line replays exactly what the scorer saw, offline, without re-fetching the live market.
					if (bestCandidate != null)
						foreach (var line in Output.ReproductionCommands.Build(bestCandidate, _pricingMode, explicitAnalyzePrices: true))
							Console.Error.WriteLine($"          ↪ {line}");
				}
			}

			// Per-structure top-N truncation.
			var survivors = new List<OpenProposal>();
			foreach (var list in scoredByStructure.Values)
				survivors.AddRange(RankForOutput(list).Take(cfg.MaxCandidatesPerStructurePerTicker));

			// Apply cash sizing. The rationale's displayed tech tilt must use the same per-candidate
			// (DTE-aware) bias the scorer saw, so recompute it from each proposal's target DTE — the
			// nearest leg expiry, which is the structure's scoring target (short-leg expiry for
			// calendars/diagonals, the shared expiry for everything else).
			for (int i = 0; i < survivors.Count; i++)
				survivors[i] = ApplyCashSizing(survivors[i], freeCash, ctx.AccountValue, cfg, BiasForDte(CalendarDteToTarget(survivors[i], ctx.Now)));

			// Per-ticker top-N. DoubleCalendar/DoubleDiagonal stay as one 4-leg proposal here and render as a
			// unified two-side panel downstream, so each counts as a single suggestion against the cap.
			// Only emit candidates the score itself endorses (FinalScore > MinScoreToOpen). The chain
			// produces a low/negative final when penalties (low POP, narrow BE, whipsaw, etc.) outweigh
			// the raw EV — that's the model saying "do not trade this." The MinScoreToOpen knob raises the
			// bar above zero for users who want only high-conviction trades; default 0 preserves the
			// legacy behavior of emitting any positive-EV trade.
			var minScore = cfg.MinScoreToOpen;
			var ranked = RankForOutput(survivors).ToList();
			// `--all` (scan-only): drop the minScoreToOpen gate so the full top-N is emitted regardless of score.
			// This is a genuine override, not a display filter — the emitted proposals are ordinary ranked picks,
			// so `--all --submit` will place the top one even when it scored below the threshold that normally
			// suppresses it. Every downstream guard (liquidity, dedup, cash, per-day cap) still applies. Without
			// `--all` the gate stands: only candidates the score endorses (FinalScore > minScoreToOpen) are emitted.
			var selected = _showAllScores ? ranked : ranked.Where(p => (p.FinalScore ?? 0m) > minScore);
			foreach (var proposal in selected.Take(cfg.TopNPerTicker))
				output.Add(proposal);

			// Structure-coverage floor (DEBUG/exploration only): an enabled structure whose best candidate
			// ranks below the global top-N (or below MinScoreToOpen) is otherwise invisible — the user
			// enabled it but never sees what it would propose. DoubleCalendar/DoubleDiagonal hit this
			// routinely: they tie up ~2x the capital and pay ~2x the friction of a single calendar/diagonal
			// for a wider-but-flatter tent, so they score well below the singles and never crack the top-N.
			// Under --log-level debug, surface the best positive-EV candidate of each enabled structure not
			// already represented, flagged Informational so it renders for visibility but is NEVER
			// auto-executed (OpenerAutoExecutor filters Informational out). In prod (info/error log levels) a
			// candidate that doesn't clear MinScoreToOpen does not show — exploration is opt-in via debug.
			if (debug)
			{
				var representedKinds = output.Select(p => p.StructureKind).ToHashSet();
				foreach (var kind in scoredByStructure.Keys)
				{
					if (representedKinds.Contains(kind)) continue;
					var best = ranked.FirstOrDefault(p => p.StructureKind == kind && (p.FinalScore ?? 0m) > 0m);
					if (best != null) output.Add(best with { Informational = true });
				}
			}
		}
		LastRegimeComponents = lastRegimeComponents;

		// Risk diagnostic: build one per surviving proposal. Trend fetched once per ticker; sentiment is
		// market-wide so we reuse the snapshot fetched at the top of Phase C (if any). Still hard-skipped
		// in backtest — unlike sentiment and events, the trend fetcher has no on-disk cache and pulls
		// current Yahoo chart state regardless of asOf. There's nothing safe to read, so cacheOnly mode
		// would just return null on every call. The diagnostic builder treats null trend as "unknown"
		// so proposals still score and render; the panel just shows fewer fields in backtest output.
		var trendByTicker = new Dictionary<string, TrendSnapshot?>(StringComparer.OrdinalIgnoreCase);
		if (!_backtestMode)
		{
			foreach (var ticker in output.Select(p => p.Ticker).Distinct(StringComparer.OrdinalIgnoreCase))
				trendByTicker[ticker] = await TrendFetcher.FetchAsync(ticker, ctx.Now, cancellation);
		}
		// The diagnostic panel shows the full SentimentSnapshot (score/rating/Δ1w). We reuse the snapshot
		// fetched once above rather than gating on the scoring value, so the reading renders regardless
		// of the sentiment weight (display decoupled from scoring).
		SentimentSnapshot? diagnosticSentiment = sentimentSnapshot;

		// HV shown in the diagnostic leg-quote line is the underlying's 20-session realized vol — the same
		// vendor-independent metric `wa analyze` displays. Reuse the value already computed for the
		// vol-fit/whipsaw factors; compute it here for any output ticker that lacks one (when both weights
		// are 0, needCloses was false) so the reading appears regardless of scoring weights.
		var diagnosticHvByTicker = new Dictionary<string, decimal>(historicalVolByTicker, StringComparer.OrdinalIgnoreCase);
		foreach (var ticker in output.Select(p => p.Ticker).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (diagnosticHvByTicker.ContainsKey(ticker)) continue;
			var closes = await _priceCache.GetRecentClosesAsync(ticker, Math.Max(cfg.VolatilityLookbackDays + 1, 4), ctx.Now, cancellation);
			var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
			if (hv is > 0m) diagnosticHvByTicker[ticker] = hv.Value;
		}

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
			var pTickerEvents = ApplyHistoricalDividend(eventCalendar.Get(p.Ticker), p.Ticker, ctx.Now);
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
				historicalVolAnnual: diagnosticHvByTicker.TryGetValue(p.Ticker, out var pHv) ? pHv : (decimal?)null,
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
		// Sandbox accounts report cash balances in the quadrillions, so the unbounded floor can exceed
		// Int32.MaxValue before MaxQtyPerProposal clamps it. Saturate at int.MaxValue to keep the cast
		// safe — the downstream Math.Min(..., MaxQtyPerProposal) drives the final qty either way.
		var cashCap = SaturatingFloorToInt(freeCash / p.CapitalAtRiskPerContract);
		// Risk budget = the smaller of (account-pct cap) and (absolute dollar cap). The dollar cap
		// stops compounding from inflating position sizes into seven-figure single-trade bets after
		// a few good months. Zero on the dollar cap means "no absolute limit" (pct-only).
		var pctBudget = accountValue * cfg.MaxRiskPctPerProposal;
		var dollarBudget = cfg.MaxDollarRiskPerProposal > 0m ? cfg.MaxDollarRiskPerProposal : decimal.MaxValue;
		var riskBudget = Math.Min(pctBudget, dollarBudget);
		var riskCap = riskBudget > 0m ? SaturatingFloorToInt(riskBudget / p.CapitalAtRiskPerContract) : 0;
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

	/// <summary>Calendar days from <paramref name="asOf"/> to a proposal's scoring target expiry — the
	/// nearest (min) leg expiry, which matches <c>CandidateSkeleton.TargetExpiry</c> for every structure
	/// (short-leg expiry on calendars/diagonals, the shared expiry otherwise). Used to recover the
	/// DTE-aware bias for a scored proposal. Falls back to 0 if no leg expiry parses.</summary>
	private static int CalendarDteToTarget(OpenProposal p, DateTime asOf)
	{
		var minExpiry = (DateTime?)null;
		foreach (var leg in p.Legs)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed == null) continue;
			if (minExpiry == null || parsed.ExpiryDate.Date < minExpiry.Value) minExpiry = parsed.ExpiryDate.Date;
		}
		return minExpiry == null ? 0 : (minExpiry.Value - asOf.Date).Days;
	}

	/// <summary>Floor a non-negative decimal to int, saturating at int.MaxValue on overflow.
	/// Used by qty-cap math where a sandbox-sized cash balance can produce an intermediate
	/// floor value larger than Int32 before the MaxQtyPerProposal Math.Min clamps it.</summary>
	private static int SaturatingFloorToInt(decimal value)
	{
		if (value <= 0m) return 0;
		var floored = Math.Floor(value);
		return floored >= int.MaxValue ? int.MaxValue : (int)floored;
	}

	private static bool IsCalendarLike(OpenProposal proposal) => StructureKindInfo.IsCalendarLike(proposal.StructureKind);

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

	private static bool IsCreditStructure(OpenStructureKind kind) => StructureKindInfo.IsCreditStructure(kind);

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
		if (s.LongVertical.Enabled) max = Math.Max(max, s.LongVertical.DteMax);
		if (s.DiagonalVertical.Enabled) max = Math.Max(max, Math.Max(s.DiagonalVertical.ShortDteMax, s.DiagonalVertical.LongDteMax));
		if (s.CalendarVertical.Enabled) max = Math.Max(max, Math.Max(s.CalendarVertical.ShortDteMax, s.CalendarVertical.LongDteMax));
		return max;
	}

	/// <summary>The DTE windows the enabled structures actually price into — one range per single-expiry
	/// structure, two (short + long) per calendar/diagonal. The snapshot sweep covers the nearest few
	/// expiries in each so both legs of a multi-expiry structure get real strikes.</summary>
	private static List<(int Min, int Max)> DteRangesForStructures(OpenerConfig cfg)
	{
		var s = cfg.Structures;
		var r = new List<(int, int)>();
		if (s.LongCalendar.Enabled) { r.Add((s.LongCalendar.ShortDteMin, s.LongCalendar.ShortDteMax)); r.Add((s.LongCalendar.LongDteMin, s.LongCalendar.LongDteMax)); }
		if (s.DoubleCalendar.Enabled) { r.Add((s.DoubleCalendar.ShortDteMin, s.DoubleCalendar.ShortDteMax)); r.Add((s.DoubleCalendar.LongDteMin, s.DoubleCalendar.LongDteMax)); }
		if (s.LongDiagonal.Enabled) { r.Add((s.LongDiagonal.ShortDteMin, s.LongDiagonal.ShortDteMax)); r.Add((s.LongDiagonal.LongDteMin, s.LongDiagonal.LongDteMax)); }
		if (s.DoubleDiagonal.Enabled) { r.Add((s.DoubleDiagonal.ShortDteMin, s.DoubleDiagonal.ShortDteMax)); r.Add((s.DoubleDiagonal.LongDteMin, s.DoubleDiagonal.LongDteMax)); }
		if (s.DiagonalVertical.Enabled) { r.Add((s.DiagonalVertical.ShortDteMin, s.DiagonalVertical.ShortDteMax)); r.Add((s.DiagonalVertical.LongDteMin, s.DiagonalVertical.LongDteMax)); }
		if (s.CalendarVertical.Enabled) { r.Add((s.CalendarVertical.ShortDteMin, s.CalendarVertical.ShortDteMax)); r.Add((s.CalendarVertical.LongDteMin, s.CalendarVertical.LongDteMax)); }
		if (s.IronButterfly.Enabled) r.Add((s.IronButterfly.DteMin, s.IronButterfly.DteMax));
		if (s.IronCondor.Enabled) r.Add((s.IronCondor.DteMin, s.IronCondor.DteMax));
		if (s.ShortVertical.Enabled) r.Add((s.ShortVertical.DteMin, s.ShortVertical.DteMax));
		if (s.LongCallPut.Enabled) r.Add((s.LongCallPut.DteMin, s.LongCallPut.DteMax));
		if (s.LongVertical.Enabled) r.Add((s.LongVertical.DteMin, s.LongVertical.DteMax));
		return r;
	}

	// Daily snapshot sweep bounds. ±8% reaches the ~delta-0.20 short anchors at 30–45 DTE. Expiries are
	// selected PER DTE BAND (the nearest few in each enabled structure's short and long window) rather than
	// "nearest N overall" — diagonals/calendars need a long leg at 21–45 DTE, and on a daily chain the
	// nearest-N are all short-dated, so the long-leg expiries would never be swept and those legs drop.
	private const int SnapshotExpiriesPerBand = 4;
	private const decimal SnapshotWindowPct = 0.08m;

	// Version prefix on the snapshot's as-of key: bump it whenever the tradeable criterion or sweep coverage
	// changes so any snapshot written by an older build is treated as absent and re-swept (rather than
	// silently reused). v2 = tradeable means a real bid/ask (v1 wrongly counted open-interest-only strikes);
	// v3 = sweep covers each DTE band (so long legs at 21–45 DTE are included, not just the nearest expiries).
	private const string SnapshotSchemaVersion = "v3";
	private static string SnapshotDate(DateTime now) => $"{SnapshotSchemaVersion}:{now.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";

	/// <summary>Once per ET day per ticker, learn which listed strikes actually trade. The chain inlines
	/// bid/ask only for the front expiry, so for the future expiries diagonals/calendars use we must fetch
	/// bid/ask for the near-money strikes to separate the tradeable grid from the dead-but-listed strikes.
	/// Persists the result in the derivative registry (<see cref="DerivativeIdRegistry.RecordSnapshot"/>);
	/// the sweep is incremental per expiry, so a re-run with the same DTE bands is a no-op (those expiries
	/// are already covered today) while a wider-DTE strategy still fills in the bands an earlier run left out.</summary>
	private async Task<IReadOnlyDictionary<string, OptionContractQuote>?> EnsureDailySnapshotAsync(EvaluationContext ctx, IReadOnlyDictionary<string, decimal> spots, IReadOnlyDictionary<string, HashSet<DateTime>> availableByTicker, IEnumerable<string> chainOccs, OpenerConfig cfg, bool debug, CancellationToken cancellation, QuoteOverrides quoteOverrides)
	{
		var ticker = _config.Ticker;
		var asOf = SnapshotDate(ctx.Now);
		if (!spots.TryGetValue(ticker, out var spot) || spot <= 0m) return null;
		if (!availableByTicker.TryGetValue(ticker, out var expiries) || expiries.Count == 0) return null;

		// Cover every enabled structure's DTE band(s): the nearest few expiries within each band, so both
		// the short (3–10 DTE) and long (21–45 DTE) legs of diagonals/calendars get swept. Skip expiries
		// today's snapshot already covers — the snapshot is shared across strategies via (ticker, ET-date),
		// and gating the whole sweep on that key let a narrow-DTE run (a 0DTE config sweeps only the front
		// expiry) mark the ticker done and starve a later calendar/diagonal (QuickDC) run of its far legs.
		// Filtering per band before the Take keeps each band's quota of FRESH expiries.
		var covered = DerivativeIdRegistry.CoveredExpiries(ticker, asOf);
		var candidateExps = new HashSet<DateTime>();
		foreach (var (min, max) in DteRangesForStructures(cfg))
			foreach (var e in expiries
				.Where(e => { var d = (e.Date - ctx.Now.Date).Days; return d >= min && d <= max; })
				.Where(e => !covered.Contains(e.Date))
				.OrderBy(e => e).Take(SnapshotExpiriesPerBand))
				candidateExps.Add(e);
		if (candidateExps.Count == 0) return null;

		var lo = spot * (1m - SnapshotWindowPct);
		var hi = spot * (1m + SnapshotWindowPct);
		var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var occ in chainOccs)
		{
			var p = ParsingHelpers.ParseOptionSymbol(occ);
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (!candidateExps.Contains(p.ExpiryDate.Date)) continue;
			if (p.Strike < lo || p.Strike > hi) continue;
			symbols.Add(occ);
		}
		if (symbols.Count == 0) return null;

		var fetched = await _quotes.GetQuotesAsync(ctx.Now, symbols, _config.TickerSet(), cancellation, quoteOverrides);
		var liquidity = new Dictionary<string, (bool Tradeable, long? OpenInterest)>(StringComparer.OrdinalIgnoreCase);
		var withOiButNoQuote = 0;
		foreach (var sym in symbols)
		{
			fetched.Options.TryGetValue(sym, out var q);
			// "Tradeable" must mean PRICEABLE: the candidate scorer needs a real bid/ask, so a strike is
			// usable only if the sweep's queryBatch returned one. Open interest alone is NOT enough — XSP
			// lists $1 strikes that carry OI but never quote, and selecting those is the live scored=0
			// failure. We still record OI for diagnostics, but it does not make a strike tradeable.
			var quoted = q is { Bid: > 0m, Ask: > 0m };
			if (!quoted && q?.OpenInterest is > 0) withOiButNoQuote++;
			liquidity[sym] = (quoted, q?.OpenInterest);
		}
		DerivativeIdRegistry.RecordSnapshot(asOf, liquidity);
		if (debug) Console.Error.WriteLine($"[debug] {ticker} daily chain snapshot {asOf}: probed {symbols.Count} near-money strikes across {candidateExps.Count} expiries, {liquidity.Count(kv => kv.Value.Tradeable)} quoted/tradeable ({withOiButNoQuote} had OI but no bid/ask — excluded).");
		return fetched.Options;
	}

	/// <summary>Turns today's snapshot of tradeable strikes into chain-overlay markers (open-interest = 1,
	/// no bid/ask) so the <see cref="StrikeLadder"/> — which prefers quoted, then OI-bearing, strikes —
	/// snaps to the real grid even on future expiries the chain returned symbol-only. Skips any strike that
	/// already carries a usable quote (front expiry) so we never overwrite real pricing.</summary>
	private static Dictionary<string, OptionContractQuote> BuildSnapshotOverlay(string ticker, DateTime now, IReadOnlyDictionary<string, OptionContractQuote> bootstrapOptions, IReadOnlyDictionary<string, OptionContractQuote> baseQuotes)
	{
		var overlay = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var occ in DerivativeIdRegistry.TradeableOccs(ticker, SnapshotDate(now)))
		{
			if (bootstrapOptions.TryGetValue(occ, out var b) && b is { Bid: > 0m, Ask: > 0m }) continue;
			if (baseQuotes.TryGetValue(occ, out var c) && c is { Bid: > 0m, Ask: > 0m }) continue;
			overlay[occ] = new OptionContractQuote(occ, null, null, null, null, null, Volume: null, OpenInterest: 1, ImpliedVolatility: null);
		}
		return overlay;
	}

	/// <summary>True when <paramref name="quotes"/> already carries at least one option contract for
	/// <paramref name="ticker"/> — i.e. the chain is present and the bootstrap probe (and, in the backtest,
	/// its captured-chain expansion) doesn't need to run for the grid. Enumerating Keys mirrors
	/// <see cref="IndexExpirations"/>; test fakes that expose empty Keys simply report "no chain" and probe.</summary>
	/// <summary>True when the live chain in <paramref name="ctx"/> doesn't span every expiry the enabled
	/// structures' DTE bands need — the check that decides whether the bootstrap probe must run. Webull
	/// returns the WHOLE chain from any single fetch, so once a chain is present every band expiry comes with
	/// it and this is false; but Schwab (and the backtest) return only the requested expiry window, so a
	/// no-position tick's front-only chain (Schwab windows an empty request to [today,today]) or a held
	/// calendar's [short,long] window leaves the other band expiries absent and the enumerator can't build
	/// the missing-band leg. Falls back to "no chain at all" when no band expiry resolves (empty cadence).</summary>
	private static bool ChainMissesNeededExpiries(string ticker, OpenerConfig cfg, EvaluationContext ctx)
	{
		var maxDte = MaxDteAcrossStructures(cfg);
		var dteRanges = DteRangesForStructures(cfg);
		bool InAnyBand(DateTime exp)
		{
			var d = (exp.Date - ctx.Now.Date).Days;
			return d >= 0 && dteRanges.Any(r => d >= r.Min && d <= r.Max);
		}
		var needed = new HashSet<DateTime>();
		foreach (var exp in OpenerExpiryHelpers.NextExpiriesForTicker(ticker, ctx.Now, 0, maxDte).Where(InAnyBand)) needed.Add(exp.Date);
		// Calendar/diagonal long legs reach into monthly DTE; include those 3rd-Fridays too.
		foreach (var exp in OpenerExpiryHelpers.MonthlyExpiriesInRange(ctx.Now, 0, maxDte).Where(InAnyBand)) needed.Add(exp.Date);
		if (needed.Count == 0) return !HasChainFor(ticker, ctx.Quotes);
		var present = ChainExpiriesFor(ticker, ctx.Quotes);
		return needed.Any(e => !present.Contains(e));
	}

	/// <summary>Set of expiry dates for which <paramref name="quotes"/> carries at least one contract of
	/// <paramref name="ticker"/>. Mirrors <see cref="HasChainFor"/> but per-expiry, so the probe trigger can
	/// tell a full chain from a windowed one.</summary>
	private static HashSet<DateTime> ChainExpiriesFor(string ticker, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var set = new HashSet<DateTime>();
		foreach (var k in quotes.Keys)
		{
			var p = ParsingHelpers.ParseOptionSymbol(k);
			if (p != null && string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) set.Add(p.ExpiryDate.Date);
		}
		return set;
	}

	private static bool HasChainFor(string ticker, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		foreach (var k in quotes.Keys)
		{
			var p = ParsingHelpers.ParseOptionSymbol(k);
			if (p != null && string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
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

	/// <summary>Computes the complete per-ticker regime signal components — VIX term structure,
	/// directional-agreement moveSign, and intraday tape — alongside the caller-supplied macro bias.
	/// Both the scanner and wa analyze risk call this same method, guaranteeing byte-identical bias
	/// values regardless of which command surfaced the trade. Feed the result into
	/// RegimeAnalyzer.BlendBias(components, cfg, dteCalendar) to get the DTE-aware blended bias.
	///
	/// includeCurrentBar: true = live (include the partial current minute), false = backtest (exclude
	/// the forming bar to prevent one-minute look-ahead in a bar-start-convention tape).</summary>
	internal static async Task<RegimeAnalyzer.RegimeComponents> ComputeRegimeComponentsAsync(string ticker, OpenerConfig cfg, decimal macroBias, DateTime asOf, HistoricalPriceCache priceCache, IntradayBarCache? intradayCache, bool includeCurrentBar, CancellationToken cancellation)
	{
		// VIX term structure (VIX vs VIX9D)
		decimal? vixTermScore = null;
		if (cfg.Weights.VixTermStructure > 0m)
		{
			try
			{
				var vixCloses = await priceCache.GetRecentClosesAsync("VIX", 1, asOf, cancellation);
				var vix9dCloses = await priceCache.GetRecentClosesAsync("VIX9D", 1, asOf, cancellation);
				if (vixCloses.Count > 0 && vix9dCloses.Count > 0)
					vixTermScore = VixTermStructureIndicator.Compute(vixCloses[^1], vix9dCloses[^1]);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Console.WriteLine($"VIX term structure: fetch failed: {ex.Message}");
			}
		}

		// Bias-calibration move sign and yesterday's close (intraday gap component)
		decimal moveSign = 0m;
		decimal? prevClose = null;
		if (cfg.BiasCalibrationLookbackDays > 0 || cfg.Weights.IntradayTape > 0m)
		{
			try
			{
				var n = cfg.BiasCalibrationLookbackDays;
				var closes = await priceCache.GetRecentClosesAsync(ticker, Math.Max(4, n + 2), asOf, cancellation);
				if (closes.Count > 0) prevClose = closes[^1];
				if (n > 0 && closes.Count > n)
				{
					var recentClose = closes[^1];
					var olderClose = closes[closes.Count - 1 - n];
					if (olderClose > 0m)
						moveSign = recentClose > olderClose ? 1m : (recentClose < olderClose ? -1m : 0m);
				}
			}
			catch { }
		}

		// Intraday tape signal — strategy ticker is the cache key; the fetcher handles source-mapping
		// internally (e.g. SPXW transparently uses SPX RTH bars). Null on any failure so the caller
		// degrades gracefully to macro+VIX-term bias.
		IntradayBias? intradayBias = null;
		if (cfg.Weights.IntradayTape > 0m && intradayCache != null)
		{
			try
			{
				var tapeCfg = cfg.Indicators.IntradayTape;
				var interval = ParseBarInterval(tapeCfg.BarIntervalCode);
				var asOfUtc = asOf.Kind == DateTimeKind.Utc
					? new DateTimeOffset(asOf, TimeSpan.Zero)
					: new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(asOf, DateTimeKind.Unspecified), NyTz), TimeSpan.Zero);
				// Live: include boundary bar (partial current minute). Backtest: exclude (look-ahead guard).
				var toUtc = includeCurrentBar ? asOfUtc : asOfUtc.AddTicks(-1);
				var fromUtc = toUtc.AddMinutes(-Math.Max(60, tapeCfg.LookbackMinutes));
				var bars = await intradayCache.GetBarsAsync(ticker, fromUtc, toUtc, interval, tapeCfg.IncludeExtended, cancellation);
				if (bars.Count > 0)
					intradayBias = IntradayTapeIndicators.Compute(bars, prevClose, toUtc, new IntradayTapeConfig { MinBars = tapeCfg.MinBars, GapWeight = tapeCfg.GapWeight, OpenToNowWeight = tapeCfg.OpenToNowWeight, VwapDeviationWeight = tapeCfg.VwapDeviationWeight, IncludeExtended = tapeCfg.IncludeExtended });
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Console.WriteLine($"Intraday tape: bar fetch failed for {ticker}: {ex.Message}");
			}
		}

		return new RegimeAnalyzer.RegimeComponents(macroBias, vixTermScore, intradayBias, moveSign);
	}

	private IntradayBarCache? GetOrCreateIntradayCache()
	{
		if (_intradayCache != null) return _intradayCache;
		if (_backtestMode)
		{
			// Disk-only path: the cache reads data/intraday/<TICKER>/<date>.csv when present. The
			// fetcher is invoked only when the file is missing; in backtest we have no Webull HTTP
			// transport, so return empty so the cache treats the date as "no intraday available"
			// and the opener degrades gracefully (skips intraday-tape signal, keeps macro bias).
			_intradayCache = new IntradayBarCache(BacktestNoopIntradayFetcher);
			return _intradayCache;
		}
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null) return null;
		_intradayCache = new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig));
		return _intradayCache;
	}

	private static Task<IReadOnlyList<MinuteBar>> BacktestNoopIntradayFetcher(string ticker, BarInterval interval, int count, bool includeExtended, CancellationToken cancellation)
		=> Task.FromResult<IReadOnlyList<MinuteBar>>(Array.Empty<MinuteBar>());

	internal static ApiConfig? TryLoadApiConfig()
	{
		try
		{
			var path = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(path)) return null;
			var cfg = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(path));
			if (cfg == null || cfg.Webull.Headers.Count == 0) return null;
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

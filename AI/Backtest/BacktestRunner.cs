using Spectre.Console;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Daily-step simulator. Each step (end-of-day, 15:45 ET):
///   1. Settle positions whose short leg has expired on or before today (intrinsic-value cash flow).
///   2. Run <see cref="RuleEvaluator"/> over surviving positions. Each Close / Roll / AlertOnly proposal
///      applies to the simulated book at the day's mark.
///   3. Run <see cref="OpenCandidateEvaluator"/>; the top-N proposals that fit free cash get opened
///      at the day's mark.
/// Fills price off <see cref="BacktestQuoteSource"/> under the active <c>--pricing</c> mode: <c>mid</c> at the
/// spread midpoint, <c>bidask</c> crossing the spread (worst-case marketable fills). Marks always use mid.
/// Fees are debited per leg-contract; <c>slippagePerSharePerOrder</c> applies additively on top.
/// Phase-1 limitations (noted for follow-up work): no intraday triggering on stop-loss / take-profit,
/// and opens fill same-day instead of at next-day open.
/// </summary>
internal sealed class BacktestRunner
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private readonly AIConfig _config;
	private readonly SimulatedBook _book;
	private readonly BacktestPositionSource _positions;
	private readonly IBacktestQuoteSource _quotes;
	private readonly HistoricalBarCache _bars;
	private readonly HistoricalPriceCache _closeCache;
	private readonly int _topNPerStep;
	private readonly IntradayBarCache _intradayBars;
	private readonly bool _oracle;
	private readonly bool _profile;
	private readonly int? _fixedContracts;
	private readonly string _pricingMode;
	private readonly int _scanStride;
	// Book each split structure as its two combo orders (live-broker representation) instead of one
	// composite position. See OpenProposalIntoBook.
	private readonly bool _splitStructures;
	private readonly IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? _dividendsByRoot;
	// Proposal-replay mode (--proposals): non-null replaces the opener entirely — each day's recorded live
	// proposal opens are booked at their recorded submit prices, then managed by the normal rule engine.
	private readonly IReadOnlyDictionary<DateTime, List<ProposalReplayOpen>>? _replayOpensByDate;

	// Conceptual fill times within a trading day. Opens, closes, and rolls all price off bar.Open
	// (BacktestQuoteSource uses the day's open as spot), so they're stamped at 09:30 ET — the
	// start-of-bar convention: a bar at 09:30:00 covers the 09:30→09:31 ET minute and contains the
	// auction-cleared open price. Webull natively stamps end-of-bar (09:31:00) but we normalize that
	// to start-of-bar at parse time in `WebullChartsClient` so the entire codebase agrees with
	// Polygon, ToS, and TradingView. Expirations settle at bar.Close intrinsic, stamped at 16:00 ET.
	private static readonly TimeSpan MarketOpenTime = TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
	private static readonly TimeSpan MarketCloseTime = TimeSpan.FromHours(16);

	public BacktestRunner(AIConfig config, SimulatedBook book, BacktestPositionSource positions, IBacktestQuoteSource quotes, HistoricalBarCache bars, HistoricalPriceCache closeCache, int topNPerStep, bool oracle = false, bool profile = false, int? fixedContracts = null, string pricingMode = SuggestionPricing.Mid, int scanStride = 1, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividendsByRoot = null, bool splitStructures = false, IReadOnlyList<ProposalReplayOpen>? replayOpens = null)
	{
		_replayOpensByDate = replayOpens?.GroupBy(o => o.OpenEt.Date).ToDictionary(g => g.Key, g => g.OrderBy(o => o.OpenEt).ToList());
		_config = config;
		_pricingMode = SuggestionPricing.Normalize(pricingMode);
		_scanStride = Math.Max(1, scanStride);
		_splitStructures = splitStructures;
		_dividendsByRoot = dividendsByRoot;
		_book = book;
		_positions = positions;
		_quotes = quotes;
		_bars = bars;
		_closeCache = closeCache;
		_topNPerStep = topNPerStep;
		_oracle = oracle;
		_profile = profile;
		_fixedContracts = fixedContracts;
		// Disk-only cache for backtest: the fetcher returns empty so any missing minute file fails
		// closed (we fall back to the single 09:30 fill path). The on-disk read path serves the
		// backfilled data/intraday/<TICKER>/<date>.csv files that the Polygon-mirror import created.
		_intradayBars = new IntradayBarCache(NoopIntradayFetcher);
	}

	private static Task<IReadOnlyList<MinuteBar>> NoopIntradayFetcher(string ticker, BarInterval interval, int count, bool includeExtended, CancellationToken cancellation)
		=> Task.FromResult<IReadOnlyList<MinuteBar>>(Array.Empty<MinuteBar>());

	/// <param name="onStep">Optional progress callback invoked after each trading day completes, with
	/// (daysDone, totalDays, day). The command wires it to an inline Spectre progress bar.</param>
	public async Task<BacktestResult> RunAsync(DateTime since, DateTime until, CancellationToken cancellation, Action<int, int, DateTime>? onStep = null)
	{
		var tickerSet = _config.TickerSet();
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(_config), _config);
		var openEvaluator = new OpenCandidateEvaluator(_config, _quotes, SuggestionPricing.Mid, _closeCache, backtestMode: true, dividendsByRoot: _dividendsByRoot);

		var equityCurve = new List<(DateTime Date, decimal Equity)>();
		var startingCash = _book.Cash;
		var peakEquity = startingCash;
		var maxDrawdown = 0m;
		var steps = EnumerateTradingDays(since, until).ToList();

		// Per-step timing diagnostic. Enabled via --profile on `wa ai backtest`; off otherwise (zero
		// overhead in normal runs). Surfaces where wall time is going across the simulator's loop —
		// useful when a recent change makes the backtest noticeably slower than before.
		var profile = _profile;
		var swTotal = profile ? System.Diagnostics.Stopwatch.StartNew() : null;
		long msStep1 = 0, msStep2 = 0, msStep3 = 0, msTriggers = 0, msMtm = 0;
		var done = 0;

		foreach (var step in steps)
		{
			cancellation.ThrowIfCancellationRequested();
			var swSection = profile ? System.Diagnostics.Stopwatch.StartNew() : null;

			// Step 1: management rules. Run BEFORE settlement so CloseBeforeShortExpiryRule (and
			// any rule that checks DTE==0) can fire on the actual expiry day. Otherwise the position
			// is gone by the time rules see it and ITM shorts go straight to assignment.
			var openPositions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			if (openPositions.Count > 0)
			{
				var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
				var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, step, _quotes, _config, cancellation);
				var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);
				var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
				var results = evaluator.Evaluate(ctx);

				foreach (var r in results)
				{
					var p = r.Proposal;
					if (!openPositions.TryGetValue(p.PositionKey, out var pos)) continue;
					if (p.Kind == ProposalKind.Close)
					{
						var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quoteSnapshot.Options);
						if (legFills != null) _book.Close(step, p.PositionKey, legFills, p.Rule, quoteSnapshot.Underlyings.GetValueOrDefault(pos.Ticker));
					}
					else if (p.Kind == ProposalKind.Roll)
					{
						var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quoteSnapshot.Options);
						if (legFills != null) _book.Roll(step, p.PositionKey, legFills, p.Rule, quoteSnapshot.Underlyings.GetValueOrDefault(pos.Ticker));
					}
					else if (p.Kind == ProposalKind.LegIn)
					{
						var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quoteSnapshot.Options);
						// Rule emits the new structure name via convention: LongCall→LongCallVertical, LongPut→LongPutVertical.
						// Derive from the existing strategy + the fact that the new leg is opposite-side.
						if (legFills != null)
						{
							var newStructure = string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongCallVertical
								: string.Equals(pos.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongPutVertical
								// CompleteCondorRule adds the opposite-side vertical to a held short vertical → iron condor.
								: string.Equals(pos.StrategyKind, "ShortPutVertical", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.IronCondor
								: string.Equals(pos.StrategyKind, "ShortCallVertical", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.IronCondor
								: (OpenStructureKind?)null;
							if (newStructure != null) _book.LegIn(step, p.PositionKey, legFills, p.Rule, newStructure.Value, quoteSnapshot.Underlyings.GetValueOrDefault(pos.Ticker));
						}
					}
					// AlertOnly: noop in backtest.
				}
			}

			if (profile) { msStep1 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// Step 2: settle anything that's still in the book and has reached expiry. Anything still
			// here at this point is either (a) the rule engine didn't decide to close it, or (b) the
			// position has only-OTM short legs and is harmless to let expire worthless.
			await SettleExpirationsAsync(step, cancellation);

			if (profile) { msStep2 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// Step 3: opener — scan intraday minute by minute. Previously fired once at 09:30 ET
			// because there was no minute data to anchor any other moment; with backfilled
			// data/intraday/<TICKER>/<date>.csv we can now re-evaluate at each minute and let the
			// existing score gate (cfg.MinScoreToOpen) decide when conditions warrant a fill. Top-N
			// per step caps the number of opens (typically 1 for SPXW); first proposal that passes
			// score + cash + qty wins the day. If no minute crosses, the day skips.
			// Proposal-replay mode (--proposals) replaces this step entirely: the day's live-recorded proposal
			// opens book at their recorded time, qty, and submit price — no scoring, sizing, or cash gate (the
			// live sim already decided those) — and the rules/triggers/settlement below manage them as usual.
			if (_replayOpensByDate != null)
			{
				if (_replayOpensByDate.TryGetValue(step.Date, out var todaysOpens))
					foreach (var o in todaysOpens)
						OpenLegsIntoBook(o.OpenEt, o.Ticker, o.StructureKind, o.Legs, o.Qty, o.Spot, o.RawScore, o.FinalScore, repIv: null, applySlippage: false);
			}
			else
			{
				var openedAtMinute = await TryOpenAcrossDayAsync(step, tickerSet, openEvaluator, cancellation);
				// Fallback: if there is no intraday data for this day (pre-2025 dates outside the
				// Polygon backfill, or any future hole), use the legacy 09:30 single-call path.
				if (!openedAtMinute.HasIntraday)
				{
					var openProposals = openedAtMinute.LegacyProposals;
					// Selection must not be dictated by affordability: consider only the top-N by score and
					// skip any we can't afford — never fall through to a cheaper, lower-ranked substitute.
					foreach (var p in openProposals.Take(_topNPerStep))
					{
						var qty = ResolveOpenQty(p);
						if (qty < 1) continue;
						OpenProposalIntoBook(step, p, qty);
					}
				}
			}

			if (profile) { msStep3 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// Intraday SL/TP simulation: re-price each open position at the day's bar.High and bar.Low
			// (with mid-session TTE for 0DTE) and fire StopLossRule / TakeProfitRule against both
			// extremes. Without this, 0DTE positions never see their stops or profit targets because
			// the daily-step engine jumps straight from open to expiry. Closes here happen BEFORE
			// settlement so any 0DTE position that would have stopped intraday doesn't double-count
			// at expiration.
			await RunIntradayTriggersAsync(step, evaluator, cancellation);

			// 0DTE: a position opened this step whose short leg expires today must settle today, not
			// roll over to tomorrow and pick up the wrong bar. The second pass is a no-op when none
			// of the opens are 0DTE.
			await SettleExpirationsAsync(step, cancellation);

			if (profile) { msTriggers += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// Track equity curve at end-of-step: cash + MTM of open positions.
			var mtm = await ComputeOpenMarkAsync(step, cancellation);
			var equity = _book.Cash + mtm;
			equityCurve.Add((step.Date, equity));
			if (equity > peakEquity) peakEquity = equity;
			else
			{
				var dd = peakEquity - equity;
				if (dd > maxDrawdown) maxDrawdown = dd;
			}

			if (profile) msMtm += swSection!.ElapsedMilliseconds;
			onStep?.Invoke(++done, steps.Count, step);
		}

		if (profile)
		{
			var total = swTotal!.ElapsedMilliseconds;
			Console.WriteLine();
			Console.WriteLine($"[profile] backtest wall time: {total:N0} ms ({steps.Count} trading days, {(double)total / Math.Max(1, steps.Count):F1} ms/day avg)");
			Console.WriteLine($"[profile]   step 1 (rules):              {msStep1:N0} ms ({(double)msStep1 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   step 2 (settle pre-open):    {msStep2:N0} ms ({(double)msStep2 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   step 3 (opener minute loop): {msStep3:N0} ms ({(double)msStep3 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   intraday triggers + settle:  {msTriggers:N0} ms ({(double)msTriggers * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   end-of-step MTM:             {msMtm:N0} ms ({(double)msMtm * 100 / Math.Max(1, total):F1}%)");
		}

		// Final per-lineage MTM for still-open positions so the renderer can compute unrealized P&L.
		var endMtmByLineage = await ComputeOpenMarkPerLineageAsync(steps.Count > 0 ? steps[^1] : until, cancellation);

		return new BacktestResult(
			StartingCash: startingCash,
			EndingCash: _book.Cash,
			TotalFees: _book.TotalFees,
			OpenFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Open),
			CloseFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Close),
			RollFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Roll),
			LegInFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.LegIn),
			ExpireFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Expire),
			MaxDrawdown: maxDrawdown,
			PeakEquity: peakEquity,
			EquityCurve: equityCurve,
			Fills: _book.Fills,
			EndMtmByLineage: endMtmByLineage,
			Provenance: ComputeProvenance(),
			Cleanliness: ComputeCleanliness());
	}

	/// <summary>Per-lineage MTM of still-open positions at the final step. Used by the renderer to split
	/// realized from unrealized P&L. Returns the same numbers <see cref="ComputeOpenMarkAsync"/> would
	/// have summed, but bucketed by lineage so we can attribute each to a lifecycle.</summary>
	private async Task<IReadOnlyDictionary<long, decimal>> ComputeOpenMarkPerLineageAsync(DateTime step, CancellationToken cancellation)
	{
		var byLineage = new Dictionary<long, decimal>();
		if (_book.OpenPositions.Count == 0) return byLineage;

		var symbols = _book.OpenPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var tickers = _book.OpenPositions.Values.Select(p => p.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var snap = await _quotes.GetQuotesAsync(step, symbols, tickers, cancellation);

		// Lineage lookup: each open position's lineage id is the LineageId of the most recent fill on that position's PositionKey.
		var lineageByKey = _book.Fills
			.GroupBy(f => f.PositionKey, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.Last().LineageId, StringComparer.OrdinalIgnoreCase);

		foreach (var pos in _book.OpenPositions.Values)
		{
			if (!lineageByKey.TryGetValue(pos.Key, out var lineage)) continue;
			decimal perContract = 0m;
			var allLegsPriced = true;
			foreach (var leg in pos.Legs)
			{
				if (!snap.Options.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
				{
					allLegsPriced = false;
					break;
				}
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				perContract += leg.Side == Side.Buy ? mid : -mid;
			}
			if (allLegsPriced) byLineage[lineage] = perContract * 100m * pos.Quantity;
		}
		return byLineage;
	}

	/// <summary>Execution price for a single leg under the active <c>--pricing</c> mode. <c>mid</c> fills at
	/// the spread midpoint; <c>bidask</c> crosses the spread (a buy lifts the ask, a sell hits the bid),
	/// modelling worst-case marketable fills. Slippage (<c>slippagePerSharePerOrder</c>) applies additively
	/// on top in <see cref="SimulatedBook"/>, independent of this. Marks (MTM / threshold detection) always
	/// use mid — only realized fills cross the spread.</summary>
	private decimal ExecPrice(Side side, decimal bid, decimal ask)
		=> _pricingMode == SuggestionPricing.BidAsk ? (side == Side.Buy ? ask : bid) : (bid + ask) / 2m;

	/// <summary>For management proposals (close/roll), re-price each leg under the active pricing mode.
	/// Returns null if any leg lacks a usable quote.</summary>
	private IReadOnlyList<BacktestLegFill>? BuildLegFillsFromQuotes(IReadOnlyList<ProposalLeg> legs, int qty, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var fills = new List<BacktestLegFill>(legs.Count);
		foreach (var l in legs)
		{
			if (!quotes.TryGetValue(l.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return null;
			var side = string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
			fills.Add(new BacktestLegFill(l.Symbol, side, qty, ExecPrice(side, q.Bid.Value, q.Ask.Value)));
		}
		return fills;
	}

	/// <summary>Resolves the contract quantity for an open. Normal mode uses the evaluator's
	/// cash/risk-scaled <c>Qty</c> and honors the cash-reserve and free-cash gates (returns 0 to skip).
	/// Fixed-lots mode (<c>--lots N</c>) overrides to a flat N every trade and ignores cash entirely:
	/// terminal P&L becomes the additive sum of per-trade results instead of a compounding curve, and
	/// every endorsed signal trades regardless of bankroll — so a sweep measures per-trade edge, not the
	/// position-sizing feedback loop (which otherwise compounds a small base until it dwarfs the signal,
	/// or overflows).</summary>
	private int ResolveOpenQty(OpenProposal p)
	{
		if (_fixedContracts.HasValue) return _fixedContracts.Value;
		if (p.CashReserveBlocked) return 0;
		if (p.Qty < 1) return 0;
		// Margin requirement per contract: for credit structures the broker holds the full spread
		// width as collateral (CapitalAtRisk + credit received); for debit structures the cost is
		// already out of cash so margin = CapitalAtRisk alone.
		var marginPerContract = p.CapitalAtRiskPerContract + Math.Max(0m, p.DebitOrCreditPerContract);
		if (marginPerContract <= 0m) return 0;
		// Buying power = cash minus margin held by existing credit positions whose collateral isn't
		// reflected in the cash balance (their credit inflated cash, but the broker locks the full
		// spread width). Debit positions already reduced cash on open — no additional hold needed.
		var marginHeld = 0m;
		foreach (var pos in _book.OpenPositions.Values)
		{
			if (pos.InitialNetDebit >= 0m) continue; // debit — already paid from cash
			var maxLoss = pos.MaxLossPerShare ?? 0m;
			// spreadWidth per share = maxLoss - InitialNetDebit (InitialNetDebit < 0 → subtracting adds)
			var spreadWidthPerShare = maxLoss - pos.InitialNetDebit;
			marginHeld += spreadWidthPerShare * 100m * pos.Quantity;
		}
		var buyingPower = _book.Cash - marginHeld;
		var affordableQty = (int)Math.Floor(buyingPower / marginPerContract);
		var qty = Math.Min(p.Qty, affordableQty);
		return qty < 1 ? 0 : qty;
	}

	/// <summary>For opener proposals, the candidate scorer has already priced each leg from the same quote
	/// source: <c>PricePerShare</c> is the mid, <c>ExecutionPricePerShare</c> the conservative cross-the-spread
	/// fill (ask on buys, bid on sells). Under <c>--pricing bidask</c> we fill at the latter; under <c>mid</c>
	/// at the former. Candidate selection/scoring is unaffected — only the fill price changes.</summary>
	/// <summary>Books a proposal into the simulated book the way the broker would hold it. Whole mode
	/// (default) keeps all legs as ONE position — the historical simulator behavior, where management
	/// rules see the structure's combined mark and debit. Split mode (--split) mirrors live execution:
	/// the two combo orders from <see cref="StructureOrderSplit"/> become two independent positions
	/// (Webull has no composite position concept), so each half is managed against its OWN debit and
	/// take-profit/stop-loss fire per half. Friction and fees are identical across modes — OrdersForStructure
	/// already charges 2-order structures per order, and per-leg fees sum the same — so an A/B isolates the
	/// exit-policy granularity. All halves must price or nothing books (live validates the whole leg set the
	/// same way). Returns true when at least one position was opened.</summary>
	private bool OpenProposalIntoBook(DateTime when, OpenProposal p, int qty)
		=> OpenLegsIntoBook(when, p.Ticker, p.StructureKind, p.Legs, qty, p.Spot, p.RawScore, p.FinalScore, p.ImpliedVolatilityAnnual, applySlippage: true);

	private bool OpenLegsIntoBook(DateTime when, string ticker, OpenStructureKind structureKind, IReadOnlyList<ProposalLeg> legs, int qty, decimal spot, decimal? rawScore, decimal? finalScore, decimal? repIv, bool applySlippage)
	{
		var groups = _splitStructures ? StructureOrderSplit.Split(structureKind, legs) : null;
		if (groups == null || groups.Count == 1)
		{
			var legFills = BuildLegFillsFromProposal(legs, qty);
			return legFills != null && _book.Open(when, ticker, structureKind, legFills, qty, spot, rawScore, finalScore, repIv, applySlippage);
		}

		var groupFills = new List<(OpenStructureKind Kind, IReadOnlyList<BacktestLegFill> Fills)>(groups.Count);
		foreach (var g in groups)
		{
			var fills = BuildLegFillsFromProposal(g.Legs, qty);
			if (fills == null) return false;
			groupFills.Add((HalfStructureKind(structureKind, fills), fills));
		}
		var opened = false;
		foreach (var (kind, fills) in groupFills)
			opened |= _book.Open(when, ticker, kind, fills, qty, spot, rawScore, finalScore, repIv, applySlippage);
		return opened;
	}

	/// <summary>Structure kind of ONE combo order of a split structure. Side-split doubles yield the
	/// single-sided analog (each half is a 2-leg cross-expiry pair). Expiry-split structures
	/// (DiagonalVertical / CalendarVertical) yield same-expiry verticals whose direction follows the
	/// half's net fill: net debit = long vertical, net credit = short vertical.</summary>
	private static OpenStructureKind HalfStructureKind(OpenStructureKind parent, IReadOnlyList<BacktestLegFill> halfFills)
	{
		if (parent == OpenStructureKind.DoubleDiagonal) return OpenStructureKind.LongDiagonal;
		if (parent == OpenStructureKind.DoubleCalendar) return OpenStructureKind.LongCalendar;
		var isCall = halfFills.Any(f => ParsingHelpers.ParseOptionSymbol(f.Symbol)?.CallPut == "C");
		var netPerShare = halfFills.Sum(f => f.Side == Side.Buy ? -f.PricePerShare : f.PricePerShare);
		return netPerShare < 0m
			? (isCall ? OpenStructureKind.LongCallVertical : OpenStructureKind.LongPutVertical)
			: (isCall ? OpenStructureKind.ShortCallVertical : OpenStructureKind.ShortPutVertical);
	}

	private IReadOnlyList<BacktestLegFill>? BuildLegFillsFromProposal(IReadOnlyList<ProposalLeg> legs, int qty)
	{
		var fills = new List<BacktestLegFill>(legs.Count);
		foreach (var l in legs)
		{
			if (!l.PricePerShare.HasValue) return null;
			var price = (_pricingMode == SuggestionPricing.BidAsk ? l.ExecutionPricePerShare : l.PricePerShare) ?? l.PricePerShare.Value;
			var side = string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
			fills.Add(new BacktestLegFill(l.Symbol, side, qty, price));
		}
		return fills;
	}

	/// <summary>Intraday SL/TP simulation. Per-position: re-price each leg at bar.Low and bar.High,
	/// determine whether the position's mark crosses the SL or TP threshold *anywhere* in that range,
	/// and if so close at the *threshold mark* itself (found via bisection over spot) rather than at
	/// the day's extreme.
	///
	/// This is the key behavioural change from the prior implementation, which closed at the
	/// bar.High / bar.Low BS mark whenever the rule fired. For a 0DTE put credit spread that ended
	/// the day deep OTM, bar.High would price the spread at ~$0.003/share — 99% of max profit
	/// captured — far past the 50% threshold the user actually wanted to exit at. Real intraday
	/// execution closes near 50%, not at the day's extreme. Bisecting for the threshold spot makes
	/// the fill price honest: if you set tp = 0.5, you close at $0.30/share (50% captured), not
	/// $0.003/share (99% captured).
	///
	/// Whipsaw convention: SL fires before TP. If both thresholds are crossed in [Low, High] the
	/// adverse move is assumed to have come first (bear-case path).</summary>
	/// <summary>True when every expiring leg of <paramref name="pos"/> expires on or before
	/// <paramref name="step"/> (DTE ≤ 0) — i.e. the position carries no leg that lives past today.
	/// Only such positions are eligible for the intraday-trigger minute-walk, whose parametric leg
	/// pricing (<see cref="BacktestQuoteSource.PriceAtSpot"/>) is consistent with the captured-bar
	/// entry pricing only for short-TTE near-intrinsic 0DTE legs. A position with any future-dated leg
	/// (calendar / diagonal long leg) must be managed by the daily rule loop instead, which prices on
	/// the same captured-bar basis as entry.</summary>
	private static bool IsAllZeroDte(OpenPosition pos, DateTime step)
	{
		foreach (var leg in pos.Legs)
		{
			if (leg.Expiry is { } exp && (exp.Date - step.Date).Days > 0) return false;
		}
		return true;
	}

	private async Task RunIntradayTriggersAsync(DateTime step, RuleEvaluator evaluator, CancellationToken cancellation)
	{
		if (_book.OpenPositions.Count == 0) return;
		var realizedExpectancy = _config.Opener.RealizedExpectancy;
		if (!realizedExpectancy.Enabled) return;

		var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
		// Snapshot positions — the book mutates as we close.
		var positions = _book.OpenPositions.Values.ToList();

		foreach (var pos in positions)
		{
			cancellation.ThrowIfCancellationRequested();
			if (!_book.OpenPositions.ContainsKey(pos.Key)) continue;

			// Intraday SL/TP/LegIn triggering is a 0DTE-only construct. A same-day-expiry position opens
			// and settles within the session, so the daily rule loop (which runs at the next step's open)
			// would never see it before expiry — hence the intraday minute-walk. But that walk prices each
			// leg with the parametric Black-Scholes model (BacktestQuoteSource.PriceAtSpot / GetIntradayQuotesAsync),
			// which deliberately ignores captured bars. For a multi-DTE structure (calendar / diagonal) the
			// entry debit is set from the captured-bar path while this pass re-marks the long leg parametrically;
			// the two models diverge and manufacture an instant same-day paper profit that TakeProfit harvests
			// (observed: +44.8% in 19 minutes, 99.4% win rate). Multi-DTE positions are instead managed by the
			// daily rule loop (Step 1), which prices via GetQuotesAsync — the same captured-bar basis as entry,
			// so entry and management never disagree. Gate the intraday pass to all-0DTE positions only.
			if (!IsAllZeroDte(pos, step)) continue;

			// Prefer the minute-walk: walks minute bars chronologically and triggers at the first
			// minute the position mark crosses SL or TP. Removes the bar.Low / bar.High pessimism
			// of the 2-point sampling (which fires SL on intraday spikes that never realized as
			// actual fills, especially for 0DTE near-the-money positions). Falls back to the
			// legacy 2-point logic when no minute data exists for this date.
			var triggered = await TryMinuteWalkTriggerAsync(step, pos, cash, accountValue, realizedExpectancy, cancellation);
			if (triggered) continue;

			// The 2-point fallback below evaluates the WHOLE day's bar Low/High — including minutes BEFORE
			// the position opened — and stamps the close at `step` (~09:30) with a bisected threshold-spot.
			// That is non-causal for any position opened intraday: it fabricates an exit (e.g. a TakeProfit
			// timestamped 09:30 with a 09:30 spot) for a position that opened at, say, 10:00, that the causal
			// minute-walk above correctly did NOT trigger. This pass only handles same-day-opened 0DTE
			// positions (IsAllZeroDte gate), so the minute-walk is the ONLY causal path — never fall back to
			// the 2-point for a position opened today. No minute data → ride to EOD settlement rather than
			// invent a pre-open intraday trigger. The 2-point remains for genuine carry-over positions only.
			if (pos.OpenedAt is null || pos.OpenedAt.Value.Date == step.Date) continue;

			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar == null) continue;
			await TryIntradayTriggerAsync(step, pos, bar.Open, bar.Low, bar.High, cash, accountValue, realizedExpectancy, cancellation);
		}
	}

	/// <summary>Walks minute bars chronologically for <paramref name="pos"/> and closes the position
	/// at the first minute its mark crosses the SL or TP threshold. Returns true when a trigger
	/// fired (caller skips the legacy fallback) and false when no minute data exists or no
	/// threshold was crossed (caller falls back to bar.Low / bar.High sampling).
	///
	/// Mechanics match <see cref="TryIntradayTriggerAsync"/> for threshold computation (SL via
	/// pos.MaxLossPerShare, TP via ProfitProjector.MaxForCurrentColumn) and conservative whipsaw
	/// (SL fires before TP). The substantive difference: instead of bracketing the day with
	/// [bar.Low, bar.High] and bisecting to find a threshold spot, we step through real minute
	/// closes from the position's open time (or 09:30 ET for carry-over positions) to 16:00 ET,
	/// price the chain at each minute's spot with a remaining-session TTE, and fire on the first
	/// real crossing. Closes at that minute's mark — no bisection needed.</summary>
	private async Task<bool> TryMinuteWalkTriggerAsync(DateTime step, OpenPosition pos, decimal cash, decimal accountValue, OpenerRealizedExpectancyConfig realizedExpectancy, CancellationToken cancellation)
	{
		var symbols = pos.Legs
			.Where(l => l.CallPut != null)
			.Select(l => l.Symbol)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (symbols.Count == 0) return false;

		// Determine when to start walking. For positions opened earlier today, start one minute
		// after the open fill so we don't trigger on the same minute we opened (mark == debit,
		// neither threshold crossed yet anyway). For carry-over positions, start at 09:30 ET.
		var openFill = _book.Fills
			.Where(f => string.Equals(f.PositionKey, pos.Key, StringComparison.OrdinalIgnoreCase) && f.Kind == BacktestFillKind.Open)
			.OrderByDescending(f => f.Date)
			.FirstOrDefault();
		DateTime walkStartEt;
		if (openFill != null && openFill.Date.Date == step.Date)
			walkStartEt = openFill.Date.AddMinutes(1);
		else
			walkStartEt = step.Date.Add(MarketOpenTime);
		var walkEndEt = step.Date.Add(MarketCloseTime);
		if (walkStartEt >= walkEndEt) return false;

		var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(walkStartEt, NyTz), TimeSpan.Zero);
		var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(walkEndEt, NyTz), TimeSpan.Zero);
		var minuteBars = await _intradayBars.GetBarsAsync(pos.Ticker, startUtc, endUtc, BarInterval.M1, includeExtended: false, cancellation);
		if (minuteBars.Count == 0) return false;

		// SL threshold (mark at or below this fires SL). Matches legacy logic exactly.
		decimal? slTarget = null;
		if (_config.Rules.StopLoss.Enabled && pos.MaxLossPerShare.HasValue && pos.MaxLossPerShare.Value > 0m
			&& realizedExpectancy.StopLossPctOfMaxLoss < 1m)
			slTarget = pos.AdjustedNetDebit - realizedExpectancy.StopLossPctOfMaxLoss * pos.MaxLossPerShare.Value;

		// Intraday take-profit was Target-B only (% of max projected profit); removed. Target A (% of debit)
		// is evaluated at start-of-day in the main rule loop, so 0DTE positions still get it there.

		// LegInShort: only meaningful on single-leg long calls/puts and only fires intraday for 0DTE
		// strategies (multi-day positions get evaluated at start-of-day in the main rule loop).
		// Instantiated outside the minute loop so we don't re-allocate per minute. Must be declared
		// BEFORE the early-return check so the carve-out can see it.
		var legInRule = _config.Rules.LegInShort.Enabled
			&& (string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) || string.Equals(pos.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase))
			? new Rules.LegInShortRule(_config.Rules.LegInShort, _config.Indicators)
			: null;

		// CompleteCondor: the 0DTE analog of LegInShort but for a held single-sided short vertical — it
		// sells the opposite-side vertical to form an iron condor once the held side has cushion. Disjoint
		// from legInRule (which only applies to single-leg longs), so at most one is non-null per position.
		var completeCondorRule = _config.Rules.CompleteCondor.Enabled
			&& pos.Legs.Count == 2
			&& (string.Equals(pos.StrategyKind, "ShortPutVertical", StringComparison.OrdinalIgnoreCase) || string.Equals(pos.StrategyKind, "ShortCallVertical", StringComparison.OrdinalIgnoreCase))
			? new Rules.CompleteCondorRule(_config.Rules.CompleteCondor)
			: null;

		// Regime indicators for LegInShort / CompleteCondor: VIX and intraday range (tracked across the
		// minute loop). Both null when not needed — keep the lookup cost off the hot path when neither
		// rule's enabled flag is set. VIX is read causally per minute from the day's real intraday tape
		// (data/intraday/VIX/, captured from Webull like the SPX tape): at decision minute M the value is
		// bar M's open (start-of-bar convention — the M:00 print) or, when M's bar is absent, the latest
		// completed bar's close. This mirrors live, where the gates see the real-time VIX quote. Where the
		// tape is missing the fallback is the most recent settled close STRICTLY BEFORE step (walked back
		// 5 days over weekends/holidays, mirroring BacktestIVProvider.GetVixSeriesCloseAsync) — never the
		// day's own 16:00 close, which is not knowable during the walk (using it let the 10:00 MaxVix gate
		// "see" a close-of-day vol spike — lookahead).
		decimal? fallbackVix = null;
		IReadOnlyList<MinuteBar> vixBars = Array.Empty<MinuteBar>();
		if (legInRule != null || completeCondorRule != null)
		{
			var rthStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(step.Date.Add(MarketOpenTime), NyTz), TimeSpan.Zero);
			vixBars = await _intradayBars.GetBarsAsync("VIX", rthStartUtc, endUtc, BarInterval.M1, includeExtended: false, cancellation);
			for (var i = 1; i <= 5 && fallbackVix == null; i++)
			{
				var vixBar = await _bars.GetBarAsync("VIX", step.Date.AddDays(-i), cancellation);
				if (vixBar != null) fallbackVix = vixBar.Close;
			}
		}
		var vixCursor = 0;

		// LegInShort needs the minute walk even when SL/TP are both disabled (e.g. SPXW's
		// profit/stop pcts pinned at 1.0 to defer all closes to expiration). Without this carve-out,
		// the rule would silently never fire on 0DTE strategies that disable both gates.
		// Broker forced liquidation applies only to physically-settled (assignable) roots; cash-settled
		// index options (XSP/SPXW) settle in cash with no assignment, so they're never force-closed.
		var forceCloseActive = _config.Rules.CloseBeforeShortExpiry.Enabled
			&& !OptionSettlement.CashSettledIndexRoots.Contains(pos.Ticker);
		if (!slTarget.HasValue && legInRule == null && completeCondorRule == null && !forceCloseActive) return false;

		// Track intraday range running from session start to the current minute. Used by LegInShort's
		// trend-day filter. We use the first minute bar's open as the day's open reference (close to
		// 09:30 ET when the data covers the full session).
		var dayOpenSpot = minuteBars.Count > 0 ? minuteBars[0].Open : 0m;
		decimal dayHigh = dayOpenSpot, dayLow = dayOpenSpot;

		// Walk minute bars. At each minute, re-price the position at that minute's bar.Open spot
		// (start-of-minute price; consistent with ctx.Now semantics elsewhere in the simulator)
		// using a remaining-session TTE. Trigger on first SL/TP crossing.
		foreach (var minuteBar in minuteBars)
		{
			cancellation.ThrowIfCancellationRequested();
			var minuteUtc = minuteBar.Timestamp;
			var spot = minuteBar.Open;
			var minutesToClose = Math.Max(1.0, (endUtc - minuteUtc).TotalMinutes);
			var minuteZeroDteTimeYears = minutesToClose / 60.0 / 24.0 / 365.0;

			// Update intraday range with this minute's H/L.
			if (minuteBar.High > dayHigh) dayHigh = minuteBar.High;
			if (minuteBar.Low < dayLow) dayLow = minuteBar.Low;

			// Causal VIX at this minute: advance to the last VIX bar at-or-before the decision minute.
			// A bar stamped exactly at this minute contributes its OPEN (the M:00 print); an earlier bar
			// its CLOSE (completed before this minute). Missing tape → prior-day settled close.
			while (vixCursor < vixBars.Count && vixBars[vixCursor].Timestamp <= minuteUtc) vixCursor++;
			var vixNow = fallbackVix;
			if (vixCursor > 0)
			{
				var vb = vixBars[vixCursor - 1];
				vixNow = vb.Timestamp == minuteUtc ? vb.Open : vb.Close;
			}

			// For LegInShort: pre-generate candidate strikes around spot at the long's expiry so the
			// rule has multiple strikes to pick from. Only done when the rule is applicable to this
			// position's structure; otherwise we fetch just the position's leg symbols.
			IEnumerable<string> quoteSymbols = symbols;
			if (legInRule != null || completeCondorRule != null)
			{
				// Side of the strikes to pre-price: LegInShort sells on the long's own side; CompleteCondor
				// sells the side OPPOSITE the held vertical. Expiry is the position's (both rules act at the
				// position's single expiry).
				var anchorLeg = pos.Legs[0];
				var candidateSide = legInRule != null
					? anchorLeg.CallPut!
					: (anchorLeg.CallPut == "P" ? "C" : "P");
				var step5 = _config.Indicators.StrikeStep > 0m ? _config.Indicators.StrikeStep : 5m;
				var candidateSymbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
				// ±100 points (at $5 step → 40 strikes) covers the 0.05–0.95 delta range for SPXW even
				// at short DTE with elevated IV. Cheap enough at minute resolution.
				for (decimal k = Math.Floor(spot / step5) * step5 - 100m; k <= Math.Ceiling(spot / step5) * step5 + 100m; k += step5)
				{
					if (k <= 0m) continue;
					candidateSymbols.Add(MatchKeys.OccSymbol(pos.Ticker, anchorLeg.Expiry!.Value, k, candidateSide));
				}
				quoteSymbols = candidateSymbols;
			}
			// Price the position at this minute through the SAME captured-bar-preferred path the entry used
			// (GetQuotesAsync with a minute-ET asOf + spot/TTE overrides), NOT the parametric-only intraday
			// path — otherwise the SL/TP mark and the close fill diverge from the entry's captured-bar pricing,
			// producing phantom early exits (e.g. a $1-wide credit spread opened at a captured $0.12 credit but
			// marked at a Black-Scholes $0.01 → fake ~90% TakeProfit). BS stays the fallback for legs with no bar.
			var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);
			var minuteOverrides = new QuoteOverrides(
				Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot },
				ZeroDteTimeYears: minuteZeroDteTimeYears);
			var quotes = (await _quotes.GetQuotesAsync(minuteEt, quoteSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pos.Ticker }, cancellation, minuteOverrides)).Options;
			var mark = ComputeMarkFromQuotes(pos.Legs, quotes);
			if (!mark.HasValue) continue;

			// Try LegInShort first at this minute. If it fires, execute the leg-in and stop the
			// minute walk for this position — the now-vertical settles at expire. We forgo intraday
			// SL/TP on the post-leg-in vertical (simplest pass; vertical's tighter risk profile
			// makes this a benign approximation for 0DTE).
			if (legInRule != null)
			{
				var legInMinuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);
				var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot };
				var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
				decimal? rangePct = dayOpenSpot > 0m ? (dayHigh - dayLow) / dayOpenSpot * 100m : null;
				var minuteCtx = new EvaluationContext(legInMinuteEt, stillOpen, underlyings, quotes, cash, accountValue, emptySignals, vixNow, rangePct);
				var legInProposal = legInRule.Evaluate(pos, minuteCtx);
				if (legInProposal != null)
				{
					// Single sell-to-open leg from the proposal. Fill under the active pricing mode: bidask
					// hits the bid (we're selling), mid takes the spread midpoint.
					var shortLeg = legInProposal.Legs[0];
					if (quotes.TryGetValue(shortLeg.Symbol, out var sq) && sq.Bid is decimal sBid && sq.Ask is decimal sAsk)
					{
						// Map to OpenStructureKind. Credit-spread mode → ShortVertical (single kind covers
						// both bear-call and bull-put credit spreads); debit-spread → LongCallVertical or
						// LongPutVertical depending on original long direction.
						OpenStructureKind newStructure;
						if (_config.Rules.LegInShort.CreditSpread)
							newStructure = (string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.ShortCallVertical : OpenStructureKind.ShortPutVertical);
						else
							newStructure = string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongCallVertical : OpenStructureKind.LongPutVertical;
						var legInFills = new[] { new BacktestLegFill(shortLeg.Symbol, Side.Sell, pos.Quantity, ExecPrice(Side.Sell, sBid, sAsk)) };
						_book.LegIn(legInMinuteEt, pos.Key, legInFills, "LegInShortRule", newStructure, spot);
						return true; // mirror SL/TP semantics: a fired rule consumes the position for the day.
					}
				}
			}

			// CompleteCondor: try converting the held short vertical into an iron condor at this minute.
			// Fires before SL/TP — but its held-side-cushion gate means it only triggers when the held
			// short is comfortably OTM, i.e. nowhere near the stop. On fire we add the opposite vertical
			// and consume the position for the day (the condor rides to expiry, same simplification as the
			// leg-in path above).
			if (completeCondorRule != null)
			{
				var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot };
				var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
				decimal? rangePct = dayOpenSpot > 0m ? (dayHigh - dayLow) / dayOpenSpot * 100m : null;
				var condorCtx = new EvaluationContext(minuteEt, stillOpen, underlyings, quotes, cash, accountValue, emptySignals, vixNow, rangePct);
				var condorProposal = completeCondorRule.Evaluate(pos, condorCtx);
				if (condorProposal != null)
				{
					var sellLeg = condorProposal.Legs.First(l => string.Equals(l.Action, "sell", StringComparison.OrdinalIgnoreCase));
					var buyLeg = condorProposal.Legs.First(l => string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase));
					if (quotes.TryGetValue(sellLeg.Symbol, out var csq) && csq.Bid is decimal csBid && csq.Ask is decimal csAsk
						&& quotes.TryGetValue(buyLeg.Symbol, out var cbq) && cbq.Bid is decimal cbBid && cbq.Ask is decimal cbAsk)
					{
						var condorFills = new[]
						{
							new BacktestLegFill(sellLeg.Symbol, Side.Sell, pos.Quantity, ExecPrice(Side.Sell, csBid, csAsk)),
							new BacktestLegFill(buyLeg.Symbol, Side.Buy, pos.Quantity, ExecPrice(Side.Buy, cbBid, cbAsk))
						};
						_book.LegIn(minuteEt, pos.Key, condorFills, "CompleteCondorRule", OpenStructureKind.IronCondor, spot);
						return true;
					}
				}
			}

			// Broker forced liquidation: an assignable position whose short is ITM in the final window before
			// settlement is closed by the broker at the current mark (Webull liquidates ITM SPY ~15:30 ET) to
			// avoid assignment, rather than riding to 16:00 intrinsic. Fires at the first in-window minute the
			// short is ITM. (The multi-day EOD path is handled by CloseBeforeShortExpiryRule; this is the 0DTE
			// intraday analog, scoped to its assignment-risk leg only — no profit/BE gating.)
			if (forceCloseActive && minutesToClose <= _config.Rules.CloseBeforeShortExpiry.BrokerForceCloseMinutesBeforeClose && HasAtRiskShortLeg(pos.Legs, spot, _config.Rules.CloseBeforeShortExpiry.BrokerForceCloseMoneynessBufferPct))
			{
				var fcFills = new List<BacktestLegFill>(pos.Legs.Count);
				bool fcPriced = true;
				foreach (var leg in pos.Legs)
				{
					if (leg.CallPut == null) continue;
					if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) { fcPriced = false; break; }
					var cs = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
					fcFills.Add(new BacktestLegFill(leg.Symbol, cs, pos.Quantity, ExecPrice(cs, q.Bid.Value, q.Ask.Value)));
				}
				if (fcPriced) { _book.Close(minuteEt, pos.Key, fcFills, "CloseBeforeShortExpiryRule", spot); return true; }
			}

			bool slFires = slTarget.HasValue && mark.Value <= slTarget.Value;
			if (!slFires) continue;

			var ruleName = "StopLossRule";

			var legFills = new List<BacktestLegFill>(pos.Legs.Count);
			bool allLegsPriced = true;
			foreach (var leg in pos.Legs)
			{
				if (leg.CallPut == null) continue;
				if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) { allLegsPriced = false; break; }
				var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
				legFills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, ExecPrice(closeSide, q.Bid.Value, q.Ask.Value)));
			}
			if (!allLegsPriced) continue;

			_book.Close(minuteEt, pos.Key, legFills, ruleName, spot);
			return true;
		}

		return false;
	}

	// True when any short leg is ITM (bufferPct 0) or within bufferPct of the money — "at risk" of finishing
	// ITM (short call: spot > strike×(1−b); short put: spot < strike×(1+b)).
	private static bool HasAtRiskShortLeg(IReadOnlyList<PositionLeg> legs, decimal spot, decimal bufferPct)
	{
		var b = bufferPct;
		foreach (var leg in legs)
		{
			if (leg.Side != Side.Sell || leg.CallPut == null) continue;
			if (leg.CallPut == "C" ? spot > leg.Strike * (1m - b) : spot < leg.Strike * (1m + b)) return true;
		}
		return false;
	}

	private async Task TryIntradayTriggerAsync(
		DateTime step, OpenPosition pos, decimal barOpen, decimal barLow, decimal barHigh,
		decimal cash, decimal accountValue,
		OpenerRealizedExpectancyConfig realizedExpectancy, CancellationToken cancellation)
	{
		var symbols = pos.Legs
			.Where(l => l.CallPut != null)
			.Select(l => l.Symbol)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (symbols.Count == 0) return;

		// Mark at the two extremes — these bracket the day's range of possible MTM values.
		var quotesLow = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, barLow, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
		var quotesHigh = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, barHigh, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
		var markLow = ComputeMarkFromQuotes(pos.Legs, quotesLow);
		var markHigh = ComputeMarkFromQuotes(pos.Legs, quotesHigh);
		if (!markLow.HasValue || !markHigh.HasValue) return;

		// SL threshold (mark at or below this = stop-loss triggered).
		// realizedLoss = InitialNetDebit - mark; SL fires when realizedLoss ≥ slPct × maxLoss,
		// i.e. when mark ≤ InitialNetDebit - slPct × maxLoss.
		//
		// slPct ≥ 1.0 disables the stop: the threshold would equal (or fall below) the position's
		// theoretical max-loss mark, which mirrors the scorer's terminal-PnL clamp at -1.0×maxLoss
		// (no effective stop). For debit structures this matters concretely — slTarget would equal 0
		// and the BS-priced mark of a deep-OTM 0DTE leg can round to exactly 0, firing SL the same
		// morning the position opens and foreclosing intraday recovery. Letting it run hits the same
		// economic outcome via expiration if the day actually ends at the floor.
		decimal? slTarget = null;
		if (_config.Rules.StopLoss.Enabled && pos.MaxLossPerShare.HasValue && pos.MaxLossPerShare.Value > 0m
			&& realizedExpectancy.StopLossPctOfMaxLoss < 1m)
			slTarget = pos.AdjustedNetDebit - realizedExpectancy.StopLossPctOfMaxLoss * pos.MaxLossPerShare.Value;

		// Intraday take-profit was Target-B only; removed (Target A fires at start-of-day in the main loop).

		// Does the stop threshold sit in [markLow, markHigh]? The mark is monotonic in spot for every
		// structure currently enumerated (verticals, naked longs, iron condors). If the threshold
		// lies between the two extreme marks, there's a spot in [barLow, barHigh] where mark equals
		// the threshold.
		decimal markMin = Math.Min(markLow.Value, markHigh.Value);
		decimal markMax = Math.Max(markLow.Value, markHigh.Value);
		bool slFires = slTarget.HasValue && slTarget.Value >= markMin && slTarget.Value <= markMax;

		// Also catch the case where the position was ALREADY past the threshold at bar.Open. We don't
		// have bar.Open here, but if BOTH extreme marks are past the threshold the day opened past it.
		// Trigger and close at the threshold price (conservative — gives back any deeper capture).
		if (slTarget.HasValue && markLow.Value <= slTarget.Value && markHigh.Value <= slTarget.Value) slFires = true;

		if (!slFires) return;

		string ruleName = "StopLossRule";
		decimal targetMark = slTarget!.Value;

		// Find the spot in [barLow, barHigh] where the position mark equals targetMark.
		var thresholdSpot = await FindSpotForMarkAsync(
			step, pos, symbols, barLow, markLow.Value, barHigh, markHigh.Value, targetMark, cancellation);
		var quotesAtThreshold = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, thresholdSpot, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);

		// Reverse each leg's side to close. Fill price follows the active pricing mode (mid or cross-spread).
		var legFills = new List<BacktestLegFill>(pos.Legs.Count);
		foreach (var leg in pos.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!quotesAtThreshold.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return;
			var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
			legFills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, ExecPrice(closeSide, q.Bid.Value, q.Ask.Value)));
		}

		_book.Close(step, pos.Key, legFills, ruleName, thresholdSpot);
	}

	/// <summary>Bisects spot in <c>[spotLow, spotHigh]</c> to find where the position's mark equals
	/// <paramref name="targetMark"/>. Mark monotonicity in spot is assumed (true for every structure
	/// the opener currently enumerates). Caller is responsible for verifying that <paramref name="targetMark"/>
	/// lies between <paramref name="markAtLow"/> and <paramref name="markAtHigh"/> — otherwise this
	/// returns the boundary spot closest to the target. 16 iterations gives <c>(spotHigh - spotLow) / 65536</c>
	/// spot precision — fractions of a penny for any realistic intraday range.</summary>
	private async Task<decimal> FindSpotForMarkAsync(
		DateTime step, OpenPosition pos, HashSet<string> symbols,
		decimal spotLow, decimal markAtLow, decimal spotHigh, decimal markAtHigh,
		decimal targetMark, CancellationToken cancellation)
	{
		if (spotHigh - spotLow < 0.01m) return (spotLow + spotHigh) / 2m;
		bool markIncreases = markAtHigh > markAtLow;
		decimal lo = spotLow, hi = spotHigh;

		for (int i = 0; i < 16 && hi - lo > 0.01m; i++)
		{
			cancellation.ThrowIfCancellationRequested();
			var mid = (lo + hi) / 2m;
			var quotes = await _quotes.GetIntradayQuotesAsync(
				step, pos.Ticker, mid, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
			var mark = ComputeMarkFromQuotes(pos.Legs, quotes);
			if (!mark.HasValue) break;

			if (markIncreases ? mark.Value < targetMark : mark.Value > targetMark)
				lo = mid;
			else
				hi = mid;
		}
		return (lo + hi) / 2m;
	}

	/// <summary>Result of <see cref="TryOpenAcrossDayAsync"/>. <c>HasIntraday</c> indicates whether
	/// per-minute scan ran (we found minute data for every configured ticker); when false the caller
	/// must execute the legacy 09:30 single-call path using <c>LegacyProposals</c>.</summary>
	private readonly record struct DailyOpenScanResult(bool HasIntraday, IReadOnlyList<OpenProposal> LegacyProposals);

	/// <summary>Scans each minute of the trading day (09:30 → 16:00 ET — see <see cref="MarketOpenTime"/>) and opens the first proposal
	/// that clears the score + cash gates. Spot is taken from the per-minute close of the configured
	/// ticker's intraday CSV; 0DTE TTE shrinks linearly to 16:00. One open per day max (early-exit on
	/// first fill). Returns <c>HasIntraday=false</c> when minute data is missing for any ticker —
	/// caller falls back to the legacy once-per-day fill.</summary>
	private async Task<DailyOpenScanResult> TryOpenAcrossDayAsync(DateTime step, HashSet<string> tickerSet, OpenCandidateEvaluator openEvaluator, CancellationToken cancellation)
	{
		// Shared per-day inputs — these depend only on the date, not the minute.
		var postMgmt = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
		var (postCash, postAccount) = await _positions.GetAccountStateAsync(step, cancellation);
		var postSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);

		// Load each ticker's minute bars for the RTH window. Convert step.Date + (09:30 ET, 16:00 ET)
		// to UTC for the cache call — the cache returns bars in UTC, but the on-disk grouping is by
		// NY date, so any ET-correct window spans the right file. Window starts at 09:30 (first RTH
		// minute under our normalized start-of-bar convention; see `MarketOpenTime`).
		var openEt = new DateTime(step.Date.Year, step.Date.Month, step.Date.Day, 9, 30, 0, DateTimeKind.Unspecified);
		var closeEt = new DateTime(step.Date.Year, step.Date.Month, step.Date.Day, 16, 0, 0, DateTimeKind.Unspecified);
		var openUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(openEt, NyTz), TimeSpan.Zero);
		var closeUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(closeEt, NyTz), TimeSpan.Zero);

		var barsByTicker = new Dictionary<string, IReadOnlyList<MinuteBar>>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in tickerSet)
		{
			var bars = await _intradayBars.GetBarsAsync(t, openUtc, closeUtc, BarInterval.M1, includeExtended: false, cancellation);
			if (bars.Count == 0)
			{
				// Fall back to legacy path: caller runs the once-per-day opener at 09:30.
				var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, step, _quotes, _config, cancellation);
				var postCtx = new EvaluationContext(step, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
				var legacy = await openEvaluator.EvaluateAsync(postCtx, cancellation);
				// Drop Informational (display-only, below-gate) proposals so log level can't change fills — see
				// the equivalent filter in EvaluateMinuteAsync and OpenerAutoExecutor.
				return new DailyOpenScanResult(HasIntraday: false, LegacyProposals: legacy.Where(p => !p.Informational).ToList());
			}
			barsByTicker[t] = bars;
		}

		// Index per ticker by UTC timestamp for O(1) per-minute lookup.
		var barIndexByTicker = new Dictionary<string, Dictionary<DateTimeOffset, MinuteBar>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (t, bars) in barsByTicker)
		{
			var idx = new Dictionary<DateTimeOffset, MinuteBar>(bars.Count);
			foreach (var b in bars) idx[b.Timestamp] = b;
			barIndexByTicker[t] = idx;
		}

		// Loop driver: union of timestamps across tickers, sorted. With aligned SPY/SPXW CSVs the
		// union equals either ticker's bars; the loop generalizes if other tickers get added.
		var allTimestamps = new SortedSet<DateTimeOffset>();
		foreach (var bars in barsByTicker.Values)
			foreach (var b in bars) allTimestamps.Add(b.Timestamp);

		// Oracle mode: scan every minute, forward-simulate every proposal to expiry using the day's
		// known bar.Close intrinsic, and open the single (minute, proposal) pair with the highest
		// realized P&L. By design lookahead — research / upper-bound tool, not realistic.
		(DateTime MinuteEt, OpenProposal Proposal, IReadOnlyList<BacktestLegFill> LegFills, decimal Pnl)? bestOracle = null;

		// Parallel minute evaluation. EvaluateMinuteAsync is a pure function over its inputs (no
		// shared mutable state) so all minutes can be evaluated concurrently. The results are then
		// processed in chronological order for entry-decision / oracle logic.
		// Earliest-entry gate: withhold opens until a configured ET wall-clock time so the intraday tape
		// can form and blend into the bias before the directional read is committed (vs trading the stale
		// 09:30 overnight macro). Parsed once per day; null/empty = no delay.
		TimeSpan? earliestEntry = null;
		if (!string.IsNullOrWhiteSpace(_config.Opener.EarliestEntryTimeEt)
			&& TimeSpan.TryParse(_config.Opener.EarliestEntryTimeEt, System.Globalization.CultureInfo.InvariantCulture, out var ee))
			earliestEntry = ee;

		var timestampList = allTimestamps.ToList();
		// Open-scan stride: evaluate every Nth minute instead of all 390. On days that never open, the
		// scan would otherwise re-run the (heavy, for multi-leg structures) candidate enumeration every
		// minute. A stride caps that cost ~N× at the price of N-minute entry granularity — negligible for
		// a backtest, decisive for a multi-leg full-year run. Stride 1 (default) = exhaustive, unchanged.
		// Oracle always scans every minute (it needs the full surface), so the stride is open-scan only.
		if (_scanStride > 1 && !_oracle && timestampList.Count > 0)
		{
			var strided = new List<DateTimeOffset>(timestampList.Count / _scanStride + 1);
			for (var i = 0; i < timestampList.Count; i += _scanStride) strided.Add(timestampList[i]);
			timestampList = strided;
		}

		// In non-oracle mode we only need the first qualifying minute. Evaluate in parallel batches of
		// ProcessorCount and stop as soon as any minute in the batch yields a qualifying entry. This gives
		// ~Nx speedup while preserving chronological entry semantics. Oracle mode needs every minute
		// evaluated, so it runs the full parallel pass.
		var needAllMinutes = _oracle;
		var batchSize = Math.Max(1, Environment.ProcessorCount);
		var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = batchSize, CancellationToken = cancellation };

		var opened = 0;
		var entryDecided = false;

		if (needAllMinutes)
		{
			// Full parallel pass: evaluate all minutes, then process sequentially.
			var minuteResults = new (DateTimeOffset MinuteUtc, DateTime MinuteEt, IReadOnlyList<OpenProposal> Proposals)?[timestampList.Count];
			await Parallel.ForEachAsync(Enumerable.Range(0, timestampList.Count), parallelOpts, async (idx, ct) =>
			{
				var result = await EvaluateMinuteAsync(timestampList[idx], closeUtc, tickerSet, barIndexByTicker, postMgmt, postCash, postAccount, postSignals, openEvaluator, ct);
				if (result != null)
					minuteResults[idx] = (timestampList[idx], result.Value.MinuteEt, result.Value.Proposals);
			});

			for (int i = 0; i < minuteResults.Length; i++)
			{
				var mr = minuteResults[i];
				if (mr == null) continue;
				var (minuteUtc2, minuteEt, openProposals) = mr.Value;

				if (_oracle)
				{
					foreach (var p in openProposals)
					{
						var qty = ResolveOpenQty(p);
						if (qty < 1) continue;
						var legFills = BuildLegFillsFromProposal(p.Legs, qty);
						if (legFills == null) continue;
						var eodPnl = await ComputeOracleEodPnlAsync(p, step.Date, cancellation);
						if (eodPnl == null) continue;
						if (bestOracle == null || eodPnl.Value > bestOracle.Value.Pnl)
							bestOracle = (minuteEt, p, legFills, eodPnl.Value);
					}
					continue;
				}

				if (opened >= _topNPerStep) break;
				if (entryDecided) break;
				var minuteTimeEt = TimeZoneInfo.ConvertTime(minuteUtc2, NyTz).TimeOfDay;
				if (earliestEntry.HasValue && minuteTimeEt < earliestEntry.Value) continue;
				if (openProposals.Count > 0)
				{
					entryDecided = true;
					foreach (var p in openProposals.Take(_topNPerStep - opened))
					{
						var qty = ResolveOpenQty(p);
						if (qty < 1) continue;
						if (OpenProposalIntoBook(minuteEt, p, qty))
							opened++;
					}
				}
			}
		}
		else
		{
			// Adaptive batch with early exit. A fixed ProcessorCount-wide batch evaluated ~32 minutes just to
			// use minute 1 — and since the chain-seeding fix every minute carries a full chain, the FIRST
			// scanned minute almost always qualifies, so those other 31 minutes (each scoring ~6k skeletons)
			// were pure waste and dominated the runtime. Start with a 1-minute probe and double the batch only
			// when early minutes don't qualify: the common case scores 1 minute, while rare late/no-open days
			// still grow to full parallel width. Chronological "first qualifying minute" semantics preserved.
			var scanBatch = 1;
			for (int batchStart = 0; batchStart < timestampList.Count && !entryDecided && opened < _topNPerStep; )
			{
				cancellation.ThrowIfCancellationRequested();
				var batchCount = Math.Min(scanBatch, timestampList.Count - batchStart);
				var batchResults = new (DateTimeOffset MinuteUtc, DateTime MinuteEt, IReadOnlyList<OpenProposal> Proposals)?[batchCount];

				await Parallel.ForEachAsync(Enumerable.Range(0, batchCount), parallelOpts, async (idx, ct) =>
				{
					var result = await EvaluateMinuteAsync(timestampList[batchStart + idx], closeUtc, tickerSet, barIndexByTicker, postMgmt, postCash, postAccount, postSignals, openEvaluator, ct);
					if (result != null)
						batchResults[idx] = (timestampList[batchStart + idx], result.Value.MinuteEt, result.Value.Proposals);
				});

				// Process batch results in chronological order.
				for (int i = 0; i < batchCount && !entryDecided && opened < _topNPerStep; i++)
				{
					var mr = batchResults[i];
					if (mr == null) continue;
					var (minuteUtc2, minuteEt, openProposals) = mr.Value;

					var minuteTimeEt = TimeZoneInfo.ConvertTime(minuteUtc2, NyTz).TimeOfDay;
					if (earliestEntry.HasValue && minuteTimeEt < earliestEntry.Value) continue;
					if (openProposals.Count > 0)
					{
						entryDecided = true;
						foreach (var p in openProposals.Take(_topNPerStep - opened))
						{
							var qty = ResolveOpenQty(p);
							if (qty < 1) continue;
							if (OpenProposalIntoBook(minuteEt, p, qty))
								opened++;
						}
					}
				}
				batchStart += batchCount;
				scanBatch = Math.Min(scanBatch * 2, batchSize);
			}
		}

		// Oracle: open the single best proposal across all minutes.
		if (_oracle && bestOracle != null)
		{
			var b = bestOracle.Value;
			_book.Open(b.MinuteEt, b.Proposal.Ticker, b.Proposal.StructureKind, b.LegFills, b.Proposal.Qty, b.Proposal.Spot, b.Proposal.RawScore, b.Proposal.FinalScore, b.Proposal.ImpliedVolatilityAnnual);
		}

		return new DailyOpenScanResult(HasIntraday: true, LegacyProposals: Array.Empty<OpenProposal>());
	}

	/// <summary>Evaluates the opener at a single minute. Pure function over the inputs — no shared
	/// mutable state — so concurrent calls are safe. Returns null when any configured ticker has no
	/// bar at this minute (the opener would have no spot to work with).</summary>
	private async Task<(DateTime MinuteEt, IReadOnlyList<OpenProposal> Proposals)?> EvaluateMinuteAsync(
		DateTimeOffset minuteUtc,
		DateTimeOffset closeUtc,
		HashSet<string> tickerSet,
		Dictionary<string, Dictionary<DateTimeOffset, MinuteBar>> barIndexByTicker,
		IReadOnlyDictionary<string, OpenPosition> postMgmt,
		decimal postCash,
		decimal postAccount,
		IReadOnlyDictionary<string, TechnicalBias> postSignals,
		OpenCandidateEvaluator openEvaluator,
		CancellationToken cancellation)
	{
		// Build per-minute spot dict. Skip when any required ticker has no bar at this minute — the
		// opener's bootstrap would have no spot to work with. Anchor the strike-selection spot on the
		// bar's TIME-MIDPOINT (Open+Close)/2, NOT bar.Open: live's tick scheduler (wa ai watch at :30
		// each minute) samples ~30s into the bar, and the leg-pricing path already prices on this same
		// midpoint (see BacktestQuoteSource: "Using bar.Open systematically under-samples by ~30 seconds
		// on trending minutes"). Anchoring strikes on bar.Open while pricing legs on the midpoint made
		// the backtest pick systematically lower strikes than live on up-drifting minutes. The midpoint
		// keeps the same within-minute semantic as pricing and does NOT introduce the full 1-minute
		// lookahead that bar.Close would — the 09:30==^GSPC.Open anchor invariant governs the SPXW
		// curve/leg pricing, not the opener's spot.
		var minuteSpots = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in tickerSet)
		{
			if (!barIndexByTicker[t].TryGetValue(minuteUtc, out var bar)) return null;
			minuteSpots[t] = (bar.Open + bar.Close) / 2m;
		}

		// TTE for 0DTE legs: remaining minutes from this bar to 16:00 ET, in years. Floored at 1
		// minute so deep-OTM 0DTE doesn't price exactly intrinsic before the actual close — the
		// existing settlement path handles the at-close cash flow.
		var minutesToClose = Math.Max(1.0, (closeUtc - minuteUtc).TotalMinutes);
		var minuteZeroDteTimeYears = minutesToClose / 60.0 / 24.0 / 365.0;

		// Convert the bar's UTC timestamp back to ET-naive (DateTimeKind.Unspecified) so it matches
		// the simulator's ET-wall-clock convention.
		var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);

		var minuteOverrides = new QuoteOverrides(Spots: minuteSpots, ZeroDteTimeYears: minuteZeroDteTimeYears);
		var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, minuteEt, _quotes, _config, cancellation, minuteOverrides);
		var postCtx = new EvaluationContext(minuteEt, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
		var proposals = await openEvaluator.EvaluateAsync(postCtx, cancellation, minuteOverrides);
		// Mirror OpenerAutoExecutor: Informational proposals (the structure-coverage floor's best-of-each-
		// enabled-structure, surfaced under --log-level debug for visibility) are display-only and below the
		// open gate. Live filters them before executing; the backtest must too, or log level changes which
		// trades open (a debug-only candidate that doesn't clear MinScoreToOpen would otherwise fill here).
		return (minuteEt, proposals.Where(p => !p.Informational).ToList());
	}

	/// <summary>Oracle forward-simulator: returns the realized P&L (in dollars) of <paramref name="p"/>
	/// if held from open to expiry, using the day's bar.Close as the at-expiry spot. Returns null when
	/// any leg expires on a date other than <paramref name="stepDate"/> (multi-day positions need a
	/// chained forward sim we don't do here — out of scope for the SPXW 0DTE strategy this is built
	/// for) or when the close-bar / leg metadata can't be resolved.</summary>
	private async Task<decimal?> ComputeOracleEodPnlAsync(OpenProposal p, DateTime stepDate, CancellationToken cancellation)
	{
		var bar = await _bars.GetBarAsync(p.Ticker, stepDate, cancellation);
		if (bar == null) return null;
		var closeSpot = bar.Close;

		decimal netOpenCashPerContract = 0m;
		decimal netExpireCashPerContract = 0m;
		foreach (var leg in p.Legs)
		{
			if (!leg.PricePerShare.HasValue) return null;
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed == null || parsed.CallPut == null) return null;
			var isBuy = string.Equals(leg.Action, "buy", StringComparison.OrdinalIgnoreCase);

			// Open cash flow per share per contract: buys are debits (negative), sells are credits (+).
			netOpenCashPerContract += (isBuy ? -1m : 1m) * leg.PricePerShare.Value;

			// Multi-day expiry: can't oracle without a chained forward sim. Skip.
			if (parsed.ExpiryDate.Date != stepDate.Date) return null;

			var intrinsic = parsed.CallPut == "C"
				? Math.Max(0m, closeSpot - parsed.Strike)
				: Math.Max(0m, parsed.Strike - closeSpot);
			// Settlement: long legs collect intrinsic (+), short legs pay (-). Matches
			// SimulatedBook.Expire's signing convention.
			netExpireCashPerContract += (isBuy ? 1m : -1m) * intrinsic;
		}

		const decimal multiplier = 100m;
		var netPnlPerContract = (netOpenCashPerContract + netExpireCashPerContract) * multiplier;
		var grossPnl = netPnlPerContract * p.Qty;
		// Fees on Open only; expirations are fee-free in the simulator. Matches SimulatedBook.
		var openFees = p.Legs.Count * p.Qty * _book.FeePerContract;
		return grossPnl - openFees;
	}

	/// <summary>Per-share mark = sum of leg mids signed by side. Returns null if any leg is missing
	/// a usable bid/ask. Matches <see cref="StopLossRule.ComputeMarkPerShare"/> and
	/// <see cref="TakeProfitRule.ComputeMarkPerContract"/> exactly (per-share, not per-contract —
	/// the two rule names are misleadingly different but the units are the same: per-share dollars).</summary>
	private static decimal? ComputeMarkFromQuotes(IReadOnlyList<PositionLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (leg.CallPut == null) continue;
			if (!quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (!q.Bid.HasValue || !q.Ask.HasValue) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	private async Task SettleExpirationsAsync(DateTime step, CancellationToken cancellation)
	{
		var expired = _book.OpenPositions.Values
			.Where(p => p.Legs.Any(l => l.Expiry.HasValue && l.Expiry.Value.Date <= step.Date))
			.Select(p => p.Key)
			.ToList();
		// Stamp expirations at 16:00 ET — the bar.Close is what determines intrinsic, so the fill
		// time should match the price source for ledger verification.
		var settleTime = step.Date.Add(MarketCloseTime);
		foreach (var key in expired)
		{
			if (!_book.OpenPositions.TryGetValue(key, out var pos)) continue;
			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar == null) continue;
			var survivorKey = _book.Expire(settleTime, key, bar.Close);

			// Partial expiry left a surviving leg (a calendar/diagonal's long after the short expired). Close
			// it at THIS day's close at market value rather than carrying it overnight. The daily management
			// pass already ran at the open, so an unclosed survivor would otherwise sit until the next day —
			// bleeding a day of theta and an overnight gap as an unintended naked directional leg. Closing at
			// market (not intrinsic) preserves the remaining extrinsic, which was the point of the partial settle.
			if (survivorKey != null)
				await CloseSurvivorAtCloseAsync(survivorKey, pos.Ticker, settleTime, bar.Close, cancellation);
		}
	}

	/// <summary>Closes a partial-expiry survivor at the short-expiry day's close, priced at market under the
	/// active pricing mode (mid/bidask) with the day's close as spot. Leaves it open only if a leg can't be
	/// priced — next day's management then handles it (the prior fallback behavior).</summary>
	private async Task CloseSurvivorAtCloseAsync(string survivorKey, string ticker, DateTime settleTime, decimal spotAtClose, CancellationToken cancellation)
	{
		if (!_book.OpenPositions.TryGetValue(survivorKey, out var pos)) return;
		var symbols = pos.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (symbols.Count == 0) return;

		var overrides = new QuoteOverrides(Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spotAtClose });
		var snap = await _quotes.GetQuotesAsync(settleTime, symbols, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker }, cancellation, overrides);

		var fills = new List<BacktestLegFill>(pos.Legs.Count);
		foreach (var leg in pos.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!snap.Options.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return; // can't price → leave open for next-day management
			var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
			fills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, ExecPrice(closeSide, q.Bid.Value, q.Ask.Value)));
		}
		if (fills.Count > 0)
			_book.Close(settleTime, survivorKey, fills, "CloseSurvivorOnShortExpiry", spotAtClose);
	}

	private async Task<decimal> ComputeOpenMarkAsync(DateTime step, CancellationToken cancellation)
	{
		if (_book.OpenPositions.Count == 0) return 0m;
		var symbols = _book.OpenPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var tickers = _book.OpenPositions.Values.Select(p => p.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var snap = await _quotes.GetQuotesAsync(step, symbols, tickers, cancellation);

		decimal total = 0m;
		foreach (var pos in _book.OpenPositions.Values)
		{
			decimal perContract = 0m;
			foreach (var leg in pos.Legs)
			{
				// Prefer the BS-priced mid from the quote source. If a leg can't be priced (e.g. a sparse
				// edge case in the bar cache), fall back to intrinsic value rather than zeroing out the
				// whole position — that would falsely report a big drawdown on a single sparse day.
				decimal legValue;
				if (snap.Options.TryGetValue(leg.Symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue)
				{
					legValue = (q.Bid.Value + q.Ask.Value) / 2m;
				}
				else if (snap.Underlyings.TryGetValue(pos.Ticker, out var spot) && leg.CallPut != null)
				{
					legValue = leg.CallPut == "C" ? Math.Max(0m, spot - leg.Strike) : Math.Max(0m, leg.Strike - spot);
				}
				else
				{
					// No spot, no BS — best we can do is treat the leg at zero. Note that this is the same
					// behavior the old code had for the WHOLE position; here we localize it to one leg so the
					// rest of the position still contributes.
					legValue = 0m;
				}
				perContract += leg.Side == Side.Buy ? legValue : -legValue;
			}
			total += perContract * 100m * pos.Quantity;
		}
		return total;
	}

	/// <summary>Pricing-provenance diagnostic. Under the quotes-only path every market-priced fill is backed
	/// by a real NBBO quote by construction — a leg with no quote is omitted from the snapshot, so the candidate
	/// never fills — so provenance is 100% real (captured == total) with no synthetic breakdown. Bucketed by leg
	/// DTE-at-fill (0DTE vs >0DTE) for the renderer; Expire fills are excluded (they settle at real bar.Close
	/// intrinsic, not a model price).</summary>
	private PricingProvenance ComputeProvenance()
	{
		int zTot = 0, mTot = 0;
		foreach (var fill in _book.Fills)
		{
			if (fill.Kind == BacktestFillKind.Expire) continue;
			foreach (var leg in fill.Legs)
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
				if (parsed?.CallPut == null) continue;
				var dte = (parsed.ExpiryDate.Date - fill.Date.Date).Days;
				if (dte <= 0) zTot++;
				else mTot++;
			}
		}
		// captured == total (all real), no synthetic legs → all the breakdown buckets are zero.
		return new PricingProvenance(zTot, zTot, mTot, mTot, 0, 0, 0, 0, 0, 0, 0);
	}

	/// <summary>Per-trade cleanliness split. Under the quotes-only path every market-priced fill used real NBBO
	/// by construction, so every finalized lineage is "clean" (none contaminated). Kept so the renderer can
	/// print the clean-trade P&L row.</summary>
	private CleanlinessBreakdown ComputeCleanliness()
	{
		int cleanN = 0, cleanW = 0, cleanL = 0;
		decimal cleanPnl = 0m, cleanGrossWin = 0m, cleanGrossLoss = 0m;
		foreach (var g in _book.Fills.GroupBy(f => f.LineageId))
		{
			if (!g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire)) continue; // only finalized trades
			var pnl = g.Sum(f => f.NetCashFlow - f.Fees);
			cleanN++; cleanPnl += pnl;
			if (pnl > 0m) { cleanW++; cleanGrossWin += pnl; } else { cleanL++; cleanGrossLoss += Math.Abs(pnl); }
		}
		return new CleanlinessBreakdown(cleanN, cleanPnl, cleanW, cleanL, cleanGrossWin, cleanGrossLoss, 0, 0m, 0, 0);
	}

	private static IEnumerable<DateTime> EnumerateTradingDays(DateTime since, DateTime until)
	{
		var d = since.Date;
		while (d <= until.Date)
		{
			// Skip weekends and NYSE holidays. Pricing the chain on a closed-market day yields zero MTM
			// for every leg (no bar) which would mis-report drawdown as if positions had imploded.
			if (MarketCalendar.IsOpen(d))
				yield return d.Add(MarketOpenTime);
			d = d.AddDays(1);
		}
	}
}

/// <summary>How much of the backtest's realized fills were priced from real captured option bars vs the
/// synthetic Black-Scholes fallback, split by leg DTE-at-fill. A high multi-DTE synthetic fraction is a
/// red flag that calendar/diagonal P&L rests on modeled prices rather than real prints.</summary>
// MultiDteSurfaceIv/VixSmile/Intrinsic break down the SYNTHETIC (non-captured) >0DTE legs by how they were
// priced: SurfaceIv = interpolated off the real same-day smile of captured neighbors (high fidelity);
// VixSmile = parametric VIX1D-anchored ATM+smile (no neighbors found); Intrinsic = no usable IV at all.
// They sum to (MultiDteTotal - MultiDteCaptured). A high VixSmile share is the signal that cross-expiry
// interpolation would add value; a high SurfaceIv share means the synthetic legs are already neighbor-anchored.
// MultiDteVixBracketed/OneSided: of the MultiDteVixSmile (parametric-fallback) legs, how many had a nearby
// captured expiry (same root+right, within the probe's day-window) on BOTH sides of the target (genuine
// total-variance interpolation) vs only ONE side (extrapolation across the gap, far less reliable). The
// remainder (MultiDteVixSmile - bracketed - oneSided) is the irreducible floor: minutes where the whole
// local term structure was untraded, which no interpolation scheme can recover.
internal readonly record struct PricingProvenance(int ZeroDteCaptured, int ZeroDteTotal, int MultiDteCaptured, int MultiDteTotal, int MultiDteSurfaceIv, int MultiDteCrossExpiry, int MultiDteVixSmile, int MultiDteIntrinsic, int MultiDteVixBracketed, int MultiDteVixOneSided, int MultiDtePhantom)
{
	public double ZeroDteCapturedPct => ZeroDteTotal == 0 ? 1.0 : (double)ZeroDteCaptured / ZeroDteTotal;
	public double MultiDteCapturedPct => MultiDteTotal == 0 ? 1.0 : (double)MultiDteCaptured / MultiDteTotal;
	public int MultiDteSynthetic => MultiDteTotal - MultiDteCaptured;
}

/// <summary>Per-trade split by pricing cleanliness: "clean" = every market-priced fill of the trade used real
/// captured bars; "contaminated" = at least one leg was synthetic (entry/exit can be mispriced). Lets the
/// summary show whether the edge lives in the real-priced trades or the synthetic artifacts.</summary>
internal readonly record struct CleanlinessBreakdown(
	int CleanCount, decimal CleanPnl, int CleanWins, int CleanLosses, decimal CleanGrossWin, decimal CleanGrossLoss,
	int ContamCount, decimal ContamPnl, int ContamWins, int ContamLosses)
{
	public decimal CleanProfitFactor => CleanGrossLoss > 0m ? CleanGrossWin / CleanGrossLoss : 0m;
	/// <summary>Profit factor for display: gross win / gross loss, but "∞" when there were no losing trades
	/// (PF is undefined, not zero) and "—" when there were no clean trades at all.</summary>
	public string CleanProfitFactorDisplay =>
		CleanGrossLoss > 0m ? (CleanGrossWin / CleanGrossLoss).ToString("F2") : (CleanGrossWin > 0m ? "∞" : "—");
	public decimal CleanExpectancy => CleanCount > 0 ? CleanPnl / CleanCount : 0m;
	public decimal CleanWinRate => CleanCount > 0 ? CleanWins * 100m / CleanCount : 0m;
	public decimal ContamWinRate => ContamCount > 0 ? ContamWins * 100m / ContamCount : 0m;
}

internal sealed record BacktestResult(
	decimal StartingCash,
	decimal EndingCash,
	decimal TotalFees,
	int OpenFills,
	int CloseFills,
	int RollFills,
	int LegInFills,
	int ExpireFills,
	decimal MaxDrawdown,
	decimal PeakEquity,
	IReadOnlyList<(DateTime Date, decimal Equity)> EquityCurve,
	IReadOnlyList<BacktestFill> Fills,
	IReadOnlyDictionary<long, decimal> EndMtmByLineage,
	PricingProvenance Provenance,
	CleanlinessBreakdown Cleanliness)
{
	/// <summary>P&L on closed lifecycles only (lineages that ended in Close or Expire). Each lifecycle's
	/// P&L = sum of (NetCashFlow - Fees) across all fills sharing its LineageId.</summary>
	public decimal RealizedPnL => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire))
		.Sum(g => g.Sum(f => f.NetCashFlow - f.Fees));

	/// <summary>Unrealized P&L = per-lineage net cash + per-lineage final MTM, summed across still-open lifecycles.</summary>
	public decimal UnrealizedPnL => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => !g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire))
		.Sum(g => g.Sum(f => f.NetCashFlow - f.Fees) + (EndMtmByLineage.TryGetValue(g.Key, out var m) ? m : 0m));

	public decimal TotalPnL => RealizedPnL + UnrealizedPnL;

	public decimal EndingEquity => StartingCash + TotalPnL;

	/// <summary>Per-lifecycle wins/losses (closed lineages only).</summary>
	/// <summary>Per-closed-lifecycle realized P&L — one entry per lineage that ended in Close/Expire.
	/// The building block for per-trade edge stats (expectancy, win/loss averages, profit factor) which,
	/// unlike compounded terminal equity, don't depend on position sizing.</summary>
	public IReadOnlyList<decimal> LifecyclePnLs() => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire))
		.Select(g => g.Sum(f => f.NetCashFlow - f.Fees))
		.ToList();

	public (int wins, int losses) LifecycleWinLoss()
	{
		var closed = LifecyclePnLs();
		return (closed.Count(p => p > 0m), closed.Count(p => p <= 0m));
	}
}

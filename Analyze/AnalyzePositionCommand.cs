using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.IO;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Report;
using WebullAnalytics.Sentiment;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

// ─── `analyze position` ───────────────────────────────────────────────────────

internal sealed class AnalyzePositionSettings : AnalyzeIvOverrideSettings
{
	[CommandArgument(0, "[spec]")]
	[Description("Open position. Format: ACTION:SYMBOL:QTY@PRICE,... where PRICE is your cost basis per leg. Example: sell:GME260424C00025000:499@0.48,buy:GME260515C00025000:499@1.11. Omit to select from open positions interactively.")]
	public string Spec { get; set; } = "";

	[CommandOption("--proposal")]
	[Description("Render the position diagnostic and management scenarios from a stored proposal snapshot instead of live quotes. Format: FILE[[:LINE]] where FILE is a path, a data/ filename (ai-proposals.SPY.0DTE.jsonl), or the TICKER.strategy shorthand (SPY.0DTE); LINE is a 1-based line number, defaulting to the last line.")]
	public string? Proposal { get; set; }

	[CommandOption("--fill [N]")]
	[Description("Analyze one of your recent OPENING fills (Webull order history, last 30 days, any ticker), its per-leg fill prices as the cost basis, whether or not it is still held, at --date: the live market now by default, --date fill for the market at the fill itself. Bare --fill picks interactively; N is the 1-based position in that list, newest first (not a file line).")]
	public FlagValue<int>? Fill { get; set; }

	protected override bool FillAllowed => Fill?.IsSet == true;

	[CommandOption("--iv-default")]
	[Description("Fallback implied volatility for hypothetical legs when no live quote exists. Percent, default 40.")]
	public decimal IvDefault { get; set; } = 40m;

	[CommandOption("--strike-step")]
	[Description("Strike increment used for near-spot scenarios. Default 0.50.")]
	public decimal StrikeStep { get; set; } = 0.50m;

	[CommandOption("--cash")]
	[Description("Available cash/BP for funding. Scenarios whose margin delta exceeds this amount are flagged as not fundable.")]
	public decimal? Cash { get; set; }

	[CommandOption("--risk <VALUE>")]
	[Description("Risk-aversion weight for ranking: each $1 of capital put at stake (added margin + net debit deployed) discounts the suggestion's expected $ by this much, so de-risking (close/reduce) ranks higher. Default 0 = pure EV.")]
	public decimal Risk { get; set; } = 0m;

	[CommandOption("--account <VALUE>")]
	[Description("Account alias or ID from api-config.json used to auto-detect cash/BP when selecting an existing open position.")]
	public string? Account { get; set; }

	[CommandOption("--log-level <LEVEL>")]
	[Description("Verbosity: error | information (default) | debug. 'debug' adds the put-call-parity implied-dividend diagnostic.")]
	public string? LogLevel { get; set; }

	[CommandOption("--calibrated")]
	[Description("ON by default: back-solve each leg's IV from its live bid/ask mid at the dividend-adjusted spot, anchored at the observation instant (now live / last close off-hours), before the risk diagnostic, probe score and scenario tables run. Pass --calibrated false to instead trust Webull's reported IV field — a debugging view only; the vendor field is 10–50 vol pts off at 0DTE.")]
	[DefaultValue(true)]
	public bool Calibrated { get; set; } = true;

	[CommandOption("--theoretical")]
	[Description("Price legs at their Black-Scholes theoretical value instead of the live market mid (the same pricing the command already falls back to for past --date runs). Composes with --calibrated (theoretical at mid-implied IVs). Defaults from the report config's 'theoretical' key; pass --theoretical false to override it off.")]
	[DefaultValue(false)]
	public bool Theoretical { get; set; }

	/// <summary>True when --log-level debug — surfaces extra diagnostics (e.g. implied dividend) that
	/// would otherwise clutter the proposal output.</summary>
	public bool IsDebug => string.Equals(LogLevel, "debug", StringComparison.OrdinalIgnoreCase);

	/// <summary>Applies the shared report-config keys from the base, then the pricing-surface knobs this
	/// command shares with 'wa report' (calibrated, theoretical) so both tools price identically by default.</summary>
	internal override void ApplyConfig(Dictionary<string, System.Text.Json.JsonElement> cfg)
	{
		base.ApplyConfig(cfg);
		if (!Program.HasCliOption("calibrated") && cfg.TryGetBool("calibrated", out var calibrated)) Calibrated = calibrated;
		if (!Program.HasCliOption("theoretical") && cfg.TryGetBool("theoretical", out var theoretical)) Theoretical = theoretical;
	}

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if ((string.IsNullOrEmpty(Spec) ? 0 : 1) + (Proposal != null ? 1 : 0) + (Fill?.IsSet == true ? 1 : 0) > 1)
			return ValidationResult.Error("pass only one of <spec>, --proposal or --fill");
		if (Fill?.IsSet == true && Fill.Value < 0)
			return ValidationResult.Error($"--fill: N must be ≥ 1, got {Fill.Value}");

		if (LogLevel != null && LogLevel.ToLowerInvariant() is not ("error" or "information" or "debug"))
			return ValidationResult.Error($"--log-level: must be error|information|debug, got '{LogLevel}'");

		if (!string.IsNullOrEmpty(Spec))
		{
			List<ParsedLeg> legs;
			try { legs = TradeLegParser.Parse(Spec); }
			catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }

			foreach (var leg in legs)
			{
				if (leg.Option == null)
					return ValidationResult.Error($"<spec>: '{leg.Symbol}' is not an OCC option symbol (analyze position requires option legs)");
				if (!leg.Price.HasValue)
					return ValidationResult.Error($"<spec>: leg '{leg.Symbol}' is missing @PRICE (cost basis per share is required)");
			}
		}

		if (IvDefault <= 0m || IvDefault > 500m)
			return ValidationResult.Error($"--iv-default: must be in (0, 500], got {IvDefault}");

		if (StrikeStep <= 0m)
			return ValidationResult.Error($"--strike-step: must be > 0, got {StrikeStep}");

		if (Cash.HasValue && Cash.Value < 0m)
			return ValidationResult.Error($"--cash: must be non-negative, got {Cash.Value}");

		return ValidationResult.Success();
	}
}

internal sealed class AnalyzePositionCommand : AsyncCommand<AnalyzePositionSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzePositionSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.PinnedInstant is { } instant) ReportCommand.PinEvaluation(instant);

		if (settings.Proposal != null && !TryLoadProposalSpec(settings))
			return 1;

		var atFill = false;
		if (settings.Fill?.IsSet == true)
		{
			var (spec, _, error) = await OpeningFillPicker.ResolveAtAsync(settings.Fill.Value, settings.DateSpec, cancellation);
			if (spec == null) { Console.Error.WriteLine($"Error: {error}"); return 1; }
			settings.Spec = spec;
			atFill = settings.DateSpec.Kind == AsOfKind.Fill;
		}

		List<PositionSnapshot> positionLegs;
		if (string.IsNullOrEmpty(settings.Spec))
		{
			var (loaded, error) = SelectPositionFromLog();
			if (loaded == null)
			{
				Console.Error.WriteLine($"Error: {error}");
				return 1;
			}
			positionLegs = loaded;
		}
		else
		{
			var legs = TradeLegParser.Parse(settings.Spec);
			positionLegs = legs.Select(l => new PositionSnapshot(Symbol: l.Symbol, Action: l.Action, Qty: l.Quantity, CostBasis: l.Price!.Value, Parsed: l.Option!)).ToList();
		}

		var ticker = positionLegs[0].Parsed.Root;

		if (string.IsNullOrEmpty(settings.Spec) && !settings.Cash.HasValue)
		{
			var detectedCash = await TryResolveAvailableCashAsync(settings.Account, cancellation);
			if (detectedCash.HasValue)
			{
				settings.Cash = detectedCash.Value.Cash;
				AnsiConsole.MarkupLine($"[dim]Using available cash/BP from account '{Markup.Escape(detectedCash.Value.AccountAlias)}': ${detectedCash.Value.Cash:N2}.[/]");
				AnsiConsole.WriteLine();
			}
		}

		// Phase 1: fetch quotes for the position legs. We need spot before we can enumerate
		// hypothetical strikes for scenarios, and the underlying price comes back as a byproduct
		// of this same fetch. --spot still wins if supplied. Also refreshes OI/IV for chain strikes
		// near spot at each position expiry, so max-pain / GEX in the diagnostic see the full chain
		// rather than just the position's strike.
		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
		IReadOnlyDictionary<string, decimal>? underlyingPrices = null;
		var positionSymbols = positionLegs.Select(l => l.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var structure = ClassifyStructure(positionLegs);

		// A past evaluation instant (--date, or --date fill) is the market at that minute: the legs' and their expiries' books, the day's OI
		// and spot all come from the as-of market (the --fill loader), never today's live chain priced on the past instant's clock.
		FillTimeMarket.Snapshot? asOfMarket = null;
		if (EvaluationDate.IsPast)
		{
			var (market, error) = await LoadAsOfMarketAsync(ticker, positionLegs, structure, settings, atFill, cancellation);
			if (market == null)
			{
				Console.Error.WriteLine($"Error: can't rebuild the market at {EvaluationDate.Instant:yyyy-MM-dd HH:mm} ET — {error}.");
				return 1;
			}
			asOfMarket = market;
			quotes = market.Quotes;
			underlyingPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = market.Spot };
		}
		else
		{
			// Single vendor seam. targetExpiries drives the Webull arm's near-ATM OI/IV refresh so max-pain/GEX see
			// the full chain rather than just the position's strike; Schwab's range=ALL chain already carries that
			// breadth. HV is backfilled uniformly inside the seam so the diagnostic reads consistently across vendors.
			var targetExpiries = positionLegs.Select(l => l.Parsed.ExpiryDate.Date).Distinct().ToList();
			try
			{
				(quotes, underlyingPrices) = await AnalyzeCommon.FetchLiveChainByVendor(positionSymbols, settings.ResolvedVendor, cancellation, targetExpiries);
			}
			catch (Exception ex) when (!cancellation.IsCancellationRequested)
			{
				var hint = settings.ResolvedVendor == QuoteVendor.Schwab ? "Re-run 'wa schwab login' if the token expired, or pass --vendor webull." : "Run 'wa sniff' to refresh Webull headers, or pass --vendor schwab.";
				Console.Error.WriteLine($"Warning: live chain fetch failed ({settings.VendorName}): {ex.Message} {hint}");
			}
		}

		var spotOverrides = await AnalyzeCommon.ResolveSpotOverrideMapAsync(settings.Spot, settings.SpotSession, [ticker], settings.ResolvedVendor, underlyingPrices, cancellation);
		decimal ovSpot = 0m;
		var hasSpotOverride = spotOverrides != null && ParsingHelpers.TryResolveForRoot(spotOverrides, ticker, out ovSpot);
		var spot = hasSpotOverride ? ovSpot
			: ResolveSpot(ticker, settings, underlyingPrices);
		if (spot == null)
		{
			Console.Error.WriteLine($"Error: no underlying price for '{ticker}'. Pass --spot {ticker}:<price> or run 'wa sniff' to refresh Webull headers.");
			return 1;
		}

		// The evaluation date every scenario, symbol and diagnostic below is computed on — read from the pin once.
		var asOf = EvaluationDate.Today;

		// Phase 2: fetch quotes for the hypothetical-scenario symbols we couldn't enumerate without spot. An as-of market already
		// carries their expiries' chains (loaded at the session's own spot).
		if (asOfMarket == null)
		{
			var alreadyFetched = quotes ?? new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			// Under --log-level debug, also pull the same-strike PUT at each position expiry so the
			// put-call-parity implied-dividend diagnostic has both sides (the chain often quotes only
			// calls / front-month, leaving far puts as stubs). Skipped by default to avoid extra fetches.
			var pcpPutSymbols = settings.IsDebug
				? positionLegs.Select(l => $"{l.Parsed.Root}{l.Parsed.ExpiryDate:yyMMdd}P{(int)Math.Round(l.Parsed.Strike * 1000m):00000000}")
				: Enumerable.Empty<string>();
			var hypotheticalSymbols = EnumerateHypotheticalSymbols(positionLegs, structure, settings, spot.Value, asOf, quotes)
				.Concat(pcpPutSymbols)
				.Where(s => !alreadyFetched.ContainsKey(s))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (hypotheticalSymbols.Count > 0)
			{
				var hypotheticalQuotes = await AnalyzeCommon.FetchQuotesForSymbolList(hypotheticalSymbols, settings.ResolvedVendor, cancellation);
				if (hypotheticalQuotes != null)
				{
					// Prefer Phase 1's refreshed entries: FetchQuotesForSymbolList re-fetches the entire
					// chain to look up derivativeIds, so its result contains fresh stub entries (no bid/ask/IV/OI)
					// for every chain symbol — overwriting Phase 1's queryBatch-refreshed position legs would
					// surface as "Long quote: bid=null ask=null …" in the diagnostic.
					var merged = new Dictionary<string, OptionContractQuote>(alreadyFetched, StringComparer.OrdinalIgnoreCase);
					foreach (var kvp in hypotheticalQuotes) merged.TryAdd(kvp.Key, kvp.Value);
					quotes = merged;
				}
			}
		}

		// Risk diagnostic: structured analysis of the current position (greeks, geometry, premium ratio,
		// trend alignment, rule hits). Logged to data/analyze-position.jsonl for AI consumers.
		var tickerForTrend = positionLegs.Count > 0 ? positionLegs[0].Parsed.Root : null;
		TrendSnapshot? trendSnap = null;
		if (!string.IsNullOrEmpty(tickerForTrend))
			trendSnap = asOfMarket != null
				? await TrendFetcher.BuildAsOfAsync(tickerForTrend, asOfMarket.Instant, asOfMarket.Spot, asOfMarket.SessionHigh, asOfMarket.SessionLow, new AI.Backtest.HistoricalBarCache(), cancellation)
				: await TrendFetcher.FetchAsync(tickerForTrend, asOf, cancellation);

		// Shared price cache for both regime and vol-fit fetches — avoids a redundant disk read for the ticker's closes.
		var priceCache = new HistoricalPriceCache();
		var aiCfg = RiskDiagnosticProbeBuilder.TryLoadAiConfigQuiet(ticker, out _);
		decimal macroBias = 0m;
		if (aiCfg?.Indicators.TechnicalFilter.Enabled == true)
		{
			var filter = aiCfg.Indicators.TechnicalFilter;
			var effectiveLookback = filter.Sma200Weight > 0m ? Math.Max(filter.LookbackDays, 200) : filter.LookbackDays;
			var closes = await priceCache.GetRecentClosesAsync(ticker, effectiveLookback, asOf, cancellation);
			macroBias = TechnicalIndicators.Compute(closes, filter)?.Score ?? 0m;
		}
		var apiConfig = OpenCandidateEvaluator.TryLoadApiConfig();
		var intradayCache = apiConfig != null ? new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig)) : null;
		var dteCalendar = positionLegs.Count > 0 ? Math.Max(1, (positionLegs.Min(l => l.Parsed.ExpiryDate.Date) - asOf.Date).Days) : 5;
		var regimeComponents = aiCfg != null ? (await OpenCandidateEvaluator.ComputeRegimeComponentsAsync(ticker, aiCfg.Opener, macroBias, asOfMarket?.Instant ?? DateTime.Now, priceCache, intradayCache, includeCurrentBar: asOfMarket == null, cancellation)).Components : default;
		var technicalBias = aiCfg != null ? RegimeAnalyzer.BlendBias(regimeComponents, aiCfg.Opener, dteCalendar) : 0m;

		var (historicalVolAnnual, realizedVol) = await RiskDiagnosticProbeBuilder.TryLoadVolInputsAsync(ticker, asOf, priceCache, cancellation);
		var sentiment = await FearGreedClient.FetchAsync(asOf, cancellation);

		// Scheduled-catalyst events (earnings + ex-dividend) for the position's ticker. cacheOnly:false
		// refreshes the 12h event cache — without this, held tickers the opener never scans (e.g. SPY)
		// never populate data/event-cache, leaving the theoretical price un-dividend-adjusted.
		var positionEvents = (await EventCalendarLoader.LoadAsync(new[] { ticker }, new OpenerEventsConfig(), asOf, cancellation, cacheOnly: false)).Get(ticker);
		var dividends = DividendScheduleBuilder.BuildForTicker(positionEvents, spot.Value, null);

		// --calibrated (defaults from the report config's 'calibrated' key): re-anchor every chain quote's IV
		// to its live bid/ask mid at the dividend-adjusted spot — the same mid-consistent surface the report
		// grid prices on — BEFORE the diagnostic, probe score and scenario tables read it. ResolveIV serves
		// the quote's iv field everywhere downstream, so one pass here aligns the whole command with
		// 'wa report --calibrated'. Legs whose mid can't back-solve keep the broker IV; explicit --iv
		// overrides still win (ResolveIV checks them first). Always calibrate against the book's own spot
		// (underlyingPrices[ticker]), never a --spot / --extended hypothetical override, so the dividend-adjusted
		// mid->IV solve reflects where the quotes were actually struck.
		if (quotes != null && (asOfMarket != null || settings.Calibrated))
		{
			var bookSpot = asOfMarket?.Spot
				?? (underlyingPrices != null && ParsingHelpers.TryResolveForRoot(underlyingPrices, ticker, out var bs) && bs > 0m ? bs : spot.Value);
			var bookDividends = DividendScheduleBuilder.BuildForTicker(positionEvents, bookSpot, null);
			var asOfCal = OptionMath.ObservationInstant(); // when the loaded quotes were struck (now live / last close off-hours / the as-of market's instant); same anchor as report
			var recalibratedQuotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			var recalibrated = 0;
			foreach (var (sym, q) in quotes)
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(sym);
				if (parsed == null) { recalibratedQuotes[sym] = q; continue; }
				var adjustedSpot = OptionMath.DividendAdjustedSpot(bookSpot, bookDividends, asOfCal, parsed.ExpiryDate.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate);
				var iv = OptionMath.TryMarketImpliedIv(sym, parsed, adjustedSpot, asOfCal, quotes);
				recalibratedQuotes[sym] = iv.HasValue ? q with { ImpliedVolatility = iv.Value, VendorImpliedVolatility = q.VendorImpliedVolatility ?? q.ImpliedVolatility } : q;
				if (iv.HasValue) recalibrated++;
			}
			quotes = recalibratedQuotes;
			if (recalibrated > 0) Log.Debug($"Calibration: re-anchored {recalibrated} contract IV(s) to the live mid surface (report parity).");
		}

		var diagnostic = BuildAndLogDiagnostic(
			logPath: Program.ResolvePath("data/analyze-position.jsonl"),
			ticker: tickerForTrend ?? "UNKNOWN",
			positionKey: string.Join("|", positionLegs.Select(l => l.Symbol)),
			legs: positionLegs,
			spot: spot.Value,
			asOf: asOf,
			ivResolver: sym => ResolveIV(sym, settings, quotes),
			legPriceResolver: sym =>
			{
				var leg = positionLegs.FirstOrDefault(l => l.Symbol == sym);
				if (leg == null) return 0m;
				var iv = ResolveIV(sym, settings, quotes);
				var dte = Math.Max(1, (leg.Parsed.ExpiryDate - asOf.Date).Days);
				return LiveOrBsMid(settings.Theoretical ? null : quotes, sym, spot.Value, leg.Parsed.Strike, dte, iv, leg.Parsed.CallPut, asOf, dividends);
			},
			trend: trendSnap,
			quotesForProbe: quotes,
			technicalBiasForProbe: technicalBias,
			historicalVolAnnual: historicalVolAnnual,
			realizedVol: realizedVol,
			// At hypothetical spots (--spot override, or --extended when the book was frozen at the 16:00 close),
			// the leg market mids are stale and back-solving IV against the overridden spot produces a nonsense IV
			// that collapses calendar/diagonal residual time value. Use the quotes' already-calibrated IV instead.
			useMarketImpliedIv: !hasSpotOverride,
			sentiment: sentiment,
			events: positionEvents);

		// When the opener scorer will re-value the scenarios, every swept candidate (rolls, single-long conversions) is kept for it and the shortlist is chosen afterwards on its EV.
		var rescore = aiCfg != null && quotes != null && !settings.Theoretical;
		var scenarios = GenerateScenarios(positionLegs, structure, settings, spot.Value, asOf, quotes, technicalBias, dividends, shortlistSwept: !rescore);
		if (rescore)
		{
			var asOfNow = asOfMarket?.Instant ?? DateTime.Now;
			var quoteAsOf = asOfMarket?.Instant ?? OptionMath.ObservationInstant();
			scenarios = await PositionAiResetScanner.ApplyAsync(
				scenarios, positionLegs, structure, settings, aiCfg!, spot.Value,
				asOfNow, quoteAsOf, quotes!, priceCache, intradayCache, !hasSpotOverride, cancellation);
			scenarios = OrderScenariosForDisplay(ShortlistSwept(scenarios, settings.Cash, settings.Risk), settings.Cash, settings.Risk);
		}

		var toText = settings.OutputFormat.Equals("text", StringComparison.OrdinalIgnoreCase);
		StringWriter? stringWriter = null;
		IAnsiConsole renderConsole;
		if (toText)
		{
			stringWriter = new StringWriter();
			renderConsole = WebullAnalytics.IO.TextFileExporter.CreateTextConsole(stringWriter);
		}
		else
		{
			renderConsole = AnsiConsole.Console;
		}

		AnalyzeCommon.RenderProposalPanel(positionLegs, structure.ToString(), spot.Value, diagnostic, renderConsole, ascii: toText);

		if (settings.IsDebug)
			RenderImpliedDividendDiagnostic(positionLegs, spot.Value, quotes, dividends, asOf, renderConsole);

		if (scenarios.Count == 0)
		{
			renderConsole.MarkupLine($"[yellow]No scenarios defined yet for structure type '{structure}'. Supported: single-long, vertical, calendar, diagonal, iron butterfly, iron condor, double calendar, double diagonal.[/]");
		}
		else
		{
			RenderScenarioTable(scenarios, positionLegs, settings, renderConsole, ascii: toText);
		}

		if (toText)
		{
			var path = settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("AnalyzePosition");
			WebullAnalytics.IO.TextFileExporter.WriteConsoleOutputToTextFile(stringWriter!, path, "Position analysis written to");
		}
		return 0;
	}

	/// <summary>--proposal: load a stored proposal snapshot and rebuild the <c>&lt;spec&gt;</c> — its legs at the
	/// entry mids captured when scored — then fall through to the normal live path so the diagnostic, quotes,
	/// spot and management scenarios all reflect how the position is behaving NOW, not when it was proposed.
	/// Only 'analyze risk --proposal' replays the frozen snapshot diagnostic verbatim. Returns false (with an
	/// error printed) when the snapshot can't be loaded.</summary>
	private static bool TryLoadProposalSpec(AnalyzePositionSettings settings)
	{
		var (snap, error) = ProposalSnapshot.TryLoad(settings.Proposal!);
		if (snap == null) { Console.Error.WriteLine($"Error: {error}"); return false; }

		settings.Spec = string.Join(",", snap.Legs.Select(l => $"{(l.Action == LegAction.Buy ? "buy" : "sell")}:{l.Symbol}:{l.Qty}@{snap.CostBasis(l.Symbol).ToString(CultureInfo.InvariantCulture)}"));

		Console.WriteLine($"Proposal snapshot: {Path.GetFileName(snap.SourcePath)} line {snap.LineNumber}, emitted {snap.AsOf:yyyy-MM-dd HH:mm:ss} — evaluating against the live market now.");
		return true;
	}

	internal static async Task<(decimal Cash, string AccountAlias)?> TryResolveAvailableCashAsync(string? accountFlag, CancellationToken cancellation)
	{
		var config = TradeConfig.Load(quiet: true);
		if (config == null) return null;

		var account = TradeConfig.Resolve(config, accountFlag, quiet: true);
		if (account == null) return null;

		try
		{
			using var client = new WebullOpenApiClient(account);
			var balance = await client.FetchAccountBalanceAsync(cancellation);
			var availableFunds = balance.TryGetAvailableFunds();
			return availableFunds.HasValue ? (availableFunds.Value, account.Alias) : null;
		}
		catch (WebullOpenApiException)
		{
			return null;
		}
		catch (System.Net.Http.HttpRequestException)
		{
			return null;
		}
	}

	// ─── Input model ──────────────────────────────────────────────────────────

	internal sealed record PositionSnapshot(string Symbol, LegAction Action, int Qty, decimal CostBasis, OptionParsed Parsed);

	/// <summary>One structure held after a scenario: its legs for one contract (Qty 1) and how many contracts of it. The opener scorer values it
	/// at the live market, from now; a leg's CostBasis is only the price it was opened or would be opened at. <paramref name="Label"/> names its
	/// role in the scenario's EV note: "existing", "rolled", "long only" or "added".</summary>
	internal sealed record BookStructure(string Label, IReadOnlyList<PositionSnapshot> Legs, int Qty);

	internal enum StructureKind { SingleLong, SingleShort, Calendar, Diagonal, Vertical, IronButterfly, IronCondor, Condor, DoubleCalendar, DoubleDiagonal, Unsupported }

	// ─── Load from trade log ──────────────────────────────────────────────────

	internal static (List<PositionSnapshot>? snapshots, string? error) SelectPositionFromLog(string title = "Select a position to analyze:")
	{
		var ordersPath = Program.ResolvePath(Program.OrdersPath);
		if (!File.Exists(ordersPath))
			return (null, $"Orders file '{ordersPath}' does not exist.");

		var (trades, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
		var (_, positions, _) = PositionTracker.ComputeReport(trades, feeLookup: feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);
		var (rows, _, _) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		var strategies = FindAllStrategyGroups(rows);

		if (strategies.Count == 0)
			return (null, "No open strategy positions found in the trade log.");

		var chosen = strategies.Count == 1
			? strategies[0]
			: AnsiConsole.Prompt(new SelectionPrompt<(PositionRow parent, List<PositionRow> legs)>()
				.Title(title)
				.UseConverter(item => FormatPositionLabel(item.parent, item.legs))
				.AddChoices(strategies));

		var snapshots = BuildSnapshotsFromLegs(chosen.legs);
		return snapshots.Count == 0
			? (null, "Could not parse any option legs for the selected position.")
			: (snapshots, null);
	}

	private static List<(PositionRow parent, List<PositionRow> legs)> FindAllStrategyGroups(IReadOnlyList<PositionRow> rows)
	{
		var result = new List<(PositionRow, List<PositionRow>)>();
		PositionRow? currentParent = null;
		var currentLegs = new List<PositionRow>();
		var standaloneByKey = new Dictionary<string, List<PositionRow>>(StringComparer.Ordinal);

		foreach (var row in rows)
		{
			if (row.IsStrategyLeg)
			{
				if (currentParent != null) currentLegs.Add(row);
				continue;
			}

			if (currentParent != null && currentLegs.Count > 0)
				result.Add((currentParent, new List<PositionRow>(currentLegs)));
			currentParent = null;
			currentLegs = new List<PositionRow>();

			if (row.Asset == Asset.OptionStrategy)
			{
				currentParent = row;
			}
			else if (row.Asset == Asset.Option && row.MatchKey != null)
			{
				if (!standaloneByKey.TryGetValue(row.MatchKey, out var bucket))
				{
					bucket = new List<PositionRow>();
					standaloneByKey[row.MatchKey] = bucket;
				}
				bucket.Add(row);
			}
		}

		if (currentParent != null && currentLegs.Count > 0)
			result.Add((currentParent, new List<PositionRow>(currentLegs)));

		// Net opposing same-OCC single-leg orphans. Two lineages on the same contract that net to zero
		// represent fully-closed exposure (e.g., a strategy's short leg closed via a separate ticket from
		// its long leg) and should not appear as analyzable positions.
		foreach (var (_, bucket) in standaloneByKey)
		{
			var netQty = bucket.Sum(r => r.Side == Side.Buy ? r.Qty : -r.Qty);
			if (netQty == 0) continue;
			var representative = bucket.First(r => (r.Side == Side.Buy) == (netQty > 0));
			result.Add((representative, new List<PositionRow> { representative }));
		}

		return result;
	}

	private static string FormatPositionLabel(PositionRow parent, List<PositionRow> legs)
	{
		var parsedLegs = new List<(PositionRow row, OptionParsed opt)>();
		foreach (var leg in legs)
		{
			if (leg.MatchKey == null) continue;
			var occ = leg.MatchKey.StartsWith("option:") ? leg.MatchKey[7..] : leg.MatchKey;
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed != null) parsedLegs.Add((leg, parsed));
		}

		if (parsedLegs.Count == 0) return parent.Instrument;

		parsedLegs.Sort((a, b) => a.opt.ExpiryDate.CompareTo(b.opt.ExpiryDate));
		var ticker = parsedLegs[0].opt.Root;
		var callPut = parsedLegs[0].opt.CallPut == "C" ? "Call" : "Put";
		var qty = parsedLegs[0].row.Qty;
		var shortOpt = parsedLegs[0].opt;
		var longOpt = parsedLegs[^1].opt;

		var strikeStr = shortOpt.Strike == longOpt.Strike ? $"${shortOpt.Strike:F2}" : $"${shortOpt.Strike:F2}→${longOpt.Strike:F2}";
		var expiryStr = parsedLegs.Count > 1 ? $"{shortOpt.ExpiryDate:MM-dd}/{longOpt.ExpiryDate:MM-dd}" : $"{shortOpt.ExpiryDate:MM-dd}";
		var kindLabel = parent.Asset == Asset.Option ? (parent.Side == Side.Buy ? "Long" : "Short") : parent.OptionKind;

		return $"{ticker}  {kindLabel}  {callPut}  {strikeStr}  {expiryStr}  x{qty}";
	}

	private static List<PositionSnapshot> BuildSnapshotsFromLegs(List<PositionRow> legRows)
	{
		var snapshots = new List<PositionSnapshot>();
		foreach (var leg in legRows)
		{
			if (leg.MatchKey == null) continue;
			var occ = leg.MatchKey.StartsWith("option:") ? leg.MatchKey[7..] : leg.MatchKey;
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed == null) continue;
			var action = leg.Side == Side.Buy ? LegAction.Buy : LegAction.Sell;
			snapshots.Add(new PositionSnapshot(Symbol: occ, Action: action, Qty: leg.Qty, CostBasis: OptionMath.GetPremium(leg), Parsed: parsed));
		}
		return snapshots;
	}

	// ─── Classifier ──────────────────────────────────────────────────────────

	/// <summary>True when two legs' roots belong to the same underlying's book — exact match, or SPX/SPXW on
	/// a standard-monthly expiry (see ParsingHelpers.RootsMatchForAggregation). Checked against EITHER leg's
	/// own expiry since a calendar/diagonal pairs two different dates and either one could be the monthly.</summary>
	private static bool RootsShareBook(string rootA, string rootB, DateTime expiryA, DateTime expiryB) =>
		ParsingHelpers.RootsMatchForAggregation(rootA, rootB, expiryA) || ParsingHelpers.RootsMatchForAggregation(rootA, rootB, expiryB);

	internal static StructureKind ClassifyStructure(IReadOnlyList<PositionSnapshot> legs)
	{
		if (legs.Count == 1)
			return legs[0].Action == LegAction.Buy ? StructureKind.SingleLong : StructureKind.SingleShort;

		if (legs.Count == 2)
		{
			var sl = legs.FirstOrDefault(l => l.Action == LegAction.Sell);
			var ll = legs.FirstOrDefault(l => l.Action == LegAction.Buy);
			if (sl == null || ll == null) return StructureKind.Unsupported;
			if (!RootsShareBook(sl.Parsed.Root, ll.Parsed.Root, sl.Parsed.ExpiryDate, ll.Parsed.ExpiryDate) || sl.Parsed.CallPut != ll.Parsed.CallPut) return StructureKind.Unsupported;
			if (sl.Parsed.ExpiryDate == ll.Parsed.ExpiryDate) return StructureKind.Vertical;
			if (sl.Parsed.ExpiryDate < ll.Parsed.ExpiryDate)
				return sl.Parsed.Strike == ll.Parsed.Strike ? StructureKind.Calendar : StructureKind.Diagonal;
		}

		if (legs.Count == 4)
		{
			// Every 4-leg structure we recognize has the same component breakdown:
			// one short put, one long put, one short call, one long call, all on the
			// same underlying. What differs is the expiry / strike geometry.
			var anchor = legs[0].Parsed;
			if (!legs.All(l => RootsShareBook(l.Parsed.Root, anchor.Root, l.Parsed.ExpiryDate, anchor.ExpiryDate))) return StructureKind.Unsupported;

			// All-same-right condor: one expiry, one side (all puts or all calls), 2 long + 2 short, four
			// distinct strikes, with the longs at the wings and shorts in the body (long condor) or the
			// reverse (short condor). Same neutral payoff shape as an iron condor, built from a single side.
			// Checked before the iron extraction below, which requires both a call and a put and would
			// otherwise reject an all-put / all-call condor as Unsupported.
			if (legs.All(l => l.Parsed.CallPut == legs[0].Parsed.CallPut)
				&& legs.All(l => l.Parsed.ExpiryDate == legs[0].Parsed.ExpiryDate)
				&& legs.Count(l => l.Action == LegAction.Buy) == 2
				&& legs.Select(l => l.Parsed.Strike).Distinct().Count() == 4)
			{
				var byStrike = legs.OrderBy(l => l.Parsed.Strike).ToList();
				bool longWings = byStrike[0].Action == LegAction.Buy && byStrike[1].Action == LegAction.Sell && byStrike[2].Action == LegAction.Sell && byStrike[3].Action == LegAction.Buy;
				bool shortWings = byStrike[0].Action == LegAction.Sell && byStrike[1].Action == LegAction.Buy && byStrike[2].Action == LegAction.Buy && byStrike[3].Action == LegAction.Sell;
				if (longWings || shortWings) return StructureKind.Condor;
			}

			var shortPut = legs.FirstOrDefault(l => l.Action == LegAction.Sell && l.Parsed.CallPut == "P");
			var longPut = legs.FirstOrDefault(l => l.Action == LegAction.Buy && l.Parsed.CallPut == "P");
			var shortCall = legs.FirstOrDefault(l => l.Action == LegAction.Sell && l.Parsed.CallPut == "C");
			var longCall = legs.FirstOrDefault(l => l.Action == LegAction.Buy && l.Parsed.CallPut == "C");
			if (shortPut == null || longPut == null || shortCall == null || longCall == null) return StructureKind.Unsupported;

			// Iron butterfly / iron condor: all four legs share an expiry, longs sandwich
			// the shorts. Body strike(s) at the shorts; wings further OTM. Short put strike
			// == short call strike → butterfly; different → condor.
			bool allSameExpiry = shortPut.Parsed.ExpiryDate == longPut.Parsed.ExpiryDate
				&& shortPut.Parsed.ExpiryDate == shortCall.Parsed.ExpiryDate
				&& shortPut.Parsed.ExpiryDate == longCall.Parsed.ExpiryDate;
			if (allSameExpiry
				&& longPut.Parsed.Strike < shortPut.Parsed.Strike
				&& shortCall.Parsed.Strike < longCall.Parsed.Strike
				&& shortPut.Parsed.Strike <= shortCall.Parsed.Strike)
			{
				return shortPut.Parsed.Strike == shortCall.Parsed.Strike
					? StructureKind.IronButterfly
					: StructureKind.IronCondor;
			}

			// Time-spread 4-legs: each side is its own calendar (same strike across expiries)
			// or diagonal (different strike). Shorts share a near expiry; longs share a further
			// expiry; short expiry strictly before long expiry. If BOTH sides are calendars
			// (same-strike on each side) → double calendar; otherwise → double diagonal.
			bool shortsShareExpiry = shortPut.Parsed.ExpiryDate == shortCall.Parsed.ExpiryDate;
			bool longsShareExpiry = longPut.Parsed.ExpiryDate == longCall.Parsed.ExpiryDate;
			bool shortBeforeLong = shortPut.Parsed.ExpiryDate < longPut.Parsed.ExpiryDate;
			if (shortsShareExpiry && longsShareExpiry && shortBeforeLong)
			{
				bool putSameStrike = shortPut.Parsed.Strike == longPut.Parsed.Strike;
				bool callSameStrike = shortCall.Parsed.Strike == longCall.Parsed.Strike;
				return (putSameStrike && callSameStrike)
					? StructureKind.DoubleCalendar
					: StructureKind.DoubleDiagonal;
			}
		}

		return StructureKind.Unsupported;
	}

	// ─── Scenario generation ─────────────────────────────────────────────────

	internal sealed record Scenario(
		string Name,
		string ActionSummary,
		decimal CashImpactPerContract,		// per-contract
		decimal ProjectedValuePerContract,	// per-contract
		decimal TotalPnLPerContract,		// per-contract
		decimal MarginDeltaPerContract,		// per-contract additional margin required (negative = margin frees up)
		int Qty,
		int DaysToTarget,					// days from evaluation date to this scenario's target date; used to rank P&L per day
		string Rationale,
		bool IsRoll = false,				// true iff this scenario closes an existing leg and opens a new one (consulted by BuildReproductionCommands)
		decimal? RankScore = null,
		decimal? ExpectedPnLPerContract = null,	// probability-weighted EV at target (lognormal spot grid), vs the spot-pinned ProjectedValue. Null when not computed for this scenario type.
		decimal AssignmentPenaltyPerContract = 0m,	// per-contract assignment-risk charge (verticals only); subtracted in the risk-adjusted rank.
		decimal PeakCashPerContract = 0m,			// up-front cash of the FIRST order when the scenario executes as sequential single-leg orders (non-calendar rolls: the buy-to-close cost) — the sell credit isn't available until that order fills. 0 when one net-priced combo covers the whole change.
		IReadOnlyList<decimal>? BreakEvens = null,	// campaign break-even spot price(s) at this scenario's target expiry (null for Close all or unsupported shapes).
		decimal? ProbabilityOfProfit = null,		// probability of profit [0, 1] from the opener scorer when available.
		IReadOnlyList<BookStructure>? Book = null,	// the structures held after the scenario (empty = flat); lets the opener scorer value every scenario on one model. Null when not modeled.
		string? EvNote = null,						// how ExpectedPnLPerContract was reached, shown after Rationale; replaced when the opener scorer re-values the scenario.
		decimal OrderContractsNow = 0m,				// broker orders the scenario places now × the contracts each covers (Close all on 22 lots = 22; a two-order roll on 7 = 14). Prices its friction.
		bool Swept = false,							// one of the swept candidates (a spread's short rolls, a single long's conversions), shortlisted by ShortlistSwept once every candidate has its EV.
		bool IsLegIn = false)						// true iff this scenario keeps every existing leg and opens new ones that form one structure with them (a single long converted to a spread).
	{
		/// <summary>The rationale as displayed: the scenario's description, then how its EV was reached.</summary>
		public string DisplayRationale => string.IsNullOrEmpty(EvNote) ? Rationale : $"{Rationale}; {EvNote}";

		/// <summary>Per-contract buying power the scenario actually consumes. Margin delta alone is wrong for
		/// rolls: a roll can be margin-neutral yet cost a large cash debit, and a split (two-order) roll must
		/// fund its buy-to-close leg in full before the sell credit lands.</summary>
		public decimal FundingPerContract => Math.Max(Math.Max(Math.Max(MarginDeltaPerContract, 0m), Math.Max(-CashImpactPerContract, 0m)), PeakCashPerContract);
	}

	/// <summary>Hypothetical OCC symbols the scenario generators will reference. Pre-enumerated so we can
	/// include them in a single up-front quote fetch.</summary>
	/// <param name="quotes">The chain fetched so far, whose front expiry lists the strikes the opposite-side adds snap to; null when none.</param>
	/// <param name="asOf">The scenarios' evaluation date — the same one passed to <see cref="GenerateScenarios"/>, so the symbols fetched are the ones they price.</param>
	internal static IEnumerable<string> EnumerateHypotheticalSymbols(IReadOnlyList<PositionSnapshot> legs, StructureKind kind, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes = null)
	{
		if (legs.Count == 0) yield break;
		var root = legs[0].Parsed.Root;
		var callPut = legs[0].Parsed.CallPut;

		if (kind == StructureKind.SingleLong)
		{
			var longLeg = legs[0];
			var shortExpiry = MarketCalendar.NextFridayAfter(asOf);
			if (shortExpiry < longLeg.Parsed.ExpiryDate)
				yield return MatchKeys.OccSymbol(root, shortExpiry, longLeg.Parsed.Strike, callPut);
		}
		else if (kind == StructureKind.Calendar || kind == StructureKind.Diagonal)
		{
			var shortLeg = legs.First(l => l.Action == LegAction.Sell);
			var longLeg = legs.First(l => l.Action == LegAction.Buy);
			var newExpiry = MarketCalendar.NextFridayAfter(shortLeg.Parsed.ExpiryDate);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike, callPut);
			var newLongExp = longLeg.Parsed.ExpiryDate > newExpiry ? longLeg.Parsed.ExpiryDate : newExpiry.AddDays(21);
			var oppositeCp = callPut == "C" ? "P" : "C";
			foreach (var strike in StrikeGrid.Bracket(spot, settings.StrikeStep))
			{
				if (strike <= 0m) continue;
				if (strike != shortLeg.Parsed.Strike)
				{
					yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, strike, callPut); // same-expiry strike roll
					yield return MatchKeys.OccSymbol(root, newExpiry, strike, callPut);                 // next-weekly strike roll
				}
				yield return MatchKeys.OccSymbol(root, newLongExp, strike, callPut);                     // reset-to-new-calendar long leg
																										 // "Add" scenarios: same-side second calendar + opposite-side double calendar/diagonal.
				yield return MatchKeys.OccSymbol(root, newExpiry, strike, oppositeCp);
				yield return MatchKeys.OccSymbol(root, newLongExp, strike, oppositeCp);
			}
			// The double calendar/diagonal adds use the position's own expiries: without their legs here, the far one is BS-estimated and the opener
			// scorer cannot value the add.
			var addGeometries = DoubleAddGeometries(ListedStrikes(quotes, root, shortLeg.Parsed.ExpiryDate, oppositeCp), ListedStrikes(quotes, longLeg.Parsed.Root, longLeg.Parsed.ExpiryDate, oppositeCp), spot, oppositeCp);
			foreach (var (_, addShort, addLong) in addGeometries)
			{
				yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, addShort, oppositeCp);
				yield return MatchKeys.OccSymbol(longLeg.Parsed.Root, longLeg.Parsed.ExpiryDate, addLong, oppositeCp);
			}
		}
		else if (kind == StructureKind.Vertical)
		{
			var shortLeg = legs.First(l => l.Action == LegAction.Sell);
			var longLeg = legs.First(l => l.Action == LegAction.Buy);
			var strikeOffset = longLeg.Parsed.Strike - shortLeg.Parsed.Strike;
			var width = Math.Abs(strikeOffset);
			var newExpiry = MarketCalendar.NextFridayAfter(shortLeg.Parsed.ExpiryDate);
			foreach (var strike in StrikeGrid.Bracket(spot, settings.StrikeStep))
			{
				if (strike <= 0m) continue;
				if (strike != shortLeg.Parsed.Strike)
					yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, strike, callPut);

				var newLongStrike = strike + strikeOffset;
				if (newLongStrike <= 0m) continue;
				yield return MatchKeys.OccSymbol(root, newExpiry, strike, callPut);
				yield return MatchKeys.OccSymbol(root, newExpiry, newLongStrike, callPut);
			}

			var oppositeCp = callPut == "C" ? "P" : "C";
			foreach (var strike in StrikeGrid.Bracket(spot, settings.StrikeStep))
			{
				if (!IsComplementaryShortStrike(oppositeCp, strike, spot)) continue;
				var newLongStrike = ComplementaryLongStrike(oppositeCp, strike, width);
				if (newLongStrike <= 0m) continue;
				yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, strike, oppositeCp);
				yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, newLongStrike, oppositeCp);
			}
		}
	}

	internal static List<Scenario> GenerateScenarios(
		List<PositionSnapshot> legs, StructureKind kind,
		AnalyzePositionSettings settings, decimal spot, DateTime asOf,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes,
		decimal technicalBias = 0m, IReadOnlyList<DividendEvent>? dividends = null, bool shortlistSwept = true) =>
		kind switch
		{
			StructureKind.SingleLong => GenerateSingleLongScenarios(legs[0], settings, spot, asOf, quotes, dividends, shortlistSwept),
			StructureKind.Vertical => GenerateVerticalScenarios(legs, settings, spot, asOf, quotes, technicalBias, dividends),
			StructureKind.Calendar or StructureKind.Diagonal => GenerateSpreadScenarios(legs, settings, spot, asOf, kind, quotes, dividends, shortlistSwept),
			StructureKind.IronButterfly or StructureKind.IronCondor => GenerateIronScenarios(legs, settings, spot, asOf, quotes, dividends),
			StructureKind.Condor => GenerateCondorScenarios(legs, settings, spot, asOf, quotes, dividends),
			StructureKind.DoubleCalendar or StructureKind.DoubleDiagonal => GenerateDoubleSpreadScenarios(legs, settings, spot, asOf, kind, quotes, dividends),
			_ => new List<Scenario>()
		};

	/// <summary>Scenarios for double calendars and double diagonals. Both have the same shape:
	/// two time-spreads (one put, one call) sharing a near and far expiry. A "calendar" side
	/// has identical strikes; a "diagonal" side has different strikes. The hold-to-short-expiry
	/// math mirrors single-spread GenerateSpreadScenarios — long legs get BS-priced at the short
	/// expiry, short legs settle at intrinsic at that point. Scenarios:
	///   • Hold to short expiry at current spot
	///   • Hold to short expiry with spot at the put short strike (max-pin on put side)
	///   • Hold to short expiry with spot at the call short strike (max-pin on call side)
	///   • Close all at mid
	///   • Close one side (whichever short is closer to spot) and run the other to short expiry</summary>
	private static List<Scenario> GenerateDoubleSpreadScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, StructureKind kind, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var list = new List<Scenario>();
		var shortPut = legs.First(l => l.Action == LegAction.Sell && l.Parsed.CallPut == "P");
		var longPut = legs.First(l => l.Action == LegAction.Buy && l.Parsed.CallPut == "P");
		var shortCall = legs.First(l => l.Action == LegAction.Sell && l.Parsed.CallPut == "C");
		var longCall = legs.First(l => l.Action == LegAction.Buy && l.Parsed.CallPut == "C");
		var shortExpiry = shortPut.Parsed.ExpiryDate;
		var longExpiry = longPut.Parsed.ExpiryDate;
		var shortDte = Math.Max(1, (shortExpiry.Date - asOf.Date).Days);
		var quotesForPricing = settings.DateSpec.Kind != AsOfKind.Now || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var ivShortPut = ResolveIV(shortPut.Symbol, settings, quotes);
		var ivLongPut = ResolveIV(longPut.Symbol, settings, quotes);
		var ivShortCall = ResolveIV(shortCall.Symbol, settings, quotes);
		var ivLongCall = ResolveIV(longCall.Symbol, settings, quotes);

		var shortPutMid = LiveOrBsMid(quotesForPricing, shortPut.Symbol, spot, shortPut.Parsed.Strike, shortDte, ivShortPut, "P", asOf, dividends);
		var longPutMid = LiveOrBsMid(quotesForPricing, longPut.Symbol, spot, longPut.Parsed.Strike, Math.Max(1, (longExpiry.Date - asOf.Date).Days), ivLongPut, "P", asOf, dividends);
		var shortCallMid = LiveOrBsMid(quotesForPricing, shortCall.Symbol, spot, shortCall.Parsed.Strike, shortDte, ivShortCall, "C", asOf, dividends);
		var longCallMid = LiveOrBsMid(quotesForPricing, longCall.Symbol, spot, longCall.Parsed.Strike, Math.Max(1, (longExpiry.Date - asOf.Date).Days), ivLongCall, "C", asOf, dividends);

		// Long-leg value at the short expiry: BS with the residual time until the long's own expiry.
		var tLongAtShortExp = MarketClock.CloseToClose(longPut.Parsed.Root, shortExpiry, longExpiry, minDays: 1);
		decimal LongPutAtShortExp(decimal s) => OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(s, dividends, shortExpiry.Date + OptionMath.MarketClose, longExpiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longPut.Parsed.Strike, tLongAtShortExp, OptionMath.RiskFreeRate, ivLongPut, "P");
		decimal LongCallAtShortExp(decimal s) => OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(s, dividends, shortExpiry.Date + OptionMath.MarketClose, longExpiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longCall.Parsed.Strike, tLongAtShortExp, OptionMath.RiskFreeRate, ivLongCall, "C");
		// Per-share value of the whole 4-leg position at the short expiry, evaluated at a given spot.
		decimal ValueAtShortExpiry(decimal s) =>
			LongPutAtShortExp(s) - OptionMath.Intrinsic(s, shortPut.Parsed.Strike, "P")
			+ LongCallAtShortExp(s) - OptionMath.Intrinsic(s, shortCall.Parsed.Strike, "C");

		// 1. Hold to short expiry at current spot.
		var holdAtNow = ValueAtShortExpiry(spot);
		list.Add(NewScenarioSpread("Hold to short expiry (current spot)", legs, "—",
			cashNow: 0m, valueAtTarget: holdAtNow, marginDeltaPerContract: 0m, daysToTarget: shortDte,
			rationale: $"at {shortExpiry:yyyy-MM-dd} with spot held at ${spot:F2}: long puts + long calls residual + shorts intrinsic = ${holdAtNow:F2}/share"));

		// 2. Hold to short expiry with spot pinned at put short strike — typical max-profit case for the
		// put side (short put expires worthless; long put still carries residual extrinsic).
		var holdAtPutShort = ValueAtShortExpiry(shortPut.Parsed.Strike);
		list.Add(NewScenarioSpread($"Hold to short expiry (spot @ put short ${shortPut.Parsed.Strike:F2})", legs, "—",
			cashNow: 0m, valueAtTarget: holdAtPutShort, marginDeltaPerContract: 0m, daysToTarget: shortDte,
			rationale: $"at {shortExpiry:yyyy-MM-dd} with spot @ put-short strike: position value ${holdAtPutShort:F2}/share"));

		// 3. Hold to short expiry with spot pinned at call short strike — put-side and call-side both
		// peak as spot approaches their respective short strikes for time spreads.
		if (shortCall.Parsed.Strike != shortPut.Parsed.Strike)
		{
			var holdAtCallShort = ValueAtShortExpiry(shortCall.Parsed.Strike);
			list.Add(NewScenarioSpread($"Hold to short expiry (spot @ call short ${shortCall.Parsed.Strike:F2})", legs, "—",
				cashNow: 0m, valueAtTarget: holdAtCallShort, marginDeltaPerContract: 0m, daysToTarget: shortDte,
				rationale: $"at {shortExpiry:yyyy-MM-dd} with spot @ call-short strike: position value ${holdAtCallShort:F2}/share"));
		}

		// 4. Close everything at mid.
		var closeAllCash = -shortPutMid + longPutMid - shortCallMid + longCallMid;
		list.Add(NewScenarioSpread("Close all", legs,
			$"BUY {shortPut.Symbol} x{shortPut.Qty} @{FmtPrice(shortPutMid)}, SELL {longPut.Symbol} x{longPut.Qty} @{FmtPrice(longPutMid)}, BUY {shortCall.Symbol} x{shortCall.Qty} @{FmtPrice(shortCallMid)}, SELL {longCall.Symbol} x{longCall.Qty} @{FmtPrice(longCallMid)}",
			cashNow: closeAllCash, valueAtTarget: 0m, marginDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"flatten at mid: net cash ${closeAllCash:+0.00;-0.00}/share to close all four legs"));

		// 5. Close the threatened side; let the other side run to short expiry.
		var distanceToPut = spot - shortPut.Parsed.Strike;   // positive when spot above short put
		var distanceToCall = shortCall.Parsed.Strike - spot; // positive when spot below short call
		bool putSideThreatened = distanceToPut < distanceToCall;
		if (putSideThreatened)
		{
			var sideCash = -shortPutMid + longPutMid;
			var residualAtShortExp = LongCallAtShortExp(spot) - OptionMath.Intrinsic(spot, shortCall.Parsed.Strike, "C");
			list.Add(NewScenarioSpread("Close put side (defensive)", legs,
				$"BUY {shortPut.Symbol} x{shortPut.Qty} @{FmtPrice(shortPutMid)}, SELL {longPut.Symbol} x{longPut.Qty} @{FmtPrice(longPutMid)}",
				cashNow: sideCash, valueAtTarget: residualAtShortExp, marginDeltaPerContract: 0m, daysToTarget: shortDte,
				rationale: $"put-side closer to short strike (${distanceToPut:F2} from short) — cut the put spread for ${sideCash:+0.00;-0.00}/share; remaining call spread runs to short exp → ${residualAtShortExp:F2}/share"));
		}
		else
		{
			var sideCash = -shortCallMid + longCallMid;
			var residualAtShortExp = LongPutAtShortExp(spot) - OptionMath.Intrinsic(spot, shortPut.Parsed.Strike, "P");
			list.Add(NewScenarioSpread("Close call side (defensive)", legs,
				$"BUY {shortCall.Symbol} x{shortCall.Qty} @{FmtPrice(shortCallMid)}, SELL {longCall.Symbol} x{longCall.Qty} @{FmtPrice(longCallMid)}",
				cashNow: sideCash, valueAtTarget: residualAtShortExp, marginDeltaPerContract: 0m, daysToTarget: shortDte,
				rationale: $"call-side closer to short strike (${distanceToCall:F2} from short) — cut the call spread for ${sideCash:+0.00;-0.00}/share; remaining put spread runs to short exp → ${residualAtShortExp:F2}/share"));
		}

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	/// <summary>Scenario set for iron butterflies and iron condors. Same payoff structure (two
	/// vertical spreads sandwiched together — one credit put spread below body, one credit call
	/// spread above body) so the scenarios are shared:
	///   • Hold to expiry at current spot — intrinsic payoff right now.
	///   • Hold to expiry at the body strikes — max profit case (short legs expire worthless).
	///   • Hold to expiry at the wings — max loss case (one spread fully assigned).
	///   • Close all at mid — flatten now.
	///   • Close one side (the threatened spread) — common defensive move when spot has moved
	///     toward one wing; cuts the side that's bleeding while keeping the credit on the other.
	/// </summary>
	private static List<Scenario> GenerateIronScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var list = new List<Scenario>();
		var shortPut = legs.First(l => l.Action == LegAction.Sell && l.Parsed.CallPut == "P");
		var longPut = legs.First(l => l.Action == LegAction.Buy && l.Parsed.CallPut == "P");
		var shortCall = legs.First(l => l.Action == LegAction.Sell && l.Parsed.CallPut == "C");
		var longCall = legs.First(l => l.Action == LegAction.Buy && l.Parsed.CallPut == "C");
		var expiry = shortPut.Parsed.ExpiryDate;
		var expiryDte = Math.Max(1, (expiry.Date - asOf.Date).Days);
		var quotesForPricing = settings.DateSpec.Kind != AsOfKind.Now || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		// Helper: intrinsic value of the whole 4-leg position at a given expiration spot. Per-share,
		// signed so a positive number is what we'd receive if we closed at that intrinsic.
		decimal IntrinsicAtSpot(decimal s) =>
			OptionMath.Intrinsic(s, longPut.Parsed.Strike, "P") - OptionMath.Intrinsic(s, shortPut.Parsed.Strike, "P")
			+ OptionMath.Intrinsic(s, longCall.Parsed.Strike, "C") - OptionMath.Intrinsic(s, shortCall.Parsed.Strike, "C");

		// 1. Hold to expiry at current spot.
		var holdAtNow = IntrinsicAtSpot(spot);
		list.Add(NewScenarioSpread("Hold to expiry (current spot)", legs, "—",
			cashNow: 0m, valueAtTarget: holdAtNow, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
			rationale: $"at {expiry:yyyy-MM-dd} with spot held at ${spot:F2}: intrinsic ${holdAtNow:F2}/share"));

		// 2. Hold to expiry at the body strike(s) — max profit for the structure. For an iron condor
		// the body is a range, so we use the put-side body (between the shorts is the max-profit zone).
		var bodyTarget = shortPut.Parsed.Strike == shortCall.Parsed.Strike
			? shortPut.Parsed.Strike
			: (shortPut.Parsed.Strike + shortCall.Parsed.Strike) / 2m;
		var holdAtBody = IntrinsicAtSpot(bodyTarget);
		list.Add(NewScenarioSpread($"Hold to expiry (spot @ body ${bodyTarget:F2})", legs, "—",
			cashNow: 0m, valueAtTarget: holdAtBody, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
			rationale: $"max-profit case: at {expiry:yyyy-MM-dd} with spot @ ${bodyTarget:F2} (between/at short strikes): all shorts expire worthless, intrinsic ${holdAtBody:F2}/share"));

		// 3. Hold to expiry at the lower wing — max loss on put side.
		var holdAtLowerWing = IntrinsicAtSpot(longPut.Parsed.Strike);
		list.Add(NewScenarioSpread($"Hold to expiry (spot @ lower wing ${longPut.Parsed.Strike:F2})", legs, "—",
			cashNow: 0m, valueAtTarget: holdAtLowerWing, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
			rationale: $"put-side wipeout: at {expiry:yyyy-MM-dd} with spot @ long-put strike ${longPut.Parsed.Strike:F2}: intrinsic ${holdAtLowerWing:F2}/share"));

		// 4. Hold to expiry at the upper wing — max loss on call side.
		var holdAtUpperWing = IntrinsicAtSpot(longCall.Parsed.Strike);
		list.Add(NewScenarioSpread($"Hold to expiry (spot @ upper wing ${longCall.Parsed.Strike:F2})", legs, "—",
			cashNow: 0m, valueAtTarget: holdAtUpperWing, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
			rationale: $"call-side wipeout: at {expiry:yyyy-MM-dd} with spot @ long-call strike ${longCall.Parsed.Strike:F2}: intrinsic ${holdAtUpperWing:F2}/share"));

		// 5. Close everything at mid right now.
		var ivShortPut = ResolveIV(shortPut.Symbol, settings, quotes);
		var ivLongPut = ResolveIV(longPut.Symbol, settings, quotes);
		var ivShortCall = ResolveIV(shortCall.Symbol, settings, quotes);
		var ivLongCall = ResolveIV(longCall.Symbol, settings, quotes);
		var shortPutMid = LiveOrBsMid(quotesForPricing, shortPut.Symbol, spot, shortPut.Parsed.Strike, expiryDte, ivShortPut, "P", asOf, dividends);
		var longPutMid = LiveOrBsMid(quotesForPricing, longPut.Symbol, spot, longPut.Parsed.Strike, expiryDte, ivLongPut, "P", asOf, dividends);
		var shortCallMid = LiveOrBsMid(quotesForPricing, shortCall.Symbol, spot, shortCall.Parsed.Strike, expiryDte, ivShortCall, "C", asOf, dividends);
		var longCallMid = LiveOrBsMid(quotesForPricing, longCall.Symbol, spot, longCall.Parsed.Strike, expiryDte, ivLongCall, "C", asOf, dividends);
		// Buy back shorts, sell longs.
		var closeAllCash = -shortPutMid + longPutMid - shortCallMid + longCallMid;
		list.Add(NewScenarioSpread("Close all", legs,
			$"BUY {shortPut.Symbol} x{shortPut.Qty} @{FmtPrice(shortPutMid)}, SELL {longPut.Symbol} x{longPut.Qty} @{FmtPrice(longPutMid)}, BUY {shortCall.Symbol} x{shortCall.Qty} @{FmtPrice(shortCallMid)}, SELL {longCall.Symbol} x{longCall.Qty} @{FmtPrice(longCallMid)}",
			cashNow: closeAllCash, valueAtTarget: 0m, marginDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"flatten at mid: net cash ${closeAllCash:+0.00;-0.00}/share to close all four legs"));

		// 6. Close the threatened side — whichever spread is closer to being breached. The "threatened
		// side" is the credit spread whose short strike is nearest spot (or already breached).
		var distanceToShortPut = spot - shortPut.Parsed.Strike;   // positive when spot > shortPut (safe)
		var distanceToShortCall = shortCall.Parsed.Strike - spot; // positive when spot < shortCall (safe)
		bool putSideThreatened = distanceToShortPut < distanceToShortCall;
		if (putSideThreatened)
		{
			// Close the put spread: buy back short put, sell back long put.
			var sideCash = -shortPutMid + longPutMid;
			var residualAtExpiry = OptionMath.Intrinsic(spot, longCall.Parsed.Strike, "C") - OptionMath.Intrinsic(spot, shortCall.Parsed.Strike, "C");
			list.Add(NewScenarioSpread("Close put side (defensive)", legs,
				$"BUY {shortPut.Symbol} x{shortPut.Qty} @{FmtPrice(shortPutMid)}, SELL {longPut.Symbol} x{longPut.Qty} @{FmtPrice(longPutMid)}",
				cashNow: sideCash, valueAtTarget: residualAtExpiry, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
				rationale: $"put-side closer to short strike (${distanceToShortPut:F2} below) — cut the put spread for ${sideCash:+0.00;-0.00}/share; remaining call spread settles to ${residualAtExpiry:F2}/share at current spot"));
		}
		else
		{
			// Close the call spread: buy back short call, sell back long call.
			var sideCash = -shortCallMid + longCallMid;
			var residualAtExpiry = OptionMath.Intrinsic(spot, longPut.Parsed.Strike, "P") - OptionMath.Intrinsic(spot, shortPut.Parsed.Strike, "P");
			list.Add(NewScenarioSpread("Close call side (defensive)", legs,
				$"BUY {shortCall.Symbol} x{shortCall.Qty} @{FmtPrice(shortCallMid)}, SELL {longCall.Symbol} x{longCall.Qty} @{FmtPrice(longCallMid)}",
				cashNow: sideCash, valueAtTarget: residualAtExpiry, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
				rationale: $"call-side closer to short strike (${distanceToShortCall:F2} above) — cut the call spread for ${sideCash:+0.00;-0.00}/share; remaining put spread settles to ${residualAtExpiry:F2}/share at current spot"));
		}

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	/// <summary>Scenarios for an all-same-right condor (put or call). Same neutral payoff shape as an iron
	/// condor — a plateau between the two inner strikes, capped losses at the wings — but built from a
	/// single side, so the pricing is right-agnostic: the position's intrinsic at any expiration spot is
	/// just the signed sum of each leg's intrinsic (long adds, short subtracts). Probes hold-to-expiry at
	/// current spot, the inner-strike plateau midpoint, and each wing, plus a close-all-at-mid; ranking then
	/// surfaces the best/worst outcomes whether it's a long condor (wings long) or short condor (wings short).</summary>
	private static List<Scenario> GenerateCondorScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var list = new List<Scenario>();
		var ordered = legs.OrderBy(l => l.Parsed.Strike).ToList(); // K1 < K2 < K3 < K4
		var expiry = ordered[0].Parsed.ExpiryDate;
		var expiryDte = Math.Max(1, (expiry.Date - asOf.Date).Days);
		var quotesForPricing = settings.DateSpec.Kind != AsOfKind.Now || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		// Signed intrinsic of the whole position held to expiry at spot s (per share): longs add their
		// intrinsic, shorts subtract it. Right-agnostic, so it covers both put and call condors.
		decimal IntrinsicAtSpot(decimal s) => ordered.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * OptionMath.Intrinsic(s, l.Parsed.Strike, l.Parsed.CallPut));

		var innerMid = (ordered[1].Parsed.Strike + ordered[2].Parsed.Strike) / 2m;
		var targets = new (string Label, decimal Spot)[]
		{
			("current spot", spot),
			($"plateau mid ${innerMid:F2}", innerMid),
			($"lower wing ${ordered[0].Parsed.Strike:F2}", ordered[0].Parsed.Strike),
			($"upper wing ${ordered[3].Parsed.Strike:F2}", ordered[3].Parsed.Strike),
		};
		foreach (var (label, targetSpot) in targets)
		{
			var v = IntrinsicAtSpot(targetSpot);
			list.Add(NewScenarioSpread($"Hold to expiry ({label})", legs, "—",
				cashNow: 0m, valueAtTarget: v, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
				rationale: $"at {expiry:yyyy-MM-dd} with spot @ ${targetSpot:F2}: intrinsic ${v:F2}/share"));
		}

		// Close all at mid now: sell the longs, buy back the shorts. Price each leg once.
		var mids = ordered.ToDictionary(l => l.Symbol, l => LiveOrBsMid(quotesForPricing, l.Symbol, spot, l.Parsed.Strike, expiryDte, ResolveIV(l.Symbol, settings, quotes), l.Parsed.CallPut, asOf, dividends), StringComparer.OrdinalIgnoreCase);
		var closeCash = ordered.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * mids[l.Symbol]);
		var closeAction = string.Join(", ", ordered.Select(l => $"{(l.Action == LegAction.Buy ? "SELL" : "BUY")} {l.Symbol} x{l.Qty} @{FmtPrice(mids[l.Symbol])}"));
		list.Add(NewScenarioSpread("Close all", legs, closeAction,
			cashNow: closeCash, valueAtTarget: 0m, marginDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"flatten at mid: net cash ${closeCash:+0.00;-0.00}/share to close all four legs"));

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	private static List<Scenario> GenerateSingleLongScenarios(PositionSnapshot longLeg, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null, bool shortlistSwept = true)
	{
		var list = new List<Scenario>();
		var iv = ResolveIV(longLeg.Symbol, settings, quotes);
		var callPut = longLeg.Parsed.CallPut;
		var quotesForPricing = settings.DateSpec.Kind != AsOfKind.Now || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var longOnlyLegs = new List<(decimal, DateTime, decimal, bool, string)> { (longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, iv, true, callPut) };

		// 1. Hold (do nothing). The pinned value is intrinsic at today's spot — for an OTM long, the whole premium lost — so the ranking uses the EV.
		var valueAtExpiry = OptionMath.Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		var holdDte = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var holdEvPerShare = ExpectedPositionValuePerShare(longLeg.Parsed.Root, longOnlyLegs, longLeg.Parsed.ExpiryDate, asOf, spot, iv, dividends);
		list.Add(NewScenarioSpread("Hold to expiry", [longLeg], "—",
			cashNow: 0m, valueAtTarget: valueAtExpiry, marginDeltaPerContract: 0m, daysToTarget: holdDte,
			rationale: $"value at expiry ({longLeg.Parsed.ExpiryDate:yyyy-MM-dd}) = intrinsic ${valueAtExpiry:F2}/share (pinned)",
			expectedValueAtTarget: holdEvPerShare,
			book: [new BookStructure("existing", UnitLegs([longLeg]), longLeg.Qty)],
			evNote: $"EV ${holdEvPerShare:F2}/share at expiry across the spot distribution"));

		// 2. Close now at theoretical mid (or live mid if available).
		var dteNow = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var midNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, dteNow, iv, callPut, asOf, dividends);
		list.Add(NewScenarioSpread("Close now", [longLeg], $"SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(midNow)}",
			cashNow: midNow, valueAtTarget: 0m, marginDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"sell at mid ${midNow:F2}/share → close position",
			expectedValueAtTarget: 0m,
			book: [],
			orderContractsNow: longLeg.Qty));

		// 3. Convert to a spread by selling a short against the long. With a live chain, sweep the listed shorts that make a vertical,
		// calendar or diagonal with it (EnumerateConversionShorts), each valued like a swept roll; without one, the same-strike next-weekly calendar.
		var conversions = new List<Scenario>();
		if (quotesForPricing != null)
		{
			foreach (var cand in EnumerateConversionShorts(quotesForPricing, longLeg, spot, asOf))
			{
				var shortMid = (cand.Quote.Bid!.Value + cand.Quote.Ask!.Value) / 2m;
				EmitConversion(conversions, longLeg, settings, spot, asOf, cand.Symbol, cand.Parsed, shortMid, iv, valueAtExpiry, holdEvPerShare, quotes, dividends);
			}
		}

		if (conversions.Count > 0)
		{
			var flagged = conversions.Select(c => c with { Swept = true }).ToList();
			list.AddRange(shortlistSwept ? ShortlistSwept(flagged, settings.Cash, settings.Risk) : flagged);
		}
		else
		{
			var shortExpiry = MarketCalendar.NextFridayAfter(asOf);
			if (shortExpiry < longLeg.Parsed.ExpiryDate)
			{
				var shortParsed = new OptionParsed(longLeg.Parsed.Root, shortExpiry, callPut, longLeg.Parsed.Strike);
				var shortSymbol = MatchKeys.OccSymbol(shortParsed.Root, shortExpiry, shortParsed.Strike, callPut);
				var shortMid = LiveOrBsMid(quotesForPricing, shortSymbol, spot, shortParsed.Strike, Math.Max(1, (shortExpiry - asOf).Days), ResolveIV(shortSymbol, settings, quotes), callPut, asOf, dividends);
				EmitConversion(list, longLeg, settings, spot, asOf, shortSymbol, shortParsed, shortMid, iv, valueAtExpiry, holdEvPerShare, quotes, dividends);
			}
		}

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	/// <summary>Listed shorts (same right, two-sided and liquid) that convert <paramref name="longLeg"/> into a two-leg spread, never expiring after it:
	/// ±5 strikes around spot at the 8 front expiries (calendars and diagonals) and at the long's own expiry (verticals), plus the long's strike at the
	/// front expiries so a same-strike calendar is offered even when the long sits far from spot.</summary>
	internal static List<RollCandidate> EnumerateConversionShorts(IReadOnlyDictionary<string, OptionContractQuote> quotes, PositionSnapshot longLeg, decimal spot, DateTime asOf)
	{
		var l = longLeg.Parsed;
		(OptionParsed Anchor, int StrikesN, int ExpN)[] windows =
		[
			(l with { Strike = spot, ExpiryDate = asOf.Date }, 5, 7),
			(l with { Strike = spot }, 5, 0),
			(l with { ExpiryDate = asOf.Date }, 0, 7),
		];
		return windows
			.SelectMany(w => RollCandidateEnumerator.Enumerate(quotes, l, longLeg.Symbol, w.Anchor, anchorSymbol: null, LegAction.Sell, w.StrikesN, w.ExpN, asOf).Candidates)
			.Where(c => c.Parsed.ExpiryDate.Date <= l.ExpiryDate.Date)
			.DistinctBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	/// <summary>Appends the scenario (and its fundable partial) of selling <paramref name="shortSymbol"/> at <paramref name="shortMid"/> against the
	/// single long: valued at the short's expiry, the long on its residual and the short at intrinsic; a vertical also states the floor it locks.</summary>
	private static void EmitConversion(
		List<Scenario> list,
		PositionSnapshot longLeg,
		AnalyzePositionSettings settings,
		decimal spot,
		DateTime asOf,
		string shortSymbol,
		OptionParsed shortParsed,
		decimal shortMid,
		decimal ivLong,
		decimal holdValuePerShare,
		decimal holdEvPerShare,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes,
		IReadOnlyList<DividendEvent>? dividends)
	{
		var callPut = longLeg.Parsed.CallPut;
		var shortExp = shortParsed.ExpiryDate;
		var ivShort = ResolveIV(shortSymbol, settings, quotes);
		var longAtShortExp = LongValueAt(longLeg.Parsed, ivLong, shortExp, spot, dividends);
		var shortAtShortExp = OptionMath.Intrinsic(spot, shortParsed.Strike, callPut);
		var projected = longAtShortExp - shortAtShortExp;
		var legsForEv = new List<(decimal, DateTime, decimal, bool, string)>
		{
			(longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut),
			(shortParsed.Strike, shortExp, ivShort, false, callPut),
		};
		var evPerShare = ExpectedPositionValuePerShare(longLeg.Parsed.Root, legsForEv, shortExp, asOf, spot, ivShort, dividends);
		var breakEvens = ComputeScenarioBreakEvens(longLeg.Parsed.Root, legsForEv, shortExp, spot, longLeg.CostBasis - shortMid, dividends);
		var margin = MarginCalculator.PairPerContract(shortParsed, shortMid, longLeg.Parsed, spot);
		var shape = PairShapeLabel(shortParsed, longLeg.Parsed);
		var floorNote = shortExp.Date == longLeg.Parsed.ExpiryDate.Date
			? $"; locks a floor of ${VerticalFloorPerShare(shortParsed, longLeg.Parsed, shortMid, longLeg.CostBasis):+0.00;-0.00}/share"
			: "";

		EmitFullAndPartial(list, [longLeg], settings.Cash,
			name: $"Convert to {shape} (sell {shortExp:MM-dd} ${shortParsed.Strike:F2})",
			actionSummary: $"SELL {shortSymbol} x{{qty}} @{FmtPrice(shortMid)}",
			cashPerShareOfChange: shortMid,
			newProjectedPerShare: projected,
			unchangedProjectedPerShare: holdValuePerShare,
			marginPerContract: margin,
			daysToTarget: Math.Max(1, (shortExp.Date - asOf.Date).Days),
			rationale: $"sell at mid ${shortMid:F2}/share credit; at {shortExp:MM-dd}: long ${longAtShortExp:F2} - short ${shortAtShortExp:F2} = ${projected:F2} (pinned){floorNote}",
			isLegIn: true,
			newExpectedPerShare: evPerShare,
			unchangedExpectedPerShare: holdEvPerShare,
			breakEvens: breakEvens,
			resultingLegs: [.. UnitLegs([longLeg]), UnitLeg(shortSymbol, LegAction.Sell, shortMid, shortParsed)],
			resultLabel: shape,
			evNote: $"EV ${evPerShare:F2}/share at {shortExp:MM-dd}");
	}

	/// <summary>The structure a short at <paramref name="shortLeg"/> makes with <paramref name="longLeg"/> (same right): a vertical at the long's
	/// expiry, else a calendar at its strike or a diagonal. "Inverted" when the short is on the long's in-the-money side (a call short below the long
	/// call, a put short above the long put): the pair then carries the strike width as margin.</summary>
	internal static string PairShapeLabel(OptionParsed shortLeg, OptionParsed longLeg)
	{
		var inverted = shortLeg.CallPut == "C" ? shortLeg.Strike < longLeg.Strike : shortLeg.Strike > longLeg.Strike;
		if (shortLeg.ExpiryDate.Date == longLeg.ExpiryDate.Date) return inverted ? "inverted vertical" : "vertical";
		if (shortLeg.Strike == longLeg.Strike) return "calendar";
		return inverted ? "inverted diagonal" : "covered diagonal";
	}

	/// <summary>The lowest P&amp;L per share at expiry of the same-expiry vertical made by selling <paramref name="shortLeg"/> for
	/// <paramref name="credit"/> against <paramref name="longLeg"/> bought for <paramref name="longBasis"/>: credit − basis, less the strike width
	/// when the short is on the long's in-the-money side. A floor ≥ 0 means the converted position cannot lose.</summary>
	internal static decimal VerticalFloorPerShare(OptionParsed shortLeg, OptionParsed longLeg, decimal credit, decimal longBasis) =>
		credit - longBasis - Math.Max(0m, shortLeg.CallPut == "C" ? longLeg.Strike - shortLeg.Strike : shortLeg.Strike - longLeg.Strike);

	/// <summary>Per-share value of <paramref name="longLeg"/> at the close of <paramref name="target"/>, with spot unchanged: intrinsic when it
	/// expires by then, else Black-Scholes on its residual life at <paramref name="ivLong"/>.</summary>
	private static decimal LongValueAt(OptionParsed longLeg, decimal ivLong, DateTime target, decimal spot, IReadOnlyList<DividendEvent>? dividends) =>
		longLeg.ExpiryDate.Date <= target.Date
			? OptionMath.Intrinsic(spot, longLeg.Strike, longLeg.CallPut)
			: (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, target.Date + OptionMath.MarketClose, longLeg.ExpiryDate.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longLeg.Strike, MarketClock.CloseToClose(longLeg.Root, target, longLeg.ExpiryDate, minDays: 1), OptionMath.RiskFreeRate, ivLong, longLeg.CallPut);

	private static List<Scenario> GenerateVerticalScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes, decimal technicalBias, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var list = new List<Scenario>();
		var shortLeg = legs.First(l => l.Action == LegAction.Sell);
		var longLeg = legs.First(l => l.Action == LegAction.Buy);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, settings, quotes);
		var ivLong = ResolveIV(longLeg.Symbol, settings, quotes);
		var quotesForPricing = settings.DateSpec.Kind != AsOfKind.Now || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var expiry = shortLeg.Parsed.ExpiryDate;
		var expiryDte = Math.Max(1, (expiry.Date - asOf.Date).Days);
		var shortMidNow = LiveOrBsMid(quotesForPricing, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, expiryDte, ivShort, callPut, asOf, dividends);
		var longMidNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, expiryDte, ivLong, callPut, asOf, dividends);
		var (_, shortAskNow) = LiveBidAsk(quotesForPricing, shortLeg.Symbol, shortMidNow);
		var (longBidNow, _) = LiveBidAsk(quotesForPricing, longLeg.Symbol, longMidNow);

		var currentMargin = MarginCalculator.PairPerContract(shortLeg.Parsed, shortMidNow, longLeg.Parsed, spot);
		var longAtExpiry = OptionMath.Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		var shortAtExpiry = OptionMath.Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
		var holdNetPerShare = longAtExpiry - shortAtExpiry;
		var strikeOffset = longLeg.Parsed.Strike - shortLeg.Parsed.Strike;

		list.Add(NewScenarioSpread("Hold to expiry", legs, "—",
			cashNow: 0m, valueAtTarget: holdNetPerShare, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
			rationale: $"at {expiry:yyyy-MM-dd}: long ${longAtExpiry:F2} intrinsic - short ${shortAtExpiry:F2} intrinsic = ${holdNetPerShare:F2}"));

		{
			var cash = -shortMidNow;
			var marginDelta = 0m - currentMargin;
			list.Add(NewScenarioSpread("Close short only", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortMidNow)}",
				cashNow: cash, valueAtTarget: longAtExpiry, marginDeltaPerContract: marginDelta, daysToTarget: expiryDte,
			  rationale: $"buy back short at mid ${shortMidNow:F2}/share; keep long → intrinsic ${longAtExpiry:F2}/share at expiry"));
		}

		{
			var cash = longMidNow - shortMidNow;
			var marginDelta = 0m - currentMargin;
			list.Add(NewScenarioSpread("Close all", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortMidNow)}, SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(longMidNow)}",
				cashNow: cash, valueAtTarget: 0m, marginDeltaPerContract: marginDelta, daysToTarget: 1,
			  rationale: $"close at mid prices → net ${cash:+0.00;-0.00}/share"));
		}

		// Same fixed-step-vs-listed-grid problem as the complementary-wing search below: a uniform
		// --strike-step around spot guesses strikes the venue never lists on SPX-family chains. Anchored at
		// spot itself (not mirrored) — a roll IS meant to move the short toward a fresh near-the-money
		// strike, unlike the complementary wing which mirrors the held strike's own OTM distance.
		var rollLadder = StrikeLadder.Build(shortLeg.Parsed.Root, expiry, callPut, quotesForPricing);
		var candidateRollStrikes = rollLadder.IsEmpty ? StrikeGrid.Bracket(spot, settings.StrikeStep) : rollLadder.Around(spot, 1);
		foreach (var newStrike in candidateRollStrikes)
		{
			if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

			var sameExpSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, expiry, newStrike, callPut);
			if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, sameExpSym)) continue;

			var ivSameExp = ResolveIV(sameExpSym, settings, quotes);
			var newShortMidSameExp = LiveOrBsMid(quotesForPricing, sameExpSym, spot, newStrike, expiryDte, ivSameExp, callPut, asOf, dividends);
			var (newShortBidSameExp, _) = LiveBidAsk(quotesForPricing, sameExpSym, newShortMidSameExp);
			var cashPerShareSameExp = newShortMidSameExp - shortMidNow;
			var projSameExpPerShare = longAtExpiry - OptionMath.Intrinsic(spot, newStrike, callPut);
			var sameExpShortParsed = new OptionParsed(shortLeg.Parsed.Root, expiry, callPut, newStrike);
			var sameExpMargin = MarginCalculator.PairPerContract(sameExpShortParsed, newShortMidSameExp, longLeg.Parsed, spot);
			var sameExpMarginDelta = sameExpMargin - currentMargin;

			EmitFullAndPartial(list, legs, settings.Cash,
				name: $"Roll short to ${newStrike:F2} (same exp {expiry:MM-dd}, {VerticalStructureLabel(callPut, newStrike, longLeg.Parsed.Strike)})",
				actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {sameExpSym} x{{qty}} @{FmtPrice(newShortMidSameExp)}",
				cashPerShareOfChange: cashPerShareSameExp,
				newProjectedPerShare: projSameExpPerShare,
				unchangedProjectedPerShare: holdNetPerShare,
				marginPerContract: sameExpMarginDelta,
				daysToTarget: expiryDte,
				rationale: $"shift short to ${newStrike:F2}, keep {expiry:MM-dd} expiry; mid net ${cashPerShareSameExp:+0.00;-0.00}/share; at expiry: ${projSameExpPerShare:F2}",
				isRoll: true);
		}

		var width = Math.Abs(longLeg.Parsed.Strike - shortLeg.Parsed.Strike);
		var oppositeCp = callPut == "C" ? "P" : "C";
		// Candidate short strikes for the complementary wing, from the LISTED chain rather than a fixed
		// --strike-step grid: SPX-family strike spacing varies ($5-50 depending on moneyness/expiry), so a
		// uniform step guesses strikes the venue never lists and every candidate below silently drops at the
		// HasLiveQuote check. Anchored at the MIRROR of the held short strike across spot, not spot itself —
		// a held vertical that's already far OTM wants its iron-condor partner the same distance OTM on the
		// other side, not an at-the-money wing. Falls back to the old spot-anchored step grid when no chain
		// is available (tests, --theoretical), which also keeps that path's exact prior behavior.
		var complementLadder = StrikeLadder.Build(shortLeg.Parsed.Root, expiry, oppositeCp, quotesForPricing);
		var mirrorStrike = 2m * spot - shortLeg.Parsed.Strike;
		var candidateShortStrikes = complementLadder.IsEmpty ? StrikeGrid.Bracket(spot, settings.StrikeStep) : complementLadder.Around(mirrorStrike, 1);
		foreach (var newShortStrike in candidateShortStrikes)
		{
			if (!IsComplementaryShortStrike(oppositeCp, newShortStrike, spot)) continue;

			var newLongStrike = ComplementaryLongStrike(oppositeCp, newShortStrike, width);
			if (newLongStrike <= 0m) continue;

			var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, expiry, newShortStrike, oppositeCp);
			var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, expiry, newLongStrike, oppositeCp);
			if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;

			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var ivNewLong = ResolveIV(newLongSym, settings, quotes);
			var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newShortStrike, expiryDte, ivNewShort, oppositeCp, asOf, dividends);
			var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newLongStrike, expiryDte, ivNewLong, oppositeCp, asOf, dividends);
			var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
			var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
			var cashPerShare = newShortMidExec - newLongMidExec;
			var newPositionValuePerShare = OptionMath.Intrinsic(spot, newLongStrike, oppositeCp) - OptionMath.Intrinsic(spot, newShortStrike, oppositeCp);
			var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, expiry, oppositeCp, newShortStrike);
			var newLongParsed = new OptionParsed(longLeg.Parsed.Root, expiry, oppositeCp, newLongStrike);
			var newMargin = MarginCalculator.PairPerContract(newShortParsed, newShortMidExec, newLongParsed, spot);
			var marginDelta = Math.Max(currentMargin, newMargin) - currentMargin;
			if (marginDelta > 0m) continue;

			var wingLabel = oppositeCp == "C" ? "call" : "put";
			EmitAdd(list, legs, settings.Cash,
				name: $"Add complementary {wingLabel} spread (${newShortStrike:F2}/${newLongStrike:F2}) → iron condor",
				newShortSym: newShortSym,
				newLongSym: newLongSym,
				newShortPrice: newShortMidExec,
				newLongPrice: newLongMidExec,
				cashPerShareOfChange: cashPerShare,
				newProjectedPerShare: newPositionValuePerShare,
				unchangedProjectedPerShare: holdNetPerShare,
				marginPerContract: marginDelta,
				daysToTarget: expiryDte,
				rationale: $"add the opposite-side {wingLabel} wing at the same expiry; combined iron condor stays within current width collateral, so additional margin is ${marginDelta:N0}. At expiry: existing ${holdNetPerShare:F2} + new ${newPositionValuePerShare:F2} = ${holdNetPerShare + newPositionValuePerShare:F2}/share",
				addedLegs: [UnitLeg(newShortSym, LegAction.Sell, newShortMidExec, newShortParsed), UnitLeg(newLongSym, LegAction.Buy, newLongMidExec, newLongParsed)]);
		}

		var newExpiry = MarketCalendar.NextFridayAfter(expiry);
		foreach (var newStrike in StrikeGrid.Bracket(spot, settings.StrikeStep))
		{
			if (newStrike <= 0m) continue;

			var newLongStrike = newStrike + strikeOffset;
			if (newLongStrike <= 0m) continue;

			var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExpiry, newStrike, callPut);
			var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newExpiry, newLongStrike, callPut);
			if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;

			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var ivNewLong = ResolveIV(newLongSym, settings, quotes);
			var dteNew = Math.Max(1, (newExpiry - asOf).Days);
			var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newStrike, dteNew, ivNewShort, callPut, asOf, dividends);
			var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newLongStrike, dteNew, ivNewLong, callPut, asOf, dividends);
			var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
			var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
			var closeNet = longMidNow - shortMidNow;
			var openNet = newShortMidExec - newLongMidExec;
			var cashPerShare = closeNet + openNet;
			var newProjectedPerShare = OptionMath.Intrinsic(spot, newLongStrike, callPut) - OptionMath.Intrinsic(spot, newStrike, callPut);
			var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExpiry, callPut, newStrike);
			var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newExpiry, callPut, newLongStrike);
			var newMargin = MarginCalculator.PairPerContract(newShortParsed, newShortMidExec, newLongParsed, spot);
			var marginDelta = newMargin - currentMargin;

			EmitFullAndPartial(list, legs, settings.Cash,
				name: $"Reset to ${newStrike:F2}/${newLongStrike:F2} vertical ({newExpiry:MM-dd})",
				actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {longLeg.Symbol} x{{qty}} @{FmtPrice(longMidNow)}, BUY {newLongSym} x{{qty}} @{FmtPrice(newLongMidExec)}, SELL {newShortSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
				cashPerShareOfChange: cashPerShare,
				newProjectedPerShare: newProjectedPerShare,
				unchangedProjectedPerShare: holdNetPerShare,
				marginPerContract: marginDelta,
				daysToTarget: dteNew,
				rationale: $"close net ${closeNet:+0.00;-0.00}, open next-week vertical net ${openNet:+0.00;-0.00} at mid; projected at {newExpiry:MM-dd}: ${newProjectedPerShare:F2}",
				isRoll: true);
		}

		return OrderScenariosForDisplay(list
			.Select(s => s with { AssignmentPenaltyPerContract = ComputeVerticalAssignmentPenalty(s, shortLeg, spot, settings.StrikeStep, technicalBias) })
			.ToList(), settings.Cash, settings.Risk);
	}

	/// <param name="shortlistSwept">False keeps every swept roll candidate (flagged <see cref="Scenario.Swept"/>) for the caller to shortlist with
	/// <see cref="ShortlistSwept"/> after re-valuing them, so the shortlist is chosen on the same EV the scenarios are ranked on.</param>
	private static List<Scenario> GenerateSpreadScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, StructureKind kind, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null, bool shortlistSwept = true)
	{
		var list = new List<Scenario>();
		var shortLeg = legs.First(l => l.Action == LegAction.Sell);
		var longLeg = legs.First(l => l.Action == LegAction.Buy);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, settings, quotes);
		var ivLong = ResolveIV(longLeg.Symbol, settings, quotes);

		// When a date override is active, live bid/ask reflects today's market prices, not the
		// evaluation date's. Null out quotes for pricing so LiveOrBsMid/LiveBidAsk always use
		// Black-Scholes with the correct DTE from asOf. IV is still sourced from live quotes above.
		var quotesForPricing = settings.DateSpec.Kind != AsOfKind.Now || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var shortMidNow = LiveOrBsMid(quotesForPricing, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivShort, callPut, asOf, dividends);
		var longMidNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivLong, callPut, asOf, dividends);
		var (shortBidNow, shortAskNow) = LiveBidAsk(quotesForPricing, shortLeg.Symbol, shortMidNow);
		var (longBidNow, longAskNow) = LiveBidAsk(quotesForPricing, longLeg.Symbol, longMidNow);

		// Current margin (ongoing; covered-structure debit is sunk).
		var currentMargin = MarginCalculator.PairPerContract(shortLeg.Parsed, shortMidNow, longLeg.Parsed, spot);

		// Compute the "hold" per-share value once — used as the unchanged-portion projection for partial variants.
		var longAtOriginalExp = LongValueAt(longLeg.Parsed, ivLong, shortLeg.Parsed.ExpiryDate, spot, dividends);
		var shortAtOriginalExp = OptionMath.Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
		var holdNetPerShare = longAtOriginalExp - shortAtOriginalExp;

		var origShortDte = Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);

		// Probability-weighted EV of holding the existing spread to short expiry (vs the spot-pinned
		// holdNetPerShare, which sits near the calendar's peak). The near-leg IV sets the spot distribution
		// over [asOf, short expiry]; the surviving long is BS-valued on its residual, the short settles to
		// intrinsic. Used both for the Hold scenario's EV and as the "unchanged" leg of every add.
		var existingLegsForEv = new List<(decimal, DateTime, decimal, bool, string)>
		{
			(longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut),
			(shortLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate, ivShort, false, callPut),
		};
		var holdEvPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, existingLegsForEv, shortLeg.Parsed.ExpiryDate, asOf, spot, ivShort, dividends);
		var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var holdBreakEvens = ComputeScenarioBreakEvens(shortLeg.Parsed.Root, existingLegsForEv, shortLeg.Parsed.ExpiryDate, spot, initialDebitPerShare, dividends);

		// 1. Hold to short expiry.
		list.Add(NewScenarioSpread("Hold to short expiry", legs, "—",
			cashNow: 0m, valueAtTarget: holdNetPerShare, marginDeltaPerContract: 0m, daysToTarget: origShortDte,
			rationale: $"at {shortLeg.Parsed.ExpiryDate:yyyy-MM-dd}: long ${longAtOriginalExp:F2} - short ${shortAtOriginalExp:F2} intrinsic = ${holdNetPerShare:F2} (pinned)",
			expectedValueAtTarget: holdEvPerShare,
			breakEvens: holdBreakEvens,
			book: [new BookStructure("existing", UnitLegs(legs), legs[0].Qty)],
			evNote: $"EV ${holdEvPerShare:F2}/share across the spot distribution"));

		// 2. Close short only (mid basis).
		{
			var cash = -shortMidNow;
			var longAtExpiry = LongValueAt(longLeg.Parsed, ivLong, shortLeg.Parsed.ExpiryDate, spot, dividends);
			var longOnlyLegs = new List<(decimal, DateTime, decimal, bool, string)> { (longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut) };
			var longEvAtExpiry = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, longOnlyLegs, shortLeg.Parsed.ExpiryDate, asOf, spot, ivShort, dividends);
			var longBe = ComputeScenarioBreakEvens(shortLeg.Parsed.Root, longOnlyLegs, shortLeg.Parsed.ExpiryDate, spot, initialDebitPerShare - cash, dividends);
			// Post-action: single long. No short = no margin requirement.
			var marginDelta = 0m - currentMargin;
			list.Add(NewScenarioSpread("Close short only", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortMidNow)}",
				cashNow: cash, valueAtTarget: longAtExpiry, marginDeltaPerContract: marginDelta, daysToTarget: origShortDte,
				rationale: $"buy back short at mid ${shortMidNow:F2}/share; keep long → ${longAtExpiry:F2}/share (pinned)",
				expectedValueAtTarget: longEvAtExpiry,
				breakEvens: longBe,
				book: [new BookStructure("long only", UnitLegs([longLeg]), longLeg.Qty)],
				evNote: $"EV ${longEvAtExpiry:F2}/share at short exp",
				orderContractsNow: shortLeg.Qty));
		}

		// 3. Close all at the default mid basis (deterministic — EV equals locked-in P&L).
		{
			var cash = longMidNow - shortMidNow;
			var marginDelta = 0m - currentMargin;
			list.Add(NewScenarioSpread("Close all", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortMidNow)}, SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(longMidNow)}",
				cashNow: cash, valueAtTarget: 0m, marginDeltaPerContract: marginDelta, daysToTarget: 1,
				rationale: $"close at mid prices → net ${cash:+0.00;-0.00}/share",
				expectedValueAtTarget: 0m,
				book: [],
				orderContractsNow: shortLeg.Qty));
		}

		var newExp = MarketCalendar.NextFridayAfter(shortLeg.Parsed.ExpiryDate);

		// 4 & 5. Roll short: when a live chain is loaded, sweep ±5 strikes × ±7 expiries via RollCandidateEnumerator
		// (scoring every candidate on the probability-weighted spot distribution) and surface the top rolls instead of
		// only checking 2–3 hardcoded guesses. Falls back to the synthetic bracket rolls when pricing purely from BS.
		var sweptRolls = new List<Scenario>();
		if (quotesForPricing != null)
		{
			var (rollCandidates, _) = RollCandidateEnumerator.Enumerate(
				quotesForPricing, shortLeg.Parsed, shortLeg.Symbol, shortLeg.Parsed, anchorSymbol: null, LegAction.Sell, strikesN: 5, expN: 7, today: asOf);

			foreach (var cand in rollCandidates)
			{
				if (cand.Parsed.ExpiryDate.Date >= longLeg.Parsed.ExpiryDate.Date) continue;
				if (!cand.Quote.Bid.HasValue || !cand.Quote.Ask.HasValue) continue;

				var candExp = cand.Parsed.ExpiryDate;
				var candStrike = cand.Parsed.Strike;
				var ivNewShort = ResolveIV(cand.Symbol, settings, quotes);
				var dteNewShort = Math.Max(1, (candExp.Date - asOf.Date).Days);
				var newShortMidExec = (cand.Quote.Bid.Value + cand.Quote.Ask.Value) / 2m;
				var cashPerShare = newShortMidExec - shortMidNow;
				var longAtNewShortExp = LongValueAt(longLeg.Parsed, ivLong, candExp, spot, dividends);
				var shortAtNewShortExp = OptionMath.Intrinsic(spot, candStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;

				var newLegsForEv = new List<(decimal, DateTime, decimal, bool, string)>
				{
					(longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut),
					(candStrike, candExp, ivNewShort, false, callPut),
				};
				var newEvPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, newLegsForEv, candExp, asOf, spot, ivNewShort, dividends);
				var newBreakEvens = ComputeScenarioBreakEvens(shortLeg.Parsed.Root, newLegsForEv, candExp, spot, initialDebitPerShare - cashPerShare, dividends);

				var newMargin = MarginCalculator.PairPerContract(cand.Parsed, newShortMidExec, longLeg.Parsed, spot);
				var marginDelta = newMargin - currentMargin;
				var structureLabel = PairShapeLabel(cand.Parsed, longLeg.Parsed);
				var strikeDesc = candStrike == shortLeg.Parsed.Strike ? "same strike" : $"to ${candStrike:F2}";
				var expDesc = candExp.Date == shortLeg.Parsed.ExpiryDate.Date ? $"same exp {candExp:MM-dd}" : $"{candExp:MM-dd}";
				var peakCash = RollShape.SupportsComboRoll([shortLeg.Symbol, cand.Symbol]) ? 0m : shortMidNow * 100m;

				EmitFullAndPartial(sweptRolls, legs, settings.Cash,
					name: $"Roll short {strikeDesc} ({expDesc}, {structureLabel})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {cand.Symbol} x{{qty}} @{FmtPrice(newShortMidExec)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: marginDelta,
					daysToTarget: dteNewShort,
					rationale: $"roll at mid net ${cashPerShare:+0.00;-0.00}/share (pinned ${newProjectedPerShare:F2} at {candExp:MM-dd})",
					isRoll: true,
					peakCashPerContract: peakCash,
					newExpectedPerShare: newEvPerShare,
					unchangedExpectedPerShare: holdEvPerShare,
					breakEvens: newBreakEvens,
					resultingLegs: [UnitLeg(longLeg.Symbol, LegAction.Buy, longMidNow, longLeg.Parsed), UnitLeg(cand.Symbol, LegAction.Sell, newShortMidExec, cand.Parsed)],
					evNote: $"EV ${newEvPerShare:F2}/share at {candExp:MM-dd}");
			}
		}

		if (sweptRolls.Count > 0)
		{
			var flagged = sweptRolls.Select(r => r with { Swept = true }).ToList();
			list.AddRange(shortlistSwept ? ShortlistSwept(flagged, settings.Cash, settings.Risk) : flagged);
		}
		else if (newExp < longLeg.Parsed.ExpiryDate)
		{
			// Fallback when no live chain is loaded (--theoretical / --date / unit tests without full chain):
			// 4. Roll short same strike, next weekly.
			{
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, shortLeg.Parsed.Strike, callPut);
				if (quotesForPricing == null || HasLiveQuote(quotesForPricing, newSym))
				{
					var ivNewShort = ResolveIV(newSym, settings, quotes);
					var dteNewShort = Math.Max(1, (newExp - asOf).Days);
					var newShortMidExec = LiveOrBsMid(quotesForPricing, newSym, spot, shortLeg.Parsed.Strike, dteNewShort, ivNewShort, callPut, asOf, dividends);
					var cashPerShare = newShortMidExec - shortMidNow;
					var longAtNewShortExp = LongValueAt(longLeg.Parsed, ivLong, newExp, spot, dividends);
					var shortAtNewShortExp = OptionMath.Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
					var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
					var newEvPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, [(longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut), (shortLeg.Parsed.Strike, newExp, ivNewShort, false, callPut)], newExp, asOf, spot, ivNewShort, dividends);
					var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, shortLeg.Parsed.Strike);
					var newMargin = MarginCalculator.PairPerContract(newShortParsed, newShortMidExec, longLeg.Parsed, spot);
					var marginDelta = newMargin - currentMargin;
					EmitFullAndPartial(list, legs, settings.Cash,
						name: $"Roll short ({newExp:MM-dd}, same strike)",
						actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
						cashPerShareOfChange: cashPerShare,
						newProjectedPerShare: newProjectedPerShare,
						unchangedProjectedPerShare: holdNetPerShare,
						marginPerContract: marginDelta,
						daysToTarget: dteNewShort,
						rationale: $"roll at mid prices for net ${cashPerShare:+0.00;-0.00}/share (pinned ${newProjectedPerShare:F2})",
						isRoll: true,
						newExpectedPerShare: newEvPerShare,
						unchangedExpectedPerShare: holdEvPerShare,
						resultingLegs: [UnitLeg(longLeg.Symbol, LegAction.Buy, longMidNow, longLeg.Parsed), UnitLeg(newSym, LegAction.Sell, newShortMidExec, newShortParsed)],
						evNote: $"EV ${newEvPerShare:F2}/share");
				}
			}

			// 4.5. Roll short to bracket strikes, SAME expiry.
			foreach (var newStrike in StrikeGrid.Bracket(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

				var sameExpSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, newStrike, callPut);
				if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, sameExpSym)) continue;

				var ivSameExp = ResolveIV(sameExpSym, settings, quotes);
				var dteSameExp = Math.Max(1, (shortLeg.Parsed.ExpiryDate - asOf).Days);
				var newShortMidSameExp = LiveOrBsMid(quotesForPricing, sameExpSym, spot, newStrike, dteSameExp, ivSameExp, callPut, asOf, dividends);
				var cashPerShareSameExp = newShortMidSameExp - shortMidNow;
				var longAtOrigExp = LongValueAt(longLeg.Parsed, ivLong, shortLeg.Parsed.ExpiryDate, spot, dividends);
				var newShortIntrinsicAtExp = OptionMath.Intrinsic(spot, newStrike, callPut);
				var projSameExpPerShare = longAtOrigExp - newShortIntrinsicAtExp;
				var evSameExpPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, [(longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut), (newStrike, shortLeg.Parsed.ExpiryDate, ivSameExp, false, callPut)], shortLeg.Parsed.ExpiryDate, asOf, spot, ivSameExp, dividends);
				var sameExpShortParsed = new OptionParsed(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, callPut, newStrike);
				var sameExpMargin = MarginCalculator.PairPerContract(sameExpShortParsed, newShortMidSameExp, longLeg.Parsed, spot);
				var sameExpMarginDelta = sameExpMargin - currentMargin;

				var sameExpStructure = PairShapeLabel(sameExpShortParsed, longLeg.Parsed);
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short to ${newStrike:F2} (same exp {shortLeg.Parsed.ExpiryDate:MM-dd}, {sameExpStructure})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {sameExpSym} x{{qty}} @{FmtPrice(newShortMidSameExp)}",
					cashPerShareOfChange: cashPerShareSameExp,
					newProjectedPerShare: projSameExpPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: sameExpMarginDelta,
					daysToTarget: dteSameExp,
					rationale: $"shift to ${newStrike:F2} strike, keep {shortLeg.Parsed.ExpiryDate:MM-dd} expiry; mid net ${cashPerShareSameExp:+0.00;-0.00}/share",
					isRoll: true,
					newExpectedPerShare: evSameExpPerShare,
					unchangedExpectedPerShare: holdEvPerShare,
					resultingLegs: [UnitLeg(longLeg.Symbol, LegAction.Buy, longMidNow, longLeg.Parsed), UnitLeg(sameExpSym, LegAction.Sell, newShortMidSameExp, sameExpShortParsed)],
					evNote: $"EV ${evSameExpPerShare:F2}/share");
			}

			// 5. Roll short to bracket strikes near spot (one per strike).
			foreach (var newStrike in StrikeGrid.Bracket(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, newStrike, callPut);
				if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, newSym)) continue;
				var ivNewShort = ResolveIV(newSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newSym, spot, newStrike, dteNewShort, ivNewShort, callPut, asOf, dividends);
				var cashPerShare = newShortMidExec - shortMidNow;
				var longAtNewShortExp = LongValueAt(longLeg.Parsed, ivLong, newExp, spot, dividends);
				var shortAtNewShortExp = OptionMath.Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newEvPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, [(longLeg.Parsed.Strike, longLeg.Parsed.ExpiryDate, ivLong, true, callPut), (newStrike, newExp, ivNewShort, false, callPut)], newExp, asOf, spot, ivNewShort, dividends);
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, newStrike);
				var newMargin = MarginCalculator.PairPerContract(newShortParsed, newShortMidExec, longLeg.Parsed, spot);
				var marginDelta = newMargin - currentMargin;

				var structureLabel = PairShapeLabel(newShortParsed, longLeg.Parsed);
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short to ${newStrike:F2} ({newExp:MM-dd}, {structureLabel})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: marginDelta,
					daysToTarget: dteNewShort,
					rationale: $"step short to ${newStrike:F2} (spot ${spot:F2}); mid net ${cashPerShare:+0.00;-0.00}/share",
					isRoll: true,
					peakCashPerContract: shortMidNow * 100m,
					newExpectedPerShare: newEvPerShare,
					unchangedExpectedPerShare: holdEvPerShare,
					resultingLegs: [UnitLeg(longLeg.Symbol, LegAction.Buy, longMidNow, longLeg.Parsed), UnitLeg(newSym, LegAction.Sell, newShortMidExec, newShortParsed)],
					evNote: $"EV ${newEvPerShare:F2}/share");
			}
		}

		// 6. Close all and reopen a fresh calendar at bracket strikes near spot (scored on the same spot distribution).
		{
			var newShortExp = newExp;
			var newLongExp = longLeg.Parsed.ExpiryDate > newShortExp ? longLeg.Parsed.ExpiryDate : newShortExp.AddDays(21);
			foreach (var newStrike in StrikeGrid.Bracket(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m) continue;

				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newShortExp, newStrike, callPut);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newLongExp, newStrike, callPut);
				if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;
				var ivNewShort = ResolveIV(newShortSym, settings, quotes);
				var ivNewLong = ResolveIV(newLongSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newShortExp - asOf).Days);
				var dteNewLong = Math.Max(1, (newLongExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newStrike, dteNewShort, ivNewShort, callPut, asOf, dividends);
				var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newStrike, dteNewLong, ivNewLong, callPut, asOf, dividends);
				var closeNet = longMidNow - shortMidNow;
				var openNet = newShortMidExec - newLongMidExec;
				var cashPerShare = closeNet + openNet;
				var longAtNewShortExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, newShortExp.Date + OptionMath.MarketClose, newLongExp.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), newStrike, MarketClock.CloseToClose(longLeg.Parsed.Root, newShortExp, newLongExp, minDays: 1), OptionMath.RiskFreeRate, ivNewLong, callPut);
				var shortAtNewShortExp = OptionMath.Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var resetLegsForEv = new List<(decimal, DateTime, decimal, bool, string)>
				{
					(newStrike, newLongExp, ivNewLong, true, callPut),
					(newStrike, newShortExp, ivNewShort, false, callPut)
				};
				var resetEvPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, resetLegsForEv, newShortExp, asOf, spot, ivNewShort, dividends);
				var resetBreakEvens = ComputeScenarioBreakEvens(shortLeg.Parsed.Root, resetLegsForEv, newShortExp, spot, initialDebitPerShare - cashPerShare, dividends);
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newShortExp, callPut, newStrike);
				var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newLongExp, callPut, newStrike);
				var newMargin = MarginCalculator.PairPerContract(newShortParsed, newShortMidExec, newLongParsed, spot);
				var marginDelta = newMargin - currentMargin;

				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Reset to ${newStrike:F2} calendar",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {longLeg.Symbol} x{{qty}} @{FmtPrice(longMidNow)}, BUY {newLongSym} x{{qty}} @{FmtPrice(newLongMidExec)}, SELL {newShortSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: marginDelta,
					daysToTarget: dteNewShort,
					rationale: $"close net ${closeNet:+0.00;-0.00}, open new net ${openNet:+0.00;-0.00} at mid (pinned ${newProjectedPerShare:F2})",
					isRoll: true,
					newExpectedPerShare: resetEvPerShare,
					unchangedExpectedPerShare: holdEvPerShare,
					breakEvens: resetBreakEvens,
					resultingLegs: [UnitLeg(newLongSym, LegAction.Buy, newLongMidExec, newLongParsed), UnitLeg(newShortSym, LegAction.Sell, newShortMidExec, newShortParsed)],
					evNote: $"EV ${resetEvPerShare:F2}/share");
			}
		}

		// 7. Add an OPPOSITE-side time-spread alongside the existing one to form a DOUBLE. A real double
		// calendar/diagonal shares the SAME near + far expiries across both sides, so the added legs use the
		// existing short's and long's expiries (not a fresh weekly) — this is what makes the combined 4-leg
		// position a genuine double rather than two unrelated spreads. The existing side already occupies one
		// side of spot, so the added side goes on the other (puts below spot when existing is calls, calls
		// above when existing is puts). Two geometries:
		//   • double calendar  — new short and long at the same strike
		//   • double diagonal  — new long one strike further OTM than the new short
		// Legs are priced live where the chain has a quote, Black-Scholes otherwise (flagged "estimated").
		// IMPORTANT: this is NOT free return for the same risk — the added side is its own debit, so it
		// roughly doubles capital at risk and adds two more legs of slippage. What it buys is a wider, more
		// neutral profit tent. Each add reports its expected profit per added dollar so the trade-off is
		// explicit, and the most capital-efficient add is flagged ★ when that profit is positive.
		{
			var oppCp = callPut == "C" ? "P" : "C";
			var addShortExp = shortLeg.Parsed.ExpiryDate;
			var addLongExp = longLeg.Parsed.ExpiryDate;
			var dteNewShort = Math.Max(1, (addShortExp - asOf).Days);
			var dteNewLong = Math.Max(1, (addLongExp - asOf).Days);
			var origShortExp = shortLeg.Parsed.ExpiryDate;
			var sideLabel = oppCp == "C" ? "call" : "put";

			bool Listed(string sym) => quotes != null && quotes.ContainsKey(sym);
			bool Quoted(string sym) => quotes != null && HasLiveQuote(quotes, sym);

			// Strikes come from what each expiry actually lists: the short from the front expiry, the long from the far one (whose chain the up-front
			// fetch requests). The far expiry can list fewer strikes than the front — on 2026-11-20 SPY listed a 690 put for Dec 11 but no 691 — so a
			// calendar takes a strike both list. Without a far-expiry chain the front grid stands in, and the far leg is BS-priced ("estimated").
			var frontGrid = ListedStrikes(quotes, shortLeg.Parsed.Root, addShortExp, oppCp);
			var farGrid = ListedStrikes(quotes, longLeg.Parsed.Root, addLongExp, oppCp);

			// Build both add geometries, then flag the one with the best incremental EV per added margin.
			var pending = new List<(string variant, decimal shortStrike, string newShortSym, decimal newShortMidExec, decimal longStrike, string newLongSym, decimal newLongMidExec, decimal cashPerShare, decimal newPositionValuePerShare, decimal evNewPerShare, decimal newMargin, bool estimated, decimal efficiency)>();
			foreach (var geometry in DoubleAddGeometries(frontGrid, farGrid, spot, oppCp))
			{
				var (variant, shortStrike, longStrike) = geometry;
				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, addShortExp, shortStrike, oppCp);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, addLongExp, longStrike, oppCp);
				// Both legs must be listed contracts when there is a chain: no phantom near legs, and no far leg the far expiry does not list.
				if (quotes != null && (!Listed(newShortSym) || (farGrid.Count > 0 && !Listed(newLongSym)))) continue;
				var ivNewShort = ResolveIV(newShortSym, settings, quotes);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, shortStrike, dteNewShort, ivNewShort, oppCp, asOf, dividends);
				var newShortAtOrigExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, origShortExp.Date + OptionMath.MarketClose, addShortExp.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), shortStrike, MarketClock.CloseToClose(shortLeg.Parsed.Root, origShortExp, addShortExp, minDays: 1), OptionMath.RiskFreeRate, ivNewShort, oppCp);
				var ivNewLong = ResolveIV(newLongSym, settings, quotes);
				var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, longStrike, dteNewLong, ivNewLong, oppCp, asOf, dividends);
				var cashPerShare = newShortMidExec - newLongMidExec; // debit (negative) to open the new spread
				var newLongAtOrigExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, origShortExp.Date + OptionMath.MarketClose, addLongExp.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longStrike, MarketClock.CloseToClose(shortLeg.Parsed.Root, origShortExp, addLongExp, minDays: 1), OptionMath.RiskFreeRate, ivNewLong, oppCp);
				var newPositionValuePerShare = newLongAtOrigExp - newShortAtOrigExp; // pinned (at current spot)
				// Probability-weighted EV of the NEW spread at the existing short expiry — the honest figure the
				// "incremental return" should be judged on, not the spot-pinned peak.
				var newLegsForEv = new List<(decimal, DateTime, decimal, bool, string)>
				{
					(longStrike, addLongExp, ivNewLong, true, oppCp),
					(shortStrike, addShortExp, ivNewShort, false, oppCp),
				};
				var evNewPerShare = ExpectedPositionValuePerShare(shortLeg.Parsed.Root, newLegsForEv, origShortExp, asOf, spot, ivNewShort, dividends);
				var newMargin = Math.Max(-cashPerShare, 0m) * 100m;           // long debit spread: margin = debit paid
				var estimated = !Quoted(newShortSym) || !Quoted(newLongSym);
				var efficiency = ExpectedProfitReturn(evNewPerShare, -cashPerShare, newMargin);
				pending.Add((variant, shortStrike, newShortSym, newShortMidExec, longStrike, newLongSym, newLongMidExec, cashPerShare, newPositionValuePerShare, evNewPerShare, newMargin, estimated, efficiency));
			}

			var bestEfficiency = pending.Count > 0 ? pending.Max(x => x.efficiency) : 0m;
			foreach (var a in pending)
			{
				var isBest = pending.Count > 1 && a.efficiency == bestEfficiency && a.efficiency > 0m; // > 0 = the add is expected to make money
				var est = a.estimated ? " [estimated]" : "";
				var strikeDesc = a.shortStrike == a.longStrike ? $"${a.shortStrike:F2}" : $"${a.shortStrike:F2}/${a.longStrike:F2}";
				var effPct = FormatExpectedProfitReturn(a.evNewPerShare, -a.cashPerShare, a.newMargin, a.efficiency);
				var bestNote = isBest ? " — best add (highest expected profit on added capital)" : "";
				EmitAdd(list, legs, settings.Cash,
					name: $"Add {sideLabel} {strikeDesc} {addShortExp:MM-dd}/{addLongExp:MM-dd} ({a.variant}, keep existing){est}",
					newShortSym: a.newShortSym,
					newLongSym: a.newLongSym,
					newShortPrice: a.newShortMidExec,
					newLongPrice: a.newLongMidExec,
					cashPerShareOfChange: a.cashPerShare,
					newProjectedPerShare: a.newPositionValuePerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: a.newMargin,
					daysToTarget: origShortDte,
					rationale: $"add a {sideLabel} {a.variant.Split(' ')[1]} at {strikeDesc} (debit ${-a.cashPerShare:F2}/share at mid){(a.estimated ? ", BS-estimated (leg not live-quoted)" : "")}; pinned ${a.newPositionValuePerShare:F2}",
					addedLegs: [UnitLeg(a.newShortSym, LegAction.Sell, a.newShortMidExec, new OptionParsed(shortLeg.Parsed.Root, addShortExp, oppCp, a.shortStrike)), UnitLeg(a.newLongSym, LegAction.Buy, a.newLongMidExec, new OptionParsed(longLeg.Parsed.Root, addLongExp, oppCp, a.longStrike))],
					expectedUnchangedPerShare: holdEvPerShare,
					expectedNewPerShare: a.evNewPerShare,
					evNote: $"new spread expected value ${a.evNewPerShare:F2}/share, 2 legs of slippage not deducted; {effPct}{bestNote}");
			}

			// Comparator: scale up the EXISTING structure instead of adding an opposite side. Same payoff shape,
			// so per-contract EV equals the hold's — this is the "just add more contracts" baseline the doubles
			// must beat on EV-per-added-dollar. Opening another copy costs the current net debit as margin.
			var scaleUpDebit = longMidNow - shortMidNow;   // >0 = debit to open one more existing calendar/diagonal
			if (scaleUpDebit > 0m)
			{
				var scaleUpMargin = scaleUpDebit * 100m;
				var scaleUpEff = ExpectedProfitReturn(holdEvPerShare, scaleUpDebit, scaleUpMargin);
				EmitAdd(list, legs, settings.Cash,
					name: $"Add 1x more of existing {(callPut == "C" ? "call" : "put")} {(shortLeg.Parsed.Strike == longLeg.Parsed.Strike ? "calendar" : "diagonal")} (scale up, keep existing)",
					newShortSym: shortLeg.Symbol,
					newLongSym: longLeg.Symbol,
					newShortPrice: shortMidNow,
					newLongPrice: longMidNow,
					cashPerShareOfChange: -scaleUpDebit,
					newProjectedPerShare: holdNetPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: scaleUpMargin,
					daysToTarget: origShortDte,
					rationale: $"open one more of the SAME structure at current mid (debit ${scaleUpDebit:F2}/share) — baseline the doubles must beat",
					addedLegs: [UnitLeg(shortLeg.Symbol, LegAction.Sell, shortMidNow, shortLeg.Parsed), UnitLeg(longLeg.Symbol, LegAction.Buy, longMidNow, longLeg.Parsed)],
					expectedUnchangedPerShare: holdEvPerShare,
					expectedNewPerShare: holdEvPerShare,
					evNote: $"same payoff → new spread expected value ${holdEvPerShare:F2}/share, 2 legs of slippage not deducted; {FormatExpectedProfitReturn(holdEvPerShare, scaleUpDebit, scaleUpMargin, scaleUpEff)}");
			}
		}

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	/// <summary>Strikes the chain lists for (<paramref name="root"/>, <paramref name="expiry"/>, <paramref name="callPut"/>), ascending; empty without a chain.
	/// Snapping to these instead of a fixed StrikeStep grid keeps an add on real contracts (SPY lists $1 near spot, not $0.50).</summary>
	internal static List<decimal> ListedStrikes(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string root, DateTime expiry, string callPut) => quotes == null ? []
		: quotes.Keys.Select(ParsingHelpers.ParseOptionSymbol)
			.Where(p => p != null && ParsingHelpers.RootsMatchForAggregation(p.Root, root, expiry) && p.ExpiryDate.Date == expiry.Date && p.CallPut == callPut)
			.Select(p => p!.Strike).Distinct().OrderBy(k => k).ToList();

	/// <summary>The opposite-side adds' strikes: the double diagonal's short at the first <paramref name="frontGrid"/> strike at or beyond spot on its
	/// side (puts at or below, calls at or above) and its long at the next <paramref name="farGrid"/> strike further out of the money; the double
	/// calendar at the first strike at or beyond spot that both expiries list. An empty <paramref name="farGrid"/> (far expiry not fetched) falls
	/// back to the front grid. A geometry whose strikes the grids cannot supply is left out.</summary>
	internal static IEnumerable<(string Variant, decimal ShortStrike, decimal LongStrike)> DoubleAddGeometries(IReadOnlyList<decimal> frontGrid, IReadOnlyList<decimal> farGrid, decimal spot, string callPut)
	{
		var far = farGrid.Count > 0 ? farGrid : frontGrid;
		var calendarStrike = FirstAtOrBeyond(frontGrid.Intersect(far).ToList(), spot, callPut);
		if (calendarStrike > 0m) yield return ("double calendar", calendarStrike, calendarStrike);
		var diagonalShort = FirstAtOrBeyond(frontGrid, spot, callPut);
		var diagonalLong = diagonalShort <= 0m ? 0m : callPut == "P" ? far.Where(k => k < diagonalShort).DefaultIfEmpty(0m).Max() : far.Where(k => k > diagonalShort).DefaultIfEmpty(0m).Min();
		if (diagonalShort > 0m && diagonalLong > 0m) yield return ("double diagonal", diagonalShort, diagonalLong);
	}

	/// <summary>The first strike at or below spot for puts, at or above for calls; 0 when there is none.</summary>
	private static decimal FirstAtOrBeyond(IReadOnlyList<decimal> grid, decimal spot, string callPut) =>
		callPut == "P" ? grid.Where(k => k <= spot).DefaultIfEmpty(0m).Max() : grid.Where(k => k >= spot).DefaultIfEmpty(0m).Min();

	/// <summary>An add's expected profit per added dollar of margin: (expected value at the target − debit paid) × 100 / margin, so 0 is
	/// break-even. Not expected value / margin, which reads 100% at break-even. 0 when no margin is added.</summary>
	internal static decimal ExpectedProfitReturn(decimal expectedValuePerShare, decimal debitPerShare, decimal marginPerContract) =>
		marginPerContract > 0m ? (expectedValuePerShare - debitPerShare) * 100m / marginPerContract : 0m;

	/// <summary>"expected profit $X/share (Y% on added $Z)", or "expected profit n/a" when no margin is added.</summary>
	internal static string FormatExpectedProfitReturn(decimal expectedValuePerShare, decimal debitPerShare, decimal marginPerContract, decimal efficiency) =>
		marginPerContract > 0m ? $"expected profit {expectedValuePerShare - debitPerShare:+$0.00;-$0.00}/share ({efficiency * 100m:+0.0;-0.0}% on added ${marginPerContract:N0})" : "expected profit n/a";

	/// <summary>Appends an "add new position alongside existing" scenario. Existing untouched;
	/// new position adds margin and debit. Combined projection = existing hold + new position value at target.
	/// Partial variant sizes the added qty to fit available cash while keeping all existing contracts.</summary>
	private static void EmitAdd(
		List<Scenario> list,
		IReadOnlyList<PositionSnapshot> legs,
		decimal? availableCash,
		string name,
		string newShortSym,
		string newLongSym,
		decimal newShortPrice,
		decimal newLongPrice,
		decimal cashPerShareOfChange,
		decimal newProjectedPerShare,
		decimal unchangedProjectedPerShare,
		decimal marginPerContract,
		int daysToTarget,
		string rationale,
		IReadOnlyList<PositionSnapshot> addedLegs,
		decimal? expectedUnchangedPerShare = null,
		decimal? expectedNewPerShare = null,
		string? evNote = null)
	{
		var fullQty = legs[0].Qty;
		var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var initialDebitPerContract = initialDebitPerShare * 100m;
		// Same funding model as EmitFullAndPartial (adds open as one net-priced combo, so no peak term):
		// BP consumed = max(margin, net cash debit). For the doubles margin already equals the debit.
		var fundingPerContract = Math.Max(Math.Max(marginPerContract, 0m), Math.Max(-cashPerShareOfChange * 100m, 0m));
		var fullMarginTotal = marginPerContract * fullQty;
		var maxPartial = !availableCash.HasValue || fundingPerContract <= 0m
			? 0
			: (int)Math.Floor(availableCash.Value / fundingPerContract);
		var fullFundable = !availableCash.HasValue || fundingPerContract * fullQty <= availableCash.Value;
		var hasFundablePartial = maxPartial > 0 && maxPartial < fullQty;
		var hasEv = expectedUnchangedPerShare.HasValue && expectedNewPerShare.HasValue;

		if (fullFundable || !hasFundablePartial)
		{
			var fullCashPerContract = cashPerShareOfChange * 100m;
			var fullCombinedValuePerContract = (unchangedProjectedPerShare + newProjectedPerShare) * 100m;
			var fullTotalPerContract = fullCombinedValuePerContract + fullCashPerContract - initialDebitPerContract;
			decimal? fullEvPnL = hasEv ? (expectedUnchangedPerShare!.Value + expectedNewPerShare!.Value) * 100m + fullCashPerContract - initialDebitPerContract : null;
			list.Add(new Scenario(
				name,
				$"BUY {newLongSym} x{fullQty} @{FmtPrice(newLongPrice)}, SELL {newShortSym} x{fullQty} @{FmtPrice(newShortPrice)}",
				CashImpactPerContract: fullCashPerContract,
				ProjectedValuePerContract: fullCombinedValuePerContract,
				TotalPnLPerContract: fullTotalPerContract,
				MarginDeltaPerContract: marginPerContract,
				Qty: fullQty,
				DaysToTarget: daysToTarget,
				Rationale: rationale,
				ExpectedPnLPerContract: fullEvPnL,
				Book: [new BookStructure("existing", UnitLegs(legs), fullQty), new BookStructure("added", addedLegs, fullQty)],
				EvNote: evNote,
				OrderContractsNow: fullQty));
		}

		if (!availableCash.HasValue || marginPerContract <= 0m) return;
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		// Partial: add `maxPartial` new contracts, keep all `fullQty` existing.
		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialNewValue = newProjectedPerShare * 100m * maxPartial;
		var partialExistingValue = unchangedProjectedPerShare * 100m * fullQty;
		var partialCombinedValue = partialNewValue + partialExistingValue;
		var partialTotalPnL = partialCashTotal + partialCombinedValue - initialDebitPerContract * fullQty;
		decimal? partialEvPnL = hasEv
			? (partialCashTotal + (expectedNewPerShare!.Value * 100m * maxPartial + expectedUnchangedPerShare!.Value * 100m * fullQty) - initialDebitPerContract * fullQty) / fullQty
			: null;

		list.Add(new Scenario(
			$"{name} · partial {maxPartial}",
			$"BUY {newLongSym} x{maxPartial} @{FmtPrice(newLongPrice)}, SELL {newShortSym} x{maxPartial} @{FmtPrice(newShortPrice)}",
			CashImpactPerContract: partialCashTotal / fullQty,
			ProjectedValuePerContract: partialCombinedValue / fullQty,
			TotalPnLPerContract: partialTotalPnL / fullQty,
			MarginDeltaPerContract: marginPerContract * maxPartial / fullQty,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: $"{rationale}; partial: add {maxPartial} new contract(s) (${marginPerContract * maxPartial:N0} margin; full size would need ${fullMarginTotal:N0}); keep all {fullQty} existing",
			ExpectedPnLPerContract: partialEvPnL,
			Book: [new BookStructure("existing", UnitLegs(legs), fullQty), new BookStructure("added", addedLegs, maxPartial)],
			EvNote: evNote,
			OrderContractsNow: maxPartial));
	}

	/// <summary>Appends a full-quantity scenario to the list. If the full margin delta exceeds available
	/// cash AND there's a positive max-fundable partial quantity, also appends a partial variant.
	/// In the partial, the unchanged portion is valued at its natural terminal date (the hold projection),
	/// so the mix doesn't double-count time decay. Pass isRoll:true when the scenario closes an existing
	/// leg and opens a new one — BuildReproductionCommands uses the flag to decide whether to split the
	/// emitted `wa trade place` command into two single-leg orders for non-calendar rolls; isLegIn:true when it keeps every existing leg and
	/// only opens new ones. <paramref name="resultLabel"/> names the structure the change leaves held in the scenario's Book.</summary>
	private static void EmitFullAndPartial(
		List<Scenario> list,
		IReadOnlyList<PositionSnapshot> legs,
		decimal? availableCash,
		string name,
		string actionSummary,
		decimal cashPerShareOfChange,
		decimal newProjectedPerShare,
		decimal unchangedProjectedPerShare,
		decimal marginPerContract,
		int daysToTarget,
		string rationale,
		bool isRoll = false,
		decimal peakCashPerContract = 0m,
		decimal? newExpectedPerShare = null,
		decimal? unchangedExpectedPerShare = null,
		IReadOnlyList<decimal>? breakEvens = null,
		IReadOnlyList<PositionSnapshot>? resultingLegs = null,
		string? evNote = null,
		bool isLegIn = false,
		string resultLabel = "rolled")
	{
		var fullQty = legs[0].Qty;
		var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var initialDebitPerContract = initialDebitPerShare * 100m;
		// A change that cannot go as one combo is two orders: buy to close first (the peak cash it needs), then the open.
		var ordersPerContract = peakCashPerContract > 0m ? 2 : 1;
		// Fundability gates on the BP the change actually consumes — max of margin delta, net cash debit, and
		// the peak first-order cost for split rolls — not margin alone (a margin-neutral roll still spends cash).
		var fundingPerContract = Math.Max(Math.Max(Math.Max(marginPerContract, 0m), Math.Max(-cashPerShareOfChange * 100m, 0m)), peakCashPerContract);
		var fullFundingTotal = fundingPerContract * fullQty;
		var maxPartial = !availableCash.HasValue || fundingPerContract <= 0m ? 0 : (int)Math.Floor(availableCash.Value / fundingPerContract);
		var fullFundable = !availableCash.HasValue || fullFundingTotal <= availableCash.Value;
		var hasFundablePartial = maxPartial > 0 && maxPartial < fullQty;

		if (fullFundable || !hasFundablePartial)
		{
			var fullCashPerContract = cashPerShareOfChange * 100m;
			var fullProjectedPerContract = newProjectedPerShare * 100m;
			var fullTotalPerContract = fullProjectedPerContract + fullCashPerContract - initialDebitPerContract;
			decimal? fullEvPerContract = newExpectedPerShare.HasValue
				? newExpectedPerShare.Value * 100m + fullCashPerContract - initialDebitPerContract
				: null;
			list.Add(new Scenario(
				name,
				actionSummary.Replace("{qty}", fullQty.ToString()),
				CashImpactPerContract: fullCashPerContract,
				ProjectedValuePerContract: fullProjectedPerContract,
				TotalPnLPerContract: fullTotalPerContract,
				MarginDeltaPerContract: marginPerContract,
				Qty: fullQty,
				DaysToTarget: daysToTarget,
				Rationale: rationale,
				IsRoll: isRoll,
				ExpectedPnLPerContract: fullEvPerContract,
				PeakCashPerContract: peakCashPerContract,
				BreakEvens: breakEvens,
				Book: resultingLegs == null ? null : [new BookStructure(resultLabel, resultingLegs, fullQty)],
				EvNote: evNote,
				OrderContractsNow: ordersPerContract * fullQty,
				IsLegIn: isLegIn));
		}

		// Partial variant: only emit if the change consumes BP and cash is constrained below full.
		if (!availableCash.HasValue || fundingPerContract <= 0m) return;
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		// Per-contract-of-total values for the partial mix.
		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialProjectedTotal = newProjectedPerShare * 100m * maxPartial + unchangedProjectedPerShare * 100m * (fullQty - maxPartial);
		var partialTotalPnL = partialCashTotal + partialProjectedTotal - initialDebitPerContract * fullQty;
		decimal? partialEvPerContract = newExpectedPerShare.HasValue && unchangedExpectedPerShare.HasValue
			? (partialCashTotal + newExpectedPerShare.Value * 100m * maxPartial + unchangedExpectedPerShare.Value * 100m * (fullQty - maxPartial) - initialDebitPerContract * fullQty) / fullQty
			: null;
		list.Add(new Scenario(
			$"{name} · partial {maxPartial}/{fullQty}",
			actionSummary.Replace("{qty}", maxPartial.ToString()),
			CashImpactPerContract: partialCashTotal / fullQty,
			ProjectedValuePerContract: partialProjectedTotal / fullQty,
			TotalPnLPerContract: partialTotalPnL / fullQty,
			MarginDeltaPerContract: marginPerContract * maxPartial / fullQty,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: $"{rationale}; partial: execute on {maxPartial} contracts (${fundingPerContract * maxPartial:N0} BP; full size would need ${fullFundingTotal:N0}); hold remaining {fullQty - maxPartial} as original → ${unchangedProjectedPerShare:F2}/share at original exp",
			IsRoll: isRoll,
			ExpectedPnLPerContract: partialEvPerContract,
			PeakCashPerContract: peakCashPerContract * maxPartial / fullQty,
			Book: resultingLegs == null ? null : [new BookStructure(resultLabel, resultingLegs, maxPartial), new BookStructure("existing", UnitLegs(legs), fullQty - maxPartial)],
			EvNote: evNote,
			OrderContractsNow: ordersPerContract * maxPartial,
			IsLegIn: isLegIn));
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static Scenario NewScenarioSpread(string name, IReadOnlyList<PositionSnapshot> legs, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal marginDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false, decimal? expectedValueAtTarget = null, IReadOnlyList<decimal>? breakEvens = null, IReadOnlyList<BookStructure>? book = null, string? evNote = null, decimal orderContractsNow = 0m)
	{
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var qty = legs[0].Qty;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		decimal? evPnL = expectedValueAtTarget.HasValue ? expectedValueAtTarget.Value * 100m + cashPerContract - initialDebit * 100m : null;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, marginDeltaPerContract, qty, daysToTarget, rationale, isRoll, RankScore: (evPnL ?? totalPerContract) / Math.Max(1m, daysToTarget), ExpectedPnLPerContract: evPnL, BreakEvens: breakEvens, Book: book, EvNote: evNote, OrderContractsNow: orderContractsNow);
	}

	/// <summary>One contract of <paramref name="legs"/> as a <see cref="BookStructure"/> leg set (Qty 1, each leg kept at its own price).</summary>
	internal static IReadOnlyList<PositionSnapshot> UnitLegs(IReadOnlyList<PositionSnapshot> legs) => legs.Select(l => l with { Qty = 1 }).ToList();

	/// <summary>One contract's leg at <paramref name="price"/>.</summary>
	private static PositionSnapshot UnitLeg(string symbol, LegAction action, decimal price, OptionParsed parsed) => new(symbol, action, 1, price, parsed);

	// Rank on probability-weighted EV when we have it (the honest figure), falling back to the spot-pinned
	// P&L only for scenario types where EV isn't computed. Without this, capital-deploying scenarios (adds)
	// rank highest purely because the pinned best-case P&L scales with contracts.
	// Risk-adjusted rank: honest EV (or spot-pinned P&L when EV isn't computed) minus a risk charge for the
	// capital put at stake (added margin + net debit deployed) and any assignment-risk penalty. Ranks on the
	// TOTAL outcome, not P&L-per-day: the old per-day normalization rewarded long-dated adds and divided a
	// one-shot close by a single day, which structurally buried "close all". riskAversion=0 → pure return.
	private static decimal RiskAdjustedRankScore(Scenario sc, decimal riskAversion)
	{
		var baseReturn = sc.ExpectedPnLPerContract ?? sc.TotalPnLPerContract;
		var capitalAtStake = Math.Max(0m, sc.MarginDeltaPerContract) + Math.Max(0m, -sc.CashImpactPerContract);
		return baseReturn - riskAversion * capitalAtStake - sc.AssignmentPenaltyPerContract;
	}

	/// <summary>Keeps, of the swept roll candidates in <paramref name="scenarios"/>, the top 3 by rank, the best pure-EV fundable one (e.g. an inverted
	/// diagonal that --risk penalized) and the best that needs no added margin; every other scenario passes through.</summary>
	internal static List<Scenario> ShortlistSwept(IReadOnlyList<Scenario> scenarios, decimal? availableCash, decimal riskAversion)
	{
		var swept = scenarios.Where(s => s.Swept).ToList();
		if (swept.Count == 0) return scenarios.ToList();
		var ranked = OrderScenariosForDisplay(swept, availableCash, riskAversion);
		var selected = new HashSet<Scenario>(ranked.Take(3), ReferenceEqualityComparer.Instance);
		var bestPureEv = swept
			.Where(r => IsScenarioFundable(r, availableCash) && r.ExpectedPnLPerContract.HasValue && !selected.Contains(r))
			.OrderByDescending(r => r.ExpectedPnLPerContract!.Value)
			.FirstOrDefault();
		if (bestPureEv != null) selected.Add(bestPureEv);
		var bestZeroBp = ranked.FirstOrDefault(r => r.MarginDeltaPerContract <= 0m && !selected.Contains(r));
		if (bestZeroBp != null) selected.Add(bestZeroBp);
		return scenarios.Where(s => !s.Swept || selected.Contains(s)).ToList();
	}

	/// <remarks>A modeled scenario left without an EV (the opener scorer could not value one of its structures) ranks after every one with an EV:
	/// its fallback, the spot-pinned P&amp;L, is a best case, not a figure comparable with the others' EV.</remarks>
	internal static List<Scenario> OrderScenariosForDisplay(IEnumerable<Scenario> scenarios, decimal? availableCash, decimal riskAversion) => scenarios
		.OrderByDescending(s => IsScenarioFundable(s, availableCash))
		.ThenByDescending(s => s.ExpectedPnLPerContract.HasValue || s.Book == null)
		.ThenByDescending(s => RiskAdjustedRankScore(s, riskAversion))
		.ToList();

	private static bool IsScenarioFundable(Scenario sc, decimal? availableCash) =>
		!availableCash.HasValue || sc.FundingPerContract * sc.Qty <= availableCash.Value;

	// Assignment-risk charge for vertical scenarios (per contract, total $); folded into the risk-adjusted rank.
	private static decimal ComputeVerticalAssignmentPenalty(Scenario sc, PositionSnapshot currentShortLeg, decimal spot, decimal strikeStep, decimal technicalBias)
	{
		var shortSymbols = sc.ActionSummary == "—"
			  ? new List<string> { currentShortLeg.Symbol }
			  : ExtractShortOptionSymbols(sc.ActionSummary, currentShortLeg.Symbol, sc.Name.StartsWith("Add complementary ", StringComparison.Ordinal)
				  ? new[] { currentShortLeg.Symbol }
				  : Array.Empty<string>());

		if (shortSymbols.Count == 0) return 0m;

		return shortSymbols
			.Select(ParsingHelpers.ParseOptionSymbol)
			.Where(p => p != null)
			.Select(p => ComputeAssignmentRiskPenaltyPerContract(spot, p!.Strike, p.CallPut, sc.DaysToTarget, strikeStep, technicalBias))
			.DefaultIfEmpty(0m)
			.Max();
	}

	private static List<string> ExtractShortOptionSymbols(string actionSummary, string currentLongSymbol, IEnumerable<string>? includeSymbols = null)
	{
		var symbols = new List<string>();
		if (includeSymbols != null) symbols.AddRange(includeSymbols);
		if (string.IsNullOrWhiteSpace(actionSummary) || actionSummary == "—") return symbols;

		foreach (var part in actionSummary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length < 2) continue;
			if (!tokens[0].Equals("SELL", StringComparison.OrdinalIgnoreCase)) continue;
			var symbol = tokens[1];
			if (symbol.Equals(currentLongSymbol, StringComparison.OrdinalIgnoreCase)) continue;
			symbols.Add(symbol);
		}

		return symbols;
	}

	internal static decimal ComputeAssignmentRiskPenaltyPerContract(decimal spot, decimal shortStrike, string callPut, int daysToTarget, decimal strikeStep, decimal technicalBias)
	{
		var otmBuffer = callPut == "P" ? spot - shortStrike : shortStrike - spot;
		var safeBuffer = Math.Max(strikeStep, spot * 0.01m);
		var nearMoneyDistance = Math.Max(safeBuffer - Math.Max(otmBuffer, 0m), 0m);
		var itmAmount = Math.Max(-otmBuffer, 0m);
		var directionalRelief = callPut == "P"
			? Math.Clamp(technicalBias, 0m, 1m)
			: Math.Clamp(-technicalBias, 0m, 1m);
		var timeFactor = Math.Clamp(7m / Math.Max(1m, daysToTarget), 0.5m, 2.5m);
		var reliefFactor = 1m - 0.85m * directionalRelief;
		var penaltyPerShare = (nearMoneyDistance * 0.75m + itmAmount * 1.50m) * timeFactor * reliefFactor;
		return penaltyPerShare * 100m;
	}

	private static string VerticalStructureLabel(string callPut, decimal shortStrike, decimal longStrike) =>
		callPut == "C"
			? (shortStrike > longStrike ? "debit vertical" : "credit vertical")
			: (shortStrike < longStrike ? "debit vertical" : "credit vertical");

	private static bool IsComplementaryShortStrike(string callPut, decimal strike, decimal spot) =>
		callPut == "C" ? strike >= spot : strike <= spot;

	private static decimal ComplementaryLongStrike(string callPut, decimal shortStrike, decimal width) =>
		callPut == "C" ? shortStrike + width : shortStrike - width;

	/// <summary>Returns true if the quote dictionary has a real, usable quote for this symbol
	/// (both bid and ask populated, ask > 0). Used when --api is set to skip scenarios whose
	/// hypothetical contracts aren't listed at the exchange (e.g., odd strikes at non-standard expiries).</summary>
	private static bool HasLiveQuote(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol) =>
		quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Ask.Value > 0m;

	/// <summary>Returns live mid from quotes if both bid and ask are populated; otherwise a BS theoretical mid.
	/// <paramref name="asOf"/> is the scenario's own evaluation date (the caller's <c>--date</c>), never the process-wide pin.</summary>
	internal static decimal LiveOrBsMid(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal spot, decimal strike, int dte, decimal iv, string callPut, DateTime asOf, IReadOnlyList<DividendEvent>? dividends = null)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value + q.Ask.Value) / 2m;
		var day = asOf.Date;
		var expiryInstant = day.AddDays(dte) + OptionMath.MarketClose;
		var adjSpot = OptionMath.DividendAdjustedSpot(spot, dividends, day, expiryInstant, OptionMath.RiskFreeRate);
		var time = MarketClock.CloseToClose(ParsingHelpers.UnderlyingRoot(symbol), day, day.AddDays(dte));
		return (decimal)OptionMath.BlackScholes(adjSpot, strike, time, OptionMath.RiskFreeRate, iv, callPut);
	}

	/// <summary>Returns (bid, ask) from live quote when available; otherwise a ±1% synthetic spread around the given mid.</summary>
	private static (decimal bid, decimal ask) LiveBidAsk(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal fallbackMid)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value, q.Ask.Value);
		var spread = Math.Max(0.01m, fallbackMid * 0.01m);
		return (Math.Max(0m, fallbackMid - spread), fallbackMid + spread);
	}

	/// <summary>Returns IV as a fraction (0.40 for 40%). Sources in priority order:
	/// per-symbol --iv override (user-supplied percent, divided by 100), then live quote
	/// (already a fraction), then --iv-default (percent, divided by 100).</summary>
	internal static decimal ResolveIV(string symbol, AnalyzePositionSettings settings, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (!string.IsNullOrEmpty(settings.IvOverrides))
		{
			foreach (var entry in settings.IvOverrides.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = entry.Split(':');
				if (parts.Length == 2 && parts[0].Equals(symbol, StringComparison.OrdinalIgnoreCase)
					&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var iv))
					return iv / 100m;
			}
		}
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m)
			return q.ImpliedVolatility.Value;
		return settings.IvDefault / 100m;
	}

	/// <summary>True when --date pins a session before today (ET): that run reads the as-of market, not the live chain.</summary>
	/// <summary>The market at the pinned past instant through the --fill loader: the legs' books, each leg expiry's chain and the scenario
	/// roll targets' expiries' chains, the day's open interest, and spot from the minute bars — at exactly that minute for a --date, at the
	/// boundary nearest it for a fill (<paramref name="nearest"/>). Pins the quote anchor (<see cref="OptionMath.HistoricalQuotesStruckAt"/>)
	/// and that session's risk-free rate, so every IV solve and mark prices on the books' clock.</summary>
	private static async Task<(FillTimeMarket.Snapshot? Market, string? Error)> LoadAsOfMarketAsync(string ticker, IReadOnlyList<PositionSnapshot> positionLegs, StructureKind structure, AnalyzePositionSettings settings, bool nearest, CancellationToken cancellation)
	{
		var at = EvaluationDate.Instant!.Value;
		if (!MarketCalendar.IsOpen(at.Date)) return (null, $"{at:yyyy-MM-dd} is not a trading session");
		var apiConfig = OpenCandidateEvaluator.TryLoadApiConfig();
		if (apiConfig == null) return (null, "the session's spot comes from the Webull minute-bar cache, and api-config.json is missing (run 'wa sniff')");

		var symbols = positionLegs.Select(l => l.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var (market, error) = await FillTimeMarket.LoadAsync(ticker, symbols, at, nearest, new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig)), cancellation, extraSymbols: spot => EnumerateHypotheticalSymbols(positionLegs, structure, settings, spot, at.Date));
		if (market == null) return (null, error);

		OptionMath.HistoricalQuotesStruckAt = market.Instant;
		await FillTimeMarket.ApplySessionRateAsync(at.Date, cancellation);
		AnsiConsole.MarkupLine($"[dim]Market as of {market.Instant:yyyy-MM-dd HH:mm} ET from {Markup.Escape(market.BookSource)}, spot ${market.Spot:F2}.[/]");
		foreach (var miss in market.ChainMisses)
			AnsiConsole.MarkupLine($"[yellow]Scenario expiry without an as-of chain: {Markup.Escape(miss)} — its scenarios price on --iv-default.[/]");
		return (market, null);
	}

	internal static decimal? ResolveSpot(string ticker, AnalyzePositionSettings settings, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		if (!string.IsNullOrEmpty(settings.Spot))
		{
			foreach (var entry in settings.Spot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = entry.Split(':');
				if (parts.Length == 2 && parts[0].Equals(ticker, StringComparison.OrdinalIgnoreCase)
					&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
					return p;
			}
		}
		if (underlyingPrices != null && underlyingPrices.TryGetValue(ticker, out var apiPrice) && apiPrice > 0m)
			return apiPrice;
		return null;
	}

	/// <summary>Finds campaign break-even spot price(s) at <paramref name="target"/> where the post-action
	/// position's BS/intrinsic value equals <paramref name="netCostBasisPerShare"/>.</summary>
	private static List<decimal> ComputeScenarioBreakEvens(
		string root,
		IReadOnlyList<(decimal strike, DateTime expiry, decimal iv, bool isLong, string cp)> legs,
		DateTime target,
		decimal spot,
		decimal netCostBasisPerShare,
		IReadOnlyList<DividendEvent>? dividends = null)
	{
		if (legs.Count == 0 || spot <= 0m) return [];
		var targetClose = target.Date + OptionMath.MarketClose;
		decimal PnlAt(decimal s)
		{
			decimal val = 0m;
			foreach (var (strike, expiry, iv, isLong, cp) in legs)
			{
				decimal legVal = expiry.Date <= target.Date
					? OptionMath.Intrinsic(s, strike, cp)
					: (decimal)OptionMath.BlackScholes(
						OptionMath.DividendAdjustedSpot(s, dividends, targetClose, expiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate),
						strike, MarketClock.CloseToClose(root, target, expiry, minDays: 1), OptionMath.RiskFreeRate, iv, cp);
				val += isLong ? legVal : -legVal;
			}
			return val - netCostBasisPerShare;
		}

		var minStrike = legs.Min(l => l.strike);
		var maxStrike = legs.Max(l => l.strike);
		var span = Math.Max(spot * 0.25m, Math.Max(maxStrike - minStrike, 5m) * 3m);
		var lo = Math.Max(0.01m, Math.Min(spot, minStrike) - span);
		var hi = Math.Max(spot, maxStrike) + span;
		const int steps = 160;
		var step = (hi - lo) / steps;
		var ladder = new List<PricePnL>(steps + 1);
		for (int i = 0; i <= steps; i++)
		{
			var p = lo + i * step;
			ladder.Add(new PricePnL(p, PnlAt(p)));
		}
		return OptionMath.FindBreakEvensNumerically(ladder, PnlAt);
	}

	/// <summary>Probability-weighted expected per-share value of a multi-leg position at <paramref name="target"/>,
	/// integrated over a log-normal spot distribution (same 5-point grid the opener's scorer uses). Legs that
	/// expire at or before the target settle to intrinsic; legs that survive are Black-Scholes-valued on their
	/// residual time. This is the honest counterpart to the spot-pinned projection shown elsewhere — at the pin
	/// a near-ATM calendar shows its peak, but most of the lognormal mass lands off-peak. <paramref name="gridIv"/>
	/// is the vol used for the spot distribution over [asOf, target] (the near-leg IV is the right horizon).</summary>
	private static decimal ExpectedPositionValuePerShare(string root,
		IReadOnlyList<(decimal strike, DateTime expiry, decimal iv, bool isLong, string cp)> legs,
		DateTime target, DateTime asOf, decimal spot, decimal gridIv, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var yearsToTarget = MarketClock.CloseToClose(root, asOf, target, minDays: 1);
		var forward = OptionMath.ForwardPrice(spot, dividends, asOf, target.Date + OptionMath.MarketClose, yearsToTarget.CalendarYears, OptionMath.RiskFreeRate);
		var grid = WebullAnalytics.AI.CandidateScorer.BuildScenarioGrid(forward, gridIv, yearsToTarget);
		decimal ev = 0m;
		foreach (var pt in grid)
		{
			decimal value = 0m;
			foreach (var (strike, expiry, iv, isLong, cp) in legs)
			{
				var legValue = expiry.Date <= target.Date
					? OptionMath.Intrinsic(pt.SpotAtExpiry, strike, cp)
					: (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(pt.SpotAtExpiry, dividends, target.Date + OptionMath.MarketClose, expiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), strike, MarketClock.CloseToClose(root, target, expiry, minDays: 1), OptionMath.RiskFreeRate, iv, cp);
				value += (isLong ? 1m : -1m) * legValue;
			}
			ev += pt.Weight * value;
		}
		return ev;
	}

	private static decimal NearestStrike(decimal spot, decimal step) =>
		Math.Round(spot / step) * step;

	// ─── Rendering ───────────────────────────────────────────────────────────

	/// <summary>One-shot put-call-parity diagnostic: backs the market-implied dividend out of the call/put
	/// mids at each position strike+expiry with no model assumption — implied PV(div) = S − (C−P) − K·e^(−rT).
	/// An expiry BEFORE any ex-date should read ~0 (a built-in control); a later expiry reads the market's
	/// implied dividend, to compare against our assumed schedule and explain any theoretical-vs-mid gap.
	/// PCP is exact for European options; SPY/equity options are American, so near-the-money reads carry a
	/// small early-exercise bias. Silent when the put side isn't quoted.</summary>
	private static void RenderImpliedDividendDiagnostic(IReadOnlyList<PositionSnapshot> legs, decimal spot, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? assumedDivs, DateTime asOf, IAnsiConsole console)
	{
		if (quotes == null || quotes.Count == 0) return;

		var pairs = legs.Select(l => (Strike: l.Parsed.Strike, Expiry: l.Parsed.ExpiryDate.Date, Root: l.Parsed.Root))
			.Distinct().OrderBy(p => p.Expiry).ToList();
		if (pairs.Count == 0) return;

		decimal? MidAt(string root, decimal strike, DateTime expiry, string cp)
		{
			foreach (var kv in quotes)
			{
				var p = ParsingHelpers.ParseOptionSymbol(kv.Key);
				if (p == null || p.CallPut != cp || p.Strike != strike || p.ExpiryDate.Date != expiry || !ParsingHelpers.RootsMatchForAggregation(p.Root, root, expiry)) continue;
				if (!kv.Value.Bid.HasValue || !kv.Value.Ask.HasValue || kv.Value.Ask.Value <= 0m) return null;
				return (kv.Value.Bid.Value + kv.Value.Ask.Value) / 2m;
			}
			return null;
		}

		var assumed = assumedDivs == null || assumedDivs.Count == 0
			? "none"
			: string.Join(", ", assumedDivs.Select(d => $"${d.Amount.ToString("0.###", CultureInfo.InvariantCulture)} ex {d.ExDate:MM-dd}"));
		console.MarkupLine($"[dim]Implied dividend — put-call parity (assumed: {Markup.Escape(assumed)}; American ⇒ approximate):[/]");

		foreach (var (strike, expiry, root) in pairs)
		{
			var c = MidAt(root, strike, expiry, "C");
			var p = MidAt(root, strike, expiry, "P");
			var label = $"{expiry:MM-dd} ${strike.ToString("0.##", CultureInfo.InvariantCulture)}";
			if (c == null || p == null)
			{
				console.MarkupLine($"[dim]  {Markup.Escape(label)}: need both call & put mids ({(c == null ? "call" : "put")} missing)[/]");
				continue;
			}
			var t = Math.Max(0.0, ((expiry + OptionMath.MarketClose) - asOf).TotalDays / 365.0);
			var dfK = strike * (decimal)Math.Exp(-OptionMath.RiskFreeRate * t);
			var impliedPvDiv = spot - ((c.Value - p.Value) + dfK);
			console.MarkupLine($"[dim]  {Markup.Escape(label)}: C {c.Value.ToString("0.00", CultureInfo.InvariantCulture)} / P {p.Value.ToString("0.00", CultureInfo.InvariantCulture)} → implied PV(div) ${impliedPvDiv.ToString("0.000", CultureInfo.InvariantCulture)}[/]");
		}
	}

	private static void RenderScenarioTable(IReadOnlyList<Scenario> scenarios, IReadOnlyList<PositionSnapshot> positionLegs, AnalyzePositionSettings settings, IAnsiConsole console, bool ascii)
	{
		var availableCash = settings.Cash;

		// Identify the first fundable scenario so we can highlight it (green). The top-ranked
		// scenario may be unfundable — the actionable recommendation is the best fundable one.
		Scenario? topFundable = null;
		foreach (var sc in scenarios)
		{
			if (IsScenarioFundable(sc, availableCash)) { topFundable = sc; break; }
		}

		// Compact side-by-side summary table (same scannable style as `analyze roll` / `analyze trade --compare`).
		if (scenarios.Count > 0)
		{
			var summary = new Table { Expand = true }.Border(ascii ? TableBorder.Ascii : TableBorder.Rounded);
			summary.AddColumn("Scenario");
			foreach (var header in new[] { "Cash Δ", "BP Δ", "Break-evens", "POP", "EV (Total)", "P&L (Pinned)" })
				summary.AddColumn(new TableColumn(header).RightAligned());

			foreach (var sc in scenarios)
			{
				var marginTotal = sc.MarginDeltaPerContract * sc.Qty;
				var fundingTotal = sc.FundingPerContract * sc.Qty;
				var fundable = !availableCash.HasValue || fundingTotal <= availableCash.Value;
				var isRec = topFundable != null && ReferenceEquals(sc, topFundable);
				var nameText = WebullAnalytics.IO.TextFileExporter.NormalizeArrows(ascii, sc.Name);
				var nameCell = isRec
					? $"[bold green]★ {Markup.Escape(nameText)}[/]"
					: fundable ? Markup.Escape(nameText) : $"[dim]{Markup.Escape(nameText)}[/]";

				var cashTotal = sc.CashImpactPerContract * sc.Qty;
				var cashCell = cashTotal == 0m ? "$0" : cashTotal > 0m ? $"[green]+${cashTotal:N0}[/]" : $"[red]-${Math.Abs(cashTotal):N0}[/]";
				var bpCell = marginTotal == 0m ? "$0" : marginTotal > 0m ? $"[yellow]+${marginTotal:N0}[/]" : $"[green]-${Math.Abs(marginTotal):N0}[/]";
				var beCell = sc.BreakEvens is { Count: > 0 }
					? string.Join(" / ", sc.BreakEvens.Select(b => b.ToString("0.00", CultureInfo.InvariantCulture)))
					: "—";
				var popCell = sc.ProbabilityOfProfit is decimal pop ? $"{pop * 100m:0.0}%" : "—";
				var evCell = sc.ExpectedPnLPerContract.HasValue
					? (sc.ExpectedPnLPerContract.Value * sc.Qty >= 0m
						? $"[green]+${sc.ExpectedPnLPerContract.Value * sc.Qty:N0}[/]"
						: $"[red]-${Math.Abs(sc.ExpectedPnLPerContract.Value * sc.Qty):N0}[/]")
					: "—";
				var pnlTotal = sc.TotalPnLPerContract * sc.Qty;
				var pnlCell = pnlTotal >= 0m ? $"[green]+${pnlTotal:N0}[/]" : $"[red]-${Math.Abs(pnlTotal):N0}[/]";

				summary.AddRow(nameCell, cashCell, bpCell, beCell, popCell, evCell, pnlCell);
			}

			var rankHeader = settings.Risk > 0m
				? $"Scenario Comparison — ranked by risk-adjusted EV (risk={settings.Risk:0.##})"
				: "Scenario Comparison — ranked by EV";
			var summaryPanel = new Panel(summary)
			{
				Header = new PanelHeader(rankHeader),
				Expand = true,
				Border = ascii ? BoxBorder.Ascii : BoxBorder.Rounded
			};
			console.Write(summaryPanel);
			console.WriteLine();
		}

		foreach (var sc in scenarios)
		{
			var marginTotal = sc.MarginDeltaPerContract * sc.Qty;
			var fundingTotal = sc.FundingPerContract * sc.Qty;
			var fundable = !availableCash.HasValue || fundingTotal <= availableCash.Value;
			var isRecommended = topFundable != null && ReferenceEquals(sc, topFundable);
			var style = isRecommended ? "bold green" : (fundable ? "white" : "dim");

			var totalTotal = sc.TotalPnLPerContract * sc.Qty;
			var totalStr = $"${sc.TotalPnLPerContract:F2}/contract → {(totalTotal >= 0 ? "+" : "-")}${Math.Abs(totalTotal):N2} total";
			var cashStr = $"${sc.CashImpactPerContract:+0.00;-0.00}/contract";
			var projStr = $"${sc.ProjectedValuePerContract:F2}/contract";
			// EV = probability-weighted P&L (lognormal spot grid); the honest counterpart to the spot-pinned
			// P&L above. Shown only for scenarios where it's computed (hold / adds / scale-up).
			var evStr = sc.ExpectedPnLPerContract.HasValue
				? $"  │  EV ${sc.ExpectedPnLPerContract.Value:F2}/contract → {(sc.ExpectedPnLPerContract.Value * sc.Qty >= 0 ? "+" : "-")}${Math.Abs(sc.ExpectedPnLPerContract.Value * sc.Qty):N2} total"
				: "";
			var marginMarkup = marginTotal == 0m
				? "[dim]no margin change[/]"
				: marginTotal < 0m
					? $"[green]margin {marginTotal:+$0;-$0;0} frees up[/]"
					: (availableCash.HasValue && marginTotal > availableCash.Value
						? $"[red]margin +${marginTotal:N2} (NEEDS ${marginTotal - availableCash.Value:N2} MORE)[/]"
						: $"[yellow]margin +${marginTotal:N2}[/]");
			var fundMarker = !fundable ? $" [red](not fundable — needs ~${fundingTotal:N0} BP, have ${availableCash!.Value:N0})[/]" : "";
			var prefix = isRecommended ? "★ " : "";

			var rationaleText = WebullAnalytics.IO.TextFileExporter.NormalizeArrows(ascii, sc.DisplayRationale);
			var nameText = WebullAnalytics.IO.TextFileExporter.NormalizeArrows(ascii, sc.Name);

			var lines = new List<IRenderable>
			{
				new Markup($"[dim]Cash {Markup.Escape(cashStr)}  │  Projected {Markup.Escape(projStr)}{Markup.Escape(evStr)}  │  P&L {Markup.Escape(totalStr)}  │  {marginMarkup}[/]"),
				new Markup($"[dim]{Markup.Escape(rationaleText)}[/]"),
			};
			var leadIn = WebullAnalytics.IO.TextFileExporter.ReproductionLeadIn(ascii);
			var (tradeCmds, analyzeCmd) = BuildReproductionCommands(sc, settings, positionLegs);
			if (tradeCmds != null)
				foreach (var cmd in tradeCmds)
					lines.Add(new Markup($"[grey50]{leadIn} {Markup.Escape(cmd)}[/]"));
			if (analyzeCmd != null)
				lines.Add(new Markup($"[grey50]{leadIn} {Markup.Escape(analyzeCmd)}[/]"));

			var panel = new Panel(new Rows(lines))
				.Header($"[{style}]{prefix}{Markup.Escape(nameText)}[/]{fundMarker}")
				.Expand()
				.Border(ascii ? BoxBorder.Ascii : BoxBorder.Rounded)
				.BorderColor(isRecommended ? Color.Green : (fundable ? Color.Grey : Color.Grey35));
			console.Write(panel);
		}

		if (availableCash.HasValue)
		{
			console.WriteLine();
			console.MarkupLine($"[dim]Fundability check: available cash/BP = ${availableCash.Value:N2}.[/]");
		}
	}

	/// <summary>Formats a per-share option price for inclusion in a trade-spec leg. Uses 3 decimals
	/// to preserve sub-penny mid precision without gratuitous trailing zeros.</summary>
	private static string FmtPrice(decimal price) => price.ToString("0.###", CultureInfo.InvariantCulture);

	/// <summary>Converts a scenario ActionSummary like "BUY SYM x200 @0.305, SELL SYM2 x200 @0.44"
	/// into reproducible commands: one or more 'wa trade place' lines for execution and a single
	/// 'wa analyze trade' line for validation. Output shape depends on scenario structure:
	///   - Same-strike calendar rolls (2 legs) and non-roll scenarios → one combo line, net `--limit` from CashImpactPerContract.
	///   - Non-calendar rolls (2 legs, diagonal/vertical) → two single-leg lines; Webull's combo engine rejects roll reversals across diagonals.
	///   - 4-leg resets (close existing position + open new one) → two combo lines (close half + open half); Webull's combo engine only accepts 3-leg butterflies and 4-leg iron condor/butterfly as 4-leg shapes, so resets can't ride as a single combo. Split by position — EmitReset's contract is legs[0..1] close existing, legs[2..3] open new.
	/// Returns (null, null) for hold/no-op scenarios.</summary>
	internal static (IReadOnlyList<string>? Trades, string? Analyze) BuildReproductionCommands(Scenario sc, AnalyzePositionSettings settings, IReadOnlyList<PositionSnapshot>? positionLegs = null)
	{
		if (string.IsNullOrWhiteSpace(sc.ActionSummary) || sc.ActionSummary == "—") return (null, null);
		var parts = sc.ActionSummary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		// Parse each "ACTION SYMBOL xQTY @PRICE" part into (action, symbol, qty, price).
		var legs = new List<(string Action, string Symbol, string Qty, string Price)>(parts.Length);
		foreach (var part in parts)
		{
			var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length != 4) return (null, null);
			var action = tokens[0].ToLowerInvariant();
			if (action != "buy" && action != "sell") return (null, null);
			legs.Add((action, tokens[1], tokens[2].TrimStart('x'), tokens[3].TrimStart('@')));
		}

		var extras = new List<string>();
		if (!string.IsNullOrEmpty(settings.Spot)) extras.Add($"--spot {settings.Spot}");
		else if (settings.SpotSession.Flag() is { } sessionFlag) extras.Add(sessionFlag);
		if (!string.IsNullOrEmpty(settings.Date)) extras.Add($"--date {settings.Date}");
		var suffix = extras.Count > 0 ? " " + string.Join(" ", extras) : "";

		var splittable = sc.IsRoll && legs.Count == 2 && !RollShape.SupportsComboRoll(legs.Select(l => l.Symbol));

		// Build the `wa analyze trade` command:
		//  • 2-leg roll with position context → original position followed by the roll leg(s) separated by ';',
		//    letting PositionTracker compute the exact after-roll basis natively (works for both full and partial rolls).
		//  • 4-leg reset with position context → the new position being opened (legs[2..3]) rather than
		//    rendering a bogus panel for the closing legs.
		//  • Leg-in with position context → the original position and the opened leg(s) as ONE group: the structure they form together.
		//  • Fallback (no position context, adds, closes) → the action's legs directly.
		string analyze;
		if (positionLegs is { Count: > 0 } && sc.IsLegIn)
		{
			var resulting = positionLegs.Select(p => $"{(p.Action == LegAction.Buy ? "buy" : "sell")}:{p.Symbol}:{p.Qty}@{FmtPrice(p.CostBasis)}")
				.Concat(legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}"));
			analyze = $"wa analyze trade \"{string.Join(",", resulting)}\"{suffix}";
		}
		else if (positionLegs is { Count: > 0 } && sc.IsRoll && legs.Count == 2)
		{
			var origGroup = string.Join(",", positionLegs.Select(p =>
				$"{(p.Action == LegAction.Buy ? "buy" : "sell")}:{p.Symbol}:{p.Qty}@{FmtPrice(p.CostBasis)}"));
			var rollLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}").ToList();
			var rollGroups = splittable ? string.Join(";", rollLegs) : string.Join(",", rollLegs);
			analyze = $"wa analyze trade \"{origGroup};{rollGroups}\"{suffix}";
		}
		else if (positionLegs is { Count: > 0 } && sc.IsRoll && legs.Count == 4)
		{
			var newPosLegs = legs.Skip(2).Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}");
			analyze = $"wa analyze trade \"{string.Join(",", newPosLegs)}\"{suffix}";
		}
		else
		{
			var analyzeGroups = new List<string>();
			var analyzeLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}").ToList();
			if (sc.IsRoll && legs.Count == 4)
			{
				analyzeGroups.Add(string.Join(",", analyzeLegs.Take(2)));
				analyzeGroups.Add(string.Join(",", analyzeLegs.Skip(2)));
			}
			else if (splittable)
			{
				analyzeGroups.AddRange(analyzeLegs);
			}
			else
			{
				analyzeGroups.Add(string.Join(",", analyzeLegs));
			}
			analyze = $"wa analyze trade \"{string.Join(";", analyzeGroups)}\"{suffix}";
		}

		// 4-leg reset: split into close-half and open-half combos.
		if (sc.IsRoll && legs.Count == 4)
		{
			var closeCmd = BuildComboFromLegs(legs.Take(2));
			var openCmd = BuildComboFromLegs(legs.Skip(2));
			return (new[] { closeCmd, openCmd }, analyze);
		}

		// Split non-calendar rolls into per-leg orders so Webull's combo engine accepts them.
		// Per-leg --limit is rounded to cents so it round-trips through the broker (sub-penny isn't a valid tick).
		if (splittable)
		{
			var trades = legs.Select(l =>
			{
				var legLimit = decimal.TryParse(l.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
					? p.ToString("F2", CultureInfo.InvariantCulture)
					: l.Price;
				return $"wa trade place \"{l.Action}:{l.Symbol}:{l.Qty}\" --limit {legLimit}";
			}).ToList();
			return (trades, analyze);
		}

		// Combo line: net --limit from the LEG PRICES, never CashImpactPerContract — partial variants
		// pro-rate that field per ORIGINAL contract for the table (× maxPartial/fullQty), which diluted
		// the emitted limit (e.g. a $3.625 net debit rendered as --limit 1.51 on a 10-of-24 partial).
		return (new[] { BuildComboFromLegs(legs) }, analyze);
	}

	/// <summary>Builds a single combo `wa trade place` line from parsed legs. `--limit` is the absolute
	/// per-share signed net (sell prices add, buy prices subtract); Webull infers side from the legs.</summary>
	private static string BuildComboFromLegs(IEnumerable<(string Action, string Symbol, string Qty, string Price)> halfLegs)
	{
		var list = halfLegs.ToList();
		decimal signedNet = 0m;
		foreach (var l in list)
		{
			if (!decimal.TryParse(l.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) continue;
			signedNet += l.Action == "sell" ? p : -p;
		}
		var halfLimit = Math.Abs(signedNet).ToString("F2", CultureInfo.InvariantCulture);
		var halfArg = string.Join(",", list.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		return $"wa trade place \"{halfArg}\" --limit {halfLimit}";
	}

	/// <summary>Builds a RiskDiagnostic for the given position and appends it to the analyze-position
	/// JSONL log at <paramref name="logPath"/>. Returns the diagnostic for the caller to render — kept
	/// side-effect-free on the console so layout decisions (panel framing, ordering with the leg header)
	/// stay with ExecuteAsync. Separated from ExecuteAsync for direct unit testing of the JSON shape.</summary>
	internal static RiskDiagnostic BuildAndLogDiagnostic(
		string logPath,
		string ticker,
		string positionKey,
		IReadOnlyList<PositionSnapshot> legs,
		decimal spot,
		DateTime asOf,
		Func<string, decimal> ivResolver,
		Func<string, decimal> legPriceResolver,
		TrendSnapshot? trend,
		IReadOnlyDictionary<string, OptionContractQuote>? quotesForProbe = null,
		decimal technicalBiasForProbe = 0m,
		decimal? historicalVolAnnual = null,
		RealizedVolModel? realizedVol = null,
		bool useMarketImpliedIv = true,
		SentimentSnapshot? sentiment = null,
		TickerEvents? events = null)
	{
		var diagLegs = legs.Select(l => new DiagnosticLeg(
			Symbol: l.Symbol,
			Parsed: l.Parsed,
			IsLong: l.Action == LegAction.Buy,
			Qty: l.Qty,
			PricePerShare: legPriceResolver(l.Symbol),
			CostBasisPerShare: l.CostBasis)).ToList();

		var diagnostic = RiskDiagnosticBuilder.Build(diagLegs, spot, asOf, ivResolver, trend, quotesForProbe, sentiment, events);
		var probe = RiskDiagnosticProbeBuilder.Build(diagLegs, spot, asOf, ivResolver, quotesForProbe, opener: null, technicalBiasOverride: technicalBiasForProbe, useCostBasisForOpenerScore: true, historicalVolAnnual: historicalVolAnnual, useMarketImpliedIv: useMarketImpliedIv, sentimentScore: sentiment?.Score, events: events, realizedVol: realizedVol);
		diagnostic = diagnostic with { Probe = probe };

		Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		var record = new
		{
			type = "analyze_position",
			ts = DateTime.Now.ToString("o"),
			ticker,
			positionKey,
			spot,
			diagnostic = SerializeDiagnostic(diagnostic),
			mode = "analyze_position",
		};
		WebullAnalytics.IO.SharedFileAppender.AppendLine(logPath, System.Text.Json.JsonSerializer.Serialize(record));

		return diagnostic;
	}

	/// <summary>Shapes a RiskDiagnostic into a stable lowerCamel JSON object. Shared between manage and
	/// open pipeline emitters so the schema is identical across both log streams.</summary>
	internal static object SerializeDiagnostic(RiskDiagnostic d) => new
	{
		structureLabel = d.StructureLabel,
		directionalBias = d.DirectionalBias,
		netDelta = d.NetDelta,
		netThetaPerDay = d.NetThetaPerDay,
		netVega = d.NetVega,
		shortLegDteMin = d.ShortLegDteMin,
		longLegDteMax = d.LongLegDteMax,
		dteGapDays = d.DteGapDays,
		longPremiumPaid = d.LongPremiumPaid,
		shortPremiumReceived = d.ShortPremiumReceived,
		netCashPerShare = d.NetCashPerShare,
		premiumRatio = d.PremiumRatio,
		spotAtEvaluation = d.SpotAtEvaluation,
		shortLegOtm = d.ShortLegOtm,
		shortLegExtrinsic = d.ShortLegExtrinsic,
		netMidPerShare = d.NetMidPerShare,
		theoreticalValuePerShare = d.TheoreticalValuePerShare,
		marketLongPremiumPaid = d.MarketLongPremiumPaid,
		marketShortPremiumReceived = d.MarketShortPremiumReceived,
		marketNetPremiumPerShare = d.MarketNetPremiumPerShare,
		marketPremiumRatio = d.MarketPremiumRatio,
		theoreticalLongPremiumPaid = d.TheoreticalLongPremiumPaid,
		theoreticalShortPremiumReceived = d.TheoreticalShortPremiumReceived,
		theoreticalNetPremiumPerShare = d.TheoreticalNetPremiumPerShare,
		theoreticalPremiumRatio = d.TheoreticalPremiumRatio,
		marketSentimentScore = d.MarketSentimentScore,
		marketSentimentRating = d.MarketSentimentRating,
		marketSentimentDelta1Week = d.MarketSentimentDelta1Week,
		probe = d.Probe is null ? null : new
		{
			enumDelta = d.Probe.EnumDelta,
			enumDeltaMin = d.Probe.EnumDeltaMin,
			enumDeltaMax = d.Probe.EnumDeltaMax,
			enumDeltaPass = d.Probe.EnumDeltaPass,
			legQuotes = d.Probe.LegQuotes.Select(q => new
			{
				label = q.Label,
				symbol = q.Symbol,
				bid = q.Bid,
				ask = q.Ask,
				impliedVolatility = q.ImpliedVolatility,
				historicalVolatility = q.HistoricalVolatility,
				openInterest = q.OpenInterest,
				volume = q.Volume,
			}),
			openerScore = d.Probe.OpenerScore is null ? null : new
			{
				structure = d.Probe.OpenerScore.Structure,
				qty = d.Probe.OpenerScore.Qty,
				debitOrCreditPerContract = d.Probe.OpenerScore.DebitOrCreditPerContract,
				maxProfitPerContract = d.Probe.OpenerScore.MaxProfitPerContract,
				maxLossPerContract = d.Probe.OpenerScore.MaxLossPerContract,
				capitalAtRiskPerContract = d.Probe.OpenerScore.CapitalAtRiskPerContract,
				probabilityOfProfit = d.Probe.OpenerScore.ProbabilityOfProfit,
				expectedValuePerContract = d.Probe.OpenerScore.ExpectedValuePerContract,
				daysToTarget = d.Probe.OpenerScore.DaysToTarget,
				rawScore = d.Probe.OpenerScore.RawScore,
				biasAdjustedScore = d.Probe.OpenerScore.BiasAdjustedScore,
				rationale = d.Probe.OpenerScore.Rationale,
			}
		},
		trend = d.Trend is null ? null : new
		{
			changePctIntraday = d.Trend.ChangePctIntraday,
			changePct5Day = d.Trend.ChangePct5Day,
			changePct20Day = d.Trend.ChangePct20Day,
			atr14Pct = d.Trend.Atr14Pct,
			asOf = d.Trend.AsOf,
		},
		costBasisPerShare = d.CostBasisPerShare,
		currentValuePerShare = d.CurrentValuePerShare,
		unrealizedPnlPerShare = d.UnrealizedPnlPerShare,
		rules = d.Rules.Select(r => new { id = r.Id, message = r.Message, inputs = r.Inputs }),
	};
}

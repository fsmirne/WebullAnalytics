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
using WebullAnalytics.Sentiment;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

// ─── `analyze position` ───────────────────────────────────────────────────────

internal sealed class AnalyzePositionSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "[spec]")]
	[Description("Open position. Format: ACTION:SYMBOL:QTY@PRICE,... where PRICE is your cost basis per leg. Example: sell:GME260424C00025000:499@0.48,buy:GME260515C00025000:499@1.11. Omit to select from open positions interactively.")]
	public string Spec { get; set; } = "";

	[CommandOption("--proposal")]
	[Description("Render the position diagnostic and management scenarios from a stored proposal snapshot instead of live quotes. Format: FILE[[:LINE]] where FILE is a path, a data/ filename (ai-proposals.SPY.0DTE.jsonl), or the TICKER.strategy shorthand (SPY.0DTE); LINE is a 1-based line number, defaulting to the last line.")]
	public string? Proposal { get; set; }

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
	[Description("Risk-aversion weight for ranking: each $1 of capital put at stake (added margin + net debit deployed) discounts the suggestion's expected $ by this much, so de-risking (close/reduce) ranks higher. 0 = pure projected return (legacy). Default 0.5.")]
	public decimal Risk { get; set; } = 0.5m;

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

		if (Proposal != null && !string.IsNullOrEmpty(Spec))
			return ValidationResult.Error("pass either <spec> or --proposal, not both");

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

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		TerminalHelper.EnsureTerminalWidthFromConfig();

		if (settings.Proposal != null && !TryLoadProposalSpec(settings))
			return 1;

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
		var apiConfigPath = Program.ResolvePath(Program.ApiConfigPath);
		if (File.Exists(apiConfigPath))
		{
			var apiCfg = System.Text.Json.JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(apiConfigPath));
			if (apiCfg != null && apiCfg.Webull.Headers.Count > 0)
			{
				var targetExpiries = positionLegs.Select(l => l.Parsed.ExpiryDate.Date).Distinct().ToList();
				var (chainQuotes, chainSpot, derivativeIds) = await WebullOptionsClient.FetchChainWithExpiryRefreshAsync(apiCfg, ticker, targetExpiries, strikeRangeFraction: 0.20m, cancellation);

				// The near-ATM refresh above gives max-pain/GEX their chain breadth, but it leaves the
				// position's OWN legs unpriced when they sit outside ±20% of spot (e.g. the deep-OTM wings of
				// a wide condor). Those legs are the whole point of `analyze position`, so refresh any that
				// came back without a usable bid/ask directly from queryBatch — resolving the derivativeId
				// from this fetch's map first, then the persisted registry so a leg the chain dropped entirely
				// (far-dated expiries outside the strategy/list cycle) can still be priced.
				var legsMissingQuote = positionSymbols.Where(s => !chainQuotes.TryGetValue(s, out var q) || q.Bid == null || q.Ask == null).ToList();
				if (legsMissingQuote.Count > 0)
				{
					var ids = new Dictionary<string, long>(derivativeIds, StringComparer.OrdinalIgnoreCase);
					foreach (var s in legsMissingQuote)
						if (!ids.ContainsKey(s) && DerivativeIdRegistry.TryGetId(s, out var rid)) ids[s] = rid;
					var mutableChain = new Dictionary<string, OptionContractQuote>(chainQuotes, StringComparer.OrdinalIgnoreCase);
					await WebullOptionsClient.RefreshContractsAsync(apiCfg, mutableChain, legsMissingQuote, ids, cancellation);
					chainQuotes = mutableChain;
				}
				quotes = chainQuotes;
				if (chainSpot.HasValue)
					underlyingPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = chainSpot.Value };
			}
		}

		if (quotes == null)
			(quotes, underlyingPrices) = await AnalyzeCommon.FetchQuotesAndUnderlyingForSymbolList(positionSymbols, cancellation);

		var spot = ResolveSpot(ticker, settings, underlyingPrices);
		if (spot == null)
		{
			Console.Error.WriteLine($"Error: no underlying price for '{ticker}'. Pass --spot {ticker}:<price> or run 'wa sniff' to refresh Webull headers.");
			return 1;
		}

		var structure = ClassifyStructure(positionLegs);

		// Phase 2: fetch quotes for the hypothetical-scenario symbols we couldn't enumerate without spot.
		{
			var alreadyFetched = quotes ?? new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			// Under --log-level debug, also pull the same-strike PUT at each position expiry so the
			// put-call-parity implied-dividend diagnostic has both sides (the chain often quotes only
			// calls / front-month, leaving far puts as stubs). Skipped by default to avoid extra fetches.
			var pcpPutSymbols = settings.IsDebug
				? positionLegs.Select(l => $"{l.Parsed.Root}{l.Parsed.ExpiryDate:yyMMdd}P{(int)Math.Round(l.Parsed.Strike * 1000m):00000000}")
				: Enumerable.Empty<string>();
			var hypotheticalSymbols = EnumerateHypotheticalSymbols(positionLegs, structure, settings, spot.Value)
				.Concat(pcpPutSymbols)
				.Where(s => !alreadyFetched.ContainsKey(s))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (hypotheticalSymbols.Count > 0)
			{
				var hypotheticalQuotes = await AnalyzeCommon.FetchQuotesForSymbolList(hypotheticalSymbols, cancellation);
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
		var asOfForDiagnostic = EvaluationDate.Today;
		var tickerForTrend = positionLegs.Count > 0 ? positionLegs[0].Parsed.Root : null;
		TrendSnapshot? trendSnap = null;
		if (!string.IsNullOrEmpty(tickerForTrend))
			trendSnap = await TrendFetcher.FetchAsync(tickerForTrend, asOfForDiagnostic, cancellation);

		// Shared price cache for both regime and vol-fit fetches — avoids a redundant disk read for the ticker's closes.
		var priceCache = new HistoricalPriceCache();
		var aiCfg = RiskDiagnosticProbeBuilder.TryLoadAiConfigQuiet(ticker, out _);
		decimal macroBias = 0m;
		if (aiCfg?.Indicators.TechnicalFilter.Enabled == true)
		{
			var filter = aiCfg.Indicators.TechnicalFilter;
			var effectiveLookback = filter.Sma200Weight > 0m ? Math.Max(filter.LookbackDays, 200) : filter.LookbackDays;
			var closes = await priceCache.GetRecentClosesAsync(ticker, effectiveLookback, asOfForDiagnostic, cancellation);
			macroBias = TechnicalIndicators.Compute(closes, filter)?.Score ?? 0m;
		}
		var apiConfig = OpenCandidateEvaluator.TryLoadApiConfig();
		var intradayCache = apiConfig != null ? new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig)) : null;
		var dteCalendar = positionLegs.Count > 0 ? Math.Max(1, (positionLegs.Min(l => l.Parsed.ExpiryDate.Date) - asOfForDiagnostic.Date).Days) : 5;
		var regimeComponents = aiCfg != null ? await OpenCandidateEvaluator.ComputeRegimeComponentsAsync(ticker, aiCfg.Opener, macroBias, DateTime.Now, priceCache, intradayCache, includeCurrentBar: true, cancellation) : default;
		var technicalBias = aiCfg != null ? RegimeAnalyzer.BlendBias(regimeComponents, aiCfg.Opener, dteCalendar) : 0m;

		var historicalVolAnnual = await TryComputeHistoricalVolAsync(ticker, asOfForDiagnostic, priceCache, cancellation);
		var sentiment = await FearGreedClient.FetchAsync(asOfForDiagnostic, cancellation);

		// Scheduled-catalyst events (earnings + ex-dividend) for the position's ticker. cacheOnly:false
		// refreshes the 12h event cache — without this, held tickers the opener never scans (e.g. SPY)
		// never populate data/event-cache, leaving the theoretical price un-dividend-adjusted.
		var positionEvents = (await EventCalendarLoader.LoadAsync(new[] { ticker }, new OpenerEventsConfig(), asOfForDiagnostic, cancellation, cacheOnly: false)).Get(ticker);
		var dividends = DividendScheduleBuilder.BuildForTicker(positionEvents, spot.Value, null);

		// --calibrated (defaults from the report config's 'calibrated' key): re-anchor every chain quote's IV
		// to its live bid/ask mid at the dividend-adjusted spot — the same mid-consistent surface the report
		// grid prices on — BEFORE the diagnostic, probe score and scenario tables read it. ResolveIV serves
		// the quote's iv field everywhere downstream, so one pass here aligns the whole command with
		// 'wa report --calibrated'. Legs whose mid can't back-solve keep the broker IV; explicit --iv
		// overrides still win (ResolveIV checks them first). Skipped at hypothetical spots (--spot): the
		// stale mids no longer reflect the overridden spot, so the inversion would fold the spot move into vol.
		if (settings.Calibrated && quotes != null && string.IsNullOrEmpty(settings.Spot))
		{
			var asOfCal = OptionMath.ObservationInstant(); // when the loaded quotes were struck (now live / last close off-hours); same anchor as report
			var recalibratedQuotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			var recalibrated = 0;
			foreach (var (sym, q) in quotes)
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(sym);
				if (parsed == null) { recalibratedQuotes[sym] = q; continue; }
				var adjustedSpot = OptionMath.DividendAdjustedSpot(spot.Value, dividends, asOfCal, parsed.ExpiryDate.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate);
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
			asOf: asOfForDiagnostic,
			ivResolver: sym => ResolveIV(sym, settings, quotes),
			legPriceResolver: sym =>
			{
				var leg = positionLegs.FirstOrDefault(l => l.Symbol == sym);
				if (leg == null) return 0m;
				var iv = ResolveIV(sym, settings, quotes);
				var dte = Math.Max(1, (leg.Parsed.ExpiryDate - asOfForDiagnostic.Date).Days);
				return LiveOrBsMid(settings.Theoretical ? null : quotes, sym, spot.Value, leg.Parsed.Strike, dte, iv, leg.Parsed.CallPut, dividends);
			},
			trend: trendSnap,
			quotesForProbe: quotes,
			technicalBiasForProbe: technicalBias,
			historicalVolAnnual: historicalVolAnnual,
			// At hypothetical spots (--spot override), the leg market mids are stale and back-solving
			// IV against them produces a nonsense IV that collapses calendar/diagonal residual time
			// value. Use the broker's reported IV instead so the projection is internally consistent.
			useMarketImpliedIv: string.IsNullOrEmpty(settings.Spot),
			sentiment: sentiment,
			events: positionEvents);

		var scenarios = GenerateScenarios(positionLegs, structure, settings, spot.Value, EvaluationDate.Today, quotes, technicalBias, dividends);

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
			RenderImpliedDividendDiagnostic(positionLegs, spot.Value, quotes, dividends, asOfForDiagnostic, renderConsole);

		if (scenarios.Count == 0)
		{
			renderConsole.MarkupLine($"[yellow]No scenarios defined yet for structure type '{structure}'. Supported: single-long, vertical, calendar, diagonal, iron butterfly, iron condor, double calendar, double diagonal.[/]");
		}
		else
		{
			RenderScenarioTable(scenarios, settings, renderConsole, ascii: toText);
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

	private static async Task<(decimal Cash, string AccountAlias)?> TryResolveAvailableCashAsync(string? accountFlag, CancellationToken cancellation)
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

	/// <summary>Mirrors OpenCandidateEvaluator's HV pull so analyze position scores get the same vol-fit
	/// factor as the live opener pipeline. Accepts the caller's shared HistoricalPriceCache so the
	/// in-memory bar cache is reused across the regime and vol-fit fetches. Returns null when the
	/// config, lookback, or cache is missing — the scorer treats that as "skip the vol factor".</summary>
	private static async Task<decimal?> TryComputeHistoricalVolAsync(string ticker, DateTime asOf, HistoricalPriceCache priceCache, CancellationToken cancellation)
	{
		try
		{
			var cfg = RiskDiagnosticProbeBuilder.TryLoadAiConfigQuiet(ticker, out _);
			if (cfg == null) return null;
			if (cfg.Opener.Weights.VolatilityFit <= 0m && cfg.Opener.Weights.IvRealizedPremium <= 0m) return null;

			var closes = await priceCache.GetRecentClosesAsync(ticker, cfg.Opener.VolatilityLookbackDays + 1, asOf, cancellation);
			var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
			return hv is decimal v && v > 0m ? v : null;
		}
		catch
		{
			return null;
		}
	}

	// ─── Input model ──────────────────────────────────────────────────────────

	internal sealed record PositionSnapshot(string Symbol, LegAction Action, int Qty, decimal CostBasis, OptionParsed Parsed);

	internal enum StructureKind { SingleLong, SingleShort, Calendar, Diagonal, Vertical, IronButterfly, IronCondor, Condor, DoubleCalendar, DoubleDiagonal, Unsupported }

	// ─── Load from trade log ──────────────────────────────────────────────────

	private static (List<PositionSnapshot>? snapshots, string? error) SelectPositionFromLog()
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
				.Title("Select a position to analyze:")
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

	internal static StructureKind ClassifyStructure(IReadOnlyList<PositionSnapshot> legs)
	{
		if (legs.Count == 1)
			return legs[0].Action == LegAction.Buy ? StructureKind.SingleLong : StructureKind.SingleShort;

		if (legs.Count == 2)
		{
			var sl = legs.FirstOrDefault(l => l.Action == LegAction.Sell);
			var ll = legs.FirstOrDefault(l => l.Action == LegAction.Buy);
			if (sl == null || ll == null) return StructureKind.Unsupported;
			if (sl.Parsed.Root != ll.Parsed.Root || sl.Parsed.CallPut != ll.Parsed.CallPut) return StructureKind.Unsupported;
			if (sl.Parsed.ExpiryDate == ll.Parsed.ExpiryDate) return StructureKind.Vertical;
			if (sl.Parsed.ExpiryDate < ll.Parsed.ExpiryDate)
				return sl.Parsed.Strike == ll.Parsed.Strike ? StructureKind.Calendar : StructureKind.Diagonal;
		}

		if (legs.Count == 4)
		{
			// Every 4-leg structure we recognize has the same component breakdown:
			// one short put, one long put, one short call, one long call, all on the
			// same underlying. What differs is the expiry / strike geometry.
			var root = legs[0].Parsed.Root;
			if (!legs.All(l => l.Parsed.Root == root)) return StructureKind.Unsupported;

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
		bool IsRoll = false,
		decimal? RankScore = null,			// true iff this scenario closes an existing leg and opens a new one (consulted by BuildReproductionCommands)
		decimal? ExpectedPnLPerContract = null,	// probability-weighted EV at target (lognormal spot grid), vs the spot-pinned ProjectedValue. Null when not computed for this scenario type.
		decimal AssignmentPenaltyPerContract = 0m,	// per-contract assignment-risk charge (verticals only); subtracted in the risk-adjusted rank.
		decimal PeakCashPerContract = 0m)	// up-front cash of the FIRST order when the scenario executes as sequential single-leg orders (non-calendar rolls: the buy-to-close cost) — the sell credit isn't available until that order fills. 0 when one net-priced combo covers the whole change.
	{
		/// <summary>Per-contract buying power the scenario actually consumes. Margin delta alone is wrong for
		/// rolls: a roll can be margin-neutral yet cost a large cash debit, and a split (two-order) roll must
		/// fund its buy-to-close leg in full before the sell credit lands.</summary>
		public decimal FundingPerContract => Math.Max(Math.Max(Math.Max(MarginDeltaPerContract, 0m), Math.Max(-CashImpactPerContract, 0m)), PeakCashPerContract);
	}

	/// <summary>Hypothetical OCC symbols the scenario generators will reference. Pre-enumerated so we can
	/// include them in a single up-front quote fetch.</summary>
	private static IEnumerable<string> EnumerateHypotheticalSymbols(IReadOnlyList<PositionSnapshot> legs, StructureKind kind, AnalyzePositionSettings settings, decimal spot)
	{
		if (legs.Count == 0) yield break;
		var root = legs[0].Parsed.Root;
		var callPut = legs[0].Parsed.CallPut;

		if (kind == StructureKind.SingleLong)
		{
			var longLeg = legs[0];
			var shortExpiry = NextWeeklyFromToday();
			if (shortExpiry < longLeg.Parsed.ExpiryDate)
				yield return MatchKeys.OccSymbol(root, shortExpiry, longLeg.Parsed.Strike, callPut);
		}
		else if (kind == StructureKind.Calendar || kind == StructureKind.Diagonal)
		{
			var shortLeg = legs.First(l => l.Action == LegAction.Sell);
			var longLeg = legs.First(l => l.Action == LegAction.Buy);
			var newExpiry = NextWeekly(shortLeg.Parsed.ExpiryDate);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike, callPut);
			var newLongExp = longLeg.Parsed.ExpiryDate > newExpiry ? longLeg.Parsed.ExpiryDate : newExpiry.AddDays(21);
			var oppositeCp = callPut == "C" ? "P" : "C";
			foreach (var strike in BracketStrikes(spot, settings.StrikeStep))
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
		}
		else if (kind == StructureKind.Vertical)
		{
			var shortLeg = legs.First(l => l.Action == LegAction.Sell);
			var longLeg = legs.First(l => l.Action == LegAction.Buy);
			var strikeOffset = longLeg.Parsed.Strike - shortLeg.Parsed.Strike;
			var width = Math.Abs(strikeOffset);
			var newExpiry = NextWeekly(shortLeg.Parsed.ExpiryDate);
			foreach (var strike in BracketStrikes(spot, settings.StrikeStep))
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
			foreach (var strike in BracketStrikes(spot, settings.StrikeStep))
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
		decimal technicalBias = 0m, IReadOnlyList<DividendEvent>? dividends = null) =>
		kind switch
		{
			StructureKind.SingleLong => GenerateSingleLongScenarios(legs[0], settings, spot, asOf, quotes, dividends),
			StructureKind.Vertical => GenerateVerticalScenarios(legs, settings, spot, asOf, quotes, technicalBias, dividends),
			StructureKind.Calendar or StructureKind.Diagonal => GenerateSpreadScenarios(legs, settings, spot, asOf, kind, quotes, dividends),
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
		var quotesForPricing = settings.EvaluationDateOverride.HasValue || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var ivShortPut = ResolveIV(shortPut.Symbol, settings, quotes);
		var ivLongPut = ResolveIV(longPut.Symbol, settings, quotes);
		var ivShortCall = ResolveIV(shortCall.Symbol, settings, quotes);
		var ivLongCall = ResolveIV(longCall.Symbol, settings, quotes);

		var shortPutMid = LiveOrBsMid(quotesForPricing, shortPut.Symbol, spot, shortPut.Parsed.Strike, shortDte, ivShortPut, "P", dividends);
		var longPutMid = LiveOrBsMid(quotesForPricing, longPut.Symbol, spot, longPut.Parsed.Strike, Math.Max(1, (longExpiry.Date - asOf.Date).Days), ivLongPut, "P", dividends);
		var shortCallMid = LiveOrBsMid(quotesForPricing, shortCall.Symbol, spot, shortCall.Parsed.Strike, shortDte, ivShortCall, "C", dividends);
		var longCallMid = LiveOrBsMid(quotesForPricing, longCall.Symbol, spot, longCall.Parsed.Strike, Math.Max(1, (longExpiry.Date - asOf.Date).Days), ivLongCall, "C", dividends);

		// Long-leg value at the short expiry: BS with the residual time until the long's own expiry.
		double tLongAtShortExp = Math.Max(1, (longExpiry.Date - shortExpiry.Date).Days) / 365.0;
		decimal LongPutAtShortExp(decimal s) => OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(s, dividends, shortExpiry.Date + OptionMath.MarketClose, longExpiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longPut.Parsed.Strike, tLongAtShortExp, OptionMath.RiskFreeRate, ivLongPut, "P");
		decimal LongCallAtShortExp(decimal s) => OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(s, dividends, shortExpiry.Date + OptionMath.MarketClose, longExpiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longCall.Parsed.Strike, tLongAtShortExp, OptionMath.RiskFreeRate, ivLongCall, "C");
		// Per-share value of the whole 4-leg position at the short expiry, evaluated at a given spot.
		decimal ValueAtShortExpiry(decimal s) =>
			LongPutAtShortExp(s) - Intrinsic(s, shortPut.Parsed.Strike, "P")
			+ LongCallAtShortExp(s) - Intrinsic(s, shortCall.Parsed.Strike, "C");

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
			var residualAtShortExp = LongCallAtShortExp(spot) - Intrinsic(spot, shortCall.Parsed.Strike, "C");
			list.Add(NewScenarioSpread("Close put side (defensive)", legs,
				$"BUY {shortPut.Symbol} x{shortPut.Qty} @{FmtPrice(shortPutMid)}, SELL {longPut.Symbol} x{longPut.Qty} @{FmtPrice(longPutMid)}",
				cashNow: sideCash, valueAtTarget: residualAtShortExp, marginDeltaPerContract: 0m, daysToTarget: shortDte,
				rationale: $"put-side closer to short strike (${distanceToPut:F2} from short) — cut the put spread for ${sideCash:+0.00;-0.00}/share; remaining call spread runs to short exp → ${residualAtShortExp:F2}/share"));
		}
		else
		{
			var sideCash = -shortCallMid + longCallMid;
			var residualAtShortExp = LongPutAtShortExp(spot) - Intrinsic(spot, shortPut.Parsed.Strike, "P");
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
		var quotesForPricing = settings.EvaluationDateOverride.HasValue || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		// Helper: intrinsic value of the whole 4-leg position at a given expiration spot. Per-share,
		// signed so a positive number is what we'd receive if we closed at that intrinsic.
		decimal IntrinsicAtSpot(decimal s) =>
			Intrinsic(s, longPut.Parsed.Strike, "P") - Intrinsic(s, shortPut.Parsed.Strike, "P")
			+ Intrinsic(s, longCall.Parsed.Strike, "C") - Intrinsic(s, shortCall.Parsed.Strike, "C");

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
		var shortPutMid = LiveOrBsMid(quotesForPricing, shortPut.Symbol, spot, shortPut.Parsed.Strike, expiryDte, ivShortPut, "P", dividends);
		var longPutMid = LiveOrBsMid(quotesForPricing, longPut.Symbol, spot, longPut.Parsed.Strike, expiryDte, ivLongPut, "P", dividends);
		var shortCallMid = LiveOrBsMid(quotesForPricing, shortCall.Symbol, spot, shortCall.Parsed.Strike, expiryDte, ivShortCall, "C", dividends);
		var longCallMid = LiveOrBsMid(quotesForPricing, longCall.Symbol, spot, longCall.Parsed.Strike, expiryDte, ivLongCall, "C", dividends);
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
			var residualAtExpiry = Intrinsic(spot, longCall.Parsed.Strike, "C") - Intrinsic(spot, shortCall.Parsed.Strike, "C");
			list.Add(NewScenarioSpread("Close put side (defensive)", legs,
				$"BUY {shortPut.Symbol} x{shortPut.Qty} @{FmtPrice(shortPutMid)}, SELL {longPut.Symbol} x{longPut.Qty} @{FmtPrice(longPutMid)}",
				cashNow: sideCash, valueAtTarget: residualAtExpiry, marginDeltaPerContract: 0m, daysToTarget: expiryDte,
				rationale: $"put-side closer to short strike (${distanceToShortPut:F2} below) — cut the put spread for ${sideCash:+0.00;-0.00}/share; remaining call spread settles to ${residualAtExpiry:F2}/share at current spot"));
		}
		else
		{
			// Close the call spread: buy back short call, sell back long call.
			var sideCash = -shortCallMid + longCallMid;
			var residualAtExpiry = Intrinsic(spot, longPut.Parsed.Strike, "P") - Intrinsic(spot, shortPut.Parsed.Strike, "P");
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
		var quotesForPricing = settings.EvaluationDateOverride.HasValue || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		// Signed intrinsic of the whole position held to expiry at spot s (per share): longs add their
		// intrinsic, shorts subtract it. Right-agnostic, so it covers both put and call condors.
		decimal IntrinsicAtSpot(decimal s) => ordered.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * Intrinsic(s, l.Parsed.Strike, l.Parsed.CallPut));

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
		var mids = ordered.ToDictionary(l => l.Symbol, l => LiveOrBsMid(quotesForPricing, l.Symbol, spot, l.Parsed.Strike, expiryDte, ResolveIV(l.Symbol, settings, quotes), l.Parsed.CallPut, dividends), StringComparer.OrdinalIgnoreCase);
		var closeCash = ordered.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * mids[l.Symbol]);
		var closeAction = string.Join(", ", ordered.Select(l => $"{(l.Action == LegAction.Buy ? "SELL" : "BUY")} {l.Symbol} x{l.Qty} @{FmtPrice(mids[l.Symbol])}"));
		list.Add(NewScenarioSpread("Close all", legs, closeAction,
			cashNow: closeCash, valueAtTarget: 0m, marginDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"flatten at mid: net cash ${closeCash:+0.00;-0.00}/share to close all four legs"));

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	private static List<Scenario> GenerateSingleLongScenarios(PositionSnapshot longLeg, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var list = new List<Scenario>();
		var iv = ResolveIV(longLeg.Symbol, settings, quotes);
		var callPut = longLeg.Parsed.CallPut;
		var quotesForPricing = settings.EvaluationDateOverride.HasValue || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		// 1. Hold (do nothing) — value at expiry = intrinsic.
		var valueAtExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		var holdDte = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		list.Add(NewScenario("Hold to expiry", longLeg, "—",
			cashNow: 0m, valueAtTarget: valueAtExpiry, marginDeltaPerContract: 0m, daysToTarget: holdDte,
			rationale: $"value at expiry ({longLeg.Parsed.ExpiryDate:yyyy-MM-dd}) = intrinsic ${valueAtExpiry:F2}/share"));

		// 2. Close now at theoretical mid (or live mid if available).
		var dteNow = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var midNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, dteNow, iv, callPut, dividends);
		list.Add(NewScenario("Close now", longLeg, $"SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(midNow)}",
			cashNow: midNow, valueAtTarget: 0m, marginDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"sell at mid ${midNow:F2}/share → close position"));

		// 3. Convert to calendar: sell a near-expiry short at same strike.
		var shortExpiry = NextWeeklyFromToday();
		if (shortExpiry < longLeg.Parsed.ExpiryDate)
		{
			var newShortSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, shortExpiry, longLeg.Parsed.Strike, callPut);
			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var dteShort = Math.Max(1, (shortExpiry - asOf).Days);
			var shortMid = LiveOrBsMid(quotesForPricing, newShortSym, spot, longLeg.Parsed.Strike, dteShort, ivNewShort, callPut, dividends);
			// Project value at short expiry: long BS value, short intrinsic.
			var dteLongAtShortExp = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days);
			var longAtShortExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, shortExpiry.Date + OptionMath.MarketClose, longLeg.Parsed.ExpiryDate.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longLeg.Parsed.Strike, dteLongAtShortExp / 365.0, OptionMath.RiskFreeRate, iv, callPut);
			var shortAtShortExp = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
			var net = longAtShortExp - shortAtShortExp;
			// margin delta: becomes a calendar (strike_loss = 0). Current is single long (no margin). Delta = 0.
			list.Add(NewScenario($"Convert to calendar (sell {shortExpiry:yyyy-MM-dd} @ ${longLeg.Parsed.Strike:F2})",
				longLeg, $"SELL {newShortSym} x{longLeg.Qty} @{FmtPrice(shortMid)}",
				cashNow: shortMid, valueAtTarget: net, marginDeltaPerContract: 0m, daysToTarget: dteShort,
				rationale: $"collect ${shortMid:F2}/share short premium; at short exp: long ${longAtShortExp:F2} - short ${shortAtShortExp:F2} = ${net:F2}"));
		}

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

	private static List<Scenario> GenerateVerticalScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes, decimal technicalBias, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var list = new List<Scenario>();
		var shortLeg = legs.First(l => l.Action == LegAction.Sell);
		var longLeg = legs.First(l => l.Action == LegAction.Buy);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, settings, quotes);
		var ivLong = ResolveIV(longLeg.Symbol, settings, quotes);
		var quotesForPricing = settings.EvaluationDateOverride.HasValue || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var expiry = shortLeg.Parsed.ExpiryDate;
		var expiryDte = Math.Max(1, (expiry.Date - asOf.Date).Days);
		var shortMidNow = LiveOrBsMid(quotesForPricing, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, expiryDte, ivShort, callPut, dividends);
		var longMidNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, expiryDte, ivLong, callPut, dividends);
		var (_, shortAskNow) = LiveBidAsk(quotesForPricing, shortLeg.Symbol, shortMidNow);
		var (longBidNow, _) = LiveBidAsk(quotesForPricing, longLeg.Symbol, longMidNow);

		var currentMargin = AnalyzeCommon.ComputeLegMargin(shortLeg.Parsed, 1, spot, shortMidNow, longLeg.Parsed, null, 1, longMidNow, isExisting: true).Total;
		var longAtExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		var shortAtExpiry = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
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

		foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
		{
			if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

			var sameExpSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, expiry, newStrike, callPut);
			if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, sameExpSym)) continue;

			var ivSameExp = ResolveIV(sameExpSym, settings, quotes);
			var newShortMidSameExp = LiveOrBsMid(quotesForPricing, sameExpSym, spot, newStrike, expiryDte, ivSameExp, callPut, dividends);
			var (newShortBidSameExp, _) = LiveBidAsk(quotesForPricing, sameExpSym, newShortMidSameExp);
			var cashPerShareSameExp = newShortMidSameExp - shortMidNow;
			var projSameExpPerShare = longAtExpiry - Intrinsic(spot, newStrike, callPut);
			var sameExpShortParsed = new OptionParsed(shortLeg.Parsed.Root, expiry, callPut, newStrike);
			var sameExpMargin = AnalyzeCommon.ComputeLegMargin(sameExpShortParsed, 1, spot, newShortMidSameExp, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
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
		foreach (var newShortStrike in BracketStrikes(spot, settings.StrikeStep))
		{
			if (!IsComplementaryShortStrike(oppositeCp, newShortStrike, spot)) continue;

			var newLongStrike = ComplementaryLongStrike(oppositeCp, newShortStrike, width);
			if (newLongStrike <= 0m) continue;

			var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, expiry, newShortStrike, oppositeCp);
			var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, expiry, newLongStrike, oppositeCp);
			if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;

			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var ivNewLong = ResolveIV(newLongSym, settings, quotes);
			var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newShortStrike, expiryDte, ivNewShort, oppositeCp, dividends);
			var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newLongStrike, expiryDte, ivNewLong, oppositeCp, dividends);
			var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
			var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
			var cashPerShare = newShortMidExec - newLongMidExec;
			var newPositionValuePerShare = Intrinsic(spot, newLongStrike, oppositeCp) - Intrinsic(spot, newShortStrike, oppositeCp);
			var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, expiry, oppositeCp, newShortStrike);
			var newLongParsed = new OptionParsed(longLeg.Parsed.Root, expiry, oppositeCp, newLongStrike);
			var newMargin = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, newLongParsed, null, 1, newLongMidExec, isExisting: false).Total;
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
				rationale: $"add the opposite-side {wingLabel} wing at the same expiry; combined iron condor stays within current width collateral, so additional margin is ${marginDelta:N0}. At expiry: existing ${holdNetPerShare:F2} + new ${newPositionValuePerShare:F2} = ${holdNetPerShare + newPositionValuePerShare:F2}/share");
		}

		var newExpiry = NextWeekly(expiry);
		foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
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
			var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newStrike, dteNew, ivNewShort, callPut, dividends);
			var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newLongStrike, dteNew, ivNewLong, callPut, dividends);
			var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
			var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
			var closeNet = longMidNow - shortMidNow;
			var openNet = newShortMidExec - newLongMidExec;
			var cashPerShare = closeNet + openNet;
			var newProjectedPerShare = Intrinsic(spot, newLongStrike, callPut) - Intrinsic(spot, newStrike, callPut);
			var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExpiry, callPut, newStrike);
			var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newExpiry, callPut, newLongStrike);
			var newMargin = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, newLongParsed, null, 1, newLongMidExec, isExisting: false).Total;
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

	private static List<Scenario> GenerateSpreadScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, StructureKind kind, IReadOnlyDictionary<string, OptionContractQuote>? quotes, IReadOnlyList<DividendEvent>? dividends = null)
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
		var quotesForPricing = settings.EvaluationDateOverride.HasValue || settings.Theoretical ? null : quotes; // --theoretical: force BS pricing even with a live chain

		var shortMidNow = LiveOrBsMid(quotesForPricing, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivShort, callPut, dividends);
		var longMidNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivLong, callPut, dividends);
		var (shortBidNow, shortAskNow) = LiveBidAsk(quotesForPricing, shortLeg.Symbol, shortMidNow);
		var (longBidNow, longAskNow) = LiveBidAsk(quotesForPricing, longLeg.Symbol, longMidNow);

		// Current margin (ongoing; covered-structure debit is sunk).
		var currentMargin = AnalyzeCommon.ComputeLegMargin(shortLeg.Parsed, 1, spot, shortMidNow, longLeg.Parsed, null, 1, longMidNow, isExisting: true).Total;

		decimal LongValueAtShortExpiry(decimal longStrike, DateTime shortExpiry) =>
			(decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, shortExpiry.Date + OptionMath.MarketClose, longLeg.Parsed.ExpiryDate.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longStrike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days) / 365.0, OptionMath.RiskFreeRate, ivLong, callPut);

		// Compute the "hold" per-share value once — used as the unchanged-portion projection for partial variants.
		var longAtOriginalExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
		var shortAtOriginalExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
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
		var holdEvPerShare = ExpectedPositionValuePerShare(existingLegsForEv, shortLeg.Parsed.ExpiryDate, asOf, spot, ivShort, dividends);

		// 1. Hold to short expiry.
		list.Add(NewScenarioSpread("Hold to short expiry", legs, "—",
			cashNow: 0m, valueAtTarget: holdNetPerShare, marginDeltaPerContract: 0m, daysToTarget: origShortDte,
			rationale: $"at {shortLeg.Parsed.ExpiryDate:yyyy-MM-dd}: long ${longAtOriginalExp:F2} - short ${shortAtOriginalExp:F2} intrinsic = ${holdNetPerShare:F2} (pinned); EV ${holdEvPerShare:F2}/share across the spot distribution",
			expectedValueAtTarget: holdEvPerShare));

		// 2. Close short only (realistic: pay short ask).
		{
			var cash = -shortMidNow;
			var longAtExpiry = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
			// Post-action: single long. No short = no margin requirement.
			var marginDelta = 0m - currentMargin;
			list.Add(NewScenarioSpread("Close short only", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortMidNow)}",
				cashNow: cash, valueAtTarget: longAtExpiry, marginDeltaPerContract: marginDelta, daysToTarget: origShortDte,
			  rationale: $"buy back short at mid ${shortMidNow:F2}/share; keep long → ${longAtExpiry:F2}/share at short exp"));
		}

		// 3. Close all at the default mid basis.
		{
			var cash = longMidNow - shortMidNow;
			var marginDelta = 0m - currentMargin;
			list.Add(NewScenarioSpread("Close all", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortMidNow)}, SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(longMidNow)}",
				cashNow: cash, valueAtTarget: 0m, marginDeltaPerContract: marginDelta, daysToTarget: 1,
			  rationale: $"close at mid prices → net ${cash:+0.00;-0.00}/share"));
		}

		var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
		if (newExp < longLeg.Parsed.ExpiryDate)
		{
			// 4. Roll short same strike, next weekly.
			{
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, shortLeg.Parsed.Strike, callPut);
				if (quotesForPricing == null || HasLiveQuote(quotesForPricing, newSym))
				{
					var ivNewShort = ResolveIV(newSym, settings, quotes);
					var dteNewShort = Math.Max(1, (newExp - asOf).Days);
					var newShortMidExec = LiveOrBsMid(quotesForPricing, newSym, spot, shortLeg.Parsed.Strike, dteNewShort, ivNewShort, callPut, dividends);
					var (newShortBid, _) = LiveBidAsk(quotesForPricing, newSym, newShortMidExec);
					var cashPerShare = newShortMidExec - shortMidNow;
					var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
					var shortAtNewShortExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
					var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
					var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, shortLeg.Parsed.Strike);
					var newMargin = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
					var marginDelta = newMargin - currentMargin;
					EmitFullAndPartial(list, legs, settings.Cash,
						name: $"Roll short ({newExp:MM-dd}, same strike)",
						actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
						cashPerShareOfChange: cashPerShare,
						newProjectedPerShare: newProjectedPerShare,
						unchangedProjectedPerShare: holdNetPerShare,
						marginPerContract: marginDelta,
						daysToTarget: dteNewShort,
						rationale: $"roll at mid prices for net ${cashPerShare:+0.00;-0.00}/share; at new exp: ${newProjectedPerShare:F2}",
						isRoll: true);
				}
			}

			// 4.5. Roll short to bracket strikes, SAME expiry — keeps the short on the current week
			// so theta keeps working over the next few days. Projects at the original short expiry.
			foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

				var sameExpSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, newStrike, callPut);
				if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, sameExpSym)) continue;

				var ivSameExp = ResolveIV(sameExpSym, settings, quotes);
				var dteSameExp = Math.Max(1, (shortLeg.Parsed.ExpiryDate - asOf).Days);
				var newShortMidSameExp = LiveOrBsMid(quotesForPricing, sameExpSym, spot, newStrike, dteSameExp, ivSameExp, callPut, dividends);
				var (newShortBidSameExp, _) = LiveBidAsk(quotesForPricing, sameExpSym, newShortMidSameExp);
				var cashPerShareSameExp = newShortMidSameExp - shortMidNow;
				// At original short expiry: long has full remaining DTE to long expiry; new short at intrinsic.
				var longAtOrigExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
				var newShortIntrinsicAtExp = Intrinsic(spot, newStrike, callPut);
				var projSameExpPerShare = longAtOrigExp - newShortIntrinsicAtExp;
				var sameExpShortParsed = new OptionParsed(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, callPut, newStrike);
				var sameExpMargin = AnalyzeCommon.ComputeLegMargin(sameExpShortParsed, 1, spot, newShortMidSameExp, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var sameExpMarginDelta = sameExpMargin - currentMargin;

				var sameExpStructure = callPut == "C"
					? (newStrike < longLeg.Parsed.Strike ? "inverted diagonal" : newStrike > longLeg.Parsed.Strike ? "covered diagonal" : "calendar")
					: (newStrike > longLeg.Parsed.Strike ? "inverted diagonal" : newStrike < longLeg.Parsed.Strike ? "covered diagonal" : "calendar");
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short to ${newStrike:F2} (same exp {shortLeg.Parsed.ExpiryDate:MM-dd}, {sameExpStructure})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {sameExpSym} x{{qty}} @{FmtPrice(newShortMidSameExp)}",
					cashPerShareOfChange: cashPerShareSameExp,
					newProjectedPerShare: projSameExpPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: sameExpMarginDelta,
					daysToTarget: dteSameExp,
					rationale: $"shift to ${newStrike:F2} strike, keep {shortLeg.Parsed.ExpiryDate:MM-dd} expiry — collect theta this week; mid net ${cashPerShareSameExp:+0.00;-0.00}/share; at exp: ${projSameExpPerShare:F2}",
					isRoll: true,
					peakCashPerContract: shortMidNow * 100m); // strike changes → splits into two orders; the buy-to-close funds alone before the sell credit lands
			}

			// 5. Roll short to bracket strikes near spot (one per strike).
			foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, newStrike, callPut);
				if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, newSym)) continue; // skip if contract not listed
				var ivNewShort = ResolveIV(newSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newSym, spot, newStrike, dteNewShort, ivNewShort, callPut, dividends);
				var (newShortBid, _) = LiveBidAsk(quotesForPricing, newSym, newShortMidExec);
				var cashPerShare = newShortMidExec - shortMidNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, newStrike);
				var newMargin = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var marginDelta = newMargin - currentMargin;

				var structureLabel = callPut == "C"
					? (newStrike < longLeg.Parsed.Strike ? "inverted diagonal" : newStrike > longLeg.Parsed.Strike ? "covered diagonal" : "calendar")
					: (newStrike > longLeg.Parsed.Strike ? "inverted diagonal" : newStrike < longLeg.Parsed.Strike ? "covered diagonal" : "calendar");
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short to ${newStrike:F2} ({newExp:MM-dd}, {structureLabel})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: marginDelta,
					daysToTarget: dteNewShort,
					rationale: $"step short to ${newStrike:F2} (spot ${spot:F2}); mid net ${cashPerShare:+0.00;-0.00}/share; at new exp: ${newProjectedPerShare:F2}",
					isRoll: true,
					peakCashPerContract: shortMidNow * 100m); // strike changes → splits into two orders; the buy-to-close funds alone before the sell credit lands
			}
		}

		// 6. Close all and reopen a fresh calendar at bracket strikes near spot.
		{
			var newShortExp = newExp;
			var newLongExp = longLeg.Parsed.ExpiryDate > newShortExp ? longLeg.Parsed.ExpiryDate : newShortExp.AddDays(21);
			foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m) continue;

				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newShortExp, newStrike, callPut);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newLongExp, newStrike, callPut);
				if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;
				var ivNewShort = ResolveIV(newShortSym, settings, quotes);
				var ivNewLong = ResolveIV(newLongSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newShortExp - asOf).Days);
				var dteNewLong = Math.Max(1, (newLongExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newStrike, dteNewShort, ivNewShort, callPut, dividends);
				var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newStrike, dteNewLong, ivNewLong, callPut, dividends);
				var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
				var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
				var closeNet = longMidNow - shortMidNow;
				var openNet = newShortMidExec - newLongMidExec;
				var cashPerShare = closeNet + openNet;
				var longAtNewShortExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, newShortExp.Date + OptionMath.MarketClose, newLongExp.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), newStrike, Math.Max(1, (newLongExp.Date - newShortExp.Date).Days) / 365.0, OptionMath.RiskFreeRate, ivNewLong, callPut);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newShortExp, callPut, newStrike);
				var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newLongExp, callPut, newStrike);
				var newMargin = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, newLongParsed, null, 1, newLongMidExec, isExisting: false).Total;
				var marginDelta = newMargin - currentMargin;

				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Reset to ${newStrike:F2} calendar",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortMidNow)}, SELL {longLeg.Symbol} x{{qty}} @{FmtPrice(longMidNow)}, BUY {newLongSym} x{{qty}} @{FmtPrice(newLongMidExec)}, SELL {newShortSym} x{{qty}} @{FmtPrice(newShortMidExec)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: marginDelta,
					daysToTarget: dteNewShort,
					rationale: $"close net ${closeNet:+0.00;-0.00}, open new net ${openNet:+0.00;-0.00} at mid; projected at new short exp: ${newProjectedPerShare:F2}",
					isRoll: true);
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
		// neutral profit tent. Each add reports its incremental return per added-margin so the trade-off is
		// explicit, and the most capital-efficient add is flagged ★.
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
			// Strikes the chain actually lists for (root, expiry, side), ascending. Snapping to these instead
			// of a fixed StrikeStep grid keeps the add on real contracts (SPY lists $1 near spot, not $0.50).
			List<decimal> ListedStrikes(DateTime exp, string cp) => quotes == null ? new List<decimal>()
				: quotes.Keys.Select(ParsingHelpers.ParseOptionSymbol)
					.Where(p => p != null && string.Equals(p.Root, shortLeg.Parsed.Root, StringComparison.OrdinalIgnoreCase) && p.ExpiryDate.Date == exp.Date && p.CallPut == cp)
					.Select(p => p!.Strike).Distinct().OrderBy(s => s).ToList();

			// Strike grid comes from the FRONT (short) expiry, which the chain fetch populates fully. The far
			// expiry's snapshot holds only the existing position's own leg (the chain endpoint returns the
			// front expiry in full and far expiries only on explicit request), so we can't read its ladder —
			// but SPY lists the same near-spot $1 strikes across expiries (the existing far long proves it),
			// so the front grid is a sound proxy. Far legs are BS-priced and the add is flagged "estimated".
			var grid = ListedStrikes(addShortExp, oppCp);
			var oppShortStrike = oppCp == "P"
				? grid.Where(s => s <= spot).DefaultIfEmpty(0m).Max()
				: grid.Where(s => s >= spot).DefaultIfEmpty(0m).Min();
			// Diagonal long = next grid strike further OTM than the short.
			var diagLongStrike = oppCp == "P"
				? grid.Where(s => s < oppShortStrike).DefaultIfEmpty(0m).Max()
				: grid.Where(s => s > oppShortStrike).DefaultIfEmpty(0m).Min();

			var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, addShortExp, oppShortStrike, oppCp);
			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, oppShortStrike, dteNewShort, ivNewShort, oppCp, dividends);
			var newShortAtOrigExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, origShortExp.Date + OptionMath.MarketClose, addShortExp.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), oppShortStrike, Math.Max(1, (addShortExp.Date - origShortExp.Date).Days) / 365.0, OptionMath.RiskFreeRate, ivNewShort, oppCp);

			// Build both add geometries, then flag the one with the best incremental EV per added margin.
			var pending = new List<(string variant, decimal longStrike, string newLongSym, decimal newLongMidExec, decimal cashPerShare, decimal newPositionValuePerShare, decimal evNewPerShare, decimal newMargin, bool estimated, decimal efficiency)>();
			foreach (var (variant, longStrike) in new[]
			{
				("double calendar", oppShortStrike),
				("double diagonal", diagLongStrike)
			})
			{
				if (oppShortStrike <= 0m || longStrike <= 0m) continue;
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, addLongExp, longStrike, oppCp);
				// Require the SHORT leg to be a listed front-expiry strike (avoids phantom near legs). The far
				// long leg's existence is inferred from the front grid; it's BS-priced when not live-quoted.
				if (quotes != null && !Listed(newShortSym)) continue;
				var ivNewLong = ResolveIV(newLongSym, settings, quotes);
				var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, longStrike, dteNewLong, ivNewLong, oppCp, dividends);
				var cashPerShare = newShortMidExec - newLongMidExec; // debit (negative) to open the new spread
				var newLongAtOrigExp = (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(spot, dividends, origShortExp.Date + OptionMath.MarketClose, addLongExp.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), longStrike, Math.Max(1, (addLongExp.Date - origShortExp.Date).Days) / 365.0, OptionMath.RiskFreeRate, ivNewLong, oppCp);
				var newPositionValuePerShare = newLongAtOrigExp - newShortAtOrigExp; // pinned (at current spot)
				// Probability-weighted EV of the NEW spread at the existing short expiry — the honest figure the
				// "incremental return" should be judged on, not the spot-pinned peak.
				var newLegsForEv = new List<(decimal, DateTime, decimal, bool, string)>
				{
					(longStrike, addLongExp, ivNewLong, true, oppCp),
					(oppShortStrike, addShortExp, ivNewShort, false, oppCp),
				};
				var evNewPerShare = ExpectedPositionValuePerShare(newLegsForEv, origShortExp, asOf, spot, ivNewShort, dividends);
				var newMargin = Math.Max(-cashPerShare, 0m) * 100m;           // long debit spread: margin = debit paid
				var estimated = !Quoted(newShortSym) || !Quoted(newLongSym);
				var efficiency = newMargin > 0m ? evNewPerShare * 100m / newMargin : 0m; // EV return per added $ of risk
				pending.Add((variant, longStrike, newLongSym, newLongMidExec, cashPerShare, newPositionValuePerShare, evNewPerShare, newMargin, estimated, efficiency));
			}

			var bestEfficiency = pending.Count > 0 ? pending.Max(x => x.efficiency) : 0m;
			foreach (var a in pending)
			{
				var isBest = pending.Count > 1 && a.efficiency == bestEfficiency && a.efficiency > 0m;
				var est = a.estimated ? " [estimated]" : "";
				var strikeDesc = a.variant == "double calendar" ? $"${oppShortStrike:F2}" : $"${oppShortStrike:F2}/${a.longStrike:F2}";
				var effPct = a.newMargin > 0m ? $"{a.efficiency * 100m:F0}% on added ${a.newMargin:N0}" : "n/a";
				var bestNote = isBest ? " — best add (highest EV return on added capital)" : "";
				EmitAdd(list, legs, settings.Cash,
					name: $"Add {sideLabel} {strikeDesc} {addShortExp:MM-dd}/{addLongExp:MM-dd} ({a.variant}, keep existing){est}",
					newShortSym: newShortSym,
					newLongSym: a.newLongSym,
					newShortPrice: newShortMidExec,
					newLongPrice: a.newLongMidExec,
					cashPerShareOfChange: a.cashPerShare,
					newProjectedPerShare: a.newPositionValuePerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					marginPerContract: a.newMargin,
					daysToTarget: origShortDte,
					rationale: $"add a {sideLabel} {a.variant.Split(' ')[1]} at {strikeDesc} (debit ${-a.cashPerShare:F2}/share, +2 legs of slippage){(a.estimated ? ", BS-estimated (leg not live-quoted)" : "")}; new spread EV ${a.evNewPerShare:F2}/share (pinned ${a.newPositionValuePerShare:F2}); incremental EV return {effPct}{bestNote}",
					expectedUnchangedPerShare: holdEvPerShare,
					expectedNewPerShare: a.evNewPerShare);
			}

			// Comparator: scale up the EXISTING structure instead of adding an opposite side. Same payoff shape,
			// so per-contract EV equals the hold's — this is the "just add more contracts" baseline the doubles
			// must beat on EV-per-added-dollar. Opening another copy costs the current net debit as margin.
			var scaleUpDebit = longMidNow - shortMidNow;   // >0 = debit to open one more existing calendar/diagonal
			if (scaleUpDebit > 0m)
			{
				var scaleUpMargin = scaleUpDebit * 100m;
				var scaleUpEff = scaleUpMargin > 0m ? holdEvPerShare * 100m / scaleUpMargin : 0m;
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
					rationale: $"open one more of the SAME structure at current mid (debit ${scaleUpDebit:F2}/share); same payoff → new spread EV ${holdEvPerShare:F2}/share; incremental EV return {(scaleUpMargin > 0m ? $"{scaleUpEff * 100m:F0}% on added ${scaleUpMargin:N0}" : "n/a")} — baseline the doubles must beat",
					expectedUnchangedPerShare: holdEvPerShare,
					expectedNewPerShare: holdEvPerShare);
			}
		}

		return OrderScenariosForDisplay(list, settings.Cash, settings.Risk);
	}

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
		decimal? expectedUnchangedPerShare = null,
		decimal? expectedNewPerShare = null)
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
				ExpectedPnLPerContract: fullEvPnL));
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
			Rationale: $"add {maxPartial} new contract(s) (${marginPerContract * maxPartial:N0} margin; full size would need ${fullMarginTotal:N0}); keep all {fullQty} existing",
			ExpectedPnLPerContract: partialEvPnL));
	}

	/// <summary>Appends a full-quantity scenario to the list. If the full margin delta exceeds available
	/// cash AND there's a positive max-fundable partial quantity, also appends a partial variant.
	/// In the partial, the unchanged portion is valued at its natural terminal date (the hold projection),
	/// so the mix doesn't double-count time decay. Pass isRoll:true when the scenario closes an existing
	/// leg and opens a new one — BuildReproductionCommands uses the flag to decide whether to split the
	/// emitted `wa trade place` command into two single-leg orders for non-calendar rolls.</summary>
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
		decimal peakCashPerContract = 0m)
	{
		var fullQty = legs[0].Qty;
		var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var initialDebitPerContract = initialDebitPerShare * 100m;
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
				PeakCashPerContract: peakCashPerContract));
		}

		// Partial variant: only emit if the change consumes BP and cash is constrained below full.
		if (!availableCash.HasValue || fundingPerContract <= 0m) return;
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		// Per-contract-of-total values for the partial mix.
		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialProjectedTotal = newProjectedPerShare * 100m * maxPartial + unchangedProjectedPerShare * 100m * (fullQty - maxPartial);
		var partialTotalPnL = partialCashTotal + partialProjectedTotal - initialDebitPerContract * fullQty;
		list.Add(new Scenario(
			$"{name} · partial {maxPartial}/{fullQty}",
			actionSummary.Replace("{qty}", maxPartial.ToString()),
			CashImpactPerContract: partialCashTotal / fullQty,
			ProjectedValuePerContract: partialProjectedTotal / fullQty,
			TotalPnLPerContract: partialTotalPnL / fullQty,
			MarginDeltaPerContract: marginPerContract * maxPartial / fullQty,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: $"execute on {maxPartial} contracts (${fundingPerContract * maxPartial:N0} BP; full size would need ${fullFundingTotal:N0}); hold remaining {fullQty - maxPartial} as original → ${unchangedProjectedPerShare:F2}/share at original exp",
			IsRoll: isRoll,
			PeakCashPerContract: peakCashPerContract * maxPartial / fullQty));
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static Scenario NewScenario(string name, PositionSnapshot longLeg, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal marginDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false)
	{
		var initialDebit = longLeg.CostBasis;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, marginDeltaPerContract, longLeg.Qty, daysToTarget, rationale, isRoll, RankScore: totalPerContract / Math.Max(1m, daysToTarget));
	}

	private static Scenario NewScenarioSpread(string name, IReadOnlyList<PositionSnapshot> legs, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal marginDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false, decimal? expectedValueAtTarget = null)
	{
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var qty = legs[0].Qty;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		decimal? evPnL = expectedValueAtTarget.HasValue ? expectedValueAtTarget.Value * 100m + cashPerContract - initialDebit * 100m : null;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, marginDeltaPerContract, qty, daysToTarget, rationale, isRoll, RankScore: (evPnL ?? totalPerContract) / Math.Max(1m, daysToTarget), ExpectedPnLPerContract: evPnL);
	}

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

	private static List<Scenario> OrderScenariosForDisplay(IEnumerable<Scenario> scenarios, decimal? availableCash, decimal riskAversion) => scenarios
		.OrderByDescending(s => IsScenarioFundable(s, availableCash))
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

	/// <summary>Returns live mid from quotes if both bid and ask are populated; otherwise a BS theoretical mid.</summary>
	internal static decimal LiveOrBsMid(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal spot, decimal strike, int dte, decimal iv, string callPut, IReadOnlyList<DividendEvent>? dividends = null)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value + q.Ask.Value) / 2m;
		var expiryInstant = EvaluationDate.Today.Date.AddDays(dte) + OptionMath.MarketClose;
		var adjSpot = OptionMath.DividendAdjustedSpot(spot, dividends, EvaluationDate.Today, expiryInstant, OptionMath.RiskFreeRate);
		return (decimal)OptionMath.BlackScholes(adjSpot, strike, dte / 365.0, OptionMath.RiskFreeRate, iv, callPut);
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

	private static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}

	private static DateTime NextWeeklyFromToday()
	{
		var d = EvaluationDate.Today.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}

	private static decimal Intrinsic(decimal spot, decimal strike, string callPut) =>
		callPut == "C" ? Math.Max(0m, spot - strike) : Math.Max(0m, strike - spot);

	/// <summary>Probability-weighted expected per-share value of a multi-leg position at <paramref name="target"/>,
	/// integrated over a log-normal spot distribution (same 5-point grid the opener's scorer uses). Legs that
	/// expire at or before the target settle to intrinsic; legs that survive are Black-Scholes-valued on their
	/// residual time. This is the honest counterpart to the spot-pinned projection shown elsewhere — at the pin
	/// a near-ATM calendar shows its peak, but most of the lognormal mass lands off-peak. <paramref name="gridIv"/>
	/// is the vol used for the spot distribution over [asOf, target] (the near-leg IV is the right horizon).</summary>
	private static decimal ExpectedPositionValuePerShare(
		IReadOnlyList<(decimal strike, DateTime expiry, decimal iv, bool isLong, string cp)> legs,
		DateTime target, DateTime asOf, decimal spot, decimal gridIv, IReadOnlyList<DividendEvent>? dividends = null)
	{
		var yearsToTarget = Math.Max(1, (target.Date - asOf.Date).Days) / 365.0;
		var grid = WebullAnalytics.AI.CandidateScorer.BuildScenarioGrid(spot, gridIv, yearsToTarget);
		decimal ev = 0m;
		foreach (var pt in grid)
		{
			decimal value = 0m;
			foreach (var (strike, expiry, iv, isLong, cp) in legs)
			{
				var legValue = expiry.Date <= target.Date
					? Intrinsic(pt.SpotAtExpiry, strike, cp)
					: (decimal)OptionMath.BlackScholes(OptionMath.DividendAdjustedSpot(pt.SpotAtExpiry, dividends, target.Date + OptionMath.MarketClose, expiry.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate), strike, Math.Max(1, (expiry.Date - target.Date).Days) / 365.0, OptionMath.RiskFreeRate, iv, cp);
				value += (isLong ? 1m : -1m) * legValue;
			}
			ev += pt.Weight * value;
		}
		return ev;
	}

	private static decimal NearestStrike(decimal spot, decimal step) =>
		Math.Round(spot / step) * step;

	/// <summary>Returns the two strikes bracketing spot on the configured step grid. When spot lands
	/// exactly on a strike, the same value is yielded once.</summary>
	private static IEnumerable<decimal> BracketStrikes(decimal spot, decimal step)
	{
		var below = Math.Floor(spot / step) * step;
		var above = Math.Ceiling(spot / step) * step;
		yield return below;
		if (above != below) yield return above;
	}

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
				if (p == null || p.CallPut != cp || p.Strike != strike || p.ExpiryDate.Date != expiry || !string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase)) continue;
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

	private static void RenderScenarioTable(IReadOnlyList<Scenario> scenarios, AnalyzePositionSettings settings, IAnsiConsole console, bool ascii)
	{
		var availableCash = settings.Cash;

		// Identify the first fundable scenario so we can highlight it (green). The top-ranked
		// scenario may be unfundable — the actionable recommendation is the best fundable one.
		Scenario? topFundable = null;
		foreach (var sc in scenarios)
		{
			if (IsScenarioFundable(sc, availableCash)) { topFundable = sc; break; }
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

			var rationaleText = WebullAnalytics.IO.TextFileExporter.NormalizeArrows(ascii, sc.Rationale);
			var nameText = WebullAnalytics.IO.TextFileExporter.NormalizeArrows(ascii, sc.Name);

			var lines = new List<IRenderable>
			{
				new Markup($"[dim]Cash {Markup.Escape(cashStr)}  │  Projected {Markup.Escape(projStr)}{Markup.Escape(evStr)}  │  P&L {Markup.Escape(totalStr)}  │  {marginMarkup}[/]"),
				new Markup($"[dim]{Markup.Escape(rationaleText)}[/]"),
			};
			var leadIn = WebullAnalytics.IO.TextFileExporter.ReproductionLeadIn(ascii);
			var (tradeCmds, analyzeCmd) = BuildReproductionCommands(sc, settings);
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
	internal static (IReadOnlyList<string>? Trades, string? Analyze) BuildReproductionCommands(Scenario sc, AnalyzePositionSettings settings)
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

		// Analyze-trade line mirrors the executable broker order grouping: unsupported roll reversals
		// must be separated with ';' so the synthetic replay doesn't force them into one combo event.
		var analyzeGroups = new List<string>();
		var analyzeLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}").ToList();
		var splittable = sc.IsRoll && legs.Count == 2 && !RollShape.IsSameStrikeCalendar(legs.Select(l => l.Symbol));
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
		var extras = new List<string>();
		if (!string.IsNullOrEmpty(settings.Spot)) extras.Add($"--spot {settings.Spot}");
		if (!string.IsNullOrEmpty(settings.Date)) extras.Add($"--date {settings.Date}");
		var suffix = extras.Count > 0 ? " " + string.Join(" ", extras) : "";
		var analyze = $"wa analyze trade \"{string.Join(";", analyzeGroups)}\"{suffix}";

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
				return $"wa trade place --trade \"{l.Action}:{l.Symbol}:{l.Qty}\" --limit {legLimit}";
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
		return $"wa trade place --trade \"{halfArg}\" --limit {halfLimit}";
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
		var probe = RiskDiagnosticProbeBuilder.Build(diagLegs, spot, asOf, ivResolver, quotesForProbe, opener: null, technicalBiasOverride: technicalBiasForProbe, useCostBasisForOpenerScore: true, historicalVolAnnual: historicalVolAnnual, useMarketImpliedIv: useMarketImpliedIv, sentimentScore: sentiment?.Score, events: events);
		diagnostic = diagnostic with { Probe = probe };

		Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		using (var writer = new StreamWriter(File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
		{
			writer.AutoFlush = true;
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
			writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(record));
		}

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
				impliedVolatility5Day = q.ImpliedVolatility5Day,
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

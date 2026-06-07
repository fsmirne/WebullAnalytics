using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using WebullAnalytics.AI.Output;
using WebullAnalytics.IO;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Report;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal abstract class AISubcommandSettings : CommandSettings
{
	private static readonly string[] ValidLogLevels = ["debug", "information", "error"];
	private static readonly string[] ValidProposalModes = ["all", "open", "management"];
	private static readonly string[] ValidPricingModes = [SuggestionPricing.Mid, SuggestionPricing.BidAsk];

	[CommandOption("--strategy <TOKEN>")]
	[Description("Strategy layer to load: ai-config.<TICKER>.<STRATEGY>.json. Overrides the config's defaultStrategy. Required when no defaultStrategy is set.")]
	public string? Strategy { get; set; }

	[CommandOption("--output <FORMAT>")]
	[Description("Output format: console or text. Default: console.")]
	public string Output { get; set; } = "console";

	[CommandOption("--output-path <PATH>")]
	[Description("Path for --output text.")]
	public string? OutputPath { get; set; }

	[CommandOption("--log-level <LEVEL>")]
	[Description("debug | information | error. Overrides config.")]
	public string? LogLevel { get; set; }

	[CommandOption("--proposals <MODE>")]
	[Description("Which proposal sets to emit: all | open | management. Default: all.")]
	public string Proposals { get; set; } = "all";

	[CommandOption("--pricing <MODE>")]
	[Description("Price basis for AI suggestion output and pricing math: mid | bidask. Default: mid.")]
	public string Pricing { get; set; } = SuggestionPricing.Mid;

	public override ValidationResult Validate()
	{
		if (Output != "console" && Output != "text") return ValidationResult.Error($"--output: must be 'console' or 'text', got '{Output}'");
		if (LogLevel != null && !ValidLogLevels.Contains(LogLevel, StringComparer.OrdinalIgnoreCase))
			return ValidationResult.Error($"--log-level: must be debug|information|error, got '{LogLevel}'");
		if (!ValidProposalModes.Contains(Proposals, StringComparer.OrdinalIgnoreCase))
			return ValidationResult.Error($"--proposals: must be all|open|management, got '{Proposals}'");
		if (!ValidPricingModes.Contains(Pricing, StringComparer.OrdinalIgnoreCase))
			return ValidationResult.Error($"--pricing: must be mid|bidask, got '{Pricing}'");
		return ValidationResult.Success();
	}

	internal bool UseTextOutput => string.Equals(Output, "text", StringComparison.OrdinalIgnoreCase);

	internal string ResolveTextOutputPath(string stem, DateTime? now = null)
	{
		var dateStr = (now ?? DateTime.Now).ToString("yyyyMMdd");
		var path = OutputPath ?? $"WebullAnalytics_{stem}_{dateStr}.txt";
		return Path.GetFullPath(path);
	}

	internal bool EmitManagementProposals => !string.Equals(Proposals, "open", StringComparison.OrdinalIgnoreCase);
	internal bool EmitOpenProposals => !string.Equals(Proposals, "management", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Base for AI subcommands that operate on a single ticker (scan, watch, replay, backtest).
/// The ticker is the first positional argument; the loader merges <c>ai-config.json</c> with the
/// per-ticker override file <c>ai-config.<TICKER>.json</c> if it exists.</summary>
internal abstract class AISingleTickerSubcommandSettings : AISubcommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Ticker to scan. Loads ai-config.json + ai-config.<TICKER>.json (deep-merged); the per-ticker file holds overrides and may be absent.")]
	public string Ticker { get; set; } = "";
}

internal static class AITextOutput
{
	internal static async Task<int> RunAsync(AISubcommandSettings settings, string stem, Func<Task<int>> action)
	{
		if (!settings.UseTextOutput)
			return await action();

		var originalOut = Console.Out;
		var originalErr = Console.Error;
		using var stringWriter = new StringWriter();
		using var stderrWriter = new StringWriter();
		var console = TextFileExporter.CreateTextConsole(stringWriter);
		AnsiConsole.Console = console;
		Console.SetOut(stringWriter);
		Console.SetError(stderrWriter);
		int exitCode;
		try
		{
			exitCode = await action();
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalErr);
			AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings());
		}

		if (stderrWriter.GetStringBuilder().Length > 0)
			originalErr.Write(stderrWriter.ToString());
		var outputPath = settings.ResolveTextOutputPath(stem);
		TextFileExporter.WriteConsoleOutputToTextFile(stringWriter, outputPath, "Text AI output exported to");
		return exitCode;
	}
}

internal static class AIContext
{
	/// <summary>Loads the merged ai-config for a run: base <c>ai-config.json</c> ⊕ per-ticker
	/// <c>ai-config.<TICKER>.json</c> ⊕ per-strategy <c>ai-config.<TICKER>.<STRATEGY>.json</c>,
	/// deep-merged most-specific-wins. Strategy is <c>--strategy</c>, else the base+ticker
	/// <c>defaultStrategy</c>; with neither set this is an error (no fallback). Returns null on failure.</summary>
	internal static AIConfig? ResolveConfig(AISubcommandSettings settings)
	{
		var layers = ResolveLayers(settings, out var ticker, out var strategy);
		if (layers == null) return null;

		var config = AIConfigMerge.LoadMerged(layers.Select(l => l.AbsPath).ToArray());
		if (config == null) { Console.Error.WriteLine("Error: ai-config is empty or unparseable."); return null; }

		config.Ticker = ticker;
		config.Strategy = strategy;
		if (!string.IsNullOrWhiteSpace(settings.LogLevel)) config.LogLevel = settings.LogLevel;
		config.Opener.Indicators = config.Indicators;        // shared indicators reachable from cfg-only helpers

		var err = AIConfigLoader.Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config: {err}"); return null; }

		return config;
	}

	/// <summary>One labeled config layer: the human label ("base", "SPY", "SPY.DC") and its absolute path.</summary>
	internal readonly record struct ConfigLayer(string Label, string AbsPath);

	/// <summary>Resolves the ordered list of existing config-layer files for a run (base → ticker → strategy),
	/// the same way <see cref="ResolveConfig"/> loads them — so the inspector reflects exactly what runs.
	/// Returns null (with a stderr message) when the ticker, a base/ticker file, or the strategy is missing.</summary>
	internal static List<ConfigLayer>? ResolveLayers(AISubcommandSettings settings, out string ticker, out string strategy)
	{
		ticker = ""; strategy = "";
		var baseRel = AIConfigLoader.ConfigPath;                  // data/ai-config.json
		var dir = Path.GetDirectoryName(baseRel) ?? string.Empty;
		var stem = Path.GetFileNameWithoutExtension(baseRel);     // ai-config
		var ext = Path.GetExtension(baseRel);                     // .json

		if (settings is not AISingleTickerSubcommandSettings single || string.IsNullOrWhiteSpace(single.Ticker))
		{
			Console.Error.WriteLine("Error: a ticker is required.");
			return null;
		}
		ticker = single.Ticker.Trim().ToUpperInvariant();

		var absBase = Program.ResolvePath(baseRel);
		var tickerRel = Path.Combine(dir, $"{stem}.{ticker}{ext}");
		var absTicker = Program.ResolvePath(tickerRel);
		if (!File.Exists(absBase) && !File.Exists(absTicker))
		{
			Console.Error.WriteLine($"Error: no ai-config found at '{baseRel}' or '{tickerRel}'.");
			Console.Error.WriteLine($"  Run: cp ai-config.example.json {baseRel} and edit.");
			return null;
		}

		// Layers base ⊕ ticker resolve the strategy via defaultStrategy.
		var core = AIConfigMerge.LoadMerged(File.Exists(absBase) ? absBase : null, File.Exists(absTicker) ? absTicker : null);
		if (core == null) { Console.Error.WriteLine("Error: ai-config is empty or unparseable."); return null; }

		strategy = (!string.IsNullOrWhiteSpace(settings.Strategy) ? settings.Strategy! : core.DefaultStrategy).Trim();
		if (string.IsNullOrWhiteSpace(strategy))
		{
			Console.Error.WriteLine($"Error: no strategy selected for {ticker}. Pass --strategy <TOKEN> or set \"defaultStrategy\" in {tickerRel} (or {baseRel}).");
			return null;
		}

		// Layer 3 (per-strategy) must exist — no fallback.
		var stratRel = Path.Combine(dir, $"{stem}.{ticker}.{strategy}{ext}");
		var absStrat = Program.ResolvePath(stratRel);
		if (!File.Exists(absStrat))
		{
			Console.Error.WriteLine($"Error: strategy '{strategy}' for {ticker} not found at '{stratRel}'.");
			return null;
		}

		var layers = new List<ConfigLayer>();
		if (File.Exists(absBase)) layers.Add(new ConfigLayer("base", absBase));
		if (File.Exists(absTicker)) layers.Add(new ConfigLayer(ticker, absTicker));
		layers.Add(new ConfigLayer($"{ticker}.{strategy}", absStrat));
		return layers;
	}

	internal static IPositionSource BuildLivePositionSource(AIConfig config, string? accountOverride = null)
	{
		var account = ResolveTradeAccount(config, accountOverride);
		// Load local trade history so PositionReplay can supply roll-adjusted cost basis to rule
		// evaluators. Webull's holdings endpoint reports current-leg cost only and ignores roll
		// credits/debits, so without this enrichment a stop-loss check on a rolled position uses
		// the wrong break-even (see `wa report` for the authoritative adjusted basis).
		var (trades, feeLookup, err) = ReportCommand.LoadTrades(new ReportSettings());
		if (err != 0)
		{
			Console.Error.WriteLine("[warn] ai: trade history unavailable; rules will use broker-reported cost basis only (rolls won't be reflected).");
			return new LivePositionSource(account);
		}
		return new LivePositionSource(account, trades, feeLookup);
	}

	internal static IQuoteSource BuildLiveQuoteSource(AIConfig config) => new LiveQuoteSource();

	/// <summary>Resolves the broker account from api-config.json. <paramref name="accountOverride"/>
	/// mirrors `wa trade place --account`: when non-null, it wins; when null, falls back to
	/// `api-config.defaultAccount`. Single source of truth for the AI pipeline — used by both the
	/// live position source (read) and the auto-executors (write), so the account that the AI evaluates
	/// against is always the same account it trades into.</summary>
	internal static TradeAccount ResolveTradeAccount(AIConfig config, string? accountOverride = null)
	{
		var tradeConfig = TradeConfig.Load() ?? throw new InvalidOperationException("api-config.json with accounts[] required for live ai");
		var resolved = TradeConfig.Resolve(tradeConfig, accountOverride);
		if (resolved == null)
		{
			var key = accountOverride ?? tradeConfig.Webull.DefaultAccount ?? "<unset>";
			throw new InvalidOperationException($"account '{key}' not found in api-config.json");
		}
		return resolved;
	}

	/// <summary>Resolves the trade account once and constructs the two auto-executors gated by
	/// <c>autoExecute.management.enabled</c> / <c>autoExecute.opener.enabled</c>. Used by both the
	/// <c>wa ai watch</c> loop (every tick) and the <c>wa ai scan</c> one-shot. Either executor may
	/// be null when its <c>enabled</c> flag is off; when account resolution fails (e.g. api-config
	/// missing) both executors are still instantiated but with a null account, so they degrade to
	/// dry-run logging — same fallback the watch loop has always used.</summary>
	internal static (ManagementAutoExecutor? Management, OpenerAutoExecutor? Opener) BuildAutoExecutors(AIConfig config, string? accountOverride = null)
	{
		TradeAccount? account = null;
		if (config.AutoExecute.Management.Enabled || config.AutoExecute.Opener.Enabled)
		{
			try { account = ResolveTradeAccount(config, accountOverride); }
			catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]auto-execute disabled (account resolution failed): {Markup.Escape(ex.Message)}[/]"); }
		}
		var brokerState = account != null ? new BrokerStateService(account) : null;
		var mgmt = config.AutoExecute.Management.Enabled ? new ManagementAutoExecutor(config.AutoExecute.Management, account, brokerState) : null;
		var opener = config.AutoExecute.Opener.Enabled ? new OpenerAutoExecutor(config.AutoExecute.Opener, account, brokerState) : null;
		return (mgmt, opener);
	}
}

/// <summary>`ai scan` — one evaluation pass, print proposals, exit.</summary>
internal sealed class AIScanSettings : AISingleTickerSubcommandSettings
{
	[CommandOption("--top <N>")]
	[Description("Override opener.topNPerTicker from ai-config.json.")]
	public int? Top { get; set; }

	[CommandOption("--theoretical")]
	[Description("Bypass the live chain and price via Black-Scholes against an explicit spot. Use for pre-market / weekend planning. Requires --spot; --date defaults to the next business day.")]
	public bool Theoretical { get; set; }

	[CommandOption("--premarket")]
	[Description("Live chain but underlying spot is back-solved from put-call parity on the ATM straddle. Use when the chain is active but the underlying quote hasn't ticked yet (e.g., SPXW before 09:30 ET). Per-ticker --spot overrides win.")]
	public bool Premarket { get; set; }

	[CommandOption("--date <DATE>")]
	[Description("With --theoretical: asOf date (YYYY-MM-DD). Defaults to the next business day.")]
	public string? Date { get; set; }

	[CommandOption("--spot <SPEC>")]
	[Description("Underlying spot override(s). Format: TICKER:PRICE (e.g., SPXW:7450). Comma-separated for multiple tickers. Required with --theoretical; optional in live mode (overrides the chain's reported spot).")]
	public string? Spot { get; set; }

	[CommandOption("--starting-cash <AMOUNT>")]
	[Description("With --theoretical: override account balance for sizing. Default: pulled from your live broker account.")]
	public decimal? StartingCash { get; set; }

	[CommandOption("--account <ALIAS>")]
	[Description("Account alias or ID from api-config.json. Mirrors `wa trade place --account`: overrides defaultAccount for this run. Affects both the live-position read and any auto-executed orders.")]
	public string? Account { get; set; }

	[CommandOption("--submit")]
	[Description("Override autoExecute.{management,opener}.submit=true for this run. Mirrors `wa trade place --submit`: keep config safe at dry-run, flip live from the CLI when ready.")]
	public bool Submit { get; set; }

	[CommandOption("--tif <VALUE>")]
	[Description("Override autoExecute.{management,opener}.timeInForce for this run. Mirrors `wa trade place --tif`: DAY (in-session only) or GTC (queues across sessions, accepted off-hours). Default: whatever config says, which itself defaults to DAY.")]
	public string? Tif { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (Top.HasValue && Top.Value < 1) return ValidationResult.Error($"--top: must be ≥ 1, got {Top.Value}");
		if (Tif != null && !string.Equals(Tif, "day", StringComparison.OrdinalIgnoreCase) && !string.Equals(Tif, "gtc", StringComparison.OrdinalIgnoreCase))
			return ValidationResult.Error($"--tif: must be 'day' or 'gtc', got '{Tif}'");

		if (Spot != null)
		{
			foreach (var pair in Spot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2 || !decimal.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0m)
					return ValidationResult.Error($"--spot: invalid entry '{pair}'. Expected TICKER:PRICE with PRICE > 0.");
			}
		}

		if (Theoretical)
		{
			if (Premarket) return ValidationResult.Error("--premarket and --theoretical are mutually exclusive (theoretical needs no chain; premarket back-solves spot from one).");
			if (string.IsNullOrWhiteSpace(Spot))
				return ValidationResult.Error("--theoretical requires --spot TICKER:PRICE (no live chain to fall back to).");
			if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
				return ValidationResult.Error($"--date: expected YYYY-MM-DD, got '{Date}'");
			if (StartingCash.HasValue && StartingCash.Value <= 0m)
				return ValidationResult.Error($"--starting-cash: must be > 0, got {StartingCash.Value}");
		}
		else
		{
			if (Date != null) return ValidationResult.Error("--date only applies with --theoretical.");
			if (StartingCash.HasValue) return ValidationResult.Error("--starting-cash only applies with --theoretical.");
		}

		return ValidationResult.Success();
	}

	internal Dictionary<string, decimal> ParseSpotOverrides()
	{
		var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(Spot)) return result;
		foreach (var pair in Spot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = pair.Split(':', 2);
			result[parts[0].Trim()] = decimal.Parse(parts[1].Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);
		}
		return result;
	}

	internal DateTime ResolveAsOf()
	{
		var date = Date != null
			? DateTime.ParseExact(Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Date
			: NextBusinessDay(DateTime.Today);
		// Stamp at 09:30 ET to match the backtest's per-step convention (the first RTH minute under
		// our normalized start-of-bar convention; see BacktestRunner.MarketOpenTime). HV/VIX lookups
		// only use .Date so the time component is cosmetic for those, but option-chart cache lookups
		// walk forward from this minute, and starting at 09:30 picks up the auction-cleared open
		// print directly.
		return date.Add(new TimeSpan(9, 30, 0));
	}

	private static DateTime NextBusinessDay(DateTime today)
	{
		var d = today.AddDays(1);
		while (!MarketCalendar.IsOpen(d)) d = d.AddDays(1);
		return d;
	}
}

internal sealed class AIScanCommand : AsyncCommand<AIScanSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIScanSettings settings, CancellationToken cancellation)
		=> await AITextOutput.RunAsync(settings, "AIScan", async () =>
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;
		if (settings.Top.HasValue) config.Opener.TopNPerTicker = settings.Top.Value;
		if (settings.Submit) { config.AutoExecute.Management.Submit = true; config.AutoExecute.Opener.Submit = true; }
		if (settings.Tif != null) { config.AutoExecute.Management.TimeInForce = settings.Tif.ToUpperInvariant(); config.AutoExecute.Opener.TimeInForce = settings.Tif.ToUpperInvariant(); }

		if (string.Equals(config.LogLevel, "debug", StringComparison.OrdinalIgnoreCase))
			Console.Error.WriteLine($"[debug] wa ai scan: log-level=debug baseDir='{Program.BaseDir}' ticker={config.Ticker} proposals={settings.Proposals} theoretical={settings.Theoretical} submit={settings.Submit}");

		TerminalHelper.EnsureTerminalWidthFromConfig();

		return settings.Theoretical
			? await RunTheoreticalAsync(settings, config, cancellation)
			: await RunLiveAsync(settings, config, cancellation);
	});

	private static async Task<int> RunLiveAsync(AIScanSettings settings, AIConfig config, CancellationToken cancellation)
	{
		var positions = AIContext.BuildLivePositionSource(config, settings.Account);
		var quotes = AIContext.BuildLiveQuoteSource(config);

		var tickerSet = config.TickerSet();
		var now = DateTime.Now;

		var priceCache = new Replay.HistoricalPriceCache();

		var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
		var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);

		var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, quotes, config, cancellation);

		// Spot overrides: --spot pins explicit values (premarket SPXW etc.); --premarket back-solves from
		// put-call parity on the ATM straddle for any ticker without an explicit pin. The underlying quote
		// from the chain is replaced before the EvaluationContext is built so the opener scores against the
		// caller's intended spot. Note: phase-2 hypothetical strike enumeration in FetchQuotesWithHypotheticals
		// runs against the broker's reported spot — for managed-position scenarios with a wildly stale
		// underlying, enumerated strike brackets may be slightly off-target, but proposal pricing/scoring
		// still uses the overridden spot.
		quoteSnapshot = await ApplySpotOverridesAsync(settings, config, quoteSnapshot, quotes, now, cancellation);
		if (quoteSnapshot == null) return 1;

		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
			tickerSet, priceCache, config.Indicators.TechnicalFilter, now, cancellation);

		var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config, settings.Pricing), config);
		var (mgmtExecutor, openerExecutor) = AIContext.BuildAutoExecutors(config, settings.Account);

		var results = evaluator.Evaluate(ctx);
		var managementCount = settings.EmitManagementProposals ? results.Count : 0;
		if (settings.EmitManagementProposals)
		{
			using var sink = new ProposalSink(config.LogLevel, config.Ticker, config.Strategy, mode: "scan", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			foreach (var r in results) sink.Emit(r.Proposal, r.IsRepeat);
		}
		if (mgmtExecutor != null)
			await mgmtExecutor.HandleAsync(results, ctx, cancellation);

		var openCount = 0;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			var openSink = new OpenProposalSink(config.LogLevel, config.Ticker, config.Strategy, mode: "scan", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			var openEvaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache, enableChainSnapshot: true);
			var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
			for (var i = 0; i < openResults.Count; i++) openSink.Emit(openResults[i], rank: i + 1);
			openCount = openResults.Count;
			if (openerExecutor != null)
				await openerExecutor.HandleAsync(openResults, openPositions, now, cancellation);
		}

		AnsiConsole.MarkupLine($"[dim]Tick complete: {openPositions.Count} position(s), {managementCount} mgmt proposal(s), {openCount} open proposal(s) emitted[/]");
		return 0;
	}

	/// <summary>Applies --spot and --premarket overrides to the live quote snapshot's Underlyings map.
	/// --spot wins per ticker; --premarket back-solves remaining tickers via put-call parity on the nearest-expiry
	/// ATM straddle in the fetched chain. Returns the merged snapshot, or null if --premarket failed to derive
	/// a spot for a configured ticker (no viable straddle in the chain — usually a chain-fetch problem).
	/// When --premarket runs and the snapshot has no contracts for a configured ticker (no open positions, so
	/// FetchQuotesWithHypotheticals short-circuited), this method bootstraps a chain fetch using one placeholder
	/// OCC symbol per ticker — same trick OpenCandidateEvaluator uses — so parity has data to work with.</summary>
	private static async Task<QuoteSnapshot?> ApplySpotOverridesAsync(AIScanSettings settings, AIConfig config, QuoteSnapshot snapshot, IQuoteSource quotes, DateTime now, CancellationToken cancellation)
	{
		var explicitOverrides = settings.ParseSpotOverrides();
		if (explicitOverrides.Count == 0 && !settings.Premarket) return snapshot;

		var merged = new Dictionary<string, decimal>(snapshot.Underlyings, StringComparer.OrdinalIgnoreCase);
		var options = snapshot.Options;
		var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var (ticker, spot) in explicitOverrides)
		{
			merged[ticker] = spot;
			sources[ticker] = "--spot";
		}

		if (settings.Premarket)
		{
			var debug = string.Equals(config.LogLevel, "debug", StringComparison.OrdinalIgnoreCase);
			var needsParity = explicitOverrides.ContainsKey(config.Ticker) ? new List<string>() : new List<string> { config.Ticker };
			if (needsParity.Count > 0)
			{
				// Ensure the snapshot has contracts for every ticker we need parity on. Webull's live source
				// returns the full chain for any single OCC symbol, so one placeholder per ticker suffices.
				var tickersWithoutContracts = needsParity.Where(t => !options.Any(kv => ParsingHelpers.ParseOptionSymbol(kv.Key) is { } p && string.Equals(p.Root, t, StringComparison.OrdinalIgnoreCase))).ToList();
				if (tickersWithoutContracts.Count > 0)
				{
					var placeholders = new HashSet<string>(tickersWithoutContracts.Select(t => MatchKeys.OccSymbol(t, now.Date.AddDays(7), 1m, "C")), StringComparer.OrdinalIgnoreCase);
					var tickerSet = config.TickerSet();
					if (debug) Console.Error.WriteLine($"[debug] --premarket: bootstrap-fetching chain for {string.Join(",", tickersWithoutContracts)}.");
					var boot = await quotes.GetQuotesAsync(now, placeholders, tickerSet, cancellation);
					if (boot.Options.Count > 0)
					{
						var combined = new Dictionary<string, OptionContractQuote>(options, StringComparer.OrdinalIgnoreCase);
						foreach (var (k, v) in boot.Options) combined[k] = v;
						options = combined;
					}
				}
			}

			foreach (var ticker in needsParity)
			{
				var diagLines = new List<string>();
				var derived = DeriveSpotFromParity(ticker, options, riskFreeRate: 0.036, now, diag: line => diagLines.Add(line));
				if (derived == null)
				{
					Console.Error.WriteLine($"Error: --premarket: could not back-solve spot for {ticker} (no expiry had a strike with both call+put bid/ask or last-price).");
					foreach (var l in diagLines) Console.Error.WriteLine(l);
					Console.Error.WriteLine($"  Pass --spot {ticker}:PRICE to bypass.");
					return null;
				}
				if (debug) foreach (var l in diagLines) Console.Error.WriteLine($"[debug] {l}");
				merged[ticker] = derived.Value.Spot;
				sources[ticker] = $"parity K={derived.Value.AtmStrike:N0}, {derived.Value.Dte}d";
			}
		}

		var banner = string.Join(", ", sources.Keys
			.OrderBy(k => k)
			.Select(k => $"{Markup.Escape(k)} @ ${merged[k]:N2} ({sources[k]})"));
		if (banner.Length > 0) AnsiConsole.MarkupLine($"[bold yellow]Spot overrides:[/] {banner}");

		return new QuoteSnapshot(options, merged);
	}

	internal readonly record struct ParityResult(decimal Spot, decimal AtmStrike, int Dte);

	/// <summary>Back-solves the underlying spot from put-call parity on the ATM straddle in the fetched
	/// option chain. Returns null if no expiry has a strike with both a call and put quote.
	/// Parity (European, no dividends — exact for cash-settled SPX/SPXW/NDX/XSP/RUT): S = (C - P) + K * exp(-r*T).
	/// Picks the nearest non-negative DTE expiry, then the strike where |C_mid - P_mid| is minimum (ATM).
	/// Quote preference per leg: bid+ask mid (bid≥0, ask>0, ask≥bid), else LastPrice if positive. The LastPrice
	/// fallback handles premarket chains where Webull echoes the prior-session close but omits bid/ask.
	/// </summary>
	internal static ParityResult? DeriveSpotFromParity(string ticker, IReadOnlyDictionary<string, OptionContractQuote> quotes, double riskFreeRate, DateTime asOf, Action<string>? diag = null)
	{
		// Group by expiry: { expiry → { strike → (call?, put?) } }.
		var byExpiry = new Dictionary<DateTime, Dictionary<decimal, (OptionContractQuote? call, OptionContractQuote? put)>>();
		foreach (var (sym, q) in quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (!byExpiry.TryGetValue(p.ExpiryDate, out var byStrike))
				byExpiry[p.ExpiryDate] = byStrike = new Dictionary<decimal, (OptionContractQuote?, OptionContractQuote?)>();
			byStrike.TryGetValue(p.Strike, out var pair);
			byStrike[p.Strike] = p.CallPut == "C" ? (q, pair.put) : (pair.call, q);
		}

		if (byExpiry.Count == 0)
		{
			diag?.Invoke($"  {ticker}: no contracts for this root in the fetched chain.");
			return null;
		}

		// Pick the nearest non-negative DTE expiry that has at least one strike with both call+put viable mids.
		foreach (var expiry in byExpiry.Keys.Where(d => d.Date >= asOf.Date).OrderBy(d => d))
		{
			var byStrike = byExpiry[expiry];
			decimal? bestStrike = null;
			decimal bestDiff = decimal.MaxValue;
			decimal bestC = 0m, bestP = 0m;
			int bothPresent = 0, callOnly = 0, putOnly = 0, neither = 0;
			foreach (var (k, pair) in byStrike)
			{
				var cMid = Mid(pair.call);
				var pMid = Mid(pair.put);
				if (cMid != null && pMid != null) bothPresent++;
				else if (cMid != null) callOnly++;
				else if (pMid != null) putOnly++;
				else neither++;
				if (cMid == null || pMid == null) continue;
				var diff = Math.Abs(cMid.Value - pMid.Value);
				if (diff < bestDiff) { bestDiff = diff; bestStrike = k; bestC = cMid.Value; bestP = pMid.Value; }
			}
			diag?.Invoke($"  {ticker} {expiry:yyyy-MM-dd}: {byStrike.Count} strikes (both={bothPresent}, callOnly={callOnly}, putOnly={putOnly}, neither={neither}).");
			if (bestStrike == null) continue;

			var dte = Math.Max(0, (expiry.Date - asOf.Date).Days);
			var discount = (decimal)Math.Exp(-riskFreeRate * dte / 365.0);
			var spot = (bestC - bestP) + bestStrike.Value * discount;
			return new ParityResult(spot, bestStrike.Value, dte);
		}
		return null;

		static decimal? Mid(OptionContractQuote? q)
		{
			if (q == null) return null;
			if (q.Bid is decimal b && q.Ask is decimal a && b >= 0m && a > 0m && a >= b) return (b + a) / 2m;
			if (q.LastPrice is decimal lp && lp > 0m) return lp;
			return null;
		}
	}

	/// <summary>Theoretical scan: same opener pipeline as the live path, but the live quote source is
	/// replaced with the backtest's Black-Scholes pricer fed by an explicit spot override. Used to preview
	/// what the opener would propose at a given (asOf, spot) without needing a live chain — e.g. Sunday
	/// evening before a Monday open, or a "what if SPX gaps to 7500" stress check. No live positions or
	/// account state are pulled; the user supplies <c>--starting-cash</c> for proposal sizing.</summary>
	private static async Task<int> RunTheoreticalAsync(AIScanSettings settings, AIConfig config, CancellationToken cancellation)
	{
		var spotOverrides = settings.ParseSpotOverrides();
		var missing = spotOverrides.ContainsKey(config.Ticker) ? new List<string>() : new List<string> { config.Ticker };
		if (missing.Count > 0)
		{
			Console.Error.WriteLine($"Error: --spot must cover every configured ticker. Missing: {string.Join(", ", missing)}.");
			return 1;
		}

		var asOf = settings.ResolveAsOf();
		var tickerSet = config.TickerSet();

		var bars = new Backtest.HistoricalBarCache(offline: true);
		var smile = new Backtest.SmileIndexCache(offline: true);
		var ivProvider = new Backtest.BacktestIVProvider(bars, smile: smile);
		var dividendsByRoot = await new Backtest.HistoricalDividendCache(offline: true).BuildScheduleMapAsync(tickerSet, cancellation);
		var quotes = new Backtest.BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036, spotOverrides: spotOverrides, dividendsByRoot: dividendsByRoot);
		var priceCache = new Replay.HistoricalPriceCache(bars);

		// Cash sizing: prefer the live broker balance so proposals reflect what the user could actually
		// trade tomorrow. --starting-cash is an explicit override (e.g. "what if I had $50k instead?")
		// or a fallback when the broker is unreachable.
		decimal cash, accountValue;
		string cashSource;
		if (settings.StartingCash.HasValue)
		{
			cash = settings.StartingCash.Value;
			accountValue = settings.StartingCash.Value;
			cashSource = "override";
		}
		else
		{
			try
			{
				var account = AIContext.ResolveTradeAccount(config, settings.Account);
				var livePositions = new LivePositionSource(account);
				(cash, accountValue) = await livePositions.GetAccountStateAsync(asOf, cancellation);
				cashSource = "live broker";
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Error: could not fetch live account state ({ex.Message}). Pass --starting-cash <AMOUNT> to bypass the broker.");
				return 1;
			}
		}

		var spotLine = string.Join(", ", spotOverrides.OrderBy(kv => kv.Key).Select(kv => $"{Markup.Escape(kv.Key)} @ ${kv.Value:N2}"));
		AnsiConsole.MarkupLine($"[bold yellow]Theoretical scan[/]: {spotLine} | asOf {asOf:yyyy-MM-dd HH:mm} ET | cash ${cash:N2} ({cashSource})");
		AnsiConsole.MarkupLine("[dim]Black-Scholes pricing; VIX/HV from most recent settled data; no live chain. For planning only.[/]");
		AnsiConsole.WriteLine();

		// No live positions in theoretical mode — we score from a clean book regardless of any real
		// holdings the broker reports.
		var openPositions = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);

		var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, asOf, quotes, config, cancellation);
		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
			tickerSet, priceCache, config.Indicators.TechnicalFilter, asOf, cancellation);

		var ctx = new EvaluationContext(asOf, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
		var (_, openerExecutor) = AIContext.BuildAutoExecutors(config, settings.Account);

		var openCount = 0;
		var openerOrdersThisRun = 0;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			var openSink = new OpenProposalSink(config.LogLevel, config.Ticker, config.Strategy, mode: "scan", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			var openEvaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache, backtestMode: true, dividendsByRoot: dividendsByRoot);
			var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
			for (var i = 0; i < openResults.Count; i++) openSink.Emit(openResults[i], rank: i + 1);
			openCount = openResults.Count;
			// Theoretical mode bypasses the management executor — no live positions in scope, so the
			// rule engine had nothing to react to. Opener executor still fires so the user can validate
			// open-order placement off-hours against a sandbox account.
			if (openerExecutor != null)
				openerOrdersThisRun = await openerExecutor.HandleAsync(openResults, openPositions, asOf, cancellation);
		}

		var execSuffix = openerExecutor != null ? $" | opener auto-execute: {openerOrdersThisRun} order(s) acted on" : "";
		AnsiConsole.MarkupLine($"[dim]Theoretical tick complete: {openCount} open proposal(s) emitted{execSuffix}[/]");
		return 0;
	}
}

/// <summary>`ai replay` — historical replay against orders.jsonl with agreement analysis.</summary>
internal sealed class AIReplaySettings : AISingleTickerSubcommandSettings
{
	[CommandOption("--since <DATE>")]
	[Description("Start date YYYY-MM-DD. Default: earliest fill.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[Description("End date YYYY-MM-DD. Default: latest fill.")]
	public string? Until { get; set; }

	[CommandOption("--granularity <LEVEL>")]
	[Description("daily or hourly. Default: daily.")]
	public string Granularity { get; set; } = "daily";

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--until: must be YYYY-MM-DD, got '{Until}'");
		if (Granularity != "daily" && Granularity != "hourly")
			return ValidationResult.Error($"--granularity: must be 'daily' or 'hourly', got '{Granularity}'");
		return ValidationResult.Success();
	}
}

internal sealed class AIReplayCommand : AsyncCommand<AIReplaySettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIReplaySettings settings, CancellationToken cancellation)
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		TerminalHelper.EnsureTerminalWidthFromConfig();

		// Load all historical trades using the existing pipeline.
		var reportSettings = new ReportSettings();
		var (trades, feeLookup, err) = ReportCommand.LoadTrades(reportSettings);
		if (err != 0) return err;

		var since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: trades.Count > 0 ? trades.Min(t => t.Timestamp).Date : DateTime.Today;
		var until = settings.Until != null
			? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: trades.Count > 0 ? trades.Max(t => t.Timestamp).Date : DateTime.Today;

		// Build historical price cache and IV back-solver seeded with user's option fills.
		var priceCache = new Replay.HistoricalPriceCache();
		var ivSolver = new Replay.IVBackSolver();
		foreach (var t in trades.Where(t => t.Asset == Asset.Option))
		{
			var occ = t.MatchKey.StartsWith("option:") ? t.MatchKey[7..] : t.MatchKey;
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed == null) continue;
			var spot = await priceCache.GetCloseAsync(parsed.Root, t.Timestamp.Date, cancellation);
			if (!spot.HasValue) continue;
			ivSolver.RegisterFill(occ, t.Timestamp, t.Price, spot.Value);
		}

		var positions = new Sources.ReplayPositionSource(trades, feeLookup);
		var quotes = new Sources.ReplayQuoteSource(priceCache, ivSolver, riskFreeRate: 0.036);

		var runner = new Replay.ReplayRunner(config, positions, quotes, trades, priceCache);
		return await runner.RunAsync(since, until, settings.Granularity, cancellation);
	}
}

/// <summary>`ai backtest` — simulate opening/managing positions from scratch over a historical window
/// using the AI rules + opener. No real fills involved; produces simulated P&L for rule tuning.</summary>
internal sealed class AIBacktestSettings : AISingleTickerSubcommandSettings
{
	[CommandOption("--since <DATE>")]
	[Description("Start date YYYY-MM-DD. Default: Jan 1 of current year.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[Description("End date YYYY-MM-DD. Default: today.")]
	public string? Until { get; set; }

	[CommandOption("--starting-cash <AMOUNT>")]
	[Description("Starting cash balance. Default: 10000.")]
	public decimal StartingCash { get; set; } = 10000m;

	[CommandOption("--fee-per-contract <AMOUNT>")]
	[Description("Per-leg-contract commission. Defaults from ticker: $1.14 for SPX/SPXW (and other cash-settled indexes), $0.55 for XSP, $1.30 for NDX, $0.05 for everything else (Webull ETFs/equities incl. IWM).")]
	public decimal? FeePerContract { get; set; }

	[CommandOption("--scan-stride <N>")]
	[Description("Open-scan minute stride: evaluate every Nth minute for entries. Default 1 (every minute), matching live watch's per-tick cadence — coarser strides alias past the minutes where a marginal score crosses minScoreToOpen and silently under-count opens vs reality. Raise N to speed up multi-year sweeps (~Nx fewer candidate enumerations) at the cost of missing single-minute threshold crossings.")]
	public int ScanStride { get; set; } = 1;


	[CommandOption("--iv-hv-premium <RATIO>")]
	[Description("IV/HV multiplier for non-SPY tickers (SPY uses real VIX). Default: 1.15.")]
	public decimal IvHvPremium { get; set; } = 1.15m;

	[CommandOption("--smile <MODE>")]
	[Description("Volatility smile model: 'off' (flat IV across strikes) or 'static' (quadratic skew + curvature, ticker-class defaults). Default: static.")]
	public string Smile { get; set; } = "static";

	[CommandOption("--top-per-step <N>")]
	[Description("Maximum new opens per trading day. Default: 1.")]
	public int TopPerStep { get; set; } = 1;

	[CommandOption("--show-fills")]
	[Description("Print per-fill ledger in addition to the summary.")]
	public bool ShowFills { get; set; }

	[CommandOption("--fills-jsonl <PATH>")]
	[Description("Also write each fill as a JSON line to the given path. Useful for parameter-sweep scripts that need structure mix / per-trade P&L without scraping the Spectre table (which wraps under piped stdout). Independent of --show-fills.")]
	public string? FillsJsonlPath { get; set; }

	[CommandOption("--oracle")]
	[Description("Research mode (by-design lookahead): the minute loop evaluates every minute of the trading day, forward-simulates each proposal to expiry using known daily close intrinsic, and opens the minute whose proposal yields the highest realized P&L. Produces an upper bound on strategy performance with perfect timing. Not realistic — use to size the gap between the current realistic scan and a theoretical ceiling.")]
	public bool Oracle { get; set; }

	[CommandOption("--profile")]
	[Description("Print a per-step timing breakdown (rules, settle, opener minute loop, intraday triggers, MTM) at the end of the run. Useful when a recent change made the backtest noticeably slower.")]
	public bool Profile { get; set; }

	[CommandOption("--bias-drift <VALUE>")]
	[Description("Override opener.weights.biasDrift for this run (after the per-ticker config merge). Useful for ad-hoc parameter exploration without copying the config file. Range typically 1.0–1.5 for SPXW.")]
	public decimal? BiasDriftOverride { get; set; }

	[CommandOption("--min-score-to-open <VALUE>")]
	[Description("Override opener.minScoreToOpen for this run (after the per-ticker config merge). Lowering opens more days; raising fires only on high-conviction setups. Range typically 0.0–0.20.")]
	public decimal? MinScoreToOpenOverride { get; set; }

	[CommandOption("--intraday-tape-weight <VALUE>")]
	[Description("Override opener.weights.intradayTape for this run. 0.0 = pure macro bias; 1.0 = pure intraday tape. Must be between 0 and 1 inclusive.")]
	public decimal? IntradayTapeWeightOverride { get; set; }

	[CommandOption("--intraday-w0 <VALUE>")]
	[Description("Enable the DTE-aware intraday-tape curve and set its 0DTE blend weight (opener.intradayTapeDteCurve.weightAt0Dte). 1.0 = a 0DTE trade reads direction purely from the live tape; 0.0 = pure macro. Must be 0 to 1 inclusive. Sweep knob for the 0DTE flat-day fix.")]
	public decimal? IntradayW0Override { get; set; }

	[CommandOption("--long-conviction <VALUE>")]
	[Description("Set opener.longConvictionGate.weight: penalty depth for low-conviction long-premium trades (long call/put, debit verticals). 0 = off (no de-rating); 0.8 = a zero-conviction long scores 0.2x. De-rates flat-day directional coin-flips. Must be 0 to 1 inclusive.")]
	public decimal? LongConvictionOverride { get; set; }

	[CommandOption("--open-after <HHMM>")]
	[Description("Set opener.earliestEntryTimeEt: withhold opens until this ET time (\"HH:mm\", e.g. 10:00) so the intraday tape forms and blends into the bias before the directional read commits. Empty/omitted = 09:30 open. Sweep knob for delayed-entry research.")]
	public string? OpenAfterOverride { get; set; }

	[CommandOption("--enable-structure <NAME>")]
	[Description("Force-enable a structure for this run (repeatable). Names: longCalendar, doubleCalendar, longDiagonal, doubleDiagonal, ironButterfly, ironCondor, shortVertical, longCallPut, longVertical. Sets the structure's Enabled=true on top of the merged config; doesn't disable other enabled structures.")]
	public string[] EnableStructures { get; set; } = Array.Empty<string>();

	[CommandOption("--tp <VALUE>")]
	[Description("Override opener.realizedExpectancy.profitTargetPctOfMaxProfit for this run. 1.0 = no profit cap (ride to expiry); 0.5 = close at half max profit. Range 0..1.")]
	public decimal? TpOverride { get; set; }

	[CommandOption("--lots <N>")]
	[Description("Fixed contracts per trade (sizing-neutral). Every open trades exactly N contracts and the cash/reserve gates are bypassed, so terminal P&L is the additive sum of per-trade results instead of a compounding curve — use this to measure per-trade edge (expectancy, profit factor) without the position-sizing feedback loop. Omit for normal equity-scaled sizing.")]
	public int? Lots { get; set; }

	[CommandOption("--sl <VALUE>")]
	[Description("Override opener.realizedExpectancy.stopLossPctOfMaxLoss for this run. 1.0 = SL effectively off (ride to expiry/settlement); 0.5 = cut at half of max loss. Range 0..1.")]
	public decimal? SlOverride { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (TpOverride.HasValue && (TpOverride.Value <= 0m || TpOverride.Value > 1m))
			return ValidationResult.Error($"--tp: must be in (0, 1], got {TpOverride}");
		if (SlOverride.HasValue && (SlOverride.Value <= 0m || SlOverride.Value > 1m))
			return ValidationResult.Error($"--sl: must be in (0, 1], got {SlOverride}");
		if (Lots.HasValue && Lots.Value < 1)
			return ValidationResult.Error($"--lots: must be ≥ 1, got {Lots}");
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--until: must be YYYY-MM-DD, got '{Until}'");
		if (StartingCash <= 0m) return ValidationResult.Error($"--starting-cash: must be > 0, got {StartingCash}");
		if (FeePerContract.HasValue && FeePerContract.Value < 0m) return ValidationResult.Error($"--fee-per-contract: must be ≥ 0, got {FeePerContract}");
		if (IvHvPremium <= 0m) return ValidationResult.Error($"--iv-hv-premium: must be > 0, got {IvHvPremium}");
		if (Smile != "off" && Smile != "static")
			return ValidationResult.Error($"--smile: must be 'off' or 'static', got '{Smile}'");
		if (TopPerStep < 1) return ValidationResult.Error($"--top-per-step: must be ≥ 1, got {TopPerStep}");
		if (BiasDriftOverride.HasValue && BiasDriftOverride.Value < 0m)
			return ValidationResult.Error($"--bias-drift: must be ≥ 0, got {BiasDriftOverride}");
		if (IntradayTapeWeightOverride.HasValue && (IntradayTapeWeightOverride.Value < 0m || IntradayTapeWeightOverride.Value > 1m))
			return ValidationResult.Error($"--intraday-tape-weight: must be in [0, 1], got {IntradayTapeWeightOverride}");
		if (IntradayW0Override.HasValue && (IntradayW0Override.Value < 0m || IntradayW0Override.Value > 1m))
			return ValidationResult.Error($"--intraday-w0: must be in [0, 1], got {IntradayW0Override}");
		if (LongConvictionOverride.HasValue && (LongConvictionOverride.Value < 0m || LongConvictionOverride.Value > 1m))
			return ValidationResult.Error($"--long-conviction: must be in [0, 1], got {LongConvictionOverride}");
		if (!string.IsNullOrWhiteSpace(OpenAfterOverride) && !TimeSpan.TryParse(OpenAfterOverride, System.Globalization.CultureInfo.InvariantCulture, out _))
			return ValidationResult.Error($"--open-after: must be HH:mm, got '{OpenAfterOverride}'");
		return ValidationResult.Success();
	}
}

internal sealed class AIBacktestCommand : AsyncCommand<AIBacktestSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIBacktestSettings settings, CancellationToken cancellation)
		=> await AITextOutput.RunAsync(settings, "AIBacktest", async () =>
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		config.Ticker = settings.Ticker.ToUpperInvariant();

		// Apply per-run CLI overrides on top of the merged config. Used by parameter sweeps
		// to vary one knob at a time without maintaining N config-file copies (the per-ticker
		// override file only contains diffs, so a copy-and-tweak approach loses the base config's
		// scoring weights at load time — these flags sidestep that by mutating the already-merged
		// in-memory config).
		if (settings.BiasDriftOverride.HasValue) config.Opener.Weights.BiasDrift = settings.BiasDriftOverride.Value;
		if (settings.MinScoreToOpenOverride.HasValue) config.Opener.MinScoreToOpen = settings.MinScoreToOpenOverride.Value;
		if (settings.IntradayTapeWeightOverride.HasValue) config.Opener.Weights.IntradayTape = settings.IntradayTapeWeightOverride.Value;
		if (settings.IntradayW0Override.HasValue)
		{
			config.Opener.IntradayTapeDteCurve.Enabled = true;
			config.Opener.IntradayTapeDteCurve.WeightAt0Dte = settings.IntradayW0Override.Value;
		}
		if (settings.LongConvictionOverride.HasValue) config.Opener.LongConvictionGate.Weight = settings.LongConvictionOverride.Value;
		if (!string.IsNullOrWhiteSpace(settings.OpenAfterOverride)) config.Opener.EarliestEntryTimeEt = settings.OpenAfterOverride;
		if (settings.TpOverride.HasValue) config.Opener.RealizedExpectancy.ProfitTargetPctOfMaxProfit = settings.TpOverride.Value;
		if (settings.SlOverride.HasValue) config.Opener.RealizedExpectancy.StopLossPctOfMaxLoss = settings.SlOverride.Value;
		foreach (var name in settings.EnableStructures)
		{
			switch (name.ToLowerInvariant())
			{
				case "longcalendar": config.Opener.Structures.LongCalendar.Enabled = true; break;
				case "doublecalendar": config.Opener.Structures.DoubleCalendar.Enabled = true; break;
				case "longdiagonal": config.Opener.Structures.LongDiagonal.Enabled = true; break;
				case "doublediagonal": config.Opener.Structures.DoubleDiagonal.Enabled = true; break;
				case "ironbutterfly": config.Opener.Structures.IronButterfly.Enabled = true; break;
				case "ironcondor": config.Opener.Structures.IronCondor.Enabled = true; break;
				case "shortvertical": config.Opener.Structures.ShortVertical.Enabled = true; break;
				case "longcallput": config.Opener.Structures.LongCallPut.Enabled = true; break;
				case "longvertical": config.Opener.Structures.LongVertical.Enabled = true; break;
				default:
					Console.Error.WriteLine($"Warning: --enable-structure '{name}' is not a recognized structure name; ignoring.");
					break;
			}
		}

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: new DateTime(DateTime.Today.Year, 1, 1);
		var until = settings.Until != null
			? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: DateTime.Today;

		// Backtest is offline — the caches MUST already cover [since, until]. Run `wa ai history <ticker>` first.
		var bars = new Backtest.HistoricalBarCache(offline: true);

		if (!await bars.HasCoverageAsync(config.Ticker, since, until, cancellation))
		{
			Console.Error.WriteLine($"Error: missing bar history for {config.Ticker} in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history {config.Ticker}");
			return 1;
		}
		// VIX-driven tickers (SPX family) need a VIX bar history for ATM IV and a CBOE SMILE history
		// for per-day smile scaling. VIX1D (≤1 DTE) and VIX9D (≤9 DTE) anchor short-dated ATM IV at
		// the appropriate term. All ride along with the strategy ticker's history command
		// (wa ai history SPXW/SPX/XSP/SPY pulls VIX, VIX1D, VIX9D, and SMILE).
		var vixDriven = new[] { "SPY", "SPX", "SPXW", "XSP" };
		var smile = new Backtest.SmileIndexCache(offline: true);
		if (vixDriven.Contains(config.Ticker, StringComparer.OrdinalIgnoreCase))
		{
			if (!await bars.HasCoverageAsync("VIX", since, until, cancellation))
			{
				Console.Error.WriteLine($"Error: missing VIX history in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history {settings.Ticker}");
				return 1;
			}
			if (!await bars.HasCoverageAsync("VIX9D", since, until, cancellation))
			{
				Console.Error.WriteLine($"Error: missing VIX9D history in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history {settings.Ticker}");
				return 1;
			}
			// VIX1D launched 2023-04-24. For windows that start before that date, partial coverage is
			// expected — the IV provider falls back to VIX9D / VIX for missing dates. Warn loudly so
			// the user knows their pre-launch backtest is using a longer-term anchor than ideal, but
			// don't fail the run. Inside the launch window, treat missing data as a cache problem.
			var vix1DLaunch = new DateTime(2023, 4, 24);
			var vix1DWindowStart = since < vix1DLaunch ? vix1DLaunch : since;
			if (vix1DWindowStart <= until && !await bars.HasCoverageAsync("VIX1D", vix1DWindowStart, until, cancellation))
			{
				Console.Error.WriteLine($"Warning: missing VIX1D history in [{vix1DWindowStart:yyyy-MM-dd} → {until:yyyy-MM-dd}]. 0DTE pricing will fall back to VIX9D. Run: wa ai history {settings.Ticker}");
			}
			if (!await smile.HasCoverageAsync(since, until, cancellation))
			{
				Console.Error.WriteLine($"Error: missing CBOE SMILE history in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history {settings.Ticker}");
				return 1;
			}
		}

		var closes = new Replay.HistoricalPriceCache(bars);
		var ivProvider = new Backtest.BacktestIVProvider(bars, ivHvPremium: settings.IvHvPremium, smileEnabled: settings.Smile == "static", smile: smile);
		// Historical dividend schedules (data/dividends/<TICKER>.csv, populated by `wa ai history`) make the
		// parametric reprice dividend-aware, matching the live feed. Offline read; absent files
		// (non-payers, index roots) leave that root unadjusted. Built over the full ticker set so any
		// hypothetical/secondary root the opener touches is covered.
		var dividends = new Backtest.HistoricalDividendCache(offline: true);
		var dividendsByRoot = await dividends.BuildScheduleMapAsync(config.TickerSet(), cancellation);
		// Per-day full-chain OI (data/oi) — makes the GEX / max-pain factors computable in the backtest.
		var oiCache = new Backtest.ChainSnapshotOiCache();
		// Price foundation = real minute NBBO (data/quotes). The parametric source (BS + IV + smile, NO
		// captured-bar overlay) answers ONLY the counterfactual reprices real NBBO can't — intraday SL/TP
		// brackets and the profit projector price legs at a hypothetical spot. It is never a price foundation.
		var parametric = new Backtest.BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036, dividendsByRoot: dividendsByRoot, oiCache: oiCache);
		Backtest.IBacktestQuoteSource quotes = new Backtest.QuotesQuoteSource(
			bars, new Backtest.QuoteStoreCache(since: since, until: until), parametric, riskFreeRate: 0.036, dividendsByRoot: dividendsByRoot, oiCache: oiCache);

		var feePerContract = settings.FeePerContract ?? Backtest.SimulatedBook.DefaultFeePerContractFor(settings.Ticker);
		var book = new Backtest.SimulatedBook(settings.StartingCash, feePerContract, config.Opener.RealizedExpectancy);
		var positions = new Backtest.BacktestPositionSource(book, quotes);
		var runner = new Backtest.BacktestRunner(config, book, positions, quotes, bars, closes, settings.TopPerStep, oracle: settings.Oracle, profile: settings.Profile, fixedContracts: settings.Lots, pricingMode: settings.Pricing, scanStride: settings.ScanStride, dividendsByRoot: dividendsByRoot);

		AnsiConsole.MarkupLine($"[bold]Backtest:[/] {since:yyyy-MM-dd} → {until:yyyy-MM-dd} | ticker {Markup.Escape(config.Ticker)} | start ${settings.StartingCash:N0} | fee ${feePerContract}/contract | smile={settings.Smile} | fills={SuggestionPricing.Normalize(settings.Pricing)}{(settings.Oracle ? " | [yellow]ORACLE (lookahead)[/]" : "")}{(settings.Lots.HasValue ? $" | [yellow]FIXED {settings.Lots} lot(s) — no compounding[/]" : "")}");
		AnsiConsole.WriteLine();

		Backtest.BacktestResult result;
		if (settings.Profile)
		{
			// --profile prints its own per-section timing inside the run; skip the bar so it isn't clobbered.
			result = await runner.RunAsync(since, until, cancellation);
		}
		else
		{
			Backtest.BacktestResult? captured = null;
			await AnsiConsole.Progress()
				.AutoClear(true)            // remove the bar once done; the summary table prints below
				.HideCompleted(true)
				.Columns(new SpinnerColumn(), new TaskDescriptionColumn(), new ProgressBarColumn(),
						 new PercentageColumn(), new ElapsedTimeColumn())
				.StartAsync(async ctx =>
				{
					var task = ctx.AddTask($"[green]Backtesting {Markup.Escape(config.Ticker)}[/]");
					captured = await runner.RunAsync(since, until, cancellation, (doneDays, totalDays, day) =>
					{
						task.MaxValue = totalDays;
						task.Value = doneDays;
						task.Description = $"[green]Backtesting {Markup.Escape(config.Ticker)}[/] [grey]{day:yyyy-MM-dd}[/]";
					});
				});
			result = captured!;
		}
		Backtest.BacktestSummaryRenderer.Render(result, settings.ShowFills);

		if (!string.IsNullOrWhiteSpace(settings.FillsJsonlPath))
		{
			var path = Path.IsPathRooted(settings.FillsJsonlPath) ? settings.FillsJsonlPath : Path.GetFullPath(settings.FillsJsonlPath);
			using var w = new StreamWriter(path);
			foreach (var f in result.Fills)
			{
				var legs = string.Join(",", f.Legs.Select(l => $"{{\"sym\":\"{l.Symbol}\",\"side\":\"{l.Side}\",\"qty\":{l.Qty},\"price\":{l.PricePerShare.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
				w.WriteLine($"{{\"ts\":\"{f.Date:yyyy-MM-ddTHH:mm:ss}\",\"ticker\":\"{f.Ticker}\",\"key\":\"{f.PositionKey}\",\"kind\":\"{f.Kind}\",\"strategy\":\"{f.StrategyKind}\",\"qty\":{f.Qty},\"net\":{f.NetCashFlow.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"fees\":{f.Fees.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"rule\":{(f.RuleName == null ? "null" : $"\"{f.RuleName}\"")},\"lineage\":{f.LineageId},\"legs\":[{legs}]}}");
			}
		}
		return 0;
	});
}

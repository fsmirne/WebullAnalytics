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

	[CommandOption("--config <PATH>")]
	[Description("Path to ai-config.json. Default: data/ai-config.json.")]
	public string? ConfigPath { get; set; }

	[CommandOption("--output <FORMAT>")]
	[Description("Output format: console or text. Default: console.")]
	public string Output { get; set; } = "console";

	[CommandOption("--output-path <PATH>")]
	[Description("Path for --output text.")]
	public string? OutputPath { get; set; }

	[CommandOption("--api <SOURCE>")]
	[Description("Override quoteSource: webull or yahoo.")]
	public string? Api { get; set; }

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
		if (Api != null && Api != "webull" && Api != "yahoo") return ValidationResult.Error($"--api: must be 'webull' or 'yahoo', got '{Api}'");
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

/// <summary>Base for AI subcommands that operate on a portfolio of tickers (scan, watch, replay).
/// Backtest does not extend this — it takes a single positional ticker instead.</summary>
internal abstract class AIMultiTickerSubcommandSettings : AISubcommandSettings
{
	[CommandOption("--tickers <LIST>")]
	[Description("Override config tickers (comma-separated).")]
	public string? Tickers { get; set; }
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
	/// <summary>Loads and merges config + CLI overrides. Returns null on failure (with stderr messages).</summary>
	internal static AIConfig? ResolveConfig(AISubcommandSettings settings)
	{
		var path = settings.ConfigPath ?? AIConfigLoader.ConfigPath;
		var abspath = Program.ResolvePath(path);
		if (!File.Exists(abspath))
		{
			Console.Error.WriteLine($"Error: ai config not found at '{path}'.");
			Console.Error.WriteLine($"  Run: cp ai-config.example.json {AIConfigLoader.ConfigPath} and edit.");
			return null;
		}

		AIConfig? config;
		try { config = System.Text.Json.JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(abspath)); }
		catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"Error: failed to parse ai-config.json: {ex.Message}"); return null; }

		if (config == null) { Console.Error.WriteLine("Error: ai-config.json is empty."); return null; }

		// Apply CLI overrides. --tickers is only available on multi-ticker subcommands.
		if (settings is AIMultiTickerSubcommandSettings multi && !string.IsNullOrWhiteSpace(multi.Tickers))
			config.Tickers = multi.Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		if (!string.IsNullOrWhiteSpace(settings.Api)) config.QuoteSource = settings.Api;
		if (!string.IsNullOrWhiteSpace(settings.LogLevel)) config.Log.ConsoleVerbosity = settings.LogLevel;

		var err = AIConfigLoader.Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config.json: {err}"); return null; }

		return config;
	}

	internal static IPositionSource BuildLivePositionSource(AIConfig config)
	{
		var account = ResolveTradeAccount(config);
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

	internal static IQuoteSource BuildLiveQuoteSource(AIConfig config) => new LiveQuoteSource(config.QuoteSource);

	/// <summary>Resolves the broker account that ai-config points at via positionSource.account.
	/// Used by the position source AND by the auto-executor for order submission.</summary>
	internal static TradeAccount ResolveTradeAccount(AIConfig config)
	{
		var tradeConfig = TradeConfig.Load() ?? throw new InvalidOperationException("trade-config.json required for live ai");
		return TradeConfig.Resolve(tradeConfig, config.PositionSource.Account) ?? throw new InvalidOperationException($"account '{config.PositionSource.Account}' not found");
	}
}

/// <summary>`ai scan` — one evaluation pass, print proposals, exit.</summary>
internal sealed class AIScanSettings : AIMultiTickerSubcommandSettings
{
	[CommandOption("--top <N>")]
	[Description("Override opener.topNPerTicker from ai-config.json.")]
	public int? Top { get; set; }

	[CommandOption("--theoretical")]
	[Description("Bypass the live chain and price via Black-Scholes against an explicit spot. Use for pre-market / weekend planning. Requires --spot; --date defaults to the next business day.")]
	public bool Theoretical { get; set; }

	[CommandOption("--date <DATE>")]
	[Description("With --theoretical: asOf date (YYYY-MM-DD). Defaults to the next business day.")]
	public string? Date { get; set; }

	[CommandOption("--spot <SPEC>")]
	[Description("With --theoretical: underlying spot override(s). Format: TICKER:PRICE (e.g., SPXW:7450). Comma-separated for multiple tickers.")]
	public string? Spot { get; set; }

	[CommandOption("--starting-cash <AMOUNT>")]
	[Description("With --theoretical: override account balance for sizing. Default: pulled from your live broker account.")]
	public decimal? StartingCash { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (Top.HasValue && Top.Value < 1) return ValidationResult.Error($"--top: must be ≥ 1, got {Top.Value}");

		if (Theoretical)
		{
			if (string.IsNullOrWhiteSpace(Spot))
				return ValidationResult.Error("--theoretical requires --spot TICKER:PRICE (no live chain to fall back to).");
			foreach (var pair in Spot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2 || !decimal.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0m)
					return ValidationResult.Error($"--spot: invalid entry '{pair}'. Expected TICKER:PRICE with PRICE > 0.");
			}
			if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
				return ValidationResult.Error($"--date: expected YYYY-MM-DD, got '{Date}'");
			if (StartingCash.HasValue && StartingCash.Value <= 0m)
				return ValidationResult.Error($"--starting-cash: must be > 0, got {StartingCash.Value}");
		}
		else
		{
			if (Date != null || Spot != null)
				return ValidationResult.Error("--date and --spot only apply with --theoretical.");
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
		// Stamp at 09:30 ET to match the backtest's per-step convention (HV/VIX lookups only use .Date,
		// so the time is cosmetic — but the rendered banner reads cleaner with a real market-open time).
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

		if (string.Equals(config.Log.ConsoleVerbosity, "debug", StringComparison.OrdinalIgnoreCase))
			Console.Error.WriteLine($"[debug] wa ai scan: log-level=debug baseDir='{Program.BaseDir}' quoteSource='{config.QuoteSource}' tickers=[{string.Join(",", config.Tickers)}] proposals={settings.Proposals} theoretical={settings.Theoretical}");

		TerminalHelper.EnsureTerminalWidthFromConfig();

		return settings.Theoretical
			? await RunTheoreticalAsync(settings, config, cancellation)
			: await RunLiveAsync(settings, config, cancellation);
	});

	private static async Task<int> RunLiveAsync(AIScanSettings settings, AIConfig config, CancellationToken cancellation)
	{
		var positions = AIContext.BuildLivePositionSource(config);
		var quotes = AIContext.BuildLiveQuoteSource(config);

		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);
		var now = DateTime.Now;

		var priceCache = new Replay.HistoricalPriceCache();

		var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
		var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);

		var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, quotes, config, cancellation);

		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
			tickerSet, priceCache, config.Rules.OpportunisticRoll.TechnicalFilter, now, cancellation);

		var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config, settings.Pricing), config);

		var results = evaluator.Evaluate(ctx);
		var managementCount = settings.EmitManagementProposals ? results.Count : 0;
		if (settings.EmitManagementProposals)
		{
			using var sink = new ProposalSink(config.Log, mode: "scan", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			foreach (var r in results) sink.Emit(r.Proposal, r.IsRepeat);
		}

		var openCount = 0;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			var openSink = new OpenProposalSink(config.Log, mode: "once", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			var openEvaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache);
			var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
			for (var i = 0; i < openResults.Count; i++) openSink.Emit(openResults[i], rank: i + 1);
			openCount = openResults.Count;
		}

		AnsiConsole.MarkupLine($"[dim]Tick complete: {openPositions.Count} position(s), {managementCount} mgmt proposal(s), {openCount} open proposal(s) emitted[/]");
		return 0;
	}

	/// <summary>Theoretical scan: same opener pipeline as the live path, but the live quote source is
	/// replaced with the backtest's Black-Scholes pricer fed by an explicit spot override. Used to preview
	/// what the opener would propose at a given (asOf, spot) without needing a live chain — e.g. Sunday
	/// evening before a Monday open, or a "what if SPX gaps to 7500" stress check. No live positions or
	/// account state are pulled; the user supplies <c>--starting-cash</c> for proposal sizing.</summary>
	private static async Task<int> RunTheoreticalAsync(AIScanSettings settings, AIConfig config, CancellationToken cancellation)
	{
		var spotOverrides = settings.ParseSpotOverrides();
		var missing = config.Tickers.Where(t => !spotOverrides.ContainsKey(t)).ToList();
		if (missing.Count > 0)
		{
			Console.Error.WriteLine($"Error: --spot must cover every configured ticker. Missing: {string.Join(", ", missing)}.");
			return 1;
		}

		var asOf = settings.ResolveAsOf();
		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);

		var bars = new Backtest.HistoricalBarCache(offline: true);
		var vix = new Backtest.HistoricalVixCache(offline: true);
		var ivProvider = new Backtest.BacktestIVProvider(vix, bars);
		var quotes = new Backtest.BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036, spotOverrides: spotOverrides);
		var priceCache = new Replay.HistoricalPriceCache();

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
				var account = AIContext.ResolveTradeAccount(config);
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
			tickerSet, priceCache, config.Rules.OpportunisticRoll.TechnicalFilter, asOf, cancellation);

		var ctx = new EvaluationContext(asOf, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);

		var openCount = 0;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			var openSink = new OpenProposalSink(config.Log, mode: "once", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			var openEvaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache, backtestMode: true);
			var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
			for (var i = 0; i < openResults.Count; i++) openSink.Emit(openResults[i], rank: i + 1);
			openCount = openResults.Count;
		}

		AnsiConsole.MarkupLine($"[dim]Theoretical tick complete: {openCount} open proposal(s) emitted[/]");
		return 0;
	}
}

/// <summary>`ai replay` — historical replay against orders.jsonl with agreement analysis.</summary>
internal sealed class AIReplaySettings : AIMultiTickerSubcommandSettings
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
/// using the AI rules + opener. No real fills involved; produces simulated P&amp;L for rule tuning.</summary>
internal sealed class AIBacktestSettings : AISubcommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., SPY, QQQ, GME).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--since <DATE>")]
	[Description("Start date YYYY-MM-DD. Default: Jan 1 of current year.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[Description("End date YYYY-MM-DD. Default: today.")]
	public string? Until { get; set; }

	[CommandOption("--starting-cash <AMOUNT>")]
	[Description("Starting cash balance. Default: 25000.")]
	public decimal StartingCash { get; set; } = 25000m;

	[CommandOption("--fee-per-contract <AMOUNT>")]
	[Description("Per-leg-contract commission. Defaults from ticker: $1.14 for cash-settled index options (SPX/SPXW/NDX/XSP/RUT), $0.05 for everything else (Webull ETFs/equities).")]
	public decimal? FeePerContract { get; set; }

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

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
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

		config.Tickers = new List<string> { settings.Ticker.ToUpperInvariant() };

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: new DateTime(DateTime.Today.Year, 1, 1);
		var until = settings.Until != null
			? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: DateTime.Today;

		// Backtest is offline — the caches MUST already cover [since, until]. Run `wa ai history <ticker>` first.
		var bars = new Backtest.HistoricalBarCache(offline: true);
		var vix = new Backtest.HistoricalVixCache(offline: true);

		foreach (var t in config.Tickers)
		{
			if (!await bars.HasCoverageAsync(t, since, until, cancellation))
			{
				Console.Error.WriteLine($"Error: missing bar history for {t} in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history {t}");
				return 1;
			}
		}
		if (config.Tickers.Any(t => string.Equals(t, "SPY", StringComparison.OrdinalIgnoreCase)) && !await vix.HasCoverageAsync(since, until, cancellation))
		{
			Console.Error.WriteLine($"Error: missing VIX history in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history SPY");
			return 1;
		}

		var closes = new Replay.HistoricalPriceCache();
		var ivProvider = new Backtest.BacktestIVProvider(vix, bars, ivHvPremium: settings.IvHvPremium, smileEnabled: settings.Smile == "static");
		var quotes = new Backtest.BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036);

		var feePerContract = settings.FeePerContract ?? Backtest.SimulatedBook.DefaultFeePerContractFor(settings.Ticker);
		var book = new Backtest.SimulatedBook(settings.StartingCash, feePerContract, config.Opener.RealizedExpectancy);
		var positions = new Backtest.BacktestPositionSource(book, quotes);
		var runner = new Backtest.BacktestRunner(config, book, positions, quotes, bars, closes, settings.TopPerStep);

		AnsiConsole.MarkupLine($"[bold]Backtest:[/] {since:yyyy-MM-dd} → {until:yyyy-MM-dd} | ticker {Markup.Escape($"[{string.Join(",", config.Tickers)}]")} | start ${settings.StartingCash:N0} | fee ${feePerContract}/contract | smile={settings.Smile}");
		AnsiConsole.WriteLine();

		var result = await runner.RunAsync(since, until, cancellation);
		Backtest.BacktestSummaryRenderer.Render(result, settings.ShowFills);
		return 0;
	});
}

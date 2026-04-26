using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Report;
using WebullAnalytics.Pricing;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal abstract class AISubcommandSettings : CommandSettings
{
	[CommandOption("--config <PATH>")]
	[Description("Path to ai-config.json. Default: data/ai-config.json.")]
	public string? ConfigPath { get; set; }

	[CommandOption("--tickers <LIST>")]
	[Description("Override config tickers (comma-separated).")]
	public string? Tickers { get; set; }

	[CommandOption("--output <FORMAT>")]
	[Description("Output format: console or text. Default: console.")]
	public string Output { get; set; } = "console";

	[CommandOption("--output-path <PATH>")]
	[Description("Path for --output text.")]
	public string? OutputPath { get; set; }

	[CommandOption("--api <SOURCE>")]
	[Description("Override quoteSource: webull or yahoo.")]
	public string? Api { get; set; }

	[CommandOption("--verbosity <LEVEL>")]
	[Description("quiet | normal | debug. Overrides config.")]
	public string? Verbosity { get; set; }

	[CommandOption("--no-open-proposals")]
	[Description("Disable the opening-proposal pass for this run; management rules still run.")]
	public bool NoOpenProposals { get; set; }

	public override ValidationResult Validate()
	{
		if (Output != "console" && Output != "text") return ValidationResult.Error($"--output: must be 'console' or 'text', got '{Output}'");
		if (Output == "text" && string.IsNullOrWhiteSpace(OutputPath)) return ValidationResult.Error("--output text requires --output-path");
		if (Api != null && Api != "webull" && Api != "yahoo") return ValidationResult.Error($"--api: must be 'webull' or 'yahoo', got '{Api}'");
		if (Verbosity != null && Verbosity != "quiet" && Verbosity != "normal" && Verbosity != "debug")
			return ValidationResult.Error($"--verbosity: must be quiet|normal|debug, got '{Verbosity}'");
		return ValidationResult.Success();
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

		// Apply CLI overrides.
		if (!string.IsNullOrWhiteSpace(settings.Tickers))
			config.Tickers = settings.Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		if (!string.IsNullOrWhiteSpace(settings.Api)) config.QuoteSource = settings.Api;
		if (!string.IsNullOrWhiteSpace(settings.Verbosity)) config.Log.ConsoleVerbosity = settings.Verbosity;

		var err = AIConfigLoader.Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config.json: {err}"); return null; }

		return config;
	}

	internal static IPositionSource BuildLivePositionSource(AIConfig config)
	{
		var tradeConfig = TradeConfig.Load() ?? throw new InvalidOperationException("trade-config.json required for live ai");
		var account = TradeConfig.Resolve(tradeConfig, config.PositionSource.Account) ?? throw new InvalidOperationException($"account '{config.PositionSource.Account}' not found");
		return new LivePositionSource(account);
	}

	internal static IQuoteSource BuildLiveQuoteSource(AIConfig config) => new LiveQuoteSource(config.QuoteSource);
}

/// <summary>`ai once` — one evaluation pass, print proposals, exit.</summary>
internal sealed class AIOnceSettings : AISubcommandSettings
{
	[CommandOption("--probe-vertical <SPEC>")]
	[Description("Compute the opener score for one specific vertical: TICKER:YYYY-MM-DD:P|C:SHORT:LONG (e.g. GME:2026-06-19:P:24:23). Can be specified multiple times.")]
	public string[]? ProbeVertical { get; set; }
}

internal sealed class AIOnceCommand : AsyncCommand<AIOnceSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIOnceSettings settings, CancellationToken cancellation)
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		if (string.Equals(config.Log.ConsoleVerbosity, "debug", StringComparison.OrdinalIgnoreCase))
			Console.Error.WriteLine($"[debug] wa ai once: verbosity=debug baseDir='{Program.BaseDir}' quoteSource='{config.QuoteSource}' tickers=[{string.Join(",", config.Tickers)}]");

		TerminalHelper.EnsureTerminalWidthFromConfig();

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
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config), config);

		if (settings.ProbeVertical != null && settings.ProbeVertical.Length > 0)
			await RunVerticalProbesAsync(settings.ProbeVertical, ctx, tickerSet, quotes, config, cancellation);

		using var sink = new ProposalSink(config.Log, mode: "once");
		var results = evaluator.Evaluate(ctx);
		foreach (var r in results) sink.Emit(r.Proposal, r.IsRepeat);

		var openCount = 0;
		if (config.Opener.Enabled && !settings.NoOpenProposals)
		{
			using var openSink = new OpenProposalSink(config.Log, mode: "once");
			var openEvaluator = new OpenCandidateEvaluator(config, quotes);
			var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
			foreach (var p in openResults) openSink.Emit(p);
			openCount = openResults.Count;
		}

		AnsiConsole.MarkupLine($"[dim]Tick complete: {openPositions.Count} position(s), {results.Count} mgmt proposal(s), {openCount} open proposal(s) emitted[/]");
		return 0;
	}

	private static async Task RunVerticalProbesAsync(
		IReadOnlyList<string> specs,
		EvaluationContext ctx,
		IReadOnlySet<string> tickerSet,
		IQuoteSource quotes,
		AIConfig config,
		CancellationToken cancellation)
	{
		foreach (var spec in specs)
		{
			if (!TryParseVerticalSpec(spec, out var parsed, out var err))
			{
				Console.Error.WriteLine($"Error: --probe-vertical '{spec}': {err}");
				continue;
			}

			if (parsed.Expiry.Date < ctx.Now.Date)
			{
				Console.Error.WriteLine($"Error: --probe-vertical '{spec}': expiry {parsed.Expiry:yyyy-MM-dd} is before as-of date {ctx.Now:yyyy-MM-dd}; Webull won't return quotes for expired contracts.");
				continue;
			}

			var shortSym = MatchKeys.OccSymbol(parsed.Ticker, parsed.Expiry, parsed.ShortStrike, parsed.CallPut);
			var longSym = MatchKeys.OccSymbol(parsed.Ticker, parsed.Expiry, parsed.LongStrike, parsed.CallPut);
			var legs = new[]
			{
				new ProposalLeg("sell", shortSym, 1),
				new ProposalLeg("buy", longSym, 1),
			};
			var kind = parsed.CallPut == "P" ? OpenStructureKind.ShortPutVertical : OpenStructureKind.ShortCallVertical;
         var skel = new CandidateSkeleton(parsed.Ticker, kind, legs, TargetExpiry: parsed.Expiry);

			// Fetch/overlay quotes for the two symbols so this probe works even when they're not part of the main pipeline.
			var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shortSym, longSym };
			var snap = await quotes.GetQuotesAsync(ctx.Now, needed, tickerSet, cancellation);
         var normalizedKey = snap.Options.Keys.ToDictionary(k => k.Replace(" ", ""), k => k, StringComparer.OrdinalIgnoreCase);

			if (!snap.Options.ContainsKey(shortSym) && normalizedKey.TryGetValue(shortSym, out var altShort))
				Console.Error.WriteLine($"[probe] note: Webull returned short symbol as '{altShort}' (requested '{shortSym}')");
			if (!snap.Options.ContainsKey(longSym) && normalizedKey.TryGetValue(longSym, out var altLong))
				Console.Error.WriteLine($"[probe] note: Webull returned long symbol as '{altLong}' (requested '{longSym}')");

           var mergedQuotes = new OverlayQuoteDictionary(ctx.Quotes, snap.Options);
			var spot = snap.Underlyings.TryGetValue(parsed.Ticker, out var s)
				? s
				: ctx.UnderlyingPrices.TryGetValue(parsed.Ticker, out var s2) ? s2 : 0m;

			ctx.TechnicalSignals.TryGetValue(parsed.Ticker, out var biasSignal);
			var bias = biasSignal?.Score ?? 0m;

			Console.Error.WriteLine($"[probe] {parsed.Ticker} {parsed.CallPut} {parsed.ShortStrike:F2}/{parsed.LongStrike:F2} exp {parsed.Expiry:yyyy-MM-dd} spot={spot:F2}");

			// Report the delta gate used by enumeration so the user can tell if this candidate would have been filtered before scoring.
			var days = Math.Max(1, (parsed.Expiry.Date - ctx.Now.Date).Days);
			var years = days / 365.0;
           var iv = config.Opener.IvDefaultPct / 100m;
			if (spot > 0m)
			{
              var enumDelta = Math.Abs(OptionMath.Delta(spot, parsed.ShortStrike, years, OptionMath.RiskFreeRate, iv, parsed.CallPut));
				var inBand = enumDelta >= config.Opener.Structures.ShortVertical.ShortDeltaMin && enumDelta <= config.Opener.Structures.ShortVertical.ShortDeltaMax;
				Console.Error.WriteLine($"[probe] enum delta≈{enumDelta:F3} (band {config.Opener.Structures.ShortVertical.ShortDeltaMin:F2}-{config.Opener.Structures.ShortVertical.ShortDeltaMax:F2}) => {(inBand ? "PASS" : "FAIL")}");
			}
			else
			{
				Console.Error.WriteLine($"[probe] spot missing; cannot compute enumeration delta gate");
			}

			PrintProbeQuote("short", shortSym, mergedQuotes);
			PrintProbeQuote("long", longSym, mergedQuotes);
			var shortBA = CandidateScorer.TryLiveBidAsk(shortSym, mergedQuotes);
			var longBA = CandidateScorer.TryLiveBidAsk(longSym, mergedQuotes);
			if (shortBA != null && longBA != null)
				Console.Error.WriteLine($"[probe] execution: short@bid {shortBA.Value.bid:F2}, long@ask {longBA.Value.ask:F2}, credit/share {(shortBA.Value.bid - longBA.Value.ask):F2}");

         var p = CandidateScorer.ScoreShortVertical(skel, spot, ctx.Now, mergedQuotes, bias, config.Opener);
			if (p == null)
			{
               var reason = CandidateScorer.DiagnoseShortVerticalRejection(skel, mergedQuotes, out var detail);
				Console.Error.WriteLine($"[probe] dropped: {reason} ({detail})");
				continue;
			}

			Console.Error.WriteLine($"[probe] credit={p.DebitOrCreditPerContract:F2} maxProfit={p.MaxProfitPerContract:F2} maxLoss={p.MaxLossPerContract:F2} risk={p.CapitalAtRiskPerContract:F2}");
			Console.Error.WriteLine($"[probe] POP={p.ProbabilityOfProfit:P1} EV={p.ExpectedValuePerContract:F2} days={p.DaysToTarget} rawScore={p.RawScore:F6} biasScore={p.BiasAdjustedScore:F6}");
           Console.Error.WriteLine($"[probe] {CandidateScorer.BuildRationale(p, bias, config.Opener)}");
		}
	}

	private readonly record struct VerticalSpec(string Ticker, DateTime Expiry, string CallPut, decimal ShortStrike, decimal LongStrike);

	private static bool TryParseVerticalSpec(string spec, out VerticalSpec parsed, out string error)
	{
		parsed = default;
		error = "";
		var parts = spec.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 5)
		{
			error = "expected format TICKER:YYYY-MM-DD:P|C:SHORT:LONG";
			return false;
		}

		var ticker = parts[0];
		if (string.IsNullOrWhiteSpace(ticker))
		{
			error = "ticker is empty";
			return false;
		}

		if (!DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp))
		{
			error = "expiry must be YYYY-MM-DD";
			return false;
		}

		var cp = parts[2].ToUpperInvariant();
		if (cp != "P" && cp != "C")
		{
			error = "option type must be 'P' or 'C'";
			return false;
		}

		if (!decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var shortStrike))
		{
			error = "short strike is invalid";
			return false;
		}
		if (!decimal.TryParse(parts[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var longStrike))
		{
			error = "long strike is invalid";
			return false;
		}
		if (shortStrike <= 0m || longStrike <= 0m)
		{
			error = "strikes must be > 0";
			return false;
		}

       var expiry = exp.Date;
		if (!MarketCalendar.IsOpen(expiry))
		{
			var adjusted = expiry;
			while (!MarketCalendar.IsOpen(adjusted)) adjusted = adjusted.AddDays(-1);
			expiry = adjusted;
		}

		parsed = new VerticalSpec(ticker, expiry, cp, shortStrike, longStrike);
		return true;
	}

	private static void PrintProbeQuote(string label, string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		if (!quotes.TryGetValue(symbol, out var q))
		{
			Console.Error.WriteLine($"[probe] {label} quote: {symbol} => missing");
			return;
		}

		var bid = q.Bid.HasValue ? q.Bid.Value.ToString("F2", CultureInfo.InvariantCulture) : "null";
		var ask = q.Ask.HasValue ? q.Ask.Value.ToString("F2", CultureInfo.InvariantCulture) : "null";
		var iv = q.ImpliedVolatility.HasValue ? q.ImpliedVolatility.Value.ToString("F3", CultureInfo.InvariantCulture) : "null";
		var oi = q.OpenInterest.HasValue ? q.OpenInterest.Value.ToString(CultureInfo.InvariantCulture) : "null";
		var vol = q.Volume.HasValue ? q.Volume.Value.ToString(CultureInfo.InvariantCulture) : "null";
		Console.Error.WriteLine($"[probe] {label} quote: bid={bid} ask={ask} iv={iv} oi={oi} vol={vol} sym={q.ContractSymbol}");
	}
}

/// <summary>`ai replay` — historical replay against orders.jsonl with agreement analysis.</summary>
internal sealed class AIReplaySettings : AISubcommandSettings
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

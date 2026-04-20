using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.AI.Sources;

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
internal sealed class AIOnceSettings : AISubcommandSettings { }

internal sealed class AIOnceCommand : AsyncCommand<AIOnceSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIOnceSettings settings, CancellationToken cancellation)
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		var positions = AIContext.BuildLivePositionSource(config);
		var quotes = AIContext.BuildLiveQuoteSource(config);

		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);
		var now = DateTime.Now;

		var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
		var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);

		var optionSymbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var quoteSnapshot = await quotes.GetQuotesAsync(now, optionSymbols, tickerSet, cancellation);

		var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config), config);

		using var sink = new ProposalSink(config.Log, mode: "once");
		var results = evaluator.Evaluate(ctx);
		foreach (var r in results) sink.Emit(r.Proposal, r.IsRepeat);

		AnsiConsole.MarkupLine($"[dim]Tick complete: {openPositions.Count} position(s), {results.Count} proposal(s) emitted[/]");
		return 0;
	}
}

using Spectre.Console.Cli;
using System.Text.Json;
using WebullAnalytics.Analyze;
using WebullAnalytics.Fetch;
using WebullAnalytics.Report;
using WebullAnalytics.Sniff;
using WebullAnalytics.Trading;

namespace WebullAnalytics;

/// <summary>
/// Entry point for WebullAnalytics CLI.
/// </summary>
class Program
{
    /// <summary>
	/// Base directory for resolving relative paths. The repository has multiple copies of <c>data/</c>
	/// (source, bin/Debug, bin/Release, published exe), and only one of them is the user's "production"
	/// data — the one under <c>%LOCALAPPDATA%/WebullAnalytics</c> on Windows (or the OS equivalent of
	/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> elsewhere). Reading from any of the
	/// build-output copies silently picked up stale configs whenever the user edited prod and didn't
	/// rebuild — most recently surfaced when a disabled-structure edit didn't take effect because the
	/// debug build was still reading its own copy.
	///
	/// Resolution order:
	///   1. If <c>%LOCALAPPDATA%/WebullAnalytics/data</c> exists, use <c>%LOCALAPPDATA%/WebullAnalytics</c>
	///      as the base regardless of where the executable lives. This makes the prod data dir the
	///      single source of truth across every invocation path (published wa.exe, dotnet run from
	///      source, bin/Debug/wa.exe, etc).
	///   2. Otherwise fall back to the executable's directory — preserves the prior behavior for
	///      first-run setups where the user hasn't created an AppData dir yet, and for tests that
	///      rely on bin/.../data for fixtures.
	///
	/// Single-file published apps need <c>Environment.ProcessPath</c> rather than
	/// <c>AppContext.BaseDirectory</c> — the latter resolves to a temp extraction dir.
	/// </summary>
	internal static readonly string BaseDir = ResolveBaseDir();

	private static string ResolveBaseDir()
	{
		var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
		try
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!string.IsNullOrEmpty(appData))
			{
				var candidate = Path.Combine(appData, "WebullAnalytics");
				if (Directory.Exists(Path.Combine(candidate, "data")))
					return candidate;
			}
		}
		catch (PlatformNotSupportedException) { }
		catch (IOException) { }
		return exeDir;
	}

	internal const string ApiConfigPath = "data/api-config.json";
	internal const string OrdersPath = "data/orders.jsonl";
	internal const string AppConfigPath = "data/config.json";
	internal const string DerivativeIdsPath = "data/derivative-ids.json";

	/// <summary>
	/// Resolves a path relative to the executable's directory. Absolute paths are returned as-is.
	/// </summary>
	internal static string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDir, path));

	/// <summary>Raw CLI args, used to detect which options were explicitly provided on the command line.</summary>
	internal static string[] RawArgs = [];

	static int Main(string[] args)
	{
		// Windows defaults to legacy code page (1252/437) for Console output; force UTF-8 so
		// Unicode characters in Spectre tables, markup, and rationales render correctly even
		// in terminals that support UTF-8.
		Console.OutputEncoding = System.Text.Encoding.UTF8;

		RawArgs = args;
		var app = new CommandApp();
		app.Configure(config =>
		{
			config.SetApplicationName("WebullAnalytics");
			config.Settings.StrictParsing = true;
			config.AddCommand<ReportCommand>("report");
			config.AddBranch("analyze", analyze =>
			{
				analyze.AddCommand<AnalyzeTradeCommand>("trade");
				analyze.AddCommand<AnalyzeRiskCommand>("risk");
				analyze.AddCommand<AnalyzeRollCommand>("roll");
				analyze.AddCommand<AnalyzePositionCommand>("position");
				analyze.AddCommand<AnalyzeGexCommand>("gex");
				analyze.AddCommand<AnalyzeSentimentCommand>("sentiment");
			});
			config.AddCommand<FetchCommand>("fetch");
			config.AddCommand<SniffCommand>("sniff");
			config.AddBranch("trade", trade =>
			{
				trade.AddCommand<TradePlaceCommand>("place");
				trade.AddCommand<TradeCancelCommand>("cancel");
				trade.AddCommand<TradeStatusCommand>("status");
				trade.AddCommand<TradeListCommand>("list");
				trade.AddCommand<TradeHistoryCommand>("history");
				trade.AddCommand<TradeAccountsCommand>("accounts");
				trade.AddCommand<TradePositionsCommand>("positions");
				trade.AddBranch("token", token =>
				{
					token.AddCommand<TradeTokenCreateCommand>("create");
					token.AddCommand<TradeTokenCheckCommand>("check");
				});
			});
			config.AddBranch("ai", ai =>
			{
				ai.AddCommand<AI.AIScanCommand>("scan");
				ai.AddCommand<AI.AIWatchCommand>("watch");
				ai.AddCommand<AI.AIReplayCommand>("replay");
				ai.AddCommand<AI.AIBacktestCommand>("backtest");
				ai.AddCommand<AI.AIHistoryCommand>("history");
			});
			config.AddBranch("options", options =>
			{
				options.AddCommand<Options.OptionsDiscoverCommand>("discover");
				options.AddCommand<Options.OptionsBackfillCommand>("backfill");
				options.AddCommand<Options.OptionsSeedChainCommand>("chain");
			});
			config.AddBranch("data", data =>
			{
				data.AddCommand<Data.DataBackupCommand>("backup");
				data.AddCommand<Data.DataRestoreCommand>("restore");
			});
		});
		return app.Run(args);
	}

	private static Dictionary<string, JsonElement>? _appConfigRoot;
	private static bool _appConfigLoaded;

	/// <summary>Loads and caches the root config.json dictionary. Returns null if the file doesn't exist or is invalid.</summary>
	internal static Dictionary<string, JsonElement>? LoadAppConfigRoot()
	{
		if (_appConfigLoaded) return _appConfigRoot;
		_appConfigLoaded = true;
		var path = ResolvePath(AppConfigPath);
		if (!File.Exists(path)) return null;
		try { _appConfigRoot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)); }
		catch (JsonException ex) { Console.WriteLine($"Warning: Failed to parse config.json: {ex.Message}"); }
		return _appConfigRoot;
	}

	/// <summary>Returns a named section (e.g. "report", "fetch") from config.json, or null if missing.</summary>
	internal static Dictionary<string, JsonElement>? LoadAppConfig(string section)
	{
		var root = LoadAppConfigRoot();
		if (root != null && root.TryGetValue(section, out var sectionElement))
			return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sectionElement.GetRawText());
		return null;
	}

	/// <summary>Returns true if the given CLI option name (e.g. "source", "yahoo") was explicitly passed on the command line.</summary>
	internal static bool HasCliOption(string optionName) => RawArgs.Any(a => a.Equals($"--{optionName}", StringComparison.OrdinalIgnoreCase));
}

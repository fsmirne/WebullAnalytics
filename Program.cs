using Spectre.Console.Cli;
using System.Text.Json;

namespace WebullAnalytics;

/// <summary>
/// Entry point for WebullAnalytics CLI.
/// </summary>
class Program
{
	/// <summary>
	/// Base directory for resolving relative paths — the directory containing the executable.
	/// For single-file published apps, AppContext.BaseDirectory points to a temp extraction directory,
	/// so we use the actual executable path from ProcessPath instead.
	/// </summary>
	internal static readonly string BaseDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

	internal const string ApiConfigPath = "data/api-config.json";
	internal const string OrdersPath = "data/orders.jsonl";
	internal const string AppConfigPath = "data/config.json";

	/// <summary>
	/// Resolves a path relative to the executable's directory. Absolute paths are returned as-is.
	/// </summary>
	internal static string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDir, path));

	/// <summary>Raw CLI args, used to detect which options were explicitly provided on the command line.</summary>
	internal static string[] RawArgs = [];

	static int Main(string[] args)
	{
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
				analyze.AddCommand<AnalyzeRollCommand>("roll");
			});
			config.AddCommand<FetchCommand>("fetch");
			config.AddCommand<SniffCommand>("sniff");
			config.AddBranch("trade", trade =>
			{
				trade.AddCommand<TradePlaceCommand>("place");
				trade.AddCommand<TradeCancelCommand>("cancel");
				trade.AddCommand<TradeStatusCommand>("status");
				trade.AddCommand<TradeListCommand>("list");
				trade.AddCommand<TradeAccountsCommand>("accounts");
				trade.AddBranch("token", token =>
				{
					token.AddCommand<TradeTokenCreateCommand>("create");
					token.AddCommand<TradeTokenCheckCommand>("check");
				});
			});
			config.AddBranch("ai", ai =>
			{
				ai.AddCommand<AI.AIOnceCommand>("once");
				ai.AddCommand<AI.AIWatchCommand>("watch");
				ai.AddCommand<AI.AIReplayCommand>("replay");
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

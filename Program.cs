using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
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
			config.AddCommand<FetchCommand>("fetch");
			config.AddCommand<SniffCommand>("sniff");
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
		var path = ResolvePath("data/config.json");
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

	/// <summary>Returns true if the given CLI option name (e.g. "source", "iv-long") was explicitly passed on the command line.</summary>
	internal static bool HasCliOption(string optionName) => RawArgs.Any(a => a.Equals($"--{optionName}", StringComparison.OrdinalIgnoreCase));
}

class ReportSettings : CommandSettings
{
	[Description("Data source: 'api' (JSONL from API, default) or 'export' (Webull CSV exports)")]
	[CommandOption("--source")]
	[DefaultValue("api")]
	public string Source { get; set; } = "api";

	[Description("Path to the JSONL orders file")]
	[CommandOption("--data-orders")]
	[DefaultValue("data/orders.jsonl")]
	public string DataOrders { get; set; } = "data/orders.jsonl";

	[Description("Fetch orders from the Webull API before generating the report")]
	[CommandOption("--fetch")]
	[DefaultValue(false)]
	public bool Fetch { get; set; }

	[Description("Path to the API config JSON file (used with --fetch)")]
	[CommandOption("--config")]
	[DefaultValue("data/api-config.json")]
	public string Config { get; set; } = "data/api-config.json";

	[Description("Include only trades on or after this date (YYYY-MM-DD format)")]
	[CommandOption("--since")]
	public string? Since { get; set; }

	[Description("Output format: 'console', 'excel', or 'text'")]
	[CommandOption("--output")]
	[DefaultValue("console")]
	public string OutputFormat { get; set; } = "console";

	[Description("Path for output file (used with --output excel or text)")]
	[CommandOption("--output-path")]
	public string? OutputPath { get; set; }

	[Description("Initial portfolio amount in dollars (default: 0)")]
	[CommandOption("--initial-amount")]
	[DefaultValue(0)]
	public decimal InitialAmount { get; set; } = 0m;

	[Description("Report view: 'detailed' (all columns, default) or 'simplified' (hides Asset, Option, Closed Qty, Realized P&L)")]
	[CommandOption("--view")]
	[DefaultValue("detailed")]
	public string View { get; set; } = "detailed";

	[Description("Implied volatility for long legs (annual %, e.g., 50 for 50%)")]
	[CommandOption("--iv-long")]
	public decimal? ImpliedVolatilityLong { get; set; }

	[Description("Implied volatility for short legs (annual %, e.g., 50 for 50%)")]
	[CommandOption("--iv-short")]
	public decimal? ImpliedVolatilityShort { get; set; }

	[Description("Fetch option chain data from Yahoo Finance for break-even analysis (bid/ask/IV/etc)")]
	[CommandOption("--yahoo")]
	[DefaultValue(false)]
	public bool UseYahoo { get; set; }

	[Description("Grid granularity: rows per strike gap in the time-decay grid (default: 2, higher = more rows)")]
	[CommandOption("--range")]
	[DefaultValue(2.0)]
	public decimal Range { get; set; } = 2;

	[Description("Grid display mode: 'value' (contract value, default) or 'pnl' (profit/loss)")]
	[CommandOption("--display")]
	[DefaultValue("value")]
	public string DisplayMode { get; set; } = "value";

	[Description("Override underlying price(s). Format: TICKER:PRICE (e.g., GME:24.88). Comma-separated for multiple tickers (e.g., GME:24.88,SPY:580.50)")]
	[CommandOption("--current-underlying-price")]
	public string? CurrentUnderlyingPrice { get; set; }

	[Description("Use Black-Scholes theoretical price instead of market mid for today's option value in the time-decay grid")]
	[CommandOption("--theoretical")]
	[DefaultValue(false)]
	public bool Theoretical { get; set; }

	[Description("Additional prices to show in break-even reports. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30,SPY:580/590)")]
	[CommandOption("--notable-prices")]
	public string? NotablePrices { get; set; }

	public bool Simplified => View.Equals("simplified", StringComparison.OrdinalIgnoreCase);

	public DateTime SinceDate => Since != null ? DateTime.ParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.MinValue;

	/// <summary>Applies config.json defaults for any option not explicitly passed on the CLI.</summary>
	internal void ApplyConfig(Dictionary<string, JsonElement> cfg)
	{
		if (!Program.HasCliOption("source") && cfg.TryGetString("source", out var source)) Source = source;
		if (!Program.HasCliOption("data-orders") && cfg.TryGetString("dataOrders", out var dataOrders)) DataOrders = dataOrders;
		if (!Program.HasCliOption("fetch") && cfg.TryGetBool("fetch", out var fetch)) Fetch = fetch;
		if (!Program.HasCliOption("config") && cfg.TryGetString("config", out var config)) Config = config;
		if (!Program.HasCliOption("since") && cfg.TryGetString("since", out var since)) Since = since;
		if (!Program.HasCliOption("output") && cfg.TryGetString("output", out var output)) OutputFormat = output;
		if (!Program.HasCliOption("output-path") && cfg.TryGetString("outputPath", out var outputPath)) OutputPath = outputPath;
		if (!Program.HasCliOption("initial-amount") && cfg.TryGetDecimal("initialAmount", out var initialAmount)) InitialAmount = initialAmount;
		if (!Program.HasCliOption("view") && cfg.TryGetString("view", out var view)) View = view;
		if (!Program.HasCliOption("iv-long") && cfg.TryGetDecimal("ivLong", out var ivLong)) ImpliedVolatilityLong = ivLong;
		if (!Program.HasCliOption("iv-short") && cfg.TryGetDecimal("ivShort", out var ivShort)) ImpliedVolatilityShort = ivShort;
		if (!Program.HasCliOption("yahoo") && cfg.TryGetBool("yahoo", out var yahoo)) UseYahoo = yahoo;
		if (!Program.HasCliOption("range") && cfg.TryGetDecimal("range", out var range)) Range = range;
		if (!Program.HasCliOption("display") && cfg.TryGetString("display", out var display)) DisplayMode = display;
		if (!Program.HasCliOption("current-underlying-price") && cfg.TryGetString("currentUnderlyingPrice", out var cup)) CurrentUnderlyingPrice = cup;
		if (!Program.HasCliOption("theoretical") && cfg.TryGetBool("theoretical", out var theoretical)) Theoretical = theoretical;
		if (!Program.HasCliOption("notable-prices") && cfg.TryGetString("notablePrices", out var notablePrices)) NotablePrices = notablePrices;
	}

	public override ValidationResult Validate()
	{
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error("--since must be in YYYY-MM-DD format");

		var source = Source.ToLowerInvariant();
		if (source is not ("api" or "export"))
			return ValidationResult.Error("--source must be 'api' or 'export'");

		if (Fetch && source == "export")
			return ValidationResult.Error("--fetch cannot be used with --source export");

		if (Fetch && !File.Exists(Program.ResolvePath(Config)))
			return ValidationResult.Error($"--fetch requires a config file. '{Config}' does not exist.");

		var format = OutputFormat.ToLowerInvariant();
		if (format is not ("console" or "excel" or "text"))
			return ValidationResult.Error("--output must be 'console', 'excel', or 'text'");

		var view = View.ToLowerInvariant();
		if (view is not ("detailed" or "simplified"))
			return ValidationResult.Error("--view must be 'detailed' or 'simplified'");

		if (Range <= 0)
			return ValidationResult.Error("--range must be greater than 0");

		var display = DisplayMode.ToLowerInvariant();
		if (display is not ("value" or "pnl"))
			return ValidationResult.Error("--display must be 'value' or 'pnl'");

		if (CurrentUnderlyingPrice != null)
		{
			foreach (var pair in CurrentUnderlyingPrice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2 || !decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
					return ValidationResult.Error($"--current-underlying-price: invalid entry '{pair}'. Expected format: TICKER:PRICE (e.g., GME:24.88)");
			}
		}

		if (NotablePrices != null)
		{
			foreach (var pair in NotablePrices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2)
					return ValidationResult.Error($"--notable-prices: invalid entry '{pair}'. Expected format: TICKER:P1/P2/P3 (e.g., GME:20/25/30)");
				foreach (var priceStr in parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					if (!decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
						return ValidationResult.Error($"--notable-prices: invalid price '{priceStr}' for ticker '{parts[0].Trim()}'. Prices must be numeric.");
				}
			}
		}

		return ValidationResult.Success();
	}
}

class FetchSettings : CommandSettings
{
	[Description("Path to the API config JSON file")]
	[CommandOption("--config")]
	[DefaultValue("data/api-config.json")]
	public string Config { get; set; } = "data/api-config.json";

	[Description("Output path for the JSONL orders file")]
	[CommandOption("--output")]
	[DefaultValue("data/orders.jsonl")]
	public string Output { get; set; } = "data/orders.jsonl";

	/// <summary>Applies config.json defaults for any option not explicitly passed on the CLI.</summary>
	internal void ApplyConfig(Dictionary<string, JsonElement> cfg)
	{
		if (!Program.HasCliOption("config") && cfg.TryGetString("config", out var config)) Config = config;
		if (!Program.HasCliOption("output") && cfg.TryGetString("output", out var output)) Output = output;
	}

	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Config))) return ValidationResult.Error($"Config file '{Config}' does not exist.");
		return ValidationResult.Success();
	}
}

class FetchCommand : AsyncCommand<FetchSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, FetchSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("fetch");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		var configPath = Program.ResolvePath(settings.Config);
		var outputPath = Program.ResolvePath(settings.Output);

		var config = LoadApiConfig(configPath);
		if (config == null) return 1;

		Console.WriteLine($"Fetching orders for {config.TickerIds.Length} ticker(s)...");
		await ApiClient.FetchOrdersToJsonl(config, outputPath);
		Console.WriteLine($"Written to {outputPath}");
		return 0;
	}

	internal static ApiConfig? LoadApiConfig(string path)
	{
		var json = File.ReadAllText(path);
		var config = JsonSerializer.Deserialize<ApiConfig>(json);
		if (config == null || config.TickerIds.Length == 0)
		{
			Console.WriteLine("Error: Config file must contain tickerIds.");
			return null;
		}
		return config;
	}
}

class SniffSettings : CommandSettings
{
	[Description("Path to the API config JSON file")]
	[CommandOption("--config")]
	[DefaultValue("data/api-config.json")]
	public string Config { get; set; } = "data/api-config.json";

	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Config))) return ValidationResult.Error($"Config file '{Config}' does not exist.");
		return ValidationResult.Success();
	}
}

class SniffCommand : AsyncCommand<SniffSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, SniffSettings settings, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(settings.Config);
		var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
		if (config == null)
		{
			Console.WriteLine("Error: Failed to parse api-config.json.");
			return 1;
		}
		if (string.IsNullOrWhiteSpace(config.Pin))
		{
			Console.WriteLine("Error: 'pin' is required in api-config.json for header sniffing.");
			return 1;
		}

		var sniffConfig = Program.LoadAppConfig("sniff");
		var autoCloseEdge = sniffConfig != null && sniffConfig.TryGetBool("autoCloseEdge", out var ace) && ace;

		try
		{
			var headers = await HeaderSniffer.CaptureAsync(config.Pin, autoCloseEdge, cancellation);
			Console.WriteLine($"Captured {headers.Count} header(s).");

			var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
			root["headers"] = JsonSerializer.SerializeToNode(headers);
			File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 }));

			Console.WriteLine($"Updated headers in {configPath}");
			return 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
		{
			Console.WriteLine($"Error: {ex.Message}");
			return 1;
		}
	}
}

class ReportCommand : AsyncCommand<ReportSettings>
{
	private static readonly string[] WebullCsvFiles =
	[
		"Webull_Orders_Records.csv",
		"Webull_Orders_Records_Bonds.csv",
		"Webull_Orders_Records_Options.csv",
	];

	public override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);
		var rootConfig = Program.LoadAppConfigRoot();
		var autoExpandTerminal = rootConfig != null && rootConfig.TryGetBool("autoExpandTerminal", out var ae) && ae;

		var ordersPath = Program.ResolvePath(settings.DataOrders);
		var dataDir = Path.GetDirectoryName(ordersPath) ?? ".";

		if (settings.Fetch)
		{
			var configPath = Program.ResolvePath(settings.Config);
			var config = FetchCommand.LoadApiConfig(configPath);
			if (config == null) return 1;

			Console.WriteLine($"Fetching orders for {config.TickerIds.Length} ticker(s)...");
			await ApiClient.FetchOrdersToJsonl(config, ordersPath);
		}

		List<Trade> trades;
		Dictionary<(DateTime, Side, int), decimal>? feeLookup = null;

		if (settings.Source.Equals("export", StringComparison.OrdinalIgnoreCase))
		{
			trades = LoadCsvTrades(dataDir);
			if (trades.Count == 0)
			{
				Console.WriteLine($"Error: No Webull CSV export files found in '{dataDir}'.");
				return 1;
			}
			if (File.Exists(ordersPath))
				(_, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
		}
		else
		{
			if (!File.Exists(ordersPath))
			{
				Console.WriteLine($"Error: Orders file '{ordersPath}' does not exist.");
				return 1;
			}
			(trades, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
			ReconcileParentPrices(trades);
			ApplyOfficialPrices(trades, dataDir);
		}
		var initialAmount = settings.InitialAmount;
		var (rows, positions, running) = PositionTracker.ComputeReport(trades, settings.SinceDate, initialAmount, feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);
		var positionRows = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		var dateStr = DateTime.Now.ToString("yyyyMMdd");
		var ivLong = settings.ImpliedVolatilityLong.HasValue ? settings.ImpliedVolatilityLong.Value / 100m : (decimal?)null;
		var ivShort = settings.ImpliedVolatilityShort.HasValue ? settings.ImpliedVolatilityShort.Value / 100m : (decimal?)null;
		IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol = null;
		IReadOnlyDictionary<string, decimal>? underlyingPrices = null;
		if (settings.UseYahoo)
		{
			try
			{
				Console.WriteLine("Yahoo Finance: fetching option chain data...");
				var yahooData = await YahooOptionsClient.FetchOptionQuotesAsync(positionRows, cancellation);
				optionQuotesBySymbol = yahooData.OptionQuotes;
				underlyingPrices = yahooData.UnderlyingPrices;
				Console.WriteLine($"Yahoo Finance: retrieved {optionQuotesBySymbol.Count} contract quote(s).");
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException) throw;
				Console.WriteLine($"Warning: Failed to fetch Yahoo Finance option data: {ex.Message}");
			}
		}

		IReadOnlyDictionary<string, decimal>? underlyingPriceOverrides = null;
		if (settings.CurrentUnderlyingPrice != null)
		{
			var overrides = ParseUnderlyingPriceOverrides(settings.CurrentUnderlyingPrice);
			if (overrides.Count > 0)
				underlyingPriceOverrides = overrides;
		}

		var displayMode = settings.DisplayMode.ToLowerInvariant();

		IReadOnlyDictionary<string, List<decimal>>? extraNotablePrices = null;
		if (settings.NotablePrices != null)
		{
			var parsed = ParseNotablePrices(settings.NotablePrices);
			if (parsed.Count > 0)
				extraNotablePrices = parsed;
		}

		switch (settings.OutputFormat.ToLowerInvariant())
		{
			case "excel":
				ExcelExporter.ExportToExcel(rows, positionRows, trades, running, initialAmount, settings.OutputPath ?? $"WebullAnalytics_{dateStr}.xlsx", ivLong, ivShort, optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, settings.Theoretical, extraNotablePrices);
				break;

			case "text":
				TextFileExporter.ExportToTextFile(rows, positionRows, running, initialAmount, settings.OutputPath ?? $"WebullAnalytics_{dateStr}.txt", settings.Simplified, ivLong, ivShort, settings.Range, displayMode, optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, settings.Theoretical, extraNotablePrices);
				break;

			default:
				TerminalHelper.EnsureTerminalWidth(settings.Simplified, autoExpandTerminal);
				TableRenderer.RenderReport(rows, positionRows, running, initialAmount, settings.Simplified, ivLong, ivShort, settings.Range, displayMode, optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, settings.Theoretical, extraNotablePrices);
				break;
		}

		return 0;
	}

	private static Dictionary<string, decimal> ParseUnderlyingPriceOverrides(string input)
	{
		var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var pair in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = pair.Split(':', 2);
			if (parts.Length == 2 && decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
				result[parts[0].Trim().ToUpperInvariant()] = price;
			else
				Console.WriteLine($"Warning: Ignoring invalid underlying price override '{pair}'. Expected format: TICKER:PRICE");
		}
		return result;
	}

	private static Dictionary<string, List<decimal>> ParseNotablePrices(string input)
	{
		var result = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
		foreach (var pair in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = pair.Split(':', 2);
			if (parts.Length != 2) continue;
			var ticker = parts[0].Trim().ToUpperInvariant();
			var prices = new List<decimal>();
			foreach (var priceStr in parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
					prices.Add(price);
			}
			if (prices.Count > 0)
			{
				if (!result.TryGetValue(ticker, out var existing))
					result[ticker] = prices;
				else
					existing.AddRange(prices);
			}
		}
		return result;
	}

	private static List<Trade> LoadCsvTrades(string dataDir)
	{
		var trades = new List<Trade>();
		var seq = 0;
		foreach (var filename in WebullCsvFiles)
		{
			var path = Path.Combine(dataDir, filename);
			if (!File.Exists(path)) continue;
			var (parsed, nextSeq) = CsvParser.ParseTradeCsv(path, seq);
			trades.AddRange(parsed);
			seq = nextSeq;
		}
		return trades;
	}

	/// <summary>
	/// Recomputes strategy parent prices from their stored leg prices.
	/// Parent prices are originally computed from raw JSONL values, but leg prices may differ
	/// due to rounding or CSV overrides. This ensures parent cash flows match leg-level P&L.
	/// </summary>
	private static void ReconcileParentPrices(List<Trade> trades)
	{
		for (int i = 0; i < trades.Count; i++)
		{
			var trade = trades[i];
			if (trade.Asset != Asset.OptionStrategy || trade.Side is not (Side.Buy or Side.Sell))
				continue;

			var legs = trades.Where(t => t.ParentStrategySeq == trade.Seq).ToList();
			if (legs.Count < 2) continue;

			var netCash = legs.Sum(leg => (leg.Side == Side.Sell ? 1m : -1m) * leg.Price * leg.Qty);
			var expectedSide = netCash >= 0 ? Side.Sell : Side.Buy;
			var expectedPrice = Math.Abs(netCash) / trade.Qty;

			if (expectedPrice != trade.Price || expectedSide != trade.Side)
				trades[i] = trade with { Price = expectedPrice, Side = expectedSide };
		}
	}

	/// <summary>
	/// Checks the data directory for Webull CSV export files and uses their prices
	/// to override JSONL-computed values. The JSONL API rounds individual leg prices,
	/// losing sub-penny precision that the CSV exports preserve.
	/// </summary>
	private static void ApplyOfficialPrices(List<Trade> trades, string dataDir)
	{
		var csvTrades = LoadCsvTrades(dataDir);
		if (csvTrades.Count == 0) return;

		var csvPriceByKey = new Dictionary<(string matchKey, Side side, DateTime timestamp), decimal>();
		foreach (var t in csvTrades)
			csvPriceByKey.TryAdd((t.MatchKey, t.Side, t.Timestamp), t.Price);

		for (var i = 0; i < trades.Count; i++)
		{
			var t = trades[i];
			if (csvPriceByKey.TryGetValue((t.MatchKey, t.Side, t.Timestamp), out var officialPrice) && officialPrice != t.Price)
				trades[i] = t with { Price = officialPrice };
		}
	}
}

static class JsonElementExtensions
{
	internal static bool TryGetString(this Dictionary<string, JsonElement> cfg, string key, out string value)
	{
		value = "";
		if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
		value = el.GetString()!;
		return true;
	}

	internal static bool TryGetBool(this Dictionary<string, JsonElement> cfg, string key, out bool value)
	{
		value = false;
		if (!cfg.TryGetValue(key, out var el) || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
		value = el.GetBoolean();
		return true;
	}

	internal static bool TryGetDecimal(this Dictionary<string, JsonElement> cfg, string key, out decimal value)
	{
		value = 0;
		if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Number) return false;
		value = el.GetDecimal();
		return true;
	}
}

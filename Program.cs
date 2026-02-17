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

	static int Main(string[] args)
	{
		var app = new CommandApp();
		app.Configure(config =>
		{
			config.SetApplicationName("WebullAnalytics");
			config.Settings.StrictParsing = true;
			config.AddCommand<ReportCommand>("report");
			config.AddCommand<FetchCommand>("fetch");
		});
		return app.Run(args);
	}
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

	[Description("Path for Excel output file")]
	[CommandOption("--excel-path")]
	public string? ExcelPath { get; set; }

	[Description("Path for text output file")]
	[CommandOption("--text-path")]
	public string? TextPath { get; set; }

	[Description("Initial portfolio amount in dollars (default: 0)")]
	[CommandOption("--initial-amount")]
	[DefaultValue(0)]
	public decimal InitialAmount { get; set; } = 0m;

	public DateTime SinceDate => Since != null ? DateTime.ParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.MinValue;

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
			ApplyOfficialPrices(trades, dataDir);
		}

		var initialAmount = settings.InitialAmount;
		var (rows, positions, running) = PositionTracker.ComputeReport(trades, settings.SinceDate, initialAmount, feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);
		var positionRows = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		var dateStr = DateTime.Now.ToString("yyyyMMdd");

		switch (settings.OutputFormat.ToLowerInvariant())
		{
			case "excel":
				var excelPath = settings.ExcelPath ?? $"WebullAnalytics_{dateStr}.xlsx";
				ExcelExporter.ExportToExcel(rows, positionRows, trades, running, initialAmount, excelPath);
				break;

			case "text":
				var textPath = settings.TextPath ?? $"WebullAnalytics_{dateStr}.txt";
				TextFileExporter.ExportToTextFile(rows, positionRows, running, initialAmount, textPath);
				break;

			default:
				TerminalHelper.EnsureTerminalWidth();
				TableRenderer.RenderReport(rows, positionRows, running, initialAmount);
				break;
		}

		return 0;
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

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics;

class ReportSettings : CommandSettings
{
	[Description("Data source: 'api' (JSONL from API, default) or 'export' (Webull CSV exports)")]
	[CommandOption("--source")]
	[DefaultValue("api")]
	public string Source { get; set; } = "api";

	[Description("Include only trades on or after this date (YYYY-MM-DD format)")]
	[CommandOption("--since")]
	public string? Since { get; set; }

	[Description("Include only trades on or before this date (YYYY-MM-DD format)")]
	[CommandOption("--until")]
	public string? Until { get; set; }

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

	[Description("Override implied volatility per option leg. Format: SYMBOL:IV% (e.g., GME260213C00025000:50). Comma-separated for multiple legs.")]
	[CommandOption("--iv")]
	public string? IvOverrides { get; set; }

	[Description("Option chain data source for break-even analysis: 'yahoo' or 'webull' (requires sniffed headers via 'sniff' command)")]
	[CommandOption("--api")]
	public string? Api { get; set; }

	[Description("Grid granularity: rows per strike gap in the time-decay grid (default: 2, higher = more rows)")]
	[CommandOption("--range")]
	[DefaultValue(2.0)]
	public decimal Range { get; set; } = 2;

	[Description("Grid display mode: 'value' (contract value, default) or 'pnl' (profit/loss)")]
	[CommandOption("--display")]
	[DefaultValue("value")]
	public string DisplayMode { get; set; } = "value";

	[Description("Grid cell layout: 'simple' (net only, default) or 'verbose' (per-leg contract values before the net, e.g. '1.23|0.45|$0.78')")]
	[CommandOption("--grid")]
	[DefaultValue("simple")]
	public string Grid { get; set; } = "simple";

	public bool ShowLegs => Grid.Equals("verbose", StringComparison.OrdinalIgnoreCase);

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

	[Description("Show only these tickers in the report. Comma-separated list (e.g., GME,SPY,AAPL)")]
	[CommandOption("--tickers")]
	public string? Tickers { get; set; }

	public bool Simplified => View.Equals("simplified", StringComparison.OrdinalIgnoreCase);

	public DateTime SinceDate => Since != null ? DateTime.ParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.MinValue;

	public DateTime UntilDate => Until != null ? DateTime.ParseExact(Until, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.MaxValue;

	public HashSet<string>? TickerFilter => Tickers != null ? new HashSet<string>(Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase) : null;

	/// <summary>Applies config.json defaults for any option not explicitly passed on the CLI.</summary>
	internal void ApplyConfig(Dictionary<string, JsonElement> cfg)
	{
		if (!Program.HasCliOption("source") && cfg.TryGetString("source", out var source)) Source = source;
		if (!Program.HasCliOption("since") && cfg.TryGetString("since", out var since)) Since = since;
		if (!Program.HasCliOption("until") && cfg.TryGetString("until", out var until)) Until = until;
		if (!Program.HasCliOption("output") && cfg.TryGetString("output", out var output)) OutputFormat = output;
		if (!Program.HasCliOption("output-path") && cfg.TryGetString("outputPath", out var outputPath)) OutputPath = outputPath;
		if (!Program.HasCliOption("initial-amount") && cfg.TryGetDecimal("initialAmount", out var initialAmount)) InitialAmount = initialAmount;
		if (!Program.HasCliOption("view") && cfg.TryGetString("view", out var view)) View = view;
		if (!Program.HasCliOption("iv") && cfg.TryGetString("iv", out var iv)) IvOverrides = iv;
		if (!Program.HasCliOption("api") && cfg.TryGetString("api", out var api)) Api = api;
		if (!Program.HasCliOption("range") && cfg.TryGetDecimal("range", out var range)) Range = range;
		if (!Program.HasCliOption("display") && cfg.TryGetString("display", out var display)) DisplayMode = display;
		if (!Program.HasCliOption("grid") && cfg.TryGetString("grid", out var grid)) Grid = grid;
		if (!Program.HasCliOption("current-underlying-price") && cfg.TryGetString("currentUnderlyingPrice", out var cup)) CurrentUnderlyingPrice = cup;
		if (!Program.HasCliOption("theoretical") && cfg.TryGetBool("theoretical", out var theoretical)) Theoretical = theoretical;
		if (!Program.HasCliOption("notable-prices") && cfg.TryGetString("notablePrices", out var notablePrices)) NotablePrices = notablePrices;
		if (!Program.HasCliOption("tickers") && cfg.TryGetString("tickers", out var tickers)) Tickers = tickers;
	}

	public override ValidationResult Validate()
	{
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error("--since must be in YYYY-MM-DD format");

		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error("--until must be in YYYY-MM-DD format");

		if (Since != null && Until != null && SinceDate > UntilDate)
			return ValidationResult.Error("--since date must not be after --until date");

		var source = Source.ToLowerInvariant();
		if (source is not ("api" or "export"))
			return ValidationResult.Error("--source must be 'api' or 'export'");

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

		var grid = Grid.ToLowerInvariant();
		if (grid is not ("simple" or "verbose"))
			return ValidationResult.Error("--grid must be 'simple' or 'verbose'");

		if (Api != null && Api.ToLowerInvariant() is not ("yahoo" or "webull"))
			return ValidationResult.Error("--api must be 'yahoo' or 'webull'");

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

		var (trades, feeLookup, err) = LoadTrades(settings);
		if (err != 0) return err;

		return await RunReportPipeline(settings, trades, feeLookup, cancellation);
	}

	internal static (List<Trade> trades, Dictionary<(DateTime, Side, int), decimal>? feeLookup, int errorCode) LoadTrades(ReportSettings settings)
	{
		var ordersPath = Program.ResolvePath(Program.OrdersPath);
		var dataDir = Path.GetDirectoryName(ordersPath) ?? ".";

		List<Trade> trades;
		Dictionary<(DateTime, Side, int), decimal>? feeLookup = null;

		if (settings.Source.Equals("export", StringComparison.OrdinalIgnoreCase))
		{
			trades = LoadCsvTrades(dataDir);
			if (trades.Count == 0)
			{
				Console.WriteLine($"Error: No Webull CSV export files found in '{dataDir}'.");
				return (trades, null, 1);
			}
			if (File.Exists(ordersPath))
				(_, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
		}
		else
		{
			if (!File.Exists(ordersPath))
			{
				Console.WriteLine($"Error: Orders file '{ordersPath}' does not exist.");
				return ([], null, 1);
			}
			(trades, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
			ReconcileParentPrices(trades);
			ApplyOfficialPrices(trades, dataDir);
		}
		return (trades, feeLookup, 0);
	}

	internal static async Task<int> RunReportPipeline(ReportSettings settings, List<Trade> trades, Dictionary<(DateTime, Side, int), decimal>? feeLookup, CancellationToken cancellation)
	{
		var rootConfig = Program.LoadAppConfigRoot();
		var autoExpandTerminal = rootConfig != null && rootConfig.TryGetBool("autoExpandTerminal", out var ae) && ae;

		var tickerFilter = settings.TickerFilter;
		if (tickerFilter != null)
			trades.RemoveAll(t => { var ticker = MatchKeys.GetTicker(t.MatchKey); return ticker == null || !tickerFilter.Contains(ticker); });
		if (settings.Since != null)
			trades.RemoveAll(t => t.Timestamp.Date < settings.SinceDate.Date);
		if (settings.Until != null)
		{
			trades.RemoveAll(t => t.Timestamp.Date > settings.UntilDate.Date);
			EvaluationDate.Set(settings.UntilDate);
		}

		var initialAmount = settings.InitialAmount;
		var (rows, positions, running) = PositionTracker.ComputeReport(trades, initialAmount, feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);
		var (positionRows, strategyAdjustments, singleLegStandalones) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		var dateStr = DateTime.Now.ToString("yyyyMMdd");
		IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol = null;
		IReadOnlyDictionary<string, decimal>? underlyingPrices = null;
		var apiSource = settings.Api?.ToLowerInvariant();
		if (apiSource != null && positionRows.Count > 0)
		{
			try
			{
				var riskFreeTask = YahooOptionsClient.FetchRiskFreeRateAsync(cancellation);

				if (apiSource == "yahoo")
				{
					Console.WriteLine("Yahoo Finance: fetching option chain data...");
					var yahooData = await YahooOptionsClient.FetchOptionQuotesAsync(positionRows, cancellation);
					optionQuotesBySymbol = yahooData.OptionQuotes;
					underlyingPrices = yahooData.UnderlyingPrices;
					Console.WriteLine($"Yahoo Finance: retrieved {optionQuotesBySymbol.Count} contract quote(s).");
				}
				else if (apiSource == "webull")
				{
					var configPath = Program.ResolvePath(Program.ApiConfigPath);
					if (!File.Exists(configPath))
					{
						Console.WriteLine("Error: api-config.json not found. Run 'sniff' first to capture Webull headers.");
					}
					else
					{
						var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
						if (config == null || config.Headers.Count == 0)
						{
							Console.WriteLine("Error: api-config.json has no headers. Run 'sniff' to capture them.");
						}
						else
						{
							Console.WriteLine("Webull: fetching option chain data...");
							var webullData = await WebullOptionsClient.FetchOptionQuotesAsync(config, positionRows, cancellation);
							optionQuotesBySymbol = webullData.OptionQuotes;
							underlyingPrices = webullData.UnderlyingPrices;
							Console.WriteLine($"Webull: retrieved {optionQuotesBySymbol.Count} contract quote(s).");
						}
					}
				}

				var riskFreeRate = await riskFreeTask;
				if (riskFreeRate.HasValue)
				{
					OptionMath.RiskFreeRate = riskFreeRate.Value;
					Console.WriteLine($"Risk-free rate (13-week T-bill): {riskFreeRate.Value:P2}");
				}
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException) throw;
				Console.WriteLine($"Warning: Failed to fetch option data: {ex.Message}");
			}
		}

		IReadOnlyDictionary<string, decimal>? underlyingPriceOverrides = null;
		if (settings.CurrentUnderlyingPrice != null)
		{
			var overrides = ParseUnderlyingPriceOverrides(settings.CurrentUnderlyingPrice);
			if (overrides.Count > 0)
				underlyingPriceOverrides = overrides;
		}

		IReadOnlyDictionary<string, List<decimal>>? extraNotablePrices = null;
		if (settings.NotablePrices != null)
		{
			var parsed = ParseNotablePrices(settings.NotablePrices);
			if (parsed.Count > 0)
				extraNotablePrices = parsed;
		}

		IReadOnlyDictionary<string, decimal>? ivOverrides = null;
		if (settings.IvOverrides != null)
		{
			var parsed = ParseIvOverrides(settings.IvOverrides);
			if (parsed.Count > 0)
				ivOverrides = parsed;
		}

		var opts = new AnalysisOptions(optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, settings.Theoretical, extraNotablePrices, ivOverrides);
		var displayMode = settings.DisplayMode.ToLowerInvariant();

		var adjustmentBreakdowns = AdjustmentReportBuilder.Build(positionRows, trades, positions, strategyAdjustments, singleLegStandalones);

		switch (settings.OutputFormat.ToLowerInvariant())
		{
			case "excel":
				ExcelExporter.ExportToExcel(rows, positionRows, trades, running, initialAmount, settings.OutputPath ?? $"WebullAnalytics_{dateStr}.xlsx", opts);
				break;

			case "text":
				TextFileExporter.ExportToTextFile(rows, positionRows, running, initialAmount, settings.OutputPath ?? $"WebullAnalytics_{dateStr}.txt", settings.Simplified, opts, settings.Range, displayMode, adjustmentBreakdowns, settings.ShowLegs);
				break;

			default:
				TerminalHelper.EnsureTerminalWidth(settings.Simplified, autoExpandTerminal);
				TableRenderer.RenderReport(rows, positionRows, running, initialAmount, settings.Simplified, opts, settings.Range, displayMode, adjustmentBreakdowns, settings.ShowLegs);
				break;
		}

		return 0;
	}

	internal static Dictionary<string, decimal> ParseUnderlyingPriceOverrides(string input)
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

	internal static Dictionary<string, decimal> ParseIvOverrides(string input)
	{
		var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var pair in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = pair.Split(':', 2);
			if (parts.Length == 2 && decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var ivPct) && ivPct > 0)
				result[parts[0].Trim().ToUpperInvariant()] = ivPct / 100m;
			else
				Console.WriteLine($"Warning: Ignoring invalid IV override '{pair}'. Expected format: SYMBOL:IV% (e.g., GME260213C00025000:50)");
		}
		return result;
	}

	internal static Dictionary<string, List<decimal>> ParseNotablePrices(string input)
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

	internal static List<Trade> LoadCsvTrades(string dataDir)
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
	/// </summary>
	private static void ReconcileParentPrices(List<Trade> trades)
	{
		for (int i = 0; i < trades.Count; i++)
		{
			var trade = trades[i];
			if (trade.Asset != Asset.OptionStrategy || trade.Side is not (Side.Buy or Side.Sell))
				continue;

			var legs = Trade.GetLegs(trades, trade.Seq);
			if (legs.Count < 2) continue;

			var netCash = legs.Sum(leg => (leg.Side == Side.Sell ? 1m : -1m) * leg.Price * leg.Qty);
			var expectedSide = netCash >= 0 ? Side.Sell : Side.Buy;
			var expectedPrice = Math.Abs(netCash) / trade.Qty;

			if (expectedPrice != trade.Price || expectedSide != trade.Side)
				trades[i] = trade with { Price = expectedPrice, Side = expectedSide };
		}
	}

	/// <summary>
	/// Uses Webull CSV export prices to override JSONL-computed values (sub-penny precision).
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

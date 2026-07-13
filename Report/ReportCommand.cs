using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using WebullAnalytics.IO;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Report;

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

	[Description("Report view: 'detailed' (all columns, default) or 'simplified' (hides Asset, Option, Closed, Running)")]
	[CommandOption("--view")]
	[DefaultValue("detailed")]
	public string View { get; set; } = "detailed";

	[Description("Override implied volatility per option leg. Format: SYMBOL:IV% (e.g., GME260213C00025000:50). Comma-separated for multiple legs.")]
	[CommandOption("--iv")]
	public string? IvOverrides { get; set; }

	[Description("Grid granularity: rows per strike gap in the time-decay grid. Default 0 = auto (target 20 rows total). Pass a positive value to override (higher = more rows).")]
	[CommandOption("--range")]
	[DefaultValue(0.0)]
	public decimal Range { get; set; } = 0;

	[Description("Grid display mode: 'value' (contract value, default) or 'pnl' (profit/loss)")]
	[CommandOption("--display")]
	[DefaultValue("value")]
	public string DisplayMode { get; set; } = "value";

	[Description("Grid cell layout: 'simple' (net only, default) or 'verbose' (per-leg contract values before the net, e.g. '1.23|0.45|$0.78')")]
	[CommandOption("--grid")]
	[DefaultValue("simple")]
	public string Grid { get; set; } = "simple";

	public bool ShowLegs => Grid.Equals("verbose", StringComparison.OrdinalIgnoreCase);

	[Description("Override underlying spot price(s). Format: TICKER:PRICE (e.g., GME:24.88). Comma-separated for multiple tickers (e.g., GME:24.88,SPY:580.50)")]
	[CommandOption("--spot")]
	public string? Spot { get; set; }

	[Description("Use Black-Scholes theoretical price instead of market mid for today's option value in the time-decay grid")]
	[CommandOption("--theoretical")]
	[DefaultValue(false)]
	public bool Theoretical { get; set; }

	[Description("ON by default: back-solve each leg's IV from its live bid/ask mid so the grid's today column reproduces market mid and future columns decay on the mid-consistent surface. Pass --calibrated false to instead trust Webull's reported IV field — a debugging view only; the vendor field is 10–50 vol pts off at 0DTE.")]
	[CommandOption("--calibrated")]
	[DefaultValue(true)]
	public bool Calibrated { get; set; } = true;

	[Description("Additional reference price levels (support/resistance, targets) to show in break-even reports. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30,SPY:580/590)")]
	[CommandOption("--levels")]
	public string? Levels { get; set; }

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
		if (!Program.HasCliOption("range") && cfg.TryGetDecimal("range", out var range)) Range = range;
		if (!Program.HasCliOption("display") && cfg.TryGetString("display", out var display)) DisplayMode = display;
		if (!Program.HasCliOption("grid") && cfg.TryGetString("grid", out var grid)) Grid = grid;
		if (!Program.HasCliOption("spot") && cfg.TryGetString("spot", out var cup)) Spot = cup;
		if (!Program.HasCliOption("theoretical") && cfg.TryGetBool("theoretical", out var theoretical)) Theoretical = theoretical;
		if (!Program.HasCliOption("calibrated") && cfg.TryGetBool("calibrated", out var calibrated)) Calibrated = calibrated;
		if (!Program.HasCliOption("levels") && cfg.TryGetString("levels", out var levels)) Levels = levels;
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

		if (Range < 0)
			return ValidationResult.Error("--range must be 0 (auto) or a positive number");

		var display = DisplayMode.ToLowerInvariant();
		if (display is not ("value" or "pnl"))
			return ValidationResult.Error("--display must be 'value' or 'pnl'");

		var grid = Grid.ToLowerInvariant();
		if (grid is not ("simple" or "verbose"))
			return ValidationResult.Error("--grid must be 'simple' or 'verbose'");

		if (Spot != null)
		{
			foreach (var pair in Spot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2 || !decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
					return ValidationResult.Error($"--spot: invalid entry '{pair}'. Expected format: TICKER:PRICE (e.g., GME:24.88)");
			}
		}

		if (Levels != null)
		{
			foreach (var pair in Levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2)
					return ValidationResult.Error($"--levels: invalid entry '{pair}'. Expected format: TICKER:P1/P2/P3 (e.g., GME:20/25/30)");
				foreach (var priceStr in parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					if (!decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
						return ValidationResult.Error($"--levels: invalid price '{priceStr}' for ticker '{parts[0].Trim()}'. Prices must be numeric.");
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

	protected override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellation)
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

	internal static async Task<int> RunReportPipeline(ReportSettings settings, List<Trade> trades, Dictionary<(DateTime, Side, int), decimal>? feeLookup, CancellationToken cancellation, IReadOnlyDictionary<string, OptionContractQuote>? preloadedQuotes = null, IReadOnlyDictionary<string, decimal>? preloadedUnderlyingPrices = null)
	{
		var rootConfig = Program.LoadAppConfigRoot();
		var autoExpandTerminal = rootConfig != null && rootConfig.TryGetBool("autoExpandTerminal", out var ae) && ae;
		int? maxTerminalWidth = rootConfig != null && rootConfig.TryGetInt32("terminalWidth", out var tw) ? tw : null;

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
		var closeLookup = await BuildUnderlyingCloseLookup(trades, cancellation);
		var (rows, positions, running) = PositionTracker.ComputeReport(trades, initialAmount, feeLookup, closeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);
		var (positionRows, strategyAdjustments, singleLegStandalones) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		var dateStr = DateTime.Now.ToString("yyyyMMdd");
		// Fetch live option chain + risk-free rate so break-even panels show the time-decay grid with
		// current IV / spot / theta. Failures (no api-config.json, expired headers, network blip) degrade
		// gracefully: the report falls through to the 1D price ladder. Skipped when there are no positions
		// to enrich.
		IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol = preloadedQuotes;
		IReadOnlyDictionary<string, decimal>? underlyingPrices = preloadedUnderlyingPrices;
		IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends = null;
		if (positionRows.Count > 0)
		{
			try
			{
				var riskFreeTask = YahooOptionsClient.FetchRiskFreeRateAsync(cancellation);

				if (preloadedQuotes == null)
				{
					var configPath = Program.ResolvePath(Program.ApiConfigPath);
					if (!File.Exists(configPath))
					{
						Console.WriteLine("Note: api-config.json not found — skipping live chain fetch. Run 'sniff' first to enable the time-decay grid.");
					}
					else
					{
						var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
						if (config == null || config.Webull.Headers.Count == 0)
						{
							Console.WriteLine("Note: api-config.json has no headers — skipping live chain fetch. Run 'sniff' to capture them.");
						}
						else
						{
							WebullAnalytics.Utils.Log.Debug("Webull: fetching option chain data...");
							var webullData = await WebullOptionsClient.FetchOptionQuotesAsync(config, positionRows, cancellation);
							optionQuotesBySymbol = webullData.OptionQuotes;
							underlyingPrices = webullData.UnderlyingPrices;
							WebullAnalytics.Utils.Log.Debug($"Webull: retrieved {optionQuotesBySymbol.Count} contract quote(s).");
						}
					}
				}

				// Dividend schedule for the held tickers, so the theoretical time-decay grid prices
				// legs on the ex-dividend-adjusted forward (a long calendar leg trading through an
				// ex-date is otherwise overpriced). cacheOnly:false refreshes the 12h event cache —
				// this is also what keeps data/event-cache warm for tickers the opener never scans.
				if (underlyingPrices != null && underlyingPrices.Count > 0)
				{
					var eventsCfg = new OpenerEventsConfig();
					var calendar = await EventCalendarLoader.LoadAsync(underlyingPrices.Keys.ToList(), eventsCfg, EvaluationDate.Today, cancellation, cacheOnly: false);
					dividends = DividendScheduleBuilder.Build(calendar, underlyingPrices, eventsCfg);
				}

				var riskFreeRate = await riskFreeTask;
				if (riskFreeRate.HasValue)
				{
					OptionMath.RiskFreeRate = riskFreeRate.Value;
					WebullAnalytics.Utils.Log.Debug($"Risk-free rate (13-week T-bill): {riskFreeRate.Value:P2}");
				}
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException) throw;
				Console.WriteLine($"Warning: Failed to fetch option data: {ex.Message}");
			}
		}

		IReadOnlyDictionary<string, decimal>? underlyingPriceOverrides = null;
		if (settings.Spot != null)
		{
			var overrides = ParseUnderlyingPriceOverrides(settings.Spot);
			if (overrides.Count > 0)
				underlyingPriceOverrides = overrides;
		}

		IReadOnlyDictionary<string, List<decimal>>? extraLevels = null;
		if (settings.Levels != null)
		{
			var parsed = ParseLevels(settings.Levels);
			if (parsed.Count > 0)
				extraLevels = parsed;
		}

		IReadOnlyDictionary<string, decimal>? ivOverrides = null;
		if (settings.IvOverrides != null)
		{
			var parsed = ParseIvOverrides(settings.IvOverrides);
			if (parsed.Count > 0)
				ivOverrides = parsed;
		}

		IReadOnlyDictionary<string, decimal>? calibratedIv = null;
		if (settings.Calibrated && optionQuotesBySymbol != null && underlyingPrices != null)
		{
			calibratedIv = BuildCalibratedIv(optionQuotesBySymbol, underlyingPrices, ivOverrides, dividends);
			if (calibratedIv != null)
				WebullAnalytics.Utils.Log.Debug($"Calibration: solved mid-implied IV for {calibratedIv.Count} contract(s); future grid values anchored to the live mid surface.");
		}

		var opts = new AnalysisOptions(optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, settings.Theoretical, extraLevels, ivOverrides, dividends, calibratedIv);
		var displayMode = settings.DisplayMode.ToLowerInvariant();

		var adjustmentBreakdowns = AdjustmentReportBuilder.Build(positionRows, trades, positions, strategyAdjustments, singleLegStandalones);

		switch (settings.OutputFormat.ToLowerInvariant())
		{
			case "excel":
				ExcelExporter.ExportToExcel(rows, positionRows, trades, positions, running, initialAmount, settings.OutputPath ?? $"WebullAnalytics_{dateStr}.xlsx", opts);
				break;

			case "text":
				TextFileExporter.ExportToTextFile(rows, positionRows, positions, running, initialAmount, settings.OutputPath ?? $"WebullAnalytics_{dateStr}.txt", settings.Simplified, opts, settings.Range, displayMode, adjustmentBreakdowns, settings.ShowLegs);
				break;

			default:
				TerminalHelper.EnsureTerminalWidth(settings.Simplified, autoExpandTerminal, maxTerminalWidth);
				TableRenderer.RenderReport(rows, positionRows, positions, running, initialAmount, settings.Simplified, opts, settings.Range, displayMode, adjustmentBreakdowns, settings.ShowLegs);
				break;
		}

		return 0;
	}

	/// <summary>
	/// Pre-loads underlying daily closes for every (ticker, expiryDate) pair referenced by an
	/// already-expired option leg, then returns a synchronous lookup the position tracker can use
	/// to settle ITM legs at intrinsic value. Unknown pairs map to null, falling back to the
	/// legacy "expire worthless" behavior — so this is safe to call even when history is missing.
	/// </summary>
	private static async Task<Func<string, DateTime, decimal?>> BuildUnderlyingCloseLookup(List<Trade> trades, CancellationToken cancellation)
	{
		var today = EvaluationDate.Today;
		var pairs = trades
			.Where(t => t.Asset == Asset.Option && t.Expiry.HasValue && t.Expiry.Value.Date < today)
			.Select(t => (Ticker: MatchKeys.GetTicker(t.MatchKey), Date: t.Expiry!.Value.Date))
			.Where(p => !string.IsNullOrEmpty(p.Ticker))
			.Distinct()
			.ToList();

		var dict = new Dictionary<(string ticker, DateTime date), decimal>();
		if (pairs.Count > 0)
		{
			var cache = new HistoricalPriceCache();
			foreach (var (ticker, date) in pairs)
			{
				try
				{
					var close = await cache.GetCloseAsync(ticker!, date, cancellation);
					if (close.HasValue)
						dict[(ticker!, date)] = close.Value;
				}
				catch (OperationCanceledException) { throw; }
				catch
				{
					// Missing/unreadable history: leave the pair absent so the leg settles worthless.
				}
			}
		}

		return (ticker, date) => dict.TryGetValue((ticker, date.Date), out var v) ? v : (decimal?)null;
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

	/// <summary>
	/// Back-solves a per-contract IV from each leg's live bid/ask mid so the time-decay grid prices on a
	/// mid-consistent vol surface instead of the broker's reported IV. Calibration solves on the
	/// dividend-adjusted spot at today's open (matching <see cref="OptionMath.LegContractValueWithBs"/>), so
	/// the dividend is not double-counted. Legs already pinned by a user --iv override are skipped (the
	/// override wins regardless), as are legs whose mid cannot be inverted (no two-sided quote, mid ≤
	/// intrinsic, expired, or non-convergent) — those fall through to the broker IV unchanged.
	/// </summary>
	private static IReadOnlyDictionary<string, decimal>? BuildCalibratedIv(IReadOnlyDictionary<string, OptionContractQuote> quotes, IReadOnlyDictionary<string, decimal> underlyingPrices, IReadOnlyDictionary<string, decimal>? ivOverrides, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends)
	{
		var asOf = OptionMath.ObservationInstant(); // when the loaded quotes were struck (now live / last close off-hours); matches the grid's leftmost column
		var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var symbol in quotes.Keys)
		{
			if (ivOverrides != null && ivOverrides.ContainsKey(symbol)) continue;
			var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
			if (parsed == null) continue;

			// Always calibrate at the real market spot — the leg's live mid was struck there, so that's the
			// spot the mid→IV inversion must use. A --spot override only reprices/re-centers the grid; folding
			// it into the IV solve would absorb the spot move into vol and defeat the repricing. Want a
			// different vol at the new spot? Pass --iv (which wins over calibration in GetLegIv).
			if (!underlyingPrices.TryGetValue(parsed.Root, out var spot)) continue;

			IReadOnlyList<DividendEvent>? divs = null;
			dividends?.TryGetValue(parsed.Root, out divs);
			var expirationTime = parsed.ExpiryDate.Date + OptionMath.MarketClose;
			var adjustedSpot = OptionMath.DividendAdjustedSpot(spot, divs, asOf, expirationTime, OptionMath.RiskFreeRate);

			var iv = OptionMath.TryMarketImpliedIv(symbol, parsed, adjustedSpot, asOf, quotes);
			if (iv.HasValue) result[symbol] = iv.Value;
		}
		return result.Count > 0 ? result : null;
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

	internal static Dictionary<string, List<decimal>> ParseLevels(string input)
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

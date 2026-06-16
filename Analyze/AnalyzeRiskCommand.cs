using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Pricing;
using WebullAnalytics.Sentiment;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

internal sealed class AnalyzeRiskSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "<spec>")]
	[Description("Leg list to evaluate using current market quotes. Format: ACTION:SYMBOL[[:QTY]][[@PRICE]] where ACTION is buy|sell, QTY defaults to 1, and PRICE is an optional cost basis (decimal or BID|MID|ASK). When @PRICE is omitted, MID is used by default. Examples: sell:GME260501C00025500,buy:GME260522C00026000 OR sell:GME260501C00025500:10@0.38,buy:GME260522C00026000:10@0.12")]
	public string Spec { get; set; } = "";

	[CommandOption("--iv-default")]
	[Description("Fallback implied volatility for theoretical pricing when no live IV exists. Percent, default 40.")]
	public decimal IvDefault { get; set; } = 40m;

	[CommandOption("--calibrated")]
	[Description("ON by default: back-solve each leg's IV from its live bid/ask mid (the mid-consistent surface the engine prices on). Pass --calibrated false to instead trust Webull's reported IV field — a debugging view only; the vendor field is 10–50 vol pts off at 0DTE.")]
	[DefaultValue(true)]
	public bool Calibrated { get; set; } = true;

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (string.IsNullOrWhiteSpace(Spec))
			return ValidationResult.Error("<spec> is required");

		try { _ = ParseRiskLegs(Spec); }
		catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }

		if (IvDefault <= 0m || IvDefault > 500m)
			return ValidationResult.Error($"--iv-default: must be in (0, 500], got {IvDefault}");

		return ValidationResult.Success();
	}

	internal static List<ParsedLeg> ParseRiskLegs(string spec)
	{
		if (string.IsNullOrWhiteSpace(spec))
			throw new FormatException("leg list is empty");

		// Accept ACTION:SYMBOL and auto-fill QTY=1 so we can reuse TradeLegParser (which expects ACTION:SYMBOL:QTY).
		// Preserve any optional @PRICE token.
		var legs = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var normalized = new List<string>(legs.Length);
		foreach (var raw in legs)
		{
			var at = raw.IndexOf('@');
			var main = at >= 0 ? raw[..at] : raw;
			var price = at >= 0 ? raw[at..] : "";

			var parts = main.Split(':', StringSplitOptions.TrimEntries);
			if (parts.Length == 2)
				normalized.Add($"{parts[0]}:{parts[1]}:1{price}");
			else if (parts.Length == 3)
				normalized.Add($"{parts[0]}:{parts[1]}:{parts[2]}{price}");
			else
				throw new FormatException($"leg '{raw}': expected ACTION:SYMBOL[:QTY][@PRICE]");
		}

		var parsed = TradeLegParser.Parse(string.Join(',', normalized));
		foreach (var leg in parsed)
			if (leg.Option == null)
				throw new FormatException($"'{leg.Symbol}' is not a valid OCC option symbol");
		return parsed;
	}
}

internal sealed class AnalyzeRiskCommand : AsyncCommand<AnalyzeRiskSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AnalyzeRiskSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var parsedLegs = AnalyzeRiskSettings.ParseRiskLegs(settings.Spec);
		var symbols = parsedLegs.Select(l => l.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		var (quotes, underlyingPrices) = await AnalyzeCommon.FetchQuotesAndUnderlyingForSymbolList(symbols, cancellation);
		if (quotes == null) return 1;

		var ticker = parsedLegs[0].Option!.Root;
		var spot = ResolveSpot(ticker, settings.Spot, underlyingPrices);
		if (spot == null || spot.Value <= 0m)
		{
			Console.Error.WriteLine($"Error: no underlying price for '{ticker}'. Pass --spot {ticker}:<price> or run 'wa sniff' to refresh Webull headers.");
			return 1;
		}

		// Re-base IV to the live mid surface (default; --calibrated false shows raw vendor IV for debugging).
		// A --spot override makes the leg mids stale, so the back-solve would be nonsense — skip it then.
		if (settings.Calibrated && string.IsNullOrEmpty(settings.Spot))
			quotes = AnalyzeCommon.RecalibrateQuotesToMid(quotes, underlyingPrices!, dividends: null);

		var positionLegs = new List<AnalyzePositionCommand.PositionSnapshot>(parsedLegs.Count);
		foreach (var l in parsedLegs)
		{
			if (!quotes.TryGetValue(l.Symbol, out var q))
			{
				Console.Error.WriteLine($"Error: no quote found for '{l.Symbol}'.");
				return 1;
			}

			decimal costBasis;
			if (l.Price.HasValue || l.PriceKeyword != null)
			{
				try { costBasis = AnalyzeCommon.ResolvePrice(l, quotes); }
				catch (Exception ex) { Console.Error.WriteLine($"Error: failed to resolve @PRICE for '{l.Symbol}': {ex.Message}"); return 1; }
			}
			else
			{
				if (!q.Bid.HasValue || !q.Ask.HasValue || q.Bid.Value < 0m || q.Ask.Value <= 0m)
				{
					var bid = q.Bid.HasValue ? q.Bid.Value.ToString("F2", CultureInfo.InvariantCulture) : "null";
					var ask = q.Ask.HasValue ? q.Ask.Value.ToString("F2", CultureInfo.InvariantCulture) : "null";
					Console.Error.WriteLine($"Error: '{l.Symbol}' has no usable mid quote (bid={bid}, ask={ask}).");
					return 1;
				}
				costBasis = (q.Bid.Value + q.Ask.Value) / 2m;
			}

			var parsed = l.Option!;
			positionLegs.Add(new AnalyzePositionCommand.PositionSnapshot(Symbol: l.Symbol, Action: l.Action, Qty: l.Quantity, CostBasis: costBasis, Parsed: parsed));
		}

		// Ensure consistent QTY for strategy display.
		if (positionLegs.Select(l => l.Qty).Distinct().Count() != 1)
		{
			Console.Error.WriteLine("Error: all legs must have the same QTY for analyze risk.");
			return 1;
		}

		var kind = AnalyzePositionCommand.ClassifyStructure(positionLegs);
		var strategyLabel = kind != AnalyzePositionCommand.StructureKind.Unsupported
			? kind.ToString()
			: ParsingHelpers.ClassifyStrategyKind(
				legCount: positionLegs.Count,
				distinctExpiries: positionLegs.Select(l => l.Parsed.ExpiryDate.Date).Distinct().Count(),
				distinctStrikes: positionLegs.Select(l => l.Parsed.Strike).Distinct().Count(),
				distinctCallPut: positionLegs.Select(l => l.Parsed.CallPut).Distinct().Count());

		var asOf = OptionMath.ObservationInstant(); // when the loaded quotes were struck — correct T for the diagnostic's greeks/EM
		var trend = await TrendFetcher.FetchAsync(ticker, asOf, cancellation);

		var technicalBias = await TryComputeTechnicalBiasAsync(ticker, asOf, cancellation);

		decimal ResolveIv(string sym) =>
			quotes.TryGetValue(sym, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
				? q.ImpliedVolatility.Value
				: settings.IvDefault / 100m;

		decimal ResolveLegMark(string sym)
		{
			var leg = positionLegs.FirstOrDefault(l => l.Symbol == sym);
			if (leg == null) return 0m;
			var iv = ResolveIv(sym);
			var dte = Math.Max(1, (leg.Parsed.ExpiryDate - asOf.Date).Days);
			return AnalyzePositionCommand.LiveOrBsMid(quotes, sym, spot.Value, leg.Parsed.Strike, dte, iv, leg.Parsed.CallPut);
		}

		var diagLegs = positionLegs.Select(l => new DiagnosticLeg(
			Symbol: l.Symbol,
			Parsed: l.Parsed,
			IsLong: l.Action == LegAction.Buy,
			Qty: l.Qty,
			PricePerShare: ResolveLegMark(l.Symbol),
			CostBasisPerShare: l.CostBasis)).ToList();

		var historicalVolAnnual = await TryComputeHistoricalVolAsync(ticker, asOf, cancellation);
		var sentiment = await FearGreedClient.FetchAsync(asOf, cancellation);

		var diagnostic = RiskDiagnosticBuilder.Build(diagLegs, spot.Value, asOf, ResolveIv, trend, quotes, sentiment);
		// At hypothetical spots (--spot override), the leg market mids are stale and back-solving IV
		// against them produces a nonsense IV. Use broker IV in that case for an internally-consistent
		// projection.
		var useMarketImpliedIv = string.IsNullOrEmpty(settings.Spot);
		var probe = RiskDiagnosticProbeBuilder.Build(diagLegs, spot.Value, asOf, ResolveIv, quotes, opener: null, technicalBiasOverride: technicalBias, useCostBasisForOpenerScore: true, historicalVolAnnual: historicalVolAnnual, useMarketImpliedIv: useMarketImpliedIv, sentimentScore: sentiment?.Score);
		diagnostic = diagnostic with { Probe = probe };

		var logPath = Program.ResolvePath("data/analyze-risk.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		using (var writer = new StreamWriter(File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
		{
			writer.AutoFlush = true;
			var record = new
			{
				type = "analyze_risk",
				ts = DateTime.Now.ToString("o"),
				ticker,
				positionKey = string.Join("|", positionLegs.Select(l => l.Symbol)),
				spot = spot.Value,
				diagnostic = AnalyzePositionCommand.SerializeDiagnostic(diagnostic),
				mode = "analyze_risk",
			};
			writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(record));
		}

		if (settings.OutputFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
		{
			var stringWriter = new StringWriter();
			var fileConsole = WebullAnalytics.IO.TextFileExporter.CreateTextConsole(stringWriter);
			AnalyzeCommon.RenderProposalPanel(positionLegs, strategyLabel, spot.Value, diagnostic, fileConsole, ascii: true);
			var path = settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("AnalyzeRisk");
			WebullAnalytics.IO.TextFileExporter.WriteConsoleOutputToTextFile(stringWriter, path, "Risk analysis written to");
		}
		else
		{
			AnalyzeCommon.RenderProposalPanel(positionLegs, strategyLabel, spot.Value, diagnostic);
		}
		return 0;
	}

	private static async Task<decimal> TryComputeTechnicalBiasAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		try
		{
			var path = Program.ResolvePath(AIConfigLoader.ConfigPath);
			if (!File.Exists(path)) return 0m;
			var cfg = System.Text.Json.JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(path));
			if (cfg == null) return 0m;
			if (AIConfigLoader.Validate(cfg) != null) return 0m;

			var filter = cfg.Indicators.TechnicalFilter;
			if (!filter.Enabled) return 0m;

			var cache = new HistoricalPriceCache();
			var res = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
				tickers: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker },
				priceCache: cache,
				filter: filter,
				asOf: asOf,
				cancellation: cancellation);
			return res.TryGetValue(ticker, out var b) ? b.Score : 0m;
		}
		catch
		{
			return 0m;
		}
	}

	/// <summary>Mirrors OpenCandidateEvaluator's HV pull so analyze risk scores get the same vol-fit
	/// factor as the live opener pipeline. Returns null when the config, lookback, or cache is missing —
	/// the scorer treats that as "skip the vol factor".</summary>
	private static async Task<decimal?> TryComputeHistoricalVolAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		try
		{
			var path = Program.ResolvePath(AIConfigLoader.ConfigPath);
			if (!File.Exists(path)) return null;
			var cfg = System.Text.Json.JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(path));
			if (cfg == null) return null;
			if (AIConfigLoader.Validate(cfg) != null) return null;
			if (cfg.Opener.Weights.VolatilityFit <= 0m) return null;

			var cache = new HistoricalPriceCache();
			var closes = await cache.GetRecentClosesAsync(ticker, cfg.Opener.VolatilityLookbackDays + 1, asOf, cancellation);
			var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
			return hv is decimal v && v > 0m ? v : null;
		}
		catch
		{
			return null;
		}
	}

	private static decimal? ResolveSpot(string ticker, string? spotOverrides, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		if (!string.IsNullOrEmpty(spotOverrides))
		{
			foreach (var entry in spotOverrides.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = entry.Split(':');
				if (parts.Length == 2 && parts[0].Equals(ticker, StringComparison.OrdinalIgnoreCase)
					&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
					return p;
			}
		}
		if (underlyingPrices != null && underlyingPrices.TryGetValue(ticker, out var apiPrice) && apiPrice > 0m)
			return apiPrice;
		return null;
	}
}

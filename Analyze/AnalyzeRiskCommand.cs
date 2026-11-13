using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI;
using WebullAnalytics.Api;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Pricing;
using WebullAnalytics.Report;
using WebullAnalytics.Sentiment;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

internal sealed class AnalyzeRiskSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "[spec]")]
	[Description("Leg list to evaluate using current market quotes. Format: ACTION:SYMBOL[[:QTY]][[@PRICE]] where ACTION is buy|sell, QTY defaults to 1, and PRICE is an optional cost basis (decimal or BID|MID|ASK). When @PRICE is omitted, MID is used by default. Examples: sell:GME260501C00025500,buy:GME260522C00026000 OR sell:GME260501C00025500:10@0.38,buy:GME260522C00026000:10@0.12. Omit when using --proposal or --fill.")]
	public string Spec { get; set; } = "";

	[CommandOption("--proposal")]
	[Description("Render the risk diagnostic from a stored proposal snapshot instead of live quotes. Format: FILE[[:LINE]] where FILE is a path, a data/ filename (ai-proposals.SPY.0DTE.jsonl), or the TICKER.strategy shorthand (SPY.0DTE); LINE is a 1-based line number, defaulting to the last line.")]
	public string? Proposal { get; set; }

	[CommandOption("--fill [N]")]
	[Description("Risk diagnostic of one of your recent OPENING fills (Webull order history, last 30 days, opener-placed or manual), your actual per-leg fill prices as the cost basis, as it stood AT THE FILL by default: the market at that instant (stored minute NBBO from quotes.db, minute-bar spot, as-of trend/regime). --date now evaluates the fill against the live market instead, --date YYYY-MM-DD[THH:MM] at that instant. When quotes.db doesn't cover the legs at that minute (today's fills until the evening backfill; un-backfilled roots/strikes, e.g. manual trades on any ticker) the same NBBO is fetched from ThetaData if a ThetaData login is configured; refuses when no source has it. Bare --fill picks interactively; N is the 1-based position in that list, newest first (not a file line).")]
	public FlagValue<int>? Fill { get; set; }

	protected override bool FillAllowed => Fill?.IsSet == true;

	/// <summary>The evaluation instant: --date, except that a --fill without one is evaluated at the fill (risk's question is what the trade
	/// looked like when it was opened).</summary>
	internal AsOf EffectiveDate => Date == null && Fill?.IsSet == true ? new AsOf(AsOfKind.Fill, default) : DateSpec;

	[CommandOption("--iv-default")]
	[Description("Fallback implied volatility for theoretical pricing when no live IV exists. Percent, default 40.")]
	public decimal IvDefault { get; set; } = DefaultIvPct;

	/// <summary>--iv-default's default, also the fallback IV analyze trade --compare scores with.</summary>
	internal const decimal DefaultIvPct = 40m;

	[CommandOption("--calibrated")]
	[Description("ON by default: back-solve each leg's IV from its live bid/ask mid (the mid-consistent surface the engine prices on). Pass --calibrated false to instead trust Webull's reported IV field — a debugging view only; the vendor field is 10–50 vol pts off at 0DTE.")]
	[DefaultValue(true)]
	public bool Calibrated { get; set; } = true;

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		var sources = (string.IsNullOrWhiteSpace(Spec) ? 0 : 1) + (Proposal != null ? 1 : 0) + (Fill?.IsSet == true ? 1 : 0);
		if (sources > 1)
			return ValidationResult.Error("pass only one of <spec>, --proposal or --fill");
		if (sources == 0)
			return ValidationResult.Error("provide <spec>, --proposal or --fill");
		if (Proposal != null)
			return ValidationResult.Success();
		if (Fill?.IsSet == true && Fill.Value < 0)
			return ValidationResult.Error($"--fill: N must be ≥ 1, got {Fill.Value}");
		if (Fill?.IsSet == true && Date == null && !string.IsNullOrEmpty(Spot))
			return ValidationResult.Error("--fill evaluates at the fill's own instant by default, where the spot is the stored market's; pass --date now to use --spot");

		if (!string.IsNullOrWhiteSpace(Spec))
		{
			try { _ = ParseRiskLegs(Spec); }
			catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }
		}

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
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeRiskSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		var asOf = settings.EffectiveDate;
		if (asOf.Instant is { } instant) ReportCommand.PinEvaluation(instant);

		TerminalHelper.EnsureTerminalWidthFromConfig();

		if (settings.Proposal != null)
			return RenderFromProposal(settings);

		OpeningFillPicker.OpeningFill? fill = null;
		if (settings.Fill?.IsSet == true)
		{
			(var spec, fill, var error) = await OpeningFillPicker.ResolveAtAsync(settings.Fill.Value, asOf, cancellation);
			if (spec == null) { Console.Error.WriteLine($"Error: {error}"); return 1; }
			settings.Spec = spec;
		}

		var parsedLegs = AnalyzeRiskSettings.ParseRiskLegs(settings.Spec);
		if (EvaluationDate.IsPast)
			return await RenderAtPastInstantAsync(settings, parsedLegs, fill, nearest: asOf.Kind == AsOfKind.Fill, cancellation);

		var symbols = parsedLegs.Select(l => l.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		var riskFreeTask = AI.Backtest.HistoricalRateCache.FetchLiveOrLatestAsync(cancellation);
		var (quotes, underlyingPrices) = await AnalyzeCommon.FetchQuotesAndUnderlyingForSymbolList(symbols, settings.ResolvedVendor, cancellation);
		if (quotes == null) return 1;

		var ticker = parsedLegs[0].Option!.Root;
		var spot = ResolveSpot(ticker, settings.Spot, underlyingPrices);
		if (spot == null || spot.Value <= 0m)
		{
			Console.Error.WriteLine($"Error: no underlying price for '{ticker}'. Pass --spot {ticker}:<price> or run 'wa sniff' to refresh Webull headers.");
			return 1;
		}

		// Sync the risk-free rate with what RunReportPipeline (analyze trade) uses so both commands calibrate
		// the long-leg IV against the same rate; a stale default here produces a different calibrated IV,
		// which shifts the break-evens by ~$0.01 near the sign-change boundary.
		YahooOptionsClient.ApplyToOptionMath(await riskFreeTask);

		// Two anchors, same as the scanner:
		// now = DateTime.Now: how much time the trade has left as of RIGHT NOW — DTE display, EV/POP
		//   scenario grid, score divisor. On Sunday this says "5 days to Friday expiry," matching the scan.
		// quoteAsOf = ObservationInstant(): when the loaded quotes were struck — correct T for greeks,
		//   expected-move, and the IV back-solve that inverts the option mid to find the market-implied IV.
		var now = DateTime.Now;
		var quoteAsOf = OptionMath.ObservationInstant();

		// Re-base IV to the live mid surface (default; --calibrated false shows raw vendor IV for debugging).
		// A --spot override makes the leg mids stale, so the back-solve would be nonsense — skip it then.
		if (settings.Calibrated && string.IsNullOrEmpty(settings.Spot))
			quotes = AnalyzeCommon.RecalibrateQuotesToMid(quotes, underlyingPrices!, dividends: null, quoteAsOf);

		var trend = await TrendFetcher.FetchAsync(ticker, now, cancellation);
		var riskContext = new RiskContext(now, quoteAsOf, trend, IncludeCurrentBar: true, ScoreAtCostBasis: false, Mode: "analyze_risk");
		return await EvaluateAndRenderAsync(settings, parsedLegs, quotes, spot.Value, riskContext, CreateIntradayCache(), cancellation);
	}

	/// <summary>How one risk evaluation is anchored. Live: <see cref="AsOf"/> = now, <see cref="QuoteAsOf"/> = when
	/// the live quotes were struck, the forming minute bar included (it is real data now). Fill: both = the fill
	/// instant, the forming bar excluded (its close lies after the fill), scored at the fill's cost basis.</summary>
	private sealed record RiskContext(DateTime AsOf, DateTime QuoteAsOf, TrendSnapshot? Trend, bool IncludeCurrentBar, bool ScoreAtCostBasis, string Mode);

	internal static IntradayBarCache? CreateIntradayCache()
	{
		var apiConfig = OpenCandidateEvaluator.TryLoadApiConfig();
		return apiConfig != null ? new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig)) : null;
	}

	/// <summary>The shared evaluation behind both the live spec path and --fill: per-leg cost basis, regime
	/// bias, vol inputs, sentiment, risk diagnostic and opener-style probe, all anchored on
	/// <paramref name="context"/>, then the jsonl log line and the panel.</summary>
	private static async Task<int> EvaluateAndRenderAsync(AnalyzeRiskSettings settings, List<ParsedLeg> parsedLegs, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal spot, RiskContext context, IntradayBarCache? intradayCache, CancellationToken cancellation)
	{
		var ticker = parsedLegs[0].Option!.Root;
		var asOf = context.AsOf;
		var quoteAsOf = context.QuoteAsOf;
		var trend = context.Trend;

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

		var inputs = await LoadOpenerScoringInputsAsync(ticker, asOf, context.IncludeCurrentBar, intradayCache, cancellation);
		var diagLegs = BuildDiagnosticLegs(positionLegs, quotes, spot, asOf, settings.IvDefault);
		var diagnostic = RiskDiagnosticBuilder.Build(diagLegs, spot, quoteAsOf, sym => ResolveIv(quotes, sym, settings.IvDefault), trend, quotes, inputs.Sentiment);
		// At hypothetical spots (--spot override), the leg market mids are stale and back-solving IV
		// against them produces a nonsense IV. Use broker IV in that case for an internally-consistent
		// projection.
		var useMarketImpliedIv = string.IsNullOrEmpty(settings.Spot);
		// A spec is a hypothetical new trade: score it at the real bid/ask (cost-basis mode would suppress
		// useMarketImpliedIv and use broker IV, diverging from the scan). A fill is a trade that HAPPENED: score
		// it at what was paid — cost-basis mode — whose IV comes from the quotes' IV field, already
		// recalibrated to the fill-time mid surface.
		var probe = BuildProbe(diagLegs, spot, asOf, quoteAsOf, quotes, inputs, settings.IvDefault, context.ScoreAtCostBasis, useMarketImpliedIv);
		diagnostic = diagnostic with { Probe = probe };

		var logPath = Program.ResolvePath("data/analyze-risk.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		var record = new
		{
			type = "analyze_risk",
			ts = DateTime.Now.ToString("o"),
			ticker,
			positionKey = string.Join("|", positionLegs.Select(l => l.Symbol)),
			spot = spot,
			diagnostic = AnalyzePositionCommand.SerializeDiagnostic(diagnostic),
			mode = "analyze_risk",
		};
		WebullAnalytics.IO.SharedFileAppender.AppendLine(logPath, System.Text.Json.JsonSerializer.Serialize(record));

		if (settings.OutputFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
		{
			var stringWriter = new StringWriter();
			var fileConsole = WebullAnalytics.IO.TextFileExporter.CreateTextConsole(stringWriter);
			AnalyzeCommon.RenderProposalPanel(positionLegs, strategyLabel, spot, diagnostic, fileConsole, ascii: true);
			var path = settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("AnalyzeRisk");
			WebullAnalytics.IO.TextFileExporter.WriteConsoleOutputToTextFile(stringWriter, path, "Risk analysis written to");
		}
		else
		{
			AnalyzeCommon.RenderProposalPanel(positionLegs, strategyLabel, spot, diagnostic);
		}
		return 0;
	}

	/// <summary>Everything the opener scorer reads about a ticker besides the legs and their quotes — regime components, the
	/// 20-session HV and realized-vol model, sentiment, and the ticker's ai-config — loaded once per ticker and instant so several
	/// structures on the same underlying (analyze trade --compare) score against the same inputs. Ai is null when the ticker has
	/// no ai-config; the probe then falls back to its unscored rationale.</summary>
	internal sealed record OpenerScoringInputs(AIConfig? Ai, RegimeAnalyzer.RegimeComponents Components, decimal? HistoricalVolAnnual, RealizedVolModel? RealizedVol, SentimentSnapshot? Sentiment)
	{
		/// <summary>The blended regime bias the scanner uses for a structure, horizon = calendar days to its earliest expiry (≥ 1).</summary>
		internal decimal TechnicalBias(IEnumerable<OptionParsed> legs, DateTime asOf)
		{
			if (Ai == null) return 0m;
			var expiries = legs.Select(l => l.ExpiryDate.Date).ToList();
			var dteCalendar = expiries.Count > 0 ? Math.Max(1, (expiries.Min() - asOf.Date).Days) : 5;
			return RegimeAnalyzer.BlendBias(Components, Ai.Opener, dteCalendar);
		}
	}

	/// <summary>Loads <see cref="OpenerScoringInputs"/> through the same shared static pipeline the scanner uses, so bias, EV
	/// and final score are byte-identical between wa ai scan and wa analyze risk.</summary>
	internal static async Task<OpenerScoringInputs> LoadOpenerScoringInputsAsync(string ticker, DateTime asOf, bool includeCurrentBar, IntradayBarCache? intradayCache, CancellationToken cancellation)
	{
		var priceCache = new HistoricalPriceCache();
		var aiCfg = RiskDiagnosticProbeBuilder.TryLoadAiConfigQuiet(ticker, out _);
		decimal macroBias = 0m;
		if (aiCfg?.Indicators.TechnicalFilter.Enabled == true)
		{
			var filter = aiCfg.Indicators.TechnicalFilter;
			var effectiveLookback = filter.Sma200Weight > 0m ? Math.Max(filter.LookbackDays, 200) : filter.LookbackDays;
			var closes = await priceCache.GetRecentClosesAsync(ticker, effectiveLookback, asOf, cancellation);
			macroBias = TechnicalIndicators.Compute(closes, filter)?.Score ?? 0m;
		}
		var regimeComponents = aiCfg != null ? (await OpenCandidateEvaluator.ComputeRegimeComponentsAsync(ticker, aiCfg.Opener, macroBias, asOf, priceCache, intradayCache, includeCurrentBar, cancellation)).Components : default;
		var (historicalVolAnnual, realizedVol) = await RiskDiagnosticProbeBuilder.TryLoadVolInputsAsync(ticker, asOf, priceCache, cancellation);
		var sentiment = await FearGreedClient.FetchAsync(asOf, cancellation);
		return new OpenerScoringInputs(aiCfg, regimeComponents, historicalVolAnnual, realizedVol, sentiment);
	}

	/// <summary>A leg's IV: the quote's (calibrated) IV, else <paramref name="ivDefaultPct"/>.</summary>
	internal static decimal ResolveIv(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol, decimal ivDefaultPct) =>
		quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m ? q.ImpliedVolatility.Value : ivDefaultPct / 100m;

	/// <summary>The legs as the diagnostic and probe read them: each marked at its live mid (Black-Scholes when the book is unusable),
	/// with its cost basis.</summary>
	internal static List<DiagnosticLeg> BuildDiagnosticLegs(IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> positionLegs, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal spot, DateTime asOf, decimal ivDefaultPct) =>
		positionLegs.Select(l =>
		{
			var dte = Math.Max(1, (l.Parsed.ExpiryDate - asOf.Date).Days);
			var mark = AnalyzePositionCommand.LiveOrBsMid(quotes, l.Symbol, spot, l.Parsed.Strike, dte, ResolveIv(quotes, l.Symbol, ivDefaultPct), l.Parsed.CallPut);
			return new DiagnosticLeg(Symbol: l.Symbol, Parsed: l.Parsed, IsLong: l.Action == LegAction.Buy, Qty: l.Qty, PricePerShare: mark, CostBasisPerShare: l.CostBasis);
		}).ToList();

	/// <summary>The opener-style probe (structure, POP, EV, score) for <paramref name="diagLegs"/>: the scanner's CandidateScorer on these inputs.</summary>
	internal static RiskDiagnosticProbe BuildProbe(IReadOnlyList<DiagnosticLeg> diagLegs, decimal spot, DateTime asOf, DateTime quoteAsOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, OpenerScoringInputs inputs, decimal ivDefaultPct, bool scoreAtCostBasis, bool useMarketImpliedIv) =>
		RiskDiagnosticProbeBuilder.Build(diagLegs, spot, asOf, sym => ResolveIv(quotes, sym, ivDefaultPct), quotes, opener: null, technicalBiasOverride: inputs.TechnicalBias(diagLegs.Select(l => l.Parsed), asOf), useCostBasisForOpenerScore: scoreAtCostBasis, historicalVolAnnual: inputs.HistoricalVolAnnual, useMarketImpliedIv: useMarketImpliedIv, sentimentScore: inputs.Sentiment?.Score, ivSolveAsOf: quoteAsOf, realizedVol: inputs.RealizedVol);

	/// <summary>A past evaluation instant (--date, or a --fill at its own instant): the legs' risk against the market at that instant — each leg's
	/// NBBO and its expiries' chains at that minute (the boundary nearest a fill, <paramref name="nearest"/>), the minute-bar spot, as-of
	/// trend/regime/vol/sentiment and that day's rate. Covers any fill, opener-placed or manual; books the quote store lacks come from ThetaData
	/// when a login is configured (see <see cref="FillTimeMarket"/>); refuses (naming why) when no source covers the legs. A fill scores at
	/// its own fill prices.</summary>
	private static async Task<int> RenderAtPastInstantAsync(AnalyzeRiskSettings settings, List<ParsedLeg> parsedLegs, OpeningFillPicker.OpeningFill? fill, bool nearest, CancellationToken cancellation)
	{
		var intradayCache = CreateIntradayCache();
		if (intradayCache == null) { Console.Error.WriteLine("Error: api-config.json has no Webull session headers for the 1-minute spot bars."); return 1; }

		var at = EvaluationDate.Instant!.Value;
		var ticker = fill?.Ticker ?? parsedLegs[0].Option!.Root;
		var (market, marketError) = await FillTimeMarket.LoadAsync(ticker, parsedLegs.Select(l => l.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), at, nearest, intradayCache, cancellation);
		if (market == null) { Console.Error.WriteLine($"Error: can't rebuild the market at {at:yyyy-MM-dd HH:mm:ss} ET — {marketError}."); return 1; }

		// The books' clock and that day's rate: the prior ^IRX close, exactly what the backtest applies to a session.
		OptionMath.HistoricalQuotesStruckAt = market.Instant;
		await FillTimeMarket.ApplySessionRateAsync(at.Date, cancellation);

		var underlying = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = market.Spot };
		var quotes = settings.Calibrated ? AnalyzeCommon.RecalibrateQuotesToMid(market.Quotes, underlying, dividends: null, market.Instant) : market.Quotes;
		var trend = await TrendFetcher.BuildAsOfAsync(ticker, market.Instant, market.Spot, market.SessionHigh, market.SessionLow, new AI.Backtest.HistoricalBarCache(), cancellation);

		var offset = nearest ? $" ({Math.Abs((int)Math.Round((market.Instant - at).TotalSeconds))} s {(market.Instant < at ? "before" : "after")} the fill)" : "";
		AnsiConsole.MarkupLine($"[dim]Market as of {market.Instant:yyyy-MM-dd HH:mm:ss} ET{offset}, {Markup.Escape(market.BookSource)}, spot ${market.Spot:F2}[/]");
		var context = new RiskContext(market.Instant, market.Instant, trend, IncludeCurrentBar: false, ScoreAtCostBasis: fill != null, Mode: fill != null ? "analyze_risk_fill" : "analyze_risk_asof");
		return await EvaluateAndRenderAsync(settings, parsedLegs, quotes, market.Spot, context, intradayCache, cancellation);
	}

	/// <summary>--proposal: re-render the risk diagnostic exactly as it was stored in the proposal snapshot,
	/// with no live fetch or recalibration. The snapshot's diagnostic already carries the calibrated IVs,
	/// greeks, opener score and rule hits from when the proposal was scored.</summary>
	private static int RenderFromProposal(AnalyzeRiskSettings settings)
	{
		var (snap, error) = ProposalSnapshot.TryLoad(settings.Proposal!);
		if (snap == null) { Console.Error.WriteLine($"Error: {error}"); return 1; }
		if (snap.Diagnostic == null) { Console.Error.WriteLine($"Error: proposal line {snap.LineNumber} of '{snap.SourcePath}' has no diagnostic block to render."); return 1; }

		if (!settings.PinnedInstant.HasValue)
			EvaluationDate.Set(snap.AsOf);

		var positionLegs = snap.Legs
			.Select(l => new AnalyzePositionCommand.PositionSnapshot(Symbol: l.Symbol, Action: l.Action, Qty: l.Qty, CostBasis: snap.CostBasis(l.Symbol), Parsed: l.Parsed))
			.ToList();

		var underlyingPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [snap.Ticker] = snap.Spot };
		var spot = ResolveSpot(snap.Ticker, settings.Spot, underlyingPrices) ?? snap.Spot;

		var kind = AnalyzePositionCommand.ClassifyStructure(positionLegs);
		var strategyLabel = kind != AnalyzePositionCommand.StructureKind.Unsupported
			? kind.ToString()
			: ParsingHelpers.ClassifyStrategyKind(
				legCount: positionLegs.Count,
				distinctExpiries: positionLegs.Select(l => l.Parsed.ExpiryDate.Date).Distinct().Count(),
				distinctStrikes: positionLegs.Select(l => l.Parsed.Strike).Distinct().Count(),
				distinctCallPut: positionLegs.Select(l => l.Parsed.CallPut).Distinct().Count());

		AnsiConsole.MarkupLine($"[dim]Proposal snapshot: {Markup.Escape(Path.GetFileName(snap.SourcePath))} line {snap.LineNumber}, emitted {snap.AsOf:yyyy-MM-dd HH:mm:ss}[/]");
		if (settings.OutputFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
		{
			var stringWriter = new StringWriter();
			var fileConsole = WebullAnalytics.IO.TextFileExporter.CreateTextConsole(stringWriter);
			AnalyzeCommon.RenderProposalPanel(positionLegs, strategyLabel, spot, snap.Diagnostic, fileConsole, ascii: true);
			var path = settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("AnalyzeRisk");
			WebullAnalytics.IO.TextFileExporter.WriteConsoleOutputToTextFile(stringWriter, path, "Risk analysis written to");
		}
		else
		{
			AnalyzeCommon.RenderProposalPanel(positionLegs, strategyLabel, spot, snap.Diagnostic);
		}
		return 0;
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

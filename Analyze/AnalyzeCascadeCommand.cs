using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// `wa analyze cascade &lt;TICKER&gt;` — Classifies the market regime into Bear Cascade, Bear Trap,
/// Bull Cascade, or Bull Trap (plus Neutral) and renders a Spectre.Console screener panel. Descriptive only:
/// it reads the session so far and feeds nothing into the opener. Every live run appends its chain to data/iv (the
/// volume-pace factor's input) and its reading to data/cascade (<see cref="CascadeLog"/>); a past --date replay writes nothing.
/// Reuses <see cref="GexMatrix"/>, <see cref="GexSqueezeScreener"/>, <see cref="AnalyzeGexCommand"/> chain/quote loaders,
/// <see cref="CascadeScreener"/>, and <see cref="CascadePanel"/>.
/// </summary>
internal sealed class AnalyzeCascadeSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., SPXW, SPY, QQQ).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--time <HH:MM>")]
	[Description("ET time-of-day to evaluate (09:30–16:00). On a past --date replay, defaults to 10:00 ET so the screener evaluates the morning decision window rather than the 16:00 post-expiry close.")]
	public string? Time { get; set; }

	[CommandOption("--dte <N>")]
	[DefaultValue(30)]
	[Description("Multi-expiry horizon in days-to-expiry (default 30) for the 30-day GEX flip, net GEX fraction, and net Vanna (VEX) exposure.")]
	public int Dte { get; set; } = 30;

	[CommandOption("--strike-range <PCT>")]
	[DefaultValue(20)]
	[Description("Strike window as ± percent of spot. Default: 20.")]
	public int StrikeRangePct { get; set; } = 20;

	[CommandOption("--max-strikes <N>")]
	[DefaultValue(200)]
	[Description("Max strike rows retained in the matrix ladder (1–200). Default: 200.")]
	public int MaxStrikes { get; set; } = 200;

	[CommandOption("--trigger <SCORE>")]
	[DefaultValue(CascadeScreener.DefaultTriggerScore)]
	[Description("Minimum 0–100 score required (alongside confirmation gates) for a regime to count as confirmed. Default: 65.")]
	public int TriggerScore { get; set; } = CascadeScreener.DefaultTriggerScore;

	[CommandOption("--exante")]
	[Description("On a past --date replay, price front-expiry IVs from the prior trading day's snapshot when minute NBBO is unavailable.")]
	public bool Exante { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("<ticker> is required");
		if (StrikeRangePct <= 0 || StrikeRangePct > 200) return ValidationResult.Error($"--strike-range: must be in (0, 200], got {StrikeRangePct}");
		if (MaxStrikes < 1 || MaxStrikes > 200) return ValidationResult.Error($"--max-strikes: must be in [1, 200], got {MaxStrikes}");
		if (Dte < 0 || Dte > 60) return ValidationResult.Error($"--dte: must be in [0, 60], got {Dte}");
		if (TriggerScore < 1 || TriggerScore > 100) return ValidationResult.Error($"--trigger: must be in [1, 100], got {TriggerScore}");
		if (Time != null)
		{
			if (AnalyzeGexSettings.ParseEtTime(Time) is not { } t)
				return ValidationResult.Error($"--time: expected HH:MM (ET), got '{Time}'");
			if (t < AnalyzeGexSettings.RthOpen || t > AnalyzeGexSettings.RthClose)
				return ValidationResult.Error($"--time: must be within RTH 09:30-16:00 ET, got '{Time}'");
		}
		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeCascadeCommand : AsyncCommand<AnalyzeCascadeSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeCascadeSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		return await settings.RunWithOutputAsync("AnalyzeCascade", () => RenderAsync(settings, cancellation));
	}

	private static async Task<int> RenderAsync(AnalyzeCascadeSettings settings, CancellationToken cancellation)
	{
		TerminalHelper.EnsureTerminalWidthFromConfig();

		var ticker = settings.Ticker.ToUpperInvariant();
		var nowEt = MarketTime.NowEt();
		var requestedTime = AnalyzeGexSettings.ParseEtTime(settings.Time);
		DateTime asOfEt;
		if (settings.EvaluationDateOverride.HasValue)
		{
			var d = settings.EvaluationDateOverride.Value.Date;
			var tod = requestedTime ?? (d < nowEt.Date ? new TimeSpan(10, 0, 0) : nowEt.TimeOfDay);
			asOfEt = d + tod;
		}
		else
		{
			asOfEt = requestedTime.HasValue ? nowEt.Date + requestedTime.Value : nowEt;
		}

		Dictionary<string, OptionContractQuote> quotes;
		decimal? spot;
		var isOfflineHistorical = false;
		ApiConfig? apiConfig = null;

		var oiPath = Program.ResolvePath($"data/oi/{ticker}/{asOfEt:yyyy-MM-dd}.jsonl");
		if (settings.EvaluationDateOverride.HasValue && asOfEt.Date <= DateTime.Today && File.Exists(oiPath))
		{
			var (snapSpot, snapQuotes) = AnalyzeGexCommand.LoadOiSnapshot(oiPath);
			if (snapQuotes.Count == 0)
			{
				AnsiConsole.MarkupLine($"[red]Empty data/oi snapshot for {ticker} {asOfEt:yyyy-MM-dd}.[/]");
				return 1;
			}
			quotes = snapQuotes;
			spot = AnalyzeGexCommand.ResolveSpotOverride(settings.Spot, ticker) ?? snapSpot;
			AnsiConsole.MarkupLine($"[dim]Historical chain from {Markup.Escape(oiPath)} ({quotes.Count} contracts; offline — no live fetch).[/]");
			isOfflineHistorical = true;
		}
		else if (settings.EvaluationDateOverride.HasValue && asOfEt.Date < DateTime.Today)
		{
			var dir = Program.ResolvePath($"data/oi/{ticker}");
			var available = Directory.Exists(dir)
				? Directory.EnumerateFiles(dir, "????-??-??.jsonl").Select(Path.GetFileNameWithoutExtension).OrderBy(d => d).ToList()
				: new List<string?>();
			AnsiConsole.MarkupLine($"[red]No data/oi snapshot for {Markup.Escape(ticker)} on {asOfEt:yyyy-MM-dd} — a past --date renders only from a captured chain (no live fallback).[/]");
			AnsiConsole.MarkupLine(available.Count > 0
				? $"[dim]Available {Markup.Escape(ticker)} snapshot dates: {Markup.Escape(available.Count > 12 ? string.Join(", ", available.Take(3)) + $", … ({available.Count - 6} more) …, " + string.Join(", ", available.TakeLast(3)) : string.Join(", ", available))}.[/]"
				: $"[dim]No snapshots exist under data/oi/{Markup.Escape(ticker)}/.[/]");
			return 1;
		}
		else
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath))
			{
				AnsiConsole.MarkupLine("[red]Error: api-config.json not found. Run 'sniff' first (or pass a past --date with a data/oi snapshot).[/]");
				return 1;
			}
			apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
			if (settings.VendorName == "schwab")
			{
				if (apiConfig?.Schwab == null)
				{
					AnsiConsole.MarkupLine("[red]Error: schwab vendor needs Schwab credentials in api-config.json. Run 'wa schwab login' first.[/]");
					return 1;
				}
				IReadOnlyList<OptionContractQuote> schwabQuotes;
				decimal? schwabSpot;
				try
				{
					var token = await SchwabAuthClient.GetAccessTokenAsync(apiConfig.Schwab, configPath, cancellation);
					var fromExpiry = DateOnly.FromDateTime(asOfEt.Date);
					var toExpiry = DateOnly.FromDateTime(asOfEt.Date).AddDays(settings.Dte);
					(schwabSpot, schwabQuotes) = await SchwabOptionsClient.FetchChainAsync(token, ticker, fromExpiry, toExpiry, cancellation, strikeCount: 0);
				}
				catch (SchwabAuthException ex)
				{
					AnsiConsole.MarkupLine($"[red]Schwab auth failed: {Markup.Escape(ex.Message)} Re-run 'wa schwab login'.[/]");
					return 1;
				}
				var fetched = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
				foreach (var q in schwabQuotes)
					fetched[q.ContractSymbol] = q;
				quotes = schwabSpot is > 0m
					? LiveQuoteSource.NormalizeIvs(fetched, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = schwabSpot.Value })
					: fetched;
				if (quotes.Count == 0)
				{
					AnsiConsole.MarkupLine($"[red]No option chain data returned for {ticker} from Schwab.[/]");
					return 1;
				}
				spot = AnalyzeGexCommand.ResolveSpotOverride(settings.Spot, ticker) ?? schwabSpot;
			}
			else
			{
				if (apiConfig == null || apiConfig.Webull.Headers.Count == 0)
				{
					AnsiConsole.MarkupLine("[red]Error: api-config.json is empty or missing headers. Run 'sniff' first.[/]");
					return 1;
				}
				var (initialQuotes, fetchedSpot, derivativeIds) = await WebullOptionsClient.FetchChainAsync(apiConfig, ticker, cancellation);
				if (initialQuotes.Count == 0)
				{
					AnsiConsole.MarkupLine($"[red]No option chain data returned for {ticker}.[/]");
					return 1;
				}
				spot = AnalyzeGexCommand.ResolveSpotOverride(settings.Spot, ticker) ?? fetchedSpot;
				quotes = new Dictionary<string, OptionContractQuote>(initialQuotes, StringComparer.OrdinalIgnoreCase);
				if (spot.HasValue && spot.Value > 0m)
				{
					var refreshed = await AnalyzeGexCommand.RefreshInWindowContractsAsync(
						apiConfig, quotes, derivativeIds, ticker, spot.Value, asOfEt, expiryFilter: null,
						settings.StrikeRangePct / 100m, settings.Dte, settings.MaxStrikes, cancellation);
					if (refreshed > 0) Log.Debug($"Refreshed {refreshed} in-window contract(s) via queryBatch.");
				}
			}
		}

		// Minute bars have one source (Webull's chart endpoint) whatever the quote vendor: `vendor` only picks between the Webull and Schwab option-chain clients.
		var intradayCache = apiConfig != null && !isOfflineHistorical ? new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig)) : null;

		// Load RTH 1-minute bars up to asOfEt so historical/intraday evaluations use the true spot at asOfEt
		var rthBars = await CascadeScreener.LoadSessionRthBarsAsync(ticker, asOfEt, intradayCache, includeCurrentBar: true, cancellation);
		var explicitSpotOverride = AnalyzeGexCommand.ResolveSpotOverride(settings.Spot, ticker);
		if (!explicitSpotOverride.HasValue && (isOfflineHistorical || requestedTime.HasValue) && rthBars.Count > 0)
			spot = rthBars[^1].Close;

		if (!spot.HasValue || spot.Value <= 0m)
		{
			AnsiConsole.MarkupLine($"[red]No spot price available for {ticker}. Pass --spot {ticker}:PRICE to override.[/]");
			return 1;
		}

		// On historical replays, strip all 16:00 EOD prices/IVs/volumes from the snapshot, price non-front expiries
		// from the prior trading day's settled IVs, and price the front expiry from quotes.db minute NBBO at asOfEt.
		if (isOfflineHistorical)
		{
			quotes = CascadeScreener.BuildCausalHistoricalChain(ticker, asOfEt, quotes, settings.Exante, out var provenanceNote);
			AnsiConsole.MarkupLine($"[dim]{Markup.Escape(provenanceNote)}[/]");
		}
		else
		{
			// Live run: always append a data/iv capture — the volume-pace factor is the difference of consecutive captures
			var band = settings.StrikeRangePct / 100m;
			var inWindow = quotes.Values.Where(q =>
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
				if (parsed == null || !ParsingHelpers.RootsMatchForAggregation(parsed.Root, ticker, parsed.ExpiryDate)) return false;
				var exp = parsed.ExpiryDate.Date;
				if (exp < asOfEt.Date || exp > asOfEt.Date.AddDays(settings.Dte)) return false;
				return Math.Abs(parsed.Strike - spot.Value) / spot.Value <= band;
			});
			IvDumpStore.Append(ticker, settings.VendorName, nowEt, spot.Value, inWindow);
		}

		if (AnalyzeGexCommand.IsOiBlackout(quotes, ticker, asOfEt.Date))
		{
			AnsiConsole.MarkupLine($"[yellow]The vendor returned no open interest for any unexpired {Markup.Escape(ticker)} contract (overnight OI blackout — OI is zeroed until the next morning's OCC update), and the terrain is built from OI. Re-run during market hours, or use --date with a captured data/oi snapshot.[/]");
			return 1;
		}
		var read = await CascadeScreener.ReadChainAsync(ticker, spot.Value, asOfEt, quotes, settings.Dte, settings.StrikeRangePct / 100m, settings.MaxStrikes, settings.TriggerScore, rthBars, intradayCache, new HistoricalPriceCache(), new SmileIndexCache(offline: isOfflineHistorical), settings.VendorName, cancellation);
		if (read == null)
		{
			AnsiConsole.MarkupLine($"[yellow]No strikes match within ±{settings.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]");
			return 1;
		}

		var (reading, expiryCount) = read.Value;
		var scope = $"{expiryCount} expiration(s) ≤{settings.Dte}DTE";
		CascadePanel.Render(ticker, asOfEt, scope, reading);
		if (!isOfflineHistorical) CascadeLog.Append(ticker, settings.VendorName, nowEt, asOfEt, explicitSpotOverride.HasValue, reading);
		return 0;
	}
}

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
/// Bull Cascade, or Bull Trap (plus Neutral) and renders a Spectre.Console screener panel. It reads the session so far and
/// scores no opener candidate; the same live read (<see cref="CascadeLiveReader"/>) triggers the opt-in <c>opener.cascade</c>
/// trades of `wa ai watch`. Every live run appends its chain to data/iv (the volume-pace factor's input) and its reading to
/// data/cascade (<see cref="CascadeLog"/>); a past --date replay writes nothing.
/// Reuses <see cref="GexMatrix"/>, <see cref="GexSqueezeScreener"/>, <see cref="AnalyzeGexCommand"/> chain/quote loaders,
/// <see cref="CascadeScreener"/>, and <see cref="CascadePanel"/>.
/// </summary>
internal sealed class AnalyzeCascadeSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., SPXW, SPY, QQQ).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--dte <N>")]
	[DefaultValue(CascadeScreener.DefaultDte)]
	[Description("Multi-expiry horizon in days-to-expiry (default 30) for the 30-day GEX flip, net GEX fraction, and net Vanna (VEX) exposure.")]
	public int Dte { get; set; } = CascadeScreener.DefaultDte;

	[CommandOption("--strike-range <PCT>")]
	[DefaultValue(CascadeScreener.DefaultStrikeRangePct)]
	[Description("Strike window as ± percent of spot. Default: 20.")]
	public int StrikeRangePct { get; set; } = CascadeScreener.DefaultStrikeRangePct;

	[CommandOption("--max-strikes <N>")]
	[DefaultValue(CascadeScreener.DefaultMaxStrikes)]
	[Description("Max strike rows retained in the matrix ladder (1–200). Default: 200.")]
	public int MaxStrikes { get; set; } = CascadeScreener.DefaultMaxStrikes;

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
		if (PinnedInstant > MarketTime.NowEt()) return ValidationResult.Error("--date: the cascade reads the market at an instant that has happened; a future one has none");
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

		var ticker = settings.Ticker.ToUpperInvariant();
		var nowEt = MarketTime.NowEt();
		if (settings.PinnedInstant is not DateTime asOfEt)
		{
			var ovMap = await AnalyzeCommon.ResolveSpotOverrideMapAsync(settings.Spot, settings.Extended, [ticker], settings.ResolvedVendor, rthSpots: null, cancellation);
			var spotOverride = ovMap != null && ParsingHelpers.TryResolveForRoot(ovMap, ticker, out var ov) ? ov : (decimal?)null;
			var (live, error) = await CascadeLiveReader.ReadAsync(new CascadeLiveReader.Request(ticker, settings.VendorName, settings.Dte, settings.StrikeRangePct, settings.MaxStrikes, settings.TriggerScore, spotOverride), nowEt, cancellation);
			if (live == null)
			{
				AnsiConsole.MarkupLine(error!);
				return 1;
			}
			CascadePanel.Render(ticker, nowEt, Scope(live.ExpiryCount, settings.Dte), live.Reading);
			return 0;
		}

		// A past --date renders only from a captured chain (no live fallback), and writes nothing.
		var oiPath = Program.ResolvePath($"data/oi/{ticker}/{asOfEt:yyyy-MM-dd}.jsonl");
		if (!File.Exists(oiPath))
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
		var (snapSpot, quotes) = AnalyzeGexCommand.LoadOiSnapshot(oiPath);
		if (quotes.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]Empty data/oi snapshot for {ticker} {asOfEt:yyyy-MM-dd}.[/]");
			return 1;
		}
		AnsiConsole.MarkupLine($"[dim]Historical chain from {Markup.Escape(oiPath)} ({quotes.Count} contracts; offline — no live fetch).[/]");

		// RTH 1-minute bars that closed by the instant: the last one's close is the price AT it.
		var rthBars = await CascadeScreener.LoadSessionRthBarsAsync(ticker, asOfEt, intradayCache: null, includeCurrentBar: false, cancellation);
		var spot = AnalyzeGexCommand.ResolveSpotOverride(settings.Spot, ticker) ?? (rthBars.Count > 0 ? rthBars[^1].Close : snapSpot);
		if (spot is not > 0m)
		{
			AnsiConsole.MarkupLine($"[red]No spot price available for {ticker}. Pass --spot {ticker}:PRICE to override.[/]");
			return 1;
		}

		// Strip all 16:00 EOD prices/IVs/volumes from the snapshot, price non-front expiries from the prior trading day's
		// settled IVs, and price the front expiry from quotes.db minute NBBO at asOfEt.
		quotes = CascadeScreener.BuildCausalHistoricalChain(ticker, asOfEt, quotes, settings.Exante, out var provenanceNote);
		AnsiConsole.MarkupLine($"[dim]{Markup.Escape(provenanceNote)}[/]");

		if (AnalyzeGexCommand.IsOiBlackout(quotes, ticker, asOfEt.Date))
		{
			AnsiConsole.MarkupLine($"[yellow]The vendor returned no open interest for any unexpired {Markup.Escape(ticker)} contract (overnight OI blackout — OI is zeroed until the next morning's OCC update), and the terrain is built from OI. Re-run during market hours, or use --date with a captured data/oi snapshot.[/]");
			return 1;
		}
		var read = await CascadeScreener.ReadChainAsync(ticker, spot.Value, asOfEt, quotes, settings.Dte, settings.StrikeRangePct / 100m, settings.MaxStrikes, settings.TriggerScore, rthBars, null, new HistoricalPriceCache(), new SmileIndexCache(offline: true), settings.VendorName, includeCurrentBar: false, cancellation);
		if (read == null)
		{
			AnsiConsole.MarkupLine($"[yellow]No strikes match within ±{settings.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]");
			return 1;
		}

		CascadePanel.Render(ticker, asOfEt, Scope(read.Value.ExpiryCount, settings.Dte), read.Value.Reading);
		return 0;
	}

	private static string Scope(int expiryCount, int dte) => $"{expiryCount} expiration(s) ≤{dte}DTE";
}

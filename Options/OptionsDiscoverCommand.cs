using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;

namespace WebullAnalytics.Options;

/// <summary>`wa options discover &lt;ticker&gt;` — walks a backtest window and writes every reasonably-
/// scored OCC to <c>data/options-discovery/&lt;ticker&gt;.jsonl</c>. The discovery catalog is the union of
/// what the bot's evaluator considered (not just what it opened); `wa options backfill` then pulls real
/// per-minute bars for the catalog from Webull (live) and massive.com (expired).
///
/// <para>Conceptually a stripped-down backtest run: re-uses <see cref="BacktestRunner"/> internally
/// because the discovery work IS the opener walk — there's no separate code path worth duplicating.
/// What we don't render is the backtest summary table; the run's only artifact is the discovery log.</para></summary>
internal sealed class OptionsDiscoverSettings : AISingleTickerSubcommandSettings
{
	[CommandOption("--since <DATE>")]
	[Description("Start date YYYY-MM-DD. Default: Jan 1 of current year.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[Description("End date YYYY-MM-DD. Default: today.")]
	public string? Until { get; set; }

	[CommandOption("--top-k <N>")]
	[Description("Capture the top-K candidates the evaluator considered each day (by FinalScore), deduped by proposal fingerprint across the day's minutes. Larger K = broader sweep coverage at the cost of more contracts to backfill. Set to 1 for legacy 1-OCC-per-day behavior. Default: 20.")]
	public int TopK { get; set; } = 20;

	[CommandOption("--pad <N>")]
	[Description("Widen each (expiry,right) strike range by N grid steps on both sides (and fill interior gaps) in the discovery catalog, so a sweep that nudges the chosen strike still lands on a captured bar instead of a back-solved one. 0 = picked strikes + interior gaps only. Larger N = more contracts to backfill. Default: 2.")]
	public int Pad { get; set; } = 2;

	[CommandOption("--bias-drift <VALUE>")]
	[Description("Override opener.weights.biasDrift for this run. Used to sweep directional-bias regimes (each variant contributes new picks to the same discovery file).")]
	public decimal? BiasDriftOverride { get; set; }

	[CommandOption("--min-score-to-open <VALUE>")]
	[Description("Override opener.minScoreToOpen for this run. Lowering opens more days; raising fires only on high-conviction setups.")]
	public decimal? MinScoreToOpenOverride { get; set; }

	[CommandOption("--intraday-tape-weight <VALUE>")]
	[Description("Override opener.weights.intradayTape for this run. 0.0 = pure macro bias; 1.0 = pure intraday tape.")]
	public decimal? IntradayTapeWeightOverride { get; set; }

	[CommandOption("--enable-structure <NAME>")]
	[Description("Force-enable a structure for this run (repeatable). Names: longCalendar, doubleCalendar, longDiagonal, doubleDiagonal, ironButterfly, ironCondor, shortVertical, longCallPut, longVertical.")]
	public string[] EnableStructures { get; set; } = Array.Empty<string>();

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--until: must be YYYY-MM-DD, got '{Until}'");
		if (TopK < 1) return ValidationResult.Error($"--top-k: must be >= 1, got {TopK}");
		if (Pad < 0) return ValidationResult.Error($"--pad: must be >= 0, got {Pad}");
		if (BiasDriftOverride.HasValue && BiasDriftOverride.Value < 0m)
			return ValidationResult.Error($"--bias-drift: must be >= 0, got {BiasDriftOverride}");
		if (IntradayTapeWeightOverride.HasValue && (IntradayTapeWeightOverride.Value < 0m || IntradayTapeWeightOverride.Value > 1m))
			return ValidationResult.Error($"--intraday-tape-weight: must be in 0..1, got {IntradayTapeWeightOverride}");
		return ValidationResult.Success();
	}
}

internal sealed class OptionsDiscoverCommand : AsyncCommand<OptionsDiscoverSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, OptionsDiscoverSettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		var since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: new DateTime(DateTime.Today.Year, 1, 1);
		var until = settings.Until != null
			? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: DateTime.Today;

		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;
		config.Ticker = ticker;

		// Apply CLI overrides on the in-memory merged config (same pattern as `wa ai backtest`).
		if (settings.BiasDriftOverride.HasValue) config.Opener.Weights.BiasDrift = settings.BiasDriftOverride.Value;
		if (settings.MinScoreToOpenOverride.HasValue) config.Opener.MinScoreToOpen = settings.MinScoreToOpenOverride.Value;
		if (settings.IntradayTapeWeightOverride.HasValue) config.Opener.Weights.IntradayTape = settings.IntradayTapeWeightOverride.Value;
		foreach (var name in settings.EnableStructures)
		{
			switch (name.ToLowerInvariant())
			{
				case "longcalendar": config.Opener.Structures.LongCalendar.Enabled = true; break;
				case "doublecalendar": config.Opener.Structures.DoubleCalendar.Enabled = true; break;
				case "longdiagonal": config.Opener.Structures.LongDiagonal.Enabled = true; break;
				case "doublediagonal": config.Opener.Structures.DoubleDiagonal.Enabled = true; break;
				case "ironbutterfly": config.Opener.Structures.IronButterfly.Enabled = true; break;
				case "ironcondor": config.Opener.Structures.IronCondor.Enabled = true; break;
				case "shortvertical": config.Opener.Structures.ShortVertical.Enabled = true; break;
				case "longcallput": config.Opener.Structures.LongCallPut.Enabled = true; break;
				case "longvertical": config.Opener.Structures.LongVertical.Enabled = true; break;
				default: Console.Error.WriteLine($"Warning: --enable-structure '{name}' is not a recognized structure name; ignoring."); break;
			}
		}

		AnsiConsole.MarkupLine($"[bold]Discovering OCCs for {Markup.Escape(ticker)}[/] over {since:yyyy-MM-dd} → {until:yyyy-MM-dd} (top-K={settings.TopK})");

		var bars = new HistoricalBarCache(offline: true);
		if (!await bars.HasCoverageAsync(ticker, since, until, cancellation))
		{
			Console.Error.WriteLine($"Error: missing bar history for {ticker} in [{since:yyyy-MM-dd} → {until:yyyy-MM-dd}]. Run: wa ai history {ticker}");
			return 1;
		}

		var smile = new SmileIndexCache(offline: true);
		var ivProvider = new BacktestIVProvider(bars, smile: smile);
		var optionBars = new HistoricalOptionBarCache();
		var quotes = new BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036, optionBars: optionBars);
		var closes = new HistoricalPriceCache(bars);
		var feePerContract = SimulatedBook.DefaultFeePerContractFor(ticker);
		var book = new SimulatedBook(10000m, feePerContract, config.Opener.RealizedExpectancy);
		var positions = new BacktestPositionSource(book, quotes);
		var runner = new BacktestRunner(config, book, positions, quotes, bars, closes,
			topNPerStep: 1, oracle: false, profile: false, discover: true, discoverTopKPerDay: settings.TopK, discoverPadStrikes: settings.Pad);

		_ = await runner.RunAsync(since, until, cancellation);
		// FlushDiscoveryLog inside the runner prints the "N new + M prior" summary — that's the only
		// signal of interest here. No backtest summary table.
		return 0;
	}
}

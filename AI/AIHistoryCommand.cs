using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

/// <summary>`wa ai history &lt;ticker&gt;` — populate the historical OHLC caches used by `wa ai backtest`.
/// Separation of concerns: the fetch step hits the network here (where transient Yahoo failures are
/// loud and re-runnable), while the backtest runs purely offline against the cache. Mirrors the
/// `wa fetch` / `wa report` split. SPX-family tickers (SPY, SPX, SPXW, XSP) also pull VIX, VIX1D, and
/// VIX9D, since the backtest reads ATM IV from the appropriate term (VIX1D for 0–1 DTE, VIX9D for
/// 2–9 DTE, VIX for longer) and the opener reads the VIX term-structure regime score.</summary>
internal sealed class AIHistorySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., SPY, QQQ, GME). SPX family also refreshes VIX + VIX1D + VIX9D.")]
	public string Ticker { get; set; } = "";

	[CommandOption("--lookback-years <N>")]
	[Description("How far back to ensure the cache covers when starting fresh. Default: 2.")]
	public int LookbackYears { get; set; } = 2;

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (LookbackYears < 1) return ValidationResult.Error($"--lookback-years: must be ≥ 1, got {LookbackYears}");
		return ValidationResult.Success();
	}
}

internal sealed class AIHistoryCommand : AsyncCommand<AIHistorySettings>
{
	private static readonly HashSet<string> VixDrivenTickers = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPY", "SPX", "SPXW", "XSP"
	};

	public override async Task<int> ExecuteAsync(CommandContext context, AIHistorySettings settings, CancellationToken cancellation)
	{
		TerminalHelper.EnsureTerminalWidthFromConfig();
		var ticker = settings.Ticker.ToUpperInvariant();

		AnsiConsole.MarkupLine($"[bold]Fetching history for {Markup.Escape(ticker)}[/]");

		var bars = new HistoricalBarCache();
		var asOf = DateTime.UtcNow;
		var earliest = asOf.AddYears(-settings.LookbackYears);

		if (!await FetchAndReportAsync(bars, ticker, earliest, asOf, cancellation))
			return 1;

		// VIX-family rides along with SPX-family fetches. VIX1D / VIX9D / VIX anchor backtest ATM IV at
		// the appropriate term (0–1 DTE, 2–9 DTE, 10+ DTE respectively); VIX9D + VIX also feed the VIX
		// term-structure signal in the opener. CBOE SMILE drives per-day smile steepness scaling in
		// BacktestIVProvider so backtest fills track the actual regime (calm vs stressed days) instead
		// of using a single anchor calibration. SMILE comes from CBOE directly (its own daily CSV);
		// VIX / VIX1D / VIX9D come from Yahoo as OHLC. VIX1D was launched 2023-04-24; pre-launch dates
		// fall back to VIX9D in the IV provider, so historical backtests beyond that point still work.
		if (VixDrivenTickers.Contains(ticker))
		{
			if (!await FetchAndReportAsync(bars, "VIX", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX1D", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX9D", earliest, asOf, cancellation)) return 1;
			if (!await FetchSmileAsync(earliest, asOf, cancellation)) return 1;
		}

		AnsiConsole.MarkupLine("[dim]Done. Run `wa ai backtest " + Markup.Escape(ticker) + "` to use this data.[/]");
		return 0;
	}

	private static async Task<bool> FetchAndReportAsync(HistoricalBarCache bars, string ticker, DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var bar = await bars.GetBarAsync(ticker, asOf.Date.AddDays(-1), cancellation);
		if (bar == null)
		{
			AnsiConsole.MarkupLine($"  {Markup.Escape(ticker)}: [red]failed[/] (Yahoo unreachable or unknown ticker)");
			return false;
		}
		var hasCoverage = await bars.HasCoverageAsync(ticker, earliest, asOf, cancellation);
		AnsiConsole.MarkupLine(hasCoverage
			? $"  {Markup.Escape(ticker)}: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
			: $"  {Markup.Escape(ticker)}: [yellow]partial[/] (Yahoo returned a shorter window than requested)");
		return true;
	}

	private static async Task<bool> FetchSmileAsync(DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var smile = new SmileIndexCache();
		var probe = await smile.GetValueAsync(asOf.Date.AddDays(-1), cancellation);
		if (probe == null)
		{
			AnsiConsole.MarkupLine($"  SMILE: [red]failed[/] (CBOE CSV unreachable)");
			return false;
		}
		var hasCoverage = await smile.HasCoverageAsync(earliest, asOf, cancellation);
		AnsiConsole.MarkupLine(hasCoverage
			? $"  SMILE: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
			: $"  SMILE: [yellow]partial[/] (CBOE history shorter than requested)");
		return true;
	}
}

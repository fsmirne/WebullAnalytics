using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

/// <summary>`wa ai history &lt;ticker&gt;` — populate the historical OHLC + VIX caches used by `wa ai backtest`.
/// Separation of concerns: the fetch step hits the network here (where transient Yahoo/CNN failures are
/// loud and re-runnable), while the backtest runs purely offline against the cache. Mirrors the
/// `wa fetch` / `wa report` split.</summary>
internal sealed class AIHistorySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., SPY, QQQ, GME). For SPY also refreshes the VIX cache.")]
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
	public override async Task<int> ExecuteAsync(CommandContext context, AIHistorySettings settings, CancellationToken cancellation)
	{
		TerminalHelper.EnsureTerminalWidthFromConfig();
		var ticker = settings.Ticker.ToUpperInvariant();

		AnsiConsole.MarkupLine($"[bold]Fetching history for {Markup.Escape(ticker)}[/]");

		var bars = new HistoricalBarCache();
		var asOf = DateTime.UtcNow;
		var earliest = asOf.AddYears(-settings.LookbackYears);

		var bar = await bars.GetBarAsync(ticker, asOf.Date.AddDays(-1), cancellation);
		if (bar == null)
		{
			AnsiConsole.MarkupLine($"[red]Failed to fetch bars for {Markup.Escape(ticker)} (Yahoo unreachable or unknown ticker).[/]");
			return 1;
		}

		var hasCoverage = await bars.HasCoverageAsync(ticker, earliest, asOf, cancellation);
		AnsiConsole.MarkupLine(hasCoverage
			? $"  bars: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
			: $"  bars: [yellow]partial[/] (Yahoo returned a shorter window than requested)");

		// VIX is required when backtesting SPY because that's where the SPY ATM IV comes from.
		// Pull it whenever the user fetches SPY so the two caches stay in sync. The F&G endpoint only
		// returns ~6 months per call, so EnsureRangeAsync chunks calls to span the requested lookback.
		if (string.Equals(ticker, "SPY", StringComparison.OrdinalIgnoreCase))
		{
			var vix = new HistoricalVixCache();
			await vix.EnsureRangeAsync(earliest, asOf, cancellation);
			var vixVal = await vix.GetVixAsync(asOf.Date.AddDays(-1), cancellation);
			if (vixVal == null)
			{
				AnsiConsole.MarkupLine($"  VIX: [red]failed[/] (CNN F&G endpoint unreachable)");
				return 1;
			}
			var vixCoverage = await vix.HasCoverageAsync(earliest, asOf, cancellation);
			AnsiConsole.MarkupLine(vixCoverage
				? $"  VIX: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
				: $"  VIX: [yellow]partial[/] (chunked fetch covered as far back as CNN serves)");
		}

		AnsiConsole.MarkupLine("[dim]Done. Run `wa ai backtest " + Markup.Escape(ticker) + "` to use this data.[/]");
		return 0;
	}
}

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.Pricing.Clock;

namespace WebullAnalytics.Data;

internal sealed class DataClockSettings : CommandSettings
{
	[CommandArgument(0, "[ticker]")]
	[Description("Ticker to seed and fit (once per ticker whose positions cross nights). Omit to refresh every ticker already in the clock store.")]
	public string? Ticker { get; set; }

	[CommandOption("--since <yyyy-MM-dd>")]
	[Description("Seed from this date (backtest coverage). Default: the day after the newest stored session, or one fit window (365 days) back for a new ticker.")]
	public string? Since { get; set; }

	public override ValidationResult Validate() =>
		Since != null && !DateOnly.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
			? ValidationResult.Error($"--since must be yyyy-MM-dd, got '{Since}'")
			: ValidationResult.Success();
}

/// <summary>`wa data clock [TICKER]`: keeps the trading-time clock calibrated. Seeds the ticker's close/09:45 book store for missing
/// completed sessions (quotes.db when it has a usable book, else ThetaData), then refits the clock weights — a monthly history for
/// backtests plus the newest window for live pricing — and prints what the newest fit found against the calendar clock.</summary>
internal sealed class DataClockCommand : AsyncCommand<DataClockSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, DataClockSettings settings, CancellationToken cancellation)
	{
		var tickers = settings.Ticker != null ? [ClockFits.ClockRoot(settings.Ticker)] : TrackedRoots();
		if (tickers.Count == 0)
		{
			AnsiConsole.MarkupLine("[yellow]No tickers in the clock store yet: seed one, e.g. `wa data clock SPY`.[/]");
			return 0;
		}
		var today = DateOnly.FromDateTime(MarketTime.TodayEt());
		var since = settings.Since != null ? DateOnly.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture) : (DateOnly?)null;
		var failed = false;
		using var seeder = new ClockSeeder(Program.ResolvePath("data/quotes.db"));
		foreach (var root in tickers)
		{
			var stored = ClockStore.Dates(root);
			var from = since ?? (stored.Count > 0 ? stored[^1].AddDays(1) : today.AddDays(-ClockFits.WindowDays));
			var seed = await seeder.SeedAsync(root, from, today, Report, cancellation);
			if (seed.FromQuotesDb + seed.FromThetaData > 0 || seed.StillMissing > 0)
				AnsiConsole.MarkupLine($"{root}: stored {seed.FromQuotesDb} day(s) from quotes.db, {seed.FromThetaData} from ThetaData{(seed.StillMissing > 0 ? $", [yellow]{seed.StillMissing} still missing[/]" : "")}{(seed.ThetaError != null ? $" [red]({Markup.Escape(seed.ThetaError)})[/]" : "")}");
			failed |= seed.ThetaError != null && seed.StillMissing > 0;

			var (latest, _) = ClockFits.Refresh(root, Report);
			AnsiConsole.MarkupLine(latest == null
				? $"[yellow]{root}: not enough history to fit (needs {ClockFitter.MinNights} nights incl. {ClockFitter.MinClosedDayNights} over a weekend/holiday) — prices on the calendar clock.[/]"
				: $"[bold]{root}[/]: overnight {latest.Overnight:0.00}, weekend/holiday {latest.NonTrading:0.00} (per hour vs a session hour) — fitted through {latest.FittedThrough:yyyy-MM-dd} on {latest.Nights} nights ({latest.ClosedDayNights} over closed days, {latest.SkippedNights} skipped as vol jumps), {latest.Options:N0} options; 09:45 repricing error ${latest.MaeFitted:0.000} vs ${latest.MaeCalendar:0.000} on the calendar clock");
		}
		return failed ? 1 : 0;
	}

	private static void Report(string line) => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");

	/// <summary>Tickers already in the clock store — seeded once on demand with `wa data clock {TICKER}`. The daily refresh never seeds
	/// new tickers on its own: only roots whose positions cross nights need a fit (same-day roots are clock-independent).</summary>
	private static List<string> TrackedRoots()
	{
		var clockDir = Program.ResolvePath("data/clock");
		return Directory.Exists(clockDir) ? Directory.EnumerateDirectories(clockDir).Select(Path.GetFileName).OfType<string>().Select(ClockFits.ClockRoot).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList() : [];
	}
}

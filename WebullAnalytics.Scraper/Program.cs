using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.Api;

namespace WebullAnalytics.Scraper;

/// <summary>Entry point for <c>wa-scraper.exe</c>. Long-running per-minute chain-snapshot capture
/// for one ticker, mirroring <c>wa ai watch</c>'s start-time / end-time scheduling but with
/// minute-aligned firing (no drift) and no proposal evaluation — purely persists the raw chain
/// the backtest then replays against.</summary>
internal sealed class Program
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private static int Main(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;
		var app = new CommandApp<ScrapeCommand>();
		app.Configure(c =>
		{
			c.SetApplicationName("wa-scraper");
			c.Settings.StrictParsing = true;
		});
		return app.Run(args);
	}
}

internal sealed class ScrapeSettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Ticker root to scrape (e.g. SPXW). Snapshots are written to data/chain-snapshots/<TICKER>/<date>.jsonl.")]
	public string Ticker { get; set; } = "";

	[CommandOption("--config <PATH>")]
	[Description("Path to scraper-config.json. Default: data/scraper-config.json.")]
	public string? ConfigPath { get; set; }

	[CommandOption("--start <TIME>")]
	[Description("Override config startTime (ET HH:mm or HH:mm:ss).")]
	public string? Start { get; set; }

	[CommandOption("--end <TIME>")]
	[Description("Override config endTime (ET HH:mm or HH:mm:ss).")]
	public string? End { get; set; }

	[CommandOption("--interval <SECONDS>")]
	[Description("Override config intervalSeconds. Must divide 60 evenly for clean minute boundaries (1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30, 60).")]
	public int? Interval { get; set; }

	[CommandOption("--max-dte <N>")]
	[Description("Capture expiries from today out to N calendar days. Default 0 = today/0DTE only (original behavior). Use e.g. 45 to also capture the diagonal/calendar long legs for pricing validation — larger per-minute files.")]
	public int? MaxDte { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (MaxDte.HasValue && MaxDte.Value < 0) return ValidationResult.Error($"--max-dte: must be >= 0, got {MaxDte.Value}");
		if (Start != null && !TimeOnly.TryParse(Start, CultureInfo.InvariantCulture, out _))
			return ValidationResult.Error($"--start: must be HH:mm or HH:mm:ss, got '{Start}'");
		if (End != null && !TimeOnly.TryParse(End, CultureInfo.InvariantCulture, out _))
			return ValidationResult.Error($"--end: must be HH:mm or HH:mm:ss, got '{End}'");
		if (Interval.HasValue && (Interval.Value < 1 || Interval.Value > 3600))
			return ValidationResult.Error($"--interval: must be in [1, 3600], got {Interval.Value}");
		return ValidationResult.Success();
	}
}

internal sealed class ScrapeCommand : AsyncCommand<ScrapeSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	public override async Task<int> ExecuteAsync(CommandContext context, ScrapeSettings settings, CancellationToken cancellation)
	{
		var config = ScraperConfigLoader.Load(settings.ConfigPath);
		if (settings.Interval.HasValue) config.IntervalSeconds = settings.Interval.Value;
		if (settings.Start != null) config.StartTime = settings.Start;
		if (settings.End != null) config.EndTime = settings.End;
		if (settings.MaxDte.HasValue) config.MaxDte = settings.MaxDte.Value;

		var apiConfigPath = WebullAnalytics.Program.ResolvePath(WebullAnalytics.Program.ApiConfigPath);
		if (!File.Exists(apiConfigPath))
		{
			AnsiConsole.MarkupLine("[red]api-config.json not found[/] — run `wa sniff` to bootstrap it.");
			return 1;
		}
		ApiConfig? apiConfig;
		try { apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(apiConfigPath)); }
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[red]failed to parse api-config.json[/]: {Markup.Escape(ex.Message)}");
			return 1;
		}
		if (apiConfig == null || apiConfig.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("[red]api-config.json has no headers[/] — run `wa sniff` to refresh.");
			return 1;
		}

		var todayEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz).Date;
		var startEt = ParseEtTimeToday(config.StartTime, todayEt);
		var endEt = ParseEtTimeToday(config.EndTime, todayEt);
		if (endEt <= startEt)
		{
			AnsiConsole.MarkupLine($"[red]endTime ({endEt:HH:mm:ss}) must be after startTime ({startEt:HH:mm:ss})[/]");
			return 1;
		}

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

		var loop = new ScraperLoop(settings.Ticker, config, apiConfig);
		return await loop.RunAsync(startEt, endEt, cts.Token);
	}

	private static DateTime ParseEtTimeToday(string hhmm, DateTime nyDate)
	{
		var t = TimeOnly.Parse(hhmm, CultureInfo.InvariantCulture);
		return new DateTime(nyDate.Year, nyDate.Month, nyDate.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);
	}
}

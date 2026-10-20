using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data.Backfill;

internal abstract class BackfillSettings : CommandSettings
{
	[CommandOption("--tickers <LIST>")]
	[Description("Comma-separated roots. No default — no ticker preference is committed.")]
	public string Tickers { get; set; } = "";

	[CommandOption("--start <yyyy-MM-dd>")]
	[Description("Pull floor. Default 2025-01-01; sealed data before it is skipped anyway, so extend it only for a one-off history fill.")]
	public string Start { get; set; } = "2025-01-01";

	[CommandOption("--end <yyyy-MM-dd>")]
	[Description("Last day to pull. Default: today ET.")]
	public string? End { get; set; }

	[CommandOption("--timeout <SECONDS>")]
	[Description("Longest wait for the next chunk of a response before the request is cancelled as hung and retried. Default 600.")]
	public int Timeout { get; set; } = 600;

	[CommandOption("--log <PATH>")]
	[Description("Log file (appended). Default: data/logs/backfill_<mode>_<timestamp>.log.")]
	public string? Log { get; set; }

	public DateOnly StartDate => DateOnly.ParseExact(Start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
	public DateOnly EndDate => End != null ? DateOnly.ParseExact(End, "yyyy-MM-dd", CultureInfo.InvariantCulture) : MinuteBackfill.EtToday();

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Tickers)) return ValidationResult.Error("--tickers is required");
		foreach (var (name, value) in new[] { ("--start", (string?)Start), ("--end", End) })
			if (value != null && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
				return ValidationResult.Error($"{name}: expected yyyy-MM-dd, got '{value}'");
		return Timeout > 0 ? ValidationResult.Success() : ValidationResult.Error("--timeout must be positive");
	}

	internal static IEnumerable<string> Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant());
}

internal class MinuteBackfillSettings : BackfillSettings
{
	[CommandOption("--concurrency <N>")]
	[Description("Requests in flight at once on the one ThetaData session — the subscription tier's limit (Value 2). Default 2.")]
	public int Concurrency { get; set; } = 2;

	[CommandOption("--chunk <DAYS>")]
	[Description("Calendar days per request window. Smaller = each request is small enough not to stall at the server; larger = fewer requests. Default 7.")]
	public int Chunk { get; set; } = 7;

	[CommandOption("--band <FRACTION>")]
	[Description("Strike band as a fraction of each day's spot close. Default 0.10. Wider only with --supplement: a full pull at a new width records the same seals, so mixed-width history would be undetectable.")]
	public double Band { get; set; } = 0.10;

	[CommandOption("--db <PATH>")]
	[Description("Quote store to write. Default: data/quotes.db.")]
	public string? Db { get; set; }

	public override ValidationResult Validate()
	{
		var result = base.Validate();
		if (!result.Successful) return result;
		foreach (var token in Split(Tickers))
			if (token.Split(':') is not [{ Length: > 0 }, var dte] || !int.TryParse(dte, out var d) || d < 0)
				return ValidationResult.Error($"--tickers: '{token}' needs a DTE window as NAME:DTE (e.g. SPY:60, XSP:0)");
		if (Concurrency < 1 || Chunk < 1 || !(Band > 0)) return ValidationResult.Error("--concurrency and --chunk must be >= 1 and --band positive");
		return ValidationResult.Success();
	}

	public IReadOnlyList<(string Ticker, int Dte)> TickerDtes => [.. Split(Tickers).Select(t => t.Split(':')).Select(p => (p[0], int.Parse(p[1], CultureInfo.InvariantCulture)))];
}

internal sealed class QuotesBackfillSettings : MinuteBackfillSettings
{
	[CommandOption("--supplement")]
	[Description("Additive patch mode: pull sealed expiries too, never delete or (re)seal — INSERT OR IGNORE layers new rows (e.g. a wider --band ring for crash days where held strikes drifted out of the default band) alongside existing ones. Scope with --start/--end to the affected sessions.")]
	public bool Supplement { get; set; }
}

internal sealed class OhlcvBackfillSettings : MinuteBackfillSettings
{
	[CommandOption("--range <N>")]
	[Description("Server-side ATM strike window: n listed strikes above/below ATM, trimming the endpoint's dense zero-volume grid before transfer. Default 80 (about ±20% on a $1 grid — wider than the ±10% band even across a crash week); 0 = full chain.")]
	public int Range { get; set; } = 80;
}

internal sealed class OiBackfillSettings : BackfillSettings
{
	[CommandOption("--dte <DAYS>")]
	[Description("Expirations captured per day: those within this many days. Default 60.")]
	public int Dte { get; set; } = 60;

	[CommandOption("--rate <RATE>")]
	[Description("Risk-free rate for the IV back-solve. Default 0.045.")]
	public double Rate { get; set; } = 0.045;

	[CommandOption("--retries <N>")]
	[Description("Extra attempts per month chunk after a failure. Default 3.")]
	public int Retries { get; set; } = 3;

	[CommandOption("--out <DIR>")]
	[Description("OI store root. Default: data/oi.")]
	public string? Out { get; set; }
}

/// <summary>`wa data backfill quotes`: minute NBBO from ThetaData into data/quotes.db (see <see cref="MinuteBackfill"/>). Exit 1 when
/// sealable expirations remain unsealed after the retry pass — the store is incomplete for them — so chained scripts can gate on it.</summary>
internal sealed class QuotesBackfillCommand : AsyncCommand<QuotesBackfillSettings>
{
	protected override Task<int> ExecuteAsync(CommandContext context, QuotesBackfillSettings settings, CancellationToken cancellation) =>
		BackfillRunner.RunMinuteAsync(MinuteDataset.Quotes, settings, settings.Supplement, strikeRange: null, cancellation);
}

/// <summary>`wa data backfill ohlcv`: minute trade bars from ThetaData into data/quotes.db's ohlcv table.</summary>
internal sealed class OhlcvBackfillCommand : AsyncCommand<OhlcvBackfillSettings>
{
	protected override Task<int> ExecuteAsync(CommandContext context, OhlcvBackfillSettings settings, CancellationToken cancellation) =>
		BackfillRunner.RunMinuteAsync(MinuteDataset.Ohlcv, settings, supplement: false, settings.Range > 0 ? settings.Range : null, cancellation);
}

/// <summary>`wa data backfill oi`: daily full-chain EOD + open interest into data/oi (see <see cref="OiBackfill"/>).</summary>
internal sealed class OiBackfillCommand : AsyncCommand<OiBackfillSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, OiBackfillSettings settings, CancellationToken cancellation)
	{
		using var log = new BackfillLog("eod", settings.Log);
		var options = new OiBackfillOptions([.. BackfillSettings.Split(settings.Tickers).Select(t => t.Split(':')[0])], settings.StartDate, settings.EndDate, settings.Dte, settings.Rate, settings.Retries, settings.Out ?? Program.ResolvePath("data/oi"));
		return await BackfillRunner.WithSessionAsync(log, concurrency: 1, settings.Timeout, async client =>
		{
			await new OiBackfill(client, options, log).RunAsync(cancellation);
			return 0;
		}, cancellation);
	}
}

internal static class BackfillRunner
{
	internal static async Task<int> RunMinuteAsync(MinuteDataset dataset, MinuteBackfillSettings settings, bool supplement, int? strikeRange, CancellationToken cancellation)
	{
		using var log = new BackfillLog(dataset.Name, settings.Log);
		var dbPath = settings.Db ?? Program.ResolvePath("data/quotes.db");
		log.Info($"{dataset.Name} store = {dbPath} (minute, ±{settings.Band:P0} band, per-ticker DTE{(strikeRange is int r ? $", server strike window ±{r}" : "")})");
		var options = new MinuteBackfillOptions(settings.TickerDtes, settings.StartDate, settings.EndDate, settings.Chunk, settings.Concurrency, settings.Band, supplement, strikeRange);
		return await WithSessionAsync(log, settings.Concurrency, settings.Timeout, async client =>
		{
			using var db = QuoteStoreSchema.OpenWal(dbPath);
			return await new MinuteBackfill(client, db, dataset, options, log).RunAsync(cancellation) > 0 ? 1 : 0;
		}, cancellation);
	}

	/// <summary>Signs in (holding the session lock for the whole run) and runs <paramref name="body"/>; a session that is taken or
	/// lost ends the run with exit 1.</summary>
	internal static async Task<int> WithSessionAsync(BackfillLog log, int concurrency, int timeoutSeconds, Func<ThetaDataClient, Task<int>> body, CancellationToken cancellation)
	{
		log.Info($"logging to {log.FilePath}");
		try
		{
			using var client = await ThetaDataClient.ConnectAsync(ThetaDataClient.DefaultCredsPath(), concurrency, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
			return await body(client);
		}
		catch (ThetaSessionException ex)
		{
			log.Info($"[FAILED] {ex.Message}");
			AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
			return 1;
		}
	}
}

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;

namespace WebullAnalytics.Analyze;

/// <summary>
/// `wa analyze cascade-study &lt;TICKER&gt;` — research replay of `analyze cascade` over past sessions. For every session in range and
/// every checkpoint it runs the panel's own causal replay (<see cref="CascadeScreener.BuildCausalHistoricalChain"/> then
/// <see cref="CascadeScreener.ReadChainAsync"/>, exactly what `analyze cascade --date D --time T` renders) and writes one JSONL row:
/// the whole reading plus what the underlying did after it, so an offline analysis can test whether a reading leads the rest of
/// the session or only describes the part already traded.
///
/// The reading's spot is the close of the bar labeled T (START-of-bar label, so it ends at T+1:00) — the panel's own replay
/// convention, which also reads the front-expiry NBBO and store volume through that bar. Outcomes are measured from that same
/// spot over the bars labeled after T, so a row never sees its own future: <c>close</c> is the last regular-session bar's close
/// (13:00 on a half-day), <c>hiAfter</c>/<c>loAfter</c> the extremes after T, <c>fwd30</c>/<c>fwd60</c> the closes 30 and 60
/// bars later (null past the close).
/// </summary>
internal sealed class AnalyzeCascadeStudySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying root with data/oi snapshots and data/intraday tape (e.g. SPXW).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--since <DATE>")]
	[Description("First session (YYYY-MM-DD). Default: 2022-01-03.")]
	public string Since { get; set; } = "2022-01-03";

	[CommandOption("--until <DATE>")]
	[Description("Last session (YYYY-MM-DD). Default: yesterday.")]
	public string? Until { get; set; }

	[CommandOption("--times <HH:MM,...>")]
	[Description("Comma-separated ET checkpoints. Default: 09:50.")]
	public string Times { get; set; } = "09:50";

	[CommandOption("--out <PATH>")]
	[Description("JSONL output path (Windows path for wa.exe).")]
	public string Out { get; set; } = "";

	[CommandOption("--threads <N>")]
	[Description("Sessions processed in parallel. Default: half the logical cores.")]
	public int? Threads { get; set; }

	internal IReadOnlyList<TimeSpan> Checkpoints => Times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => AnalyzeGexSettings.ParseEtTime(t)!.Value).OrderBy(t => t).ToList();

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("<ticker> is required");
		if (string.IsNullOrWhiteSpace(Out)) return ValidationResult.Error("--out is required");
		if (!DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return ValidationResult.Error($"--since: expected YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return ValidationResult.Error($"--until: expected YYYY-MM-DD, got '{Until}'");
		foreach (var t in Times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (AnalyzeGexSettings.ParseEtTime(t) is not { } ts || ts < AnalyzeGexSettings.RthOpen || ts >= AnalyzeGexSettings.RthClose)
				return ValidationResult.Error($"--times: '{t}' is not an ET time within 09:30-15:59");
		if (Threads is < 1) return ValidationResult.Error("--threads must be ≥ 1");
		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeCascadeStudyCommand : AsyncCommand<AnalyzeCascadeStudySettings>
{
	// The panel's defaults (AnalyzeCascadeSettings), so a study row is what `analyze cascade --date --time` shows.
	private const int Dte = 30;
	private const decimal StrikeRangeFraction = 0.20m;
	private const int MaxStrikes = 200;

	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeCascadeStudySettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		var since = DateTime.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var until = settings.Until != null ? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-1);
		var checkpoints = settings.Checkpoints;
		var sessions = new List<DateTime>();
		for (var d = since.Date; d <= until.Date; d = d.AddDays(1))
			if (MarketCalendar.IsOpen(d)) sessions.Add(d);

		var threads = settings.Threads ?? Math.Max(1, Environment.ProcessorCount / 2);
		var minuteVolume = new System.Collections.Concurrent.ConcurrentDictionary<(string, DateTime, DateTime), Lazy<SortedDictionary<TimeSpan, long>?>>();
		var rows = new System.Collections.Concurrent.ConcurrentBag<(DateTime Date, TimeSpan Time, string Json)>();
		var skipped = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
		var done = 0;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		AnsiConsole.MarkupLine($"[dim]{sessions.Count} session(s) {since:yyyy-MM-dd}..{until:yyyy-MM-dd}, checkpoints {string.Join(",", checkpoints.Select(c => c.ToString(@"hh\:mm")))}, {threads} thread(s).[/]");
		await Parallel.ForEachAsync(sessions, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellation }, async (date, ct) =>
		{
			var reason = await StudySessionAsync(ticker, date, checkpoints, new CascadeReplayCache(minuteVolume), rows, ct);
			if (reason != null) skipped.AddOrUpdate(reason, 1, (_, n) => n + 1);
			var n = Interlocked.Increment(ref done);
			if (n % 25 == 0 || n == sessions.Count)
			{
				var elapsed = clock.Elapsed;
				var eta = TimeSpan.FromSeconds(elapsed.TotalSeconds / n * (sessions.Count - n));
				Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {n}/{sessions.Count} sessions ({n * 100 / sessions.Count}%, {rows.Count} readings), elapsed {elapsed:hh\\:mm\\:ss}, ETA {eta:hh\\:mm\\:ss}");
			}
		});

		var path = Path.IsPathRooted(settings.Out) ? settings.Out : Path.GetFullPath(settings.Out);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllLinesAsync(path, rows.OrderBy(r => r.Date).ThenBy(r => r.Time).Select(r => r.Json), cancellation);
		AnsiConsole.MarkupLine($"Wrote {rows.Count} reading(s) to {Markup.Escape(path)}.");
		foreach (var (reason, n) in skipped.OrderByDescending(kv => kv.Value))
			AnsiConsole.MarkupLine($"[dim]  skipped {n} session(s): {Markup.Escape(reason)}[/]");
		return 0;
	}

	/// <summary>Replays one session at every checkpoint; returns a skip reason, or null when it produced readings. Each session gets
	/// its own price and SMILE caches (neither is thread-safe; both read their on-disk caches without fetching for past dates) and its
	/// own replay memo, so the session-level loads run once per session rather than once per checkpoint.</summary>
	private static async Task<string?> StudySessionAsync(string ticker, DateTime date, IReadOnlyList<TimeSpan> checkpoints, CascadeReplayCache cache, System.Collections.Concurrent.ConcurrentBag<(DateTime, TimeSpan, string)> rows, CancellationToken ct)
	{
		var oiPath = Program.ResolvePath($"data/oi/{ticker}/{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(oiPath)) return "no data/oi snapshot";
		var snapshot = AnalyzeGexCommand.LoadOiSnapshot(oiPath);
		if (snapshot.Quotes.Count == 0) return "empty data/oi snapshot";

		var close = MarketCalendar.CloseTimeEt(date);
		var dayBars = await CascadeScreener.LoadSessionRthBarsAsync(ticker, date + close, intradayCache: null, includeCurrentBar: false, ct);
		if (dayBars.Count == 0) return "no data/intraday tape";

		var priceCache = new HistoricalPriceCache();
		var smileCache = new SmileIndexCache(offline: true);
		var produced = 0;
		foreach (var t in checkpoints)
		{
			if (t >= close) continue;
			var asOfEt = date + t;
			// START-of-bar labels: the bars that closed by the checkpoint are the reading's (the last close is the price AT it); the bar that
			// starts at the checkpoint is the first one after it.
			var rthBars = dayBars.Where(b => MarketTime.ToEt(b.Timestamp).TimeOfDay < t).ToList();
			var after = dayBars.Where(b => MarketTime.ToEt(b.Timestamp).TimeOfDay >= t).ToList();
			if (rthBars.Count == 0 || after.Count == 0) continue;
			var spot = rthBars[^1].Close;

			var quotes = CascadeScreener.BuildCausalHistoricalChain(ticker, asOfEt, snapshot.Quotes, forceExante: false, out _, cache);
			if (AnalyzeGexCommand.IsOiBlackout(quotes, ticker, date)) continue;
			var read = await CascadeScreener.ReadChainAsync(ticker, spot, asOfEt, quotes, Dte, StrikeRangeFraction, MaxStrikes, CascadeScreener.DefaultTriggerScore, rthBars, null, priceCache, smileCache, "schwab", includeCurrentBar: false, ct, cache);
			if (read == null) continue;

			var row = new
			{
				date = date.ToString("yyyy-MM-dd"),
				time = t.ToString(@"hh\:mm"),
				spot,
				close = after[^1].Close,
				hiAfter = after.Max(b => b.High),
				loAfter = after.Min(b => b.Low),
				fwd30 = after.Count >= 30 ? after[29].Close : (decimal?)null,
				fwd60 = after.Count >= 60 ? after[59].Close : (decimal?)null,
				reading = read.Value.Reading,
			};
			rows.Add((date, t, JsonSerializer.Serialize(row, CascadeLog.Options)));
			produced++;
		}
		return produced > 0 ? null : "no checkpoint produced a reading";
	}
}

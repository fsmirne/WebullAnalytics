using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace WebullAnalytics.Analyze;

/// <summary>
/// `wa analyze squeeze-study <TICKER>` — research replay of the `analyze gex --view squeeze` panel over past sessions.
/// For every session in range and every checkpoint (default every 15 min, 10:00-15:30 ET) it rebuilds the panel from what
/// was knowable AT that minute and writes one CSV row with the reading plus what the underlying did next (and before), so
/// an offline analysis can test whether the score leads moves or only coincides with them.
///
/// Point-in-time inputs, per checkpoint t:
/// <list type="bullet">
/// <item><description>Spot = close of the minute bar labeled t−1 (data/intraday, START-of-bar labels: that bar ends at t).</description></item>
/// <item><description>Option books = the quote-store row labeled t−1, which is the NBBO at the t:00 boundary (ThetaData rows are relabeled
/// so label T = NBBO at T+1:00); IVs are back-solved from those mids by GexMatrix.Build.</description></item>
/// <item><description>OI = that session's data/oi snapshot (fixed intraday, published pre-open); ΔOI vs the prior snapshot.</description></item>
/// <item><description>Volume = quotes.db ohlcv minute bars labeled before t (START-of-bar labels), per contract for the flow factor and summed
/// for the volume factor's window and its same-window history.</description></item>
/// </list>
/// The store carries only the SAME-DAY expiry for daily-expiry roots, so the terrain is the 0DTE book alone — the live panel
/// with --dte 0, not its 14-day default — and the daily expected move comes from the 0DTE ATM IV (the live panel prefers the
/// next expiry, which the store lacks).
/// </summary>
internal sealed class AnalyzeSqueezeStudySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying root with daily 0DTE coverage in data/quotes.db, data/oi and data/intraday (e.g. SPXW).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--since <DATE>")]
	[Description("First session (YYYY-MM-DD). Default: 2022-01-03.")]
	public string Since { get; set; } = "2022-01-03";

	[CommandOption("--until <DATE>")]
	[Description("Last session (YYYY-MM-DD). Default: yesterday.")]
	public string? Until { get; set; }

	[CommandOption("--every <MIN>")]
	[DefaultValue(15)]
	[Description("Checkpoint spacing in minutes. Default: 15.")]
	public int Every { get; set; } = 15;

	[CommandOption("--first <HH:MM>")]
	[Description("First checkpoint, ET. Default: 10:00 (leaves 30 min of session for the backward window and the volume baseline).")]
	public string First { get; set; } = "10:00";

	[CommandOption("--last <HH:MM>")]
	[Description("Last checkpoint, ET. Default: 15:30 (keeps a full 30-min forward window).")]
	public string Last { get; set; } = "15:30";

	[CommandOption("--out <PATH>")]
	[Description("CSV output path (Windows path for wa.exe).")]
	public string Out { get; set; } = "";

	[CommandOption("--threads <N>")]
	[Description("Sessions processed in parallel. Default: half the logical cores.")]
	public int? Threads { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("<ticker> is required");
		if (string.IsNullOrWhiteSpace(Out)) return ValidationResult.Error("--out is required");
		if (!DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return ValidationResult.Error($"--since: expected YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return ValidationResult.Error($"--until: expected YYYY-MM-DD, got '{Until}'");
		if (Every < 1 || Every > 120) return ValidationResult.Error($"--every: must be in [1, 120], got {Every}");
		if (!ParsingHelpers.TryParseClockTime(First, out _)) return ValidationResult.Error($"--first: expected HH:MM, got '{First}'");
		if (!ParsingHelpers.TryParseClockTime(Last, out _)) return ValidationResult.Error($"--last: expected HH:MM, got '{Last}'");
		if (Threads is < 1) return ValidationResult.Error("--threads must be ≥ 1");
		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeSqueezeStudyCommand : AsyncCommand<AnalyzeSqueezeStudySettings>
{
	private const decimal StrikeRangeFraction = 0.20m;
	private const int MaxStrikes = 200;
	private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
	private static readonly int[] Horizons = [30, 60];

	private sealed record Bar(decimal Open, decimal High, decimal Low, decimal Close);

	protected override Task<int> ExecuteAsync(CommandContext context, AnalyzeSqueezeStudySettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		var since = DateTime.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var until = settings.Until != null ? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-1);
		ParsingHelpers.TryParseClockTime(settings.First, out var first);
		ParsingHelpers.TryParseClockTime(settings.Last, out var last);
		var checkpoints = new List<TimeSpan>();
		for (var t = first; t <= last; t += TimeSpan.FromMinutes(settings.Every)) checkpoints.Add(t);

		var sessions = new List<DateTime>();
		for (var d = since.Date; d <= until.Date; d = d.AddDays(1))
			if (MarketCalendar.IsOpen(d)) sessions.Add(d);

		// Per-session minute volume, shared across workers: each session's own tape fills it, and the same-window baseline
		// reads the 20 prior sessions from it (loading any not yet seen, e.g. the ones before --since).
		var minuteVolume = new ConcurrentDictionary<DateTime, IReadOnlyDictionary<TimeSpan, long>?>();
		IReadOnlyDictionary<TimeSpan, long>? MinuteVolume(DateTime d) => minuteVolume.GetOrAdd(d, day => AnalyzeGexCommand.StoreMinuteVolume(ticker, day, day));

		var rows = new ConcurrentBag<(DateTime Date, string Csv)>();
		var skipped = new ConcurrentDictionary<string, int>();
		var done = 0;
		var threads = settings.Threads ?? Math.Max(1, Environment.ProcessorCount / 2);
		AnsiConsole.MarkupLine($"[dim]{sessions.Count} session(s) {since:yyyy-MM-dd}..{until:yyyy-MM-dd}, {checkpoints.Count} checkpoint(s) each, {threads} thread(s).[/]");
		Parallel.ForEach(sessions, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellation }, date =>
		{
			var reason = StudySession(ticker, date, checkpoints, MinuteVolume, minuteVolume, rows);
			if (reason != null) skipped.AddOrUpdate(reason, 1, (_, n) => n + 1);
			var n = Interlocked.Increment(ref done);
			if (n % 50 == 0) AnsiConsole.MarkupLine($"[dim]{n}/{sessions.Count} sessions ({rows.Count} readings)[/]");
		});

		var path = Path.IsPathRooted(settings.Out) ? settings.Out : Path.GetFullPath(settings.Out);
		using (var w = new StreamWriter(path))
		{
			w.WriteLine(CsvHeader);
			foreach (var (_, csv) in rows.OrderBy(r => r.Date).ThenBy(r => r.Csv, StringComparer.Ordinal)) w.WriteLine(csv);
		}
		AnsiConsole.MarkupLine($"Wrote {rows.Count} reading(s) from {sessions.Count - skipped.Values.Sum()} session(s) to {Markup.Escape(path)}.");
		foreach (var (reason, n) in skipped.OrderByDescending(kv => kv.Value))
			AnsiConsole.MarkupLine($"[dim]  skipped {n} session(s): {Markup.Escape(reason)}[/]");
		return Task.FromResult(0);
	}

	private const string CsvHeader = "date,time,spot,side,score,complete,regime_pts,wall_pts,flow_pts,volume_pts,doi_pts,flow_share,volume_ratio,volume_basis,doi_share,trigger,short_gamma,wall,daily_move,"
		+ "past30,fwd30,fwd60,fwd_close,hi30,lo30,hi60,lo60,hi_close,lo_close";

	/// <summary>Replays one session; returns a skip reason, or null when it produced readings.</summary>
	private static string? StudySession(string ticker, DateTime date, IReadOnlyList<TimeSpan> checkpoints, Func<DateTime, IReadOnlyDictionary<TimeSpan, long>?> minuteVolumeOf,
		ConcurrentDictionary<DateTime, IReadOnlyDictionary<TimeSpan, long>?> minuteVolumeCache, ConcurrentBag<(DateTime, string)> rows)
	{
		var oiPath = Program.ResolvePath($"data/oi/{ticker}/{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(oiPath)) return "no data/oi snapshot";
		var snapshot = AnalyzeGexCommand.LoadOiSnapshot(oiPath);
		var slice = AnalyzeGexCommand.IntradayQuoteSlice.Open(ticker, date, date, snapshot.Quotes);
		if (slice == null) return "no same-day expiry in the quote store / snapshot";
		var bars = LoadBars(ticker, date);
		if (bars.Count == 0) return "no data/intraday tape";
		var tape = AnalyzeGexCommand.LoadStoreTape(ticker, date, date);
		if (tape.Count == 0) return "no ohlcv volume";
		var sessionMinutes = new SortedDictionary<TimeSpan, long>();
		foreach (var (minute, byStrike) in tape) sessionMinutes[minute] = (long)byStrike.Values.Sum(v => v.C + v.P);
		minuteVolumeCache.TryAdd(date, sessionMinutes);
		var (priorDate, priorOi) = AnalyzeGexCommand.LoadPriorOi(ticker, date);
		var close = MarketCalendar.IsEarlyClose(date) ? new TimeSpan(13, 0, 0) : AnalyzeGexSettings.RthClose;

		// Cumulative per-contract day volume, advanced minute by minute as the checkpoints walk forward.
		var cumulative = new Dictionary<(decimal Strike, bool IsCall), long>();
		using var minutes = tape.GetEnumerator();
		var hasMinute = minutes.MoveNext();
		var produced = 0;
		foreach (var t in checkpoints)
		{
			if (t + TimeSpan.FromMinutes(Horizons[0]) > close) break;
			while (hasMinute && minutes.Current.Key < t)
			{
				foreach (var (strike, v) in minutes.Current.Value)
				{
					cumulative[(strike, true)] = cumulative.GetValueOrDefault((strike, true)) + (long)v.C;
					cumulative[(strike, false)] = cumulative.GetValueOrDefault((strike, false)) + (long)v.P;
				}
				hasMinute = minutes.MoveNext();
			}
			if (PriceAt(bars, t) is not { } spot) continue;
			var asOf = date + t;
			var quotes = slice.At(asOf - OneMinute);
			if (quotes.Count == 0) continue;
			foreach (var sym in quotes.Keys.ToList())
			{
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p != null) quotes[sym] = quotes[sym] with { Volume = cumulative.GetValueOrDefault((p.Strike, p.CallPut == "C")) };
			}
			var matrix = GexMatrix.Build(quotes, ticker, spot, asOf, expiryFilter: date, StrikeRangeFraction, maxDteDays: 0, MaxStrikes);
			if (matrix.Expiries.Count == 0) continue;

			var from = t - TimeSpan.FromMinutes(15);
			var window = new VolumeWindow(from, t, sessionMinutes.Where(kv => kv.Key >= from && kv.Key < t).Sum(kv => kv.Value), sessionMinutes.Where(kv => kv.Key >= AnalyzeGexSettings.RthOpen && kv.Key < t).Sum(kv => kv.Value));
			var pace = window.SessionVolume > 0 ? GexSqueezeScreener.VolumePaceFrom(window, AnalyzeGexSettings.RthOpen, GexSqueezeScreener.SameWindowHistory(date, window, minuteVolumeOf)) : null;
			var inputs = new SqueezeInputs(GexSqueezeScreener.FlowShare(matrix.Contributors, spot), pace, priorOi != null ? GexSqueezeScreener.DeltaOiShare(matrix.Contributors, priorOi) : null, priorDate);
			var reading = GexSqueezeScreener.Evaluate(GexSqueezeScreener.TerrainFrom(matrix, spot, asOf), inputs);

			rows.Add((date, Row(date, t, spot, reading, inputs, bars, close)));
			produced++;
		}
		return produced > 0 ? null : "no checkpoint had a price and a two-sided book";
	}

	private static string Row(DateTime date, TimeSpan t, decimal spot, SqueezeReading r, SqueezeInputs inputs, SortedDictionary<TimeSpan, Bar> bars, TimeSpan close)
	{
		static string D(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
		static string P(SqueezeReading r, int i) => r.Factors[i].Points?.ToString(CultureInfo.InvariantCulture) ?? "";
		var sb = new StringBuilder();
		sb.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',').Append(t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).Append(',').Append(D(spot)).Append(',')
			.Append(r.Side == SqueezeSide.Bullish ? "bull" : "bear").Append(',').Append(r.Score).Append(',').Append(r.Complete ? 1 : 0).Append(',')
			.Append(P(r, 0)).Append(',').Append(P(r, 1)).Append(',').Append(P(r, 2)).Append(',').Append(P(r, 3)).Append(',').Append(P(r, 4)).Append(',')
			.Append(D(inputs.FlowShare)).Append(',').Append(D(inputs.Volume?.Ratio)).Append(',').Append(inputs.Volume?.Basis.ToString() ?? "").Append(',').Append(D(inputs.DeltaOiShare)).Append(',')
			.Append(D(r.Terrain.Trigger)).Append(',').Append(r.Terrain.ShortGamma ? 1 : 0).Append(',').Append(D(r.Wall)).Append(',').Append(D(r.Terrain.DailyMove)).Append(',')
			.Append(D(PriceAt(bars, t - TimeSpan.FromMinutes(30)) is { } past ? spot - past : null)).Append(',');
		foreach (var h in Horizons)
			sb.Append(D(PriceAt(bars, t + TimeSpan.FromMinutes(h)) is { } fwd ? fwd - spot : null)).Append(',');
		sb.Append(D(PriceAt(bars, close) is { } last ? last - spot : null));
		foreach (var end in Horizons.Select(h => t + TimeSpan.FromMinutes(h)).Append(close))
		{
			var span = bars.Where(kv => kv.Key >= t && kv.Key < end).Select(kv => kv.Value).ToList();
			sb.Append(',').Append(span.Count > 0 ? D(span.Max(b => b.High) - spot) : "").Append(',').Append(span.Count > 0 ? D(span.Min(b => b.Low) - spot) : "");
		}
		return sb.ToString();
	}

	/// <summary>The price at instant <paramref name="t"/>: the close of the bar ending at t (START-of-bar label t−1), falling back
	/// up to 5 minutes for a missing bar; at the open, where no RTH bar ends at t, the open of the bar starting at t. Null when none.</summary>
	private static decimal? PriceAt(SortedDictionary<TimeSpan, Bar> bars, TimeSpan t)
	{
		for (var k = 1; k <= 5; k++)
			if (bars.TryGetValue(t - TimeSpan.FromMinutes(k), out var bar)) return bar.Close;
		return bars.TryGetValue(t, out var opening) ? opening.Open : null;
	}

	/// <summary>RTH minute bars from data/intraday/&lt;TICKER&gt;/&lt;date&gt;.csv keyed by ET START-of-bar label (the on-disk convention).</summary>
	private static SortedDictionary<TimeSpan, Bar> LoadBars(string ticker, DateTime date)
	{
		var bars = new SortedDictionary<TimeSpan, Bar>();
		var path = Program.ResolvePath($"data/intraday/{ticker}/{date:yyyy-MM-dd}.csv");
		if (!File.Exists(path)) return bars;
		var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		foreach (var line in File.ReadLines(path).Skip(1))
		{
			var f = line.Split(',');
			if (f.Length < 5 || !DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) continue;
			decimal? N(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
			if (N(f[1]) is not { } o || N(f[2]) is not { } h || N(f[3]) is not { } l || N(f[4]) is not { } c) continue;
			var tod = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, ny).TimeOfDay;
			if (tod >= AnalyzeGexSettings.RthOpen && tod < AnalyzeGexSettings.RthClose) bars[tod] = new Bar(o, h, l, c);
		}
		return bars;
	}
}

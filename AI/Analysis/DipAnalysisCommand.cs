using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Analysis;

internal sealed class DipAnalysisSettings : CommandSettings
{
	[CommandArgument(0, "[ticker]")]
	[Description("Ticker whose intraday CSVs to analyze. Default: SPXW.")]
	public string Ticker { get; set; } = "SPXW";

	[CommandOption("--since <DATE>")]
	[Description("Start session date YYYY-MM-DD. Default: 2025-01-01.")]
	public string Since { get; set; } = "2025-01-01";

	[CommandOption("--until <DATE>")]
	[Description("End session date YYYY-MM-DD (inclusive). Default: today.")]
	public string? Until { get; set; }

	[CommandOption("--rsi-low <N>")]
	[Description("Oversold threshold: signal needs RSI(14) below this. Default: 30.")]
	public decimal RsiLow { get; set; } = 30m;

	[CommandOption("--bb-k <N>")]
	[Description("Bollinger band width in std-devs. Default: 2.")]
	public decimal BbK { get; set; } = 2m;

	[CommandOption("--interval <MIN>")]
	[Description("Bar size in minutes to aggregate the 1-min CSVs into (1, 5, 10, 15, 30...). Indicators and signal run on this timeframe. Default: 5.")]
	public int Interval { get; set; } = 5;

	[CommandOption("--list <N>")]
	[Description("Also print the first N individual signals. Default: 0.")]
	public int List { get; set; }

	[CommandOption("--dump <ETSTAMPS>")]
	[Description("Diagnostic: comma-separated ET timestamps (yyyy-MM-ddTHH:mm). Prints the full indicator stack at each matching 5-min bar ±5 bars (no signal logic). Use to reverse-engineer what a given trigger uses.")]
	public string? Dump { get; set; }

	[CommandOption("--call-dte <N>")]
	[Description("If >0, also simulate buying an ATM and a ~0.25-delta call (N trading-days to expiry) on each signal, BS-priced off the real move with per-day VIX1D vol. MODEL ESTIMATE — no real option quotes. Default: 0 (off).")]
	public int CallDte { get; set; }

	[CommandOption("--call-spread <PCT>")]
	[Description("Round-trip bid/ask spread as % of premium subtracted from each call trade. Default: 3.")]
	public decimal CallSpreadPct { get; set; } = 3m;

	[CommandOption("--put-spread")]
	[Description("Simulate SELLING a 0DTE bull put spread (ATM / 0.30Δ / 0.15Δ short strikes) on each signal, held to the cash settle. Entry credit from VIX1D; exit = intrinsic (no exit-IV needed).")]
	public bool PutSpread { get; set; }

	[CommandOption("--put-width <PTS>")]
	[Description("Put-spread width (short→long strike) in index points. Default: 25.")]
	public decimal PutWidth { get; set; } = 25m;

	[CommandOption("--put-friction <PTS>")]
	[Description("Per-spread friction (entry slippage) in index points subtracted from credit. Default: 0.50.")]
	public decimal PutFriction { get; set; } = 0.50m;

	[CommandOption("--put-stop <MULT>")]
	[Description("Stop-loss as a multiple of credit: cut the spread on the 5-min path when its buy-back mark reaches credit×(1+MULT). 0 = hold to settle. Default: 0.")]
	public decimal PutStop { get; set; }

	[CommandOption("--put-skew <PTS>")]
	[Description("Linear put skew: IV rises this many vol points per 1% the strike is below spot. 0 = flat (VIX1D for both legs). Try 3-6 for SPX. Default: 0.")]
	public decimal PutSkew { get; set; }

	[CommandOption("--first-only")]
	[Description("Keep only the FIRST signal of each session (collapses a multi-bar dip into one event). All stats and overlays then run on that subset. Default: off (every trigger).")]
	public bool FirstOnly { get; set; }

	[CommandOption("--exit-on-top")]
	[Description("Round-trip swing mode: ENTER on the first bar after a dip run clears, EXIT on the first bar after a top run clears (else session close). One at a time, intraday. Reports realized P&L vs an EOD-hold baseline.")]
	public bool ExitOnTop { get; set; }

	[CommandOption("--rsi-high <N>")]
	[Description("Overbought threshold for the top/exit signal: needs RSI(14) above this. Default: 70.")]
	public decimal RsiHigh { get; set; } = 70m;

	[CommandOption("--by-vix")]
	[Description("With --exit-on-top: bucket the round-trips by standard-VIX features known at the open (prior-close level terciles, open-gap magnitude terciles). Uses data/history/VIX.csv.")]
	public bool ByVix { get; set; }

	[CommandOption("--vix-gap-up")]
	[Description("With --exit-on-top: keep only entries on days where standard VIX gapped UP at the open (open > prior close). Filters the round-trip set to the VIX-gap-up regime. Uses data/history/VIX.csv.")]
	public bool VixGapUp { get; set; }

	[CommandOption("--real-chain")]
	[Description("With --exit-on-top: price each round-trip off the REAL scraped option chain (data/oi/<TICKER>/<date>.jsonl daily snapshot) on days a chain exists. 0DTE, strikes by --delta, bid/ask crossed both ways. Skips days with no chain. SPXW is cash-settled (cleaner for the naked-call lottery).")]
	public bool RealChain { get; set; }

	[CommandOption("--delta <D>")]
	[Description("Target |delta| for real-chain strike selection (long call / short put), BS-computed from each contract's IV. Default 0.25.")]
	public decimal Delta { get; set; } = 0.25m;

	[CommandOption("--width <W>")]
	[Description("Spread width in strike dollars for the call-vertical / put-credit protective leg (snapped to the nearest listed strike). Default 2.")]
	public decimal Width { get; set; } = 2m;

	[CommandOption("--legs <LIST>")]
	[Description("Comma-separated real-chain structures to price: naked-call, call-vert, put-credit. Default all three.")]
	public string Legs { get; set; } = "naked-call,call-vert,put-credit";
}

/// <summary>`ai dip` — historical study of the strict MACD+RSI+Bollinger "buy the dip" signal on 5-minute RTH
/// candles, aggregated from the captured 1-minute intraday CSVs. Reports forward returns at +30m / +60m /
/// session close. Read-only research; opens no positions.</summary>
internal sealed class DipAnalysisCommand : AsyncCommand<DipAnalysisSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan RthOpen = new(9, 30, 0);
	private static readonly TimeSpan RthClose = new(16, 0, 0);

	protected override async Task<int> ExecuteAsync(CommandContext context, DipAnalysisSettings settings, CancellationToken cancellation)
	{
		var nyNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, NyTz);
		var since = DateTime.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var until = string.IsNullOrWhiteSpace(settings.Until) ? nyNow.Date : DateTime.ParseExact(settings.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture);

		var dir = Program.ResolvePath($"data/intraday/{settings.Ticker.ToUpperInvariant()}");
		if (!Directory.Exists(dir)) { AnsiConsole.MarkupLine($"[red]No intraday data at {dir}[/]"); return 1; }

		var rthBars = LoadRthBars(dir, since, until, cancellation);

		// The data/intraday CSVs are materialized by batch `ai history` pulls, so a mid-session run would
		// otherwise analyze a missing or stale today. Route today through IntradayBarCache — same disk files,
		// live Webull top-up, today's file grows during the session — and splice its bars in. Offline runs
		// (no api-config, fetch failure) keep whatever the CSVs already had.
		if (until >= nyNow.Date && nyNow.TimeOfDay > RthOpen)
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (File.Exists(configPath) && JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath)) is { } apiConfig && apiConfig.Webull.Headers.Count > 0)
			{
				try
				{
					var cache = new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig));
					var openEt = nyNow.Date + RthOpen;
					var fromUtc = new DateTimeOffset(openEt, NyTz.GetUtcOffset(openEt)).ToUniversalTime();
					var todays = await cache.GetBarsAsync(settings.Ticker.ToUpperInvariant(), fromUtc, DateTimeOffset.UtcNow, BarInterval.M1, includeExtended: false, cancellation);
					var fetched = todays.Where(b => { var et = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz); return et.Date == nyNow.Date && et.TimeOfDay >= RthOpen && et.TimeOfDay < RthClose; }).ToList();
					if (fetched.Count > 0)
					{
						var replaced = rthBars.RemoveAll(b => TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date == nyNow.Date);
						rthBars.AddRange(fetched);
						rthBars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
						if (fetched.Count > replaced) AnsiConsole.MarkupLine($"[dim]Today topped up live via Webull ({fetched.Count} RTH bars, {fetched.Count - replaced} new).[/]");
					}
				}
				catch (Exception ex) { AnsiConsole.MarkupLine($"[dim]Live top-up for today failed ({Markup.Escape(ex.Message)}); using captured bars only.[/]"); }
			}
		}

		if (rthBars.Count == 0) { AnsiConsole.MarkupLine("[yellow]No RTH bars in range.[/]"); return 1; }

		var bars = DipSignalAnalyzer.AggregateToMinutes(rthBars, ts => TimeZoneInfo.ConvertTime(ts, NyTz).DateTime, settings.Interval);

		if (!string.IsNullOrWhiteSpace(settings.Dump)) { Dump(bars, settings.Dump); return 0; }

		var p = new DipParams(RsiLow: settings.RsiLow, RsiHigh: settings.RsiHigh, BbK: settings.BbK);

		if (settings.ExitOnTop)
		{
			var swings = DipSignalAnalyzer.SimulateSwing(bars, p);
			if (settings.VixGapUp) swings = FilterVixGapUp(swings);
			RenderSwing(settings, since, until, swings);
			if (settings.ByVix) RenderSwingByVix(swings);
			if (settings.RealChain) RenderRealChain(settings, swings);
			return 0;
		}

		var result = DipSignalAnalyzer.Analyze(bars, p, settings.Interval);

		// --first-only: collapse each session's run of triggers (a single dip often fires several
		// consecutive bars) to just the first. Filtering result.Signals flows through every renderer
		// and overlay. Grouped by the entry bar's session date; earliest entry per day wins.
		if (settings.FirstOnly)
			result = result with
			{
				Signals = result.Signals
					.GroupBy(s => s.EntryEt.Date)
					.Select(g => g.OrderBy(x => x.EntryEt).First())
					.OrderBy(s => s.EntryEt)
					.ToList()
			};

		Render(settings, since, until, result);
		if (settings.List > 0) RenderList(result, settings.List);
		if (settings.CallDte > 0) RenderCallOverlay(settings, result);
		if (settings.PutSpread) RenderPutOverlay(settings, bars, result);
		return 0;
	}

	private static List<MinuteBar> LoadRthBars(string dir, DateTime since, DateTime until, CancellationToken cancellation)
	{
		var bars = new List<MinuteBar>();
		foreach (var file in Directory.EnumerateFiles(dir, "*.csv").OrderBy(f => f))
		{
			cancellation.ThrowIfCancellationRequested();
			var name = Path.GetFileNameWithoutExtension(file);
			if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sessionDate)) continue;
			if (sessionDate < since || sessionDate > until) continue;

			foreach (var line in File.ReadLines(file))
			{
				if (line.Length == 0 || line[0] == 't') continue; // header / blank
				var c = line.Split(',');
				if (c.Length < 6) continue;
				if (!DateTimeOffset.TryParse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
				var et = TimeZoneInfo.ConvertTime(ts, NyTz).TimeOfDay;
				if (et < RthOpen || et >= RthClose) continue; // RTH only — SPXW pre/post is SPY-synthetic
				if (!decimal.TryParse(c[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)) continue;
				if (!decimal.TryParse(c[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) continue;
				if (!decimal.TryParse(c[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) continue;
				if (!decimal.TryParse(c[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var cl)) continue;
				long.TryParse(c[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var v);
				bars.Add(new MinuteBar(ts, o, h, l, cl, v));
			}
		}
		bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return bars;
	}

	private static void Render(DipAnalysisSettings s, DateTime since, DateTime until, DipAnalysisResult r)
	{
		AnsiConsole.MarkupLine($"[bold]{s.Ticker.ToUpperInvariant()} dip signal[/]  {since:yyyy-MM-dd} → {until:yyyy-MM-dd}  ({s.Interval}-min RTH, continuous indicators{(s.FirstOnly ? ", first signal/session" : "")})");
		AnsiConsole.MarkupLine($"Rule (source indicator's black/selling-climax): MACD(12,26,9) hist < 0 AND RSI(14) < {s.RsiLow} AND close < lower BB(20,{s.BbK}σ, EMA basis). Entry = next bar open.");
		var perSession = r.SessionCount > 0 ? (double)r.Signals.Count / r.SessionCount : 0;
		AnsiConsole.MarkupLine($"{r.BarCount:N0} bars · {r.SessionCount} sessions · [bold]{r.Signals.Count} signals[/] ({perSession:F2}/session)\n");

		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Forward returns (underlying, % move after entry)[/]");
		foreach (var col in new[] { "Horizon", "N", "Win %", "Avg %", "Median %", "Best %", "Worst %" }) table.AddColumn(col);
		AddRow(table, "+30 min", r.Signals.Select(x => x.Ret30));
		AddRow(table, "+60 min", r.Signals.Select(x => x.Ret60));
		AddRow(table, "to close", r.Signals.Select(x => (decimal?)x.RetEod));
		AnsiConsole.Write(table);

		var c = r.Counts;
		var ct = new Table().Border(TableBorder.Rounded).Title($"[bold]Condition co-occurrence[/] (of {c.Evaluable:N0} evaluable bars)");
		foreach (var col in new[] { "Condition", "Bars", "% of evaluable" }) ct.AddColumn(col);
		void CRow(string label, int n) => ct.AddRow(label, n.ToString("N0"), c.Evaluable > 0 ? $"{100.0 * n / c.Evaluable:F2}%" : "—");
		CRow("MACD hist < 0", c.HistNeg);
		CRow($"RSI < {s.RsiLow}", c.Rsi);
		CRow($"close < lower BB({s.BbK}σ, EMA)", c.Band);
		CRow("[bold]all three (dip trigger)[/]", c.All);
		AnsiConsole.Write(ct);

		var byMonth = r.Signals.GroupBy(x => new DateTime(x.EntryEt.Year, x.EntryEt.Month, 1)).OrderBy(g => g.Key).ToList();
		if (byMonth.Count > 1)
		{
			var mt = new Table().Border(TableBorder.Rounded).Title("[bold]By month (entry→close)[/]");
			foreach (var col in new[] { "Month", "Signals", "Win %", "Avg %" }) mt.AddColumn(col);
			foreach (var g in byMonth)
			{
				var rets = g.Select(x => (decimal?)x.RetEod).ToList();
				mt.AddRow(g.Key.ToString("yyyy-MM"), g.Count().ToString(), Pct(WinRate(rets)), Signed(Avg(rets)));
			}
			AnsiConsole.Write(mt);
		}
	}

	private static void RenderList(DipAnalysisResult r, int n)
	{
		var t = new Table().Border(TableBorder.Rounded).Title($"[bold]First {Math.Min(n, r.Signals.Count)} signals[/]");
		foreach (var col in new[] { "Entry (ET)", "Price", "RSI", "Close≤Band", "MACD hist", "+30m", "+60m", "Close" }) t.AddColumn(col);
		foreach (var sig in r.Signals.Take(n))
			t.AddRow(sig.EntryEt.ToString("yyyy-MM-dd HH:mm"), sig.EntryPrice.ToString("F2"), sig.Rsi.ToString("F1"),
				$"{sig.Close:F2}≤{sig.LowerBand:F2}", sig.MacdHist.ToString("F2"),
				PctN(sig.Ret30), PctN(sig.Ret60), Signed(sig.RetEod));
		AnsiConsole.Write(t);
	}

	private static void RenderCallOverlay(DipAnalysisSettings s, DipAnalysisResult r)
	{
		var vix1d = LoadVix1d();
		if (vix1d.Count == 0) { AnsiConsole.MarkupLine("[red]No VIX1D history for call pricing.[/]"); return; }

		DateTime NextNTradingDays(DateTime d)
		{
			for (var n = 0; n < s.CallDte; n++)
			{
				do { d = d.AddDays(1); } while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
			}
			return d;
		}
		decimal? Iv(DateTime date) => vix1d.TryGetValue(date.Date, out var v) ? v : null;

		var stats = DipOptionsOverlay.Run(r.Signals, Iv, NextNTradingDays, OptionMath.RiskFreeRate, s.CallSpreadPct / 100m);

		AnsiConsole.MarkupLine($"\n[bold]Long-call overlay[/] — {s.CallDte}DTE, VIX1D vol, {s.CallSpreadPct}% round-trip spread. [yellow]MODEL ESTIMATE (no real option quotes; constant IV over hold flatters long calls).[/]");
		var t = new Table().Border(TableBorder.Rounded).Title("[bold]Call return on premium[/]");
		foreach (var col in new[] { "Strike", "Horizon", "N", "Win %", "Avg net %", "Median net %", "Avg gross %", "Avg premium" }) t.AddColumn(col);
		foreach (var c in stats)
			t.AddRow(c.StrikeMode, c.Horizon, c.N.ToString(), c.N == 0 ? "—" : $"{c.WinRate * 100:F1}%",
				Signed(c.AvgNet), Signed(c.MedianNet), Signed(c.AvgGross), c.AvgPremium.ToString("F1"));
		AnsiConsole.Write(t);
	}

	private static void RenderPutOverlay(DipAnalysisSettings s, IReadOnlyList<IntradayBar> bars, DipAnalysisResult r)
	{
		var vix1d = LoadVix1d();
		if (vix1d.Count == 0) { AnsiConsole.MarkupLine("[red]No VIX1D history for put-spread pricing.[/]"); return; }
		decimal? Iv(DateTime date) => vix1d.TryGetValue(date.Date, out var v) ? v : null;

		var stats = DipOptionsOverlay.RunPutSpreads(bars, r.Signals, Iv, OptionMath.RiskFreeRate, s.PutWidth, s.PutFriction, s.PutStop, s.PutSkew);

		var stopDesc = s.PutStop > 0m ? $"stop at {s.PutStop}× credit" : "held to cash settle";
		var skewDesc = s.PutSkew > 0m ? $"put skew {s.PutSkew}pt/1%" : "flat IV";
		AnsiConsole.MarkupLine($"\n[bold]0DTE bull put spread overlay[/] — ${s.PutWidth} wide, {s.PutFriction}pt friction, VIX1D + {skewDesc}, {stopDesc}.");
		AnsiConsole.MarkupLine("[grey]Exit = intrinsic at the close (no exit-IV). Skew cuts both ways on the credit (richer short, but pricier long wing).[/]");
		var t = new Table().Border(TableBorder.Rounded).Title("[bold]Put-spread P&L (return on capital at risk)[/]");
		foreach (var col in new[] { "Short", "N", "Win %", "Stopped %", "Avg credit", "Avg maxloss", "Avg P&L $", "Avg R/risk", "Median R/risk" }) t.AddColumn(col);
		foreach (var c in stats)
			t.AddRow(c.ShortMode, c.N.ToString(), c.N == 0 ? "—" : $"{c.WinRate * 100:F1}%",
				s.PutStop > 0m ? $"{c.StoppedRate * 100:F1}%" : "—",
				c.AvgCredit.ToString("F1"), c.AvgMaxLoss.ToString("F1"), $"{c.AvgPnl:+0;-0}", Signed(c.AvgRoR), Signed(c.MedianRoR));
		AnsiConsole.Write(t);
	}

	private static Dictionary<DateTime, decimal> LoadVix1d()
	{
		var map = new Dictionary<DateTime, decimal>();
		var path = Program.ResolvePath("data/history/VIX1D.csv");
		if (!File.Exists(path)) return map;
		foreach (var line in File.ReadLines(path))
		{
			if (line.Length == 0 || line[0] == 'd') continue; // header
			var c = line.Split(',');
			if (c.Length < 5) continue;
			if (DateTime.TryParseExact(c[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
				&& decimal.TryParse(c[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
				map[d.Date] = close;
		}
		return map;
	}

	private static void Dump(IReadOnlyList<IntradayBar> bars, string stamps)
	{
		var closes = bars.Select(b => b.Close).ToArray();
		var rsi = SeriesIndicators.Rsi(closes, 14);
		var (line, signal, hist) = SeriesIndicators.Macd(closes, 12, 26, 9);
		var (lower, mid, upper) = SeriesIndicators.Bollinger(closes, 20, 2m, emaBasis: true);

		foreach (var raw in stamps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var want))
			{ AnsiConsole.MarkupLine($"[red]bad timestamp {raw}[/]"); continue; }
			var center = -1;
			for (var i = 0; i < bars.Count; i++) if (bars[i].EtStart == want) { center = i; break; }
			if (center < 0) { AnsiConsole.MarkupLine($"[yellow]no 5-min bar at {want:yyyy-MM-dd HH:mm} ET[/]"); continue; }

			var t = new Table().Border(TableBorder.Rounded).Title($"[bold]{want:yyyy-MM-dd HH:mm} ET[/] ±5 bars");
			foreach (var col in new[] { "ET", "close", "RSI", "MACD", "signal", "hist", "histΔ", "BB.low", "BB.mid", "BB.up", "%B", "flags" }) t.AddColumn(col);
			for (var i = Math.Max(1, center - 5); i <= Math.Min(bars.Count - 1, center + 5); i++)
			{
				var pctB = lower[i].HasValue && upper[i] > lower[i] ? (decimal?)((closes[i] - lower[i]!.Value) / (upper[i]!.Value - lower[i]!.Value)) : null;
				var histUp = hist[i].HasValue && hist[i - 1].HasValue ? (hist[i]!.Value > hist[i - 1]!.Value ? "↑" : "↓") : "";
				var flags = new List<string>();
				if (hist[i] is { } hv && hv < 0m) flags.Add("hist<0");
				if (rsi[i] is { } rv && rv < 30m) flags.Add("RSI<30");
				if (lower[i].HasValue && closes[i] < lower[i]!.Value) flags.Add("<BBlo");
				if (hist[i] is { } hh && hh < 0m && rsi[i] is { } rr && rr < 30m && lower[i].HasValue && closes[i] < lower[i]!.Value) flags.Add("[bold]DIP★[/]");
				var label = i == center ? $"[bold]{bars[i].EtStart:MM-dd HH:mm}[/]" : bars[i].EtStart.ToString("MM-dd HH:mm");
				t.AddRow(label, closes[i].ToString("F2"), F(rsi[i], 1), F(line[i], 2), F(signal[i], 2), F(hist[i], 2), histUp,
					F(lower[i], 2), F(mid[i], 2), F(upper[i], 2), pctB.HasValue ? $"{pctB.Value * 100m:F0}%" : "—", string.Join(" ", flags));
			}
			AnsiConsole.Write(t);
		}
	}

	private static string F(decimal? v, int dp) => v.HasValue ? v.Value.ToString("F" + dp) : "—";

	private static void AddRow(Table t, string label, IEnumerable<decimal?> returns)
	{
		var v = returns.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList();
		if (v.Count == 0) { t.AddRow(label, "0", "—", "—", "—", "—", "—"); return; }
		var win = (double)v.Count(x => x > 0m) / v.Count;
		t.AddRow(label, v.Count.ToString(), Pct(win), Signed(v.Average()), Signed(v[v.Count / 2]), Signed(v[^1]), Signed(v[0]));
	}

	private static void RenderSwing(DipAnalysisSettings s, DateTime since, DateTime until, List<SwingTrade> trades)
	{
		AnsiConsole.MarkupLine($"[bold]{s.Ticker.ToUpperInvariant()} dip→top swing[/]  {since:yyyy-MM-dd} → {until:yyyy-MM-dd}  ({s.Interval}-min RTH, continuous indicators)");
		AnsiConsole.MarkupLine($"Enter = first bar after a dip run clears (RSI<{s.RsiLow} & close<lowerBB & hist<0). Exit = first bar after a top run clears (RSI>{s.RsiHigh} & close>upperBB & hist>=0), else session close. One at a time, intraday, fills at bar open.");
		if (trades.Count == 0) { AnsiConsole.MarkupLine("[yellow]No round-trips in range.[/]"); return; }

		var rets = trades.Select(t => (decimal?)t.Ret).ToList();
		var baseRets = trades.Select(t => (decimal?)t.EodBaselineRet).ToList();
		var onTop = trades.Count(t => t.ExitedOnTop);
		var avgHoldMin = trades.Average(t => t.HoldBars) * s.Interval;

		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Swing round-trips (underlying)[/]");
		foreach (var col in new[] { "Strategy", "N", "Win %", "Avg %", "Median %", "Best %", "Worst %" }) table.AddColumn(col);
		table.AddRow("dip->top/EOD", trades.Count.ToString(), Pct(WinRate(rets)), Signed(Avg(rets)), Signed(Median(rets)), Signed(rets.Max(x => x!.Value)), Signed(rets.Min(x => x!.Value)));
		table.AddRow("[dim]EOD-hold baseline[/]", trades.Count.ToString(), Pct(WinRate(baseRets)), Signed(Avg(baseRets)), Signed(Median(baseRets)), Signed(baseRets.Max(x => x!.Value)), Signed(baseRets.Min(x => x!.Value)));
		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine($"[dim]Exited on top signal: {onTop}/{trades.Count} ({100.0 * onTop / trades.Count:F0}%); rest at session close. Avg hold {avgHoldMin:F0} min. Edge vs EOD-hold baseline: {Signed(Avg(rets) - Avg(baseRets))}/trade.[/]");

		if (s.List > 0)
		{
			var t = new Table().Border(TableBorder.Rounded).Title($"[bold]First {Math.Min(s.List, trades.Count)} round-trips[/]");
			foreach (var col in new[] { "Entry (ET)", "Exit (ET)", "Exit", "Hold", "Ret %", "EOD-base %" }) t.AddColumn(col);
			foreach (var tr in trades.Take(s.List))
				t.AddRow($"{tr.EntryEt:MM-dd HH:mm}", $"{tr.ExitEt:MM-dd HH:mm}", tr.ExitedOnTop ? "top" : "EOD", $"{tr.HoldBars * s.Interval}m", Signed(tr.Ret), Signed(tr.EodBaselineRet));
			AnsiConsole.Write(t);
		}
	}

	/// <summary>Keeps only round-trips whose entry day had standard VIX gap UP at the open (open > prior
	/// close). No VIX data → no filter (returns all). Today may be absent from VIX.csv (daily, EOD) → dropped.</summary>
	private static List<SwingTrade> FilterVixGapUp(List<SwingTrade> trades)
	{
		var vix = LoadVixOhlc("VIX.csv");
		if (vix.Count == 0) { AnsiConsole.MarkupLine("[yellow]No VIX.csv — --vix-gap-up filter skipped.[/]"); return trades; }
		var dates = vix.Keys.OrderBy(d => d).ToList();
		decimal? PriorClose(DateTime d)
		{
			decimal? r = null;
			foreach (var k in dates) { if (k >= d.Date) break; r = vix[k].Close; }
			return r;
		}
		return trades.Where(t =>
		{
			var pc = PriorClose(t.EntryEt.Date);
			return vix.TryGetValue(t.EntryEt.Date, out var v) && pc.HasValue && v.Open > pc.Value;
		}).ToList();
	}

	private static Dictionary<DateTime, (decimal Open, decimal Close)> LoadVixOhlc(string file)
	{
		var map = new Dictionary<DateTime, (decimal, decimal)>();
		var path = Program.ResolvePath($"data/history/{file}");
		if (!File.Exists(path)) return map;
		foreach (var line in File.ReadLines(path))
		{
			if (line.Length == 0 || line[0] == 'd') continue;
			var c = line.Split(',');
			if (c.Length < 5) continue;
			if (DateTime.TryParseExact(c[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
				&& decimal.TryParse(c[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)
				&& decimal.TryParse(c[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var cl))
				map[d.Date] = (o, cl);
		}
		return map;
	}

	/// <summary>Conditions the swing round-trips on VIX1D features KNOWN AT THE OPEN — prior-day close level
	/// (terciles) and open-gap sign — never the circular intraday VIX move. Keeps the EOD-hold baseline per
	/// bucket so a "good" VIX bin still has to beat just-hold-to-close, not merely look positive.</summary>
	private static void RenderSwingByVix(List<SwingTrade> trades)
	{
		// Standard VIX (VIX.csv): VIX1D's daily OPEN field is broken (prints ~7-10 regardless → fake gap-down
		// every day), so any open-gap conditioning MUST use standard VIX, whose opens are sane. Level (prior
		// close) is fine on either; we use standard VIX throughout for consistency with what the chart shows.
		var vix = LoadVixOhlc("VIX.csv");
		if (vix.Count == 0) { AnsiConsole.MarkupLine("[yellow]No VIX data to condition on.[/]"); return; }
		var dates = vix.Keys.OrderBy(d => d).ToList();

		decimal? PriorClose(DateTime d)
		{
			decimal? r = null;
			foreach (var k in dates) { if (k >= d.Date) break; r = vix[k].Close; }
			return r;
		}

		var feats = trades.Select(t =>
		{
			var d = t.EntryEt.Date;
			var pc = PriorClose(d);
			decimal? open = vix.TryGetValue(d, out var v) ? v.Open : null;
			return (Trade: t, PriorClose: pc, Gap: pc.HasValue && open.HasValue ? open - pc : null);
		}).Where(x => x.PriorClose.HasValue).ToList();
		if (feats.Count == 0) { AnsiConsole.MarkupLine("[yellow]No round-trips matched VIX1D dates.[/]"); return; }

		var sorted = feats.Select(f => f.PriorClose!.Value).OrderBy(x => x).ToList();
		decimal q1 = sorted[sorted.Count / 3], q2 = sorted[2 * sorted.Count / 3];
		string Band(decimal v) => v <= q1 ? "low" : v <= q2 ? "mid" : "high";

		var t1 = new Table().Border(TableBorder.Rounded).Title($"[bold]Swing by VIX prior-close level (terciles <={q1:F1} / <={q2:F1})[/]");
		foreach (var c in new[] { "VIX band", "N", "Swing win%", "Swing avg%", "EOD-base avg%", "edge/trade" }) t1.AddColumn(c);
		foreach (var g in feats.GroupBy(f => Band(f.PriorClose!.Value)).OrderBy(g => g.Key == "low" ? 0 : g.Key == "mid" ? 1 : 2))
		{
			var rs = g.Select(x => (decimal?)x.Trade.Ret).ToList();
			var bs = g.Select(x => (decimal?)x.Trade.EodBaselineRet).ToList();
			t1.AddRow(g.Key, g.Count().ToString(), Pct(WinRate(rs)), Signed(Avg(rs)), Signed(Avg(bs)), Signed(Avg(rs) - Avg(bs)));
		}
		AnsiConsole.Write(t1);

		// Gap MAGNITUDE terciles (open − prior close). Gaps are mostly positive, so the sign alone doesn't
		// separate days — the question is whether a BIG VIX gap-up at the open predicts a stronger dip→close move.
		var gapFeats = feats.Where(f => f.Gap.HasValue).ToList();
		if (gapFeats.Count >= 3)
		{
			var gs = gapFeats.Select(f => f.Gap!.Value).OrderBy(x => x).ToList();
			decimal g1 = gs[gs.Count / 3], g2 = gs[2 * gs.Count / 3];
			string GapBand(decimal v) => v <= g1 ? $"small/down (<={g1:+0.00;-0.00})" : v <= g2 ? "mid" : $"big up (>{g2:+0.00;-0.00})";
			var t2 = new Table().Border(TableBorder.Rounded).Title("[bold]Swing by VIX open-gap magnitude (open - prior close, terciles)[/]");
			foreach (var c in new[] { "VIX gap", "N", "Swing win%", "Swing avg%", "EOD-base avg%", "edge/trade" }) t2.AddColumn(c);
			foreach (var g in gapFeats.GroupBy(f => GapBand(f.Gap!.Value)).OrderBy(g => g.First().Gap!.Value))
			{
				var rs = g.Select(x => (decimal?)x.Trade.Ret).ToList();
				var bs = g.Select(x => (decimal?)x.Trade.EodBaselineRet).ToList();
				t2.AddRow(g.Key, g.Count().ToString(), Pct(WinRate(rs)), Signed(Avg(rs)), Signed(Avg(bs)), Signed(Avg(rs) - Avg(bs)));
			}
			AnsiConsole.Write(t2);
		}
	}

	private static decimal Median(IEnumerable<decimal?> r)
	{
		var v = r.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList();
		if (v.Count == 0) return 0m;
		return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2m;
	}

	// ---- Real-chain spread pricing (off the scraped data/oi/<TICKER>/<date>.jsonl daily snapshot) ----

	private sealed record ChainSnap(DateTime Et, decimal Spot, Dictionary<string, (decimal? Bid, decimal? Ask, decimal? Iv)> Q);

	private static readonly Regex OccRe = new(@"^([A-Z]+)(\d{6})([CP])(\d{8})$", RegexOptions.Compiled);

	/// <summary>Prices each swing round-trip off the real scraped chain when one exists for the entry day.
	/// 0DTE; long-call/short-put strikes picked by BS delta (from each contract's IV); spread protective leg
	/// snapped to the next listed strike past --width. Entry crosses (buy ask / sell bid), exit crosses back.
	/// Days without a chain are skipped. n is tiny until the daily scrape accumulates history.</summary>
	private static void RenderRealChain(DipAnalysisSettings s, List<SwingTrade> trades)
	{
		var legs = new HashSet<string>(s.Legs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()));
		var ticker = s.Ticker.ToUpperInvariant();
		var rows = new List<(DateTime Day, decimal Move, decimal? Naked, decimal? Vert, decimal? Put, string Note)>();
		int priced = 0, skipped = 0;

		static decimal? Px(ChainSnap snap, string sym, bool buy) => snap.Q.TryGetValue(sym, out var q) ? (buy ? q.Ask : q.Bid) : null;

		foreach (var t in trades)
		{
			var day = t.EntryEt.Date;
			var path = Program.ResolvePath($"data/oi/{ticker}/{day:yyyy-MM-dd}.jsonl");
			if (!File.Exists(path)) { skipped++; continue; }
			var (eS, xS) = ReadChainSnaps(path, t.EntryEt, t.ExitEt);
			if (eS == null || xS == null) { skipped++; continue; }

			var tYr = Math.Max(1.0 / 24 / 365, (new DateTime(day.Year, day.Month, day.Day, 16, 0, 0) - eS.Et).TotalHours / 24 / 365);
			var calls = new List<(decimal K, double D, string Sym)>();
			var puts = new List<(decimal K, double D, string Sym)>();
			foreach (var kv in eS.Q)
			{
				var occ = ParseOcc(kv.Key);
				if (occ is null || occ.Value.Exp.Date != day) continue;           // 0DTE only
				if (kv.Value.Iv is not decimal iv || iv <= 0m) continue;
				var dl = OptDelta(occ.Value.Cp, (double)eS.Spot, (double)occ.Value.Strike, tYr, (double)iv);
				if (dl is null) continue;
				if (occ.Value.Cp == 'C') calls.Add((occ.Value.Strike, dl.Value, kv.Key));
				else puts.Add((occ.Value.Strike, dl.Value, kv.Key));
			}
			if (calls.Count == 0) { skipped++; continue; }

			var move = (xS.Spot - eS.Spot) / eS.Spot;
			var lc = calls.OrderBy(c => Math.Abs(c.D - (double)s.Delta)).First();
			decimal? naked = null, vert = null, put = null;

			if (legs.Contains("naked-call"))
			{
				var inP = Px(eS, lc.Sym, true); var outP = Px(xS, lc.Sym, false);
				if (inP.HasValue && outP.HasValue) naked = (outP.Value - inP.Value) * 100m;
			}
			if (legs.Contains("call-vert"))
			{
				var sc = calls.Where(c => c.K > lc.K).OrderBy(c => Math.Abs(c.K - (lc.K + s.Width))).FirstOrDefault();
				if (sc.Sym != null)
				{
					var ein = Px(eS, lc.Sym, true) - Px(eS, sc.Sym, false);
					var exo = Px(xS, lc.Sym, false) - Px(xS, sc.Sym, true);
					if (ein.HasValue && exo.HasValue) vert = (exo.Value - ein.Value) * 100m;
				}
			}
			if (legs.Contains("put-credit") && puts.Count > 0)
			{
				var sp = puts.OrderBy(c => Math.Abs(Math.Abs(c.D) - (double)s.Delta)).First();
				var lp = puts.Where(c => c.K < sp.K).OrderBy(c => Math.Abs(c.K - (sp.K - s.Width))).FirstOrDefault();
				if (lp.Sym != null)
				{
					var cred = Px(eS, sp.Sym, false) - Px(eS, lp.Sym, true);
					var pay = Px(xS, sp.Sym, true) - Px(xS, lp.Sym, false);
					if (cred.HasValue && pay.HasValue) put = (cred.Value - pay.Value) * 100m;
				}
			}
			rows.Add((day, move, naked, vert, put, $"{eS.Et:HH:mm}->{xS.Et:HH:mm} {lc.K:F0}C Δ{lc.D:F2}"));
			priced++;
		}

		AnsiConsole.MarkupLine($"\n[bold]{ticker} real-chain P&L[/]  Δ{s.Delta} width ${s.Width} 0DTE — priced {priced}, skipped {skipped} (no chain)");
		if (priced == 0) { AnsiConsole.MarkupLine("[yellow]No scraped chains matched these round-trips. Run the scraper to accumulate days.[/]"); return; }
		var tbl = new Table().Border(TableBorder.Rounded);
		foreach (var c in new[] { "Day", "Move%", "Naked $", "Vert $", "PutCr $", "entry->exit" }) tbl.AddColumn(c);
		static string D0(decimal? v) => v.HasValue ? v.Value.ToString("+0;-0", CultureInfo.InvariantCulture) : "—";
		foreach (var r in rows.OrderBy(r => r.Day)) tbl.AddRow($"{r.Day:MM-dd}", Signed(r.Move), D0(r.Naked), D0(r.Vert), D0(r.Put), r.Note);
		AnsiConsole.Write(tbl);

		void Agg(string name, IEnumerable<decimal?> v)
		{
			var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList();
			if (l.Count == 0) return;
			AnsiConsole.MarkupLine($"[dim]{name}: N={l.Count}  total ${l.Sum():+0;-0}  avg ${l.Average():+0;-0}  win {100.0 * l.Count(x => x > 0m) / l.Count:F0}%[/]");
		}
		Agg("naked-call", rows.Select(r => r.Naked));
		Agg("call-vert", rows.Select(r => r.Vert));
		Agg("put-credit", rows.Select(r => r.Put));
	}

	private static (ChainSnap? Entry, ChainSnap? Exit) ReadChainSnaps(string path, DateTime entryEt, DateTime exitEt)
	{
		string? bestE = null, bestX = null; long dE = long.MaxValue, dX = long.MaxValue;
		foreach (var line in File.ReadLines(path))
		{
			var i = line.IndexOf("\"tsEt\":\"", StringComparison.Ordinal);
			if (i < 0 || i + 8 + 19 > line.Length) continue;
			if (!DateTime.TryParseExact(line.Substring(i + 8, 19), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
			var de = Math.Abs((t - entryEt).Ticks); if (de < dE) { dE = de; bestE = line; }
			var dx = Math.Abs((t - exitEt).Ticks); if (dx < dX) { dX = dx; bestX = line; }
		}
		return (bestE != null ? ParseSnap(bestE) : null, bestX != null ? ParseSnap(bestX) : null);
	}

	private static ChainSnap ParseSnap(string line)
	{
		using var doc = JsonDocument.Parse(line);
		var root = doc.RootElement;
		var et = DateTime.ParseExact(root.GetProperty("tsEt").GetString()![..19], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
		var spot = root.GetProperty("underlyingPrice").GetDecimal();
		var q = new Dictionary<string, (decimal?, decimal?, decimal?)>(StringComparer.Ordinal);
		foreach (var o in root.GetProperty("options").EnumerateArray())
		{
			static decimal? Num(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
			q[o.GetProperty("symbol").GetString()!] = (Num(o, "bid"), Num(o, "ask"), Num(o, "iv"));
		}
		return new ChainSnap(et, spot, q);
	}

	private static (string Root, DateTime Exp, char Cp, decimal Strike)? ParseOcc(string s)
	{
		var m = OccRe.Match(s);
		if (!m.Success || !DateTime.TryParseExact(m.Groups[2].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp)) return null;
		return (m.Groups[1].Value, exp, m.Groups[3].Value[0], int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / 1000m);
	}

	private static double NormCdf(double x)
	{
		var t = 1 / (1 + 0.2316419 * Math.Abs(x));
		var d = 0.3989423 * Math.Exp(-x * x / 2);
		var p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
		return x > 0 ? 1 - p : p;
	}

	private static double? OptDelta(char cp, double s, double k, double tYr, double iv)
	{
		if (iv <= 0 || tYr <= 0 || s <= 0 || k <= 0) return null;
		var d1 = (Math.Log(s / k) + (0.036 + iv * iv / 2) * tYr) / (iv * Math.Sqrt(tYr));
		var cd = NormCdf(d1);
		return cp == 'C' ? cd : cd - 1;
	}

	private static double WinRate(IEnumerable<decimal?> r) { var v = r.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? 0 : (double)v.Count(x => x > 0m) / v.Count; }
	private static decimal Avg(IEnumerable<decimal?> r) { var v = r.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? 0m : v.Average(); }
	private static string Pct(double f) => $"{f * 100:F1}%";
	private static string Signed(decimal f) => $"{f * 100m:+0.00;-0.00}%";
	private static string PctN(decimal? f) => f.HasValue ? Signed(f.Value) : "—";
}

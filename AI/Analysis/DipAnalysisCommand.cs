using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
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
}

/// <summary>`ai dip` — historical study of the strict MACD+RSI+Bollinger "buy the dip" signal on 5-minute RTH
/// candles, aggregated from the captured 1-minute intraday CSVs. Reports forward returns at +30m / +60m /
/// session close. Read-only research; opens no positions.</summary>
internal sealed class DipAnalysisCommand : AsyncCommand<DipAnalysisSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan RthOpen = new(9, 30, 0);
	private static readonly TimeSpan RthClose = new(16, 0, 0);

	public override Task<int> ExecuteAsync(CommandContext context, DipAnalysisSettings settings, CancellationToken cancellation)
	{
		var since = DateTime.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var until = string.IsNullOrWhiteSpace(settings.Until) ? DateTime.UtcNow.Date : DateTime.ParseExact(settings.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture);

		var dir = Program.ResolvePath($"data/intraday/{settings.Ticker.ToUpperInvariant()}");
		if (!Directory.Exists(dir)) { AnsiConsole.MarkupLine($"[red]No intraday data at {dir}[/]"); return Task.FromResult(1); }

		var rthBars = LoadRthBars(dir, since, until, cancellation);
		if (rthBars.Count == 0) { AnsiConsole.MarkupLine("[yellow]No RTH bars in range.[/]"); return Task.FromResult(1); }

		var bars = DipSignalAnalyzer.AggregateToMinutes(rthBars, ts => TimeZoneInfo.ConvertTime(ts, NyTz).DateTime, settings.Interval);

		if (!string.IsNullOrWhiteSpace(settings.Dump)) { Dump(bars, settings.Dump); return Task.FromResult(0); }

		var p = new DipParams(RsiLow: settings.RsiLow, BbK: settings.BbK);
		var result = DipSignalAnalyzer.Analyze(bars, p, settings.Interval);

		Render(settings, since, until, result);
		if (settings.List > 0) RenderList(result, settings.List);
		if (settings.CallDte > 0) RenderCallOverlay(settings, result);
		if (settings.PutSpread) RenderPutOverlay(settings, bars, result);
		return Task.FromResult(0);
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
		AnsiConsole.MarkupLine($"[bold]{s.Ticker.ToUpperInvariant()} dip signal[/]  {since:yyyy-MM-dd} → {until:yyyy-MM-dd}  ({s.Interval}-min RTH, continuous indicators)");
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

	private static double WinRate(IEnumerable<decimal?> r) { var v = r.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? 0 : (double)v.Count(x => x > 0m) / v.Count; }
	private static decimal Avg(IEnumerable<decimal?> r) { var v = r.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? 0m : v.Average(); }
	private static string Pct(double f) => $"{f * 100:F1}%";
	private static string Signed(decimal f) => $"{f * 100m:+0.00;-0.00}%";
	private static string PctN(decimal? f) => f.HasValue ? Signed(f.Value) : "—";
}

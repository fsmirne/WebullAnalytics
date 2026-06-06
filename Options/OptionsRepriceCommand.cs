using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.Options;

/// <summary>`wa options reprice <ticker> --date <yyyy-MM-dd>` — validates the backtest's synthetic
/// pricing model against a real captured chain snapshot (<c>data/chain-snapshots/<TICKER>/<date>.jsonl</c>,
/// written by the scraper with real bid/ask/iv per contract per minute). For every (contract, minute) it
/// reprices the contract through the SAME <see cref="BacktestQuoteSource"/> the backtest uses — fed the
/// snapshot's own underlying price so the comparison isolates the pricing model from spot error — then
/// reports model-vs-truth error bucketed by pricing source (real bar / surface-IV / cross-expiry /
/// vix-smile / intrinsic) and moneyness. This is how we measure "can we recreate the quotes from the data
/// we have," and it pinpoints where the model diverges (e.g. ITM legs collapsing instead of pricing at
/// intrinsic).</summary>
internal sealed class OptionsRepriceSettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[System.ComponentModel.Description("Ticker root with a captured snapshot (e.g. SPXW).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--date <DATE>")]
	[System.ComponentModel.Description("Snapshot date YYYY-MM-DD (must have data/chain-snapshots/<TICKER>/<date>.jsonl).")]
	public string? Date { get; set; }

	[CommandOption("--max-snapshots <N>")]
	[System.ComponentModel.Description("Process at most N evenly-spaced snapshot minutes (0 = all). Default 0.")]
	public int MaxSnapshots { get; set; }

	[CommandOption("--worst <N>")]
	[System.ComponentModel.Description("Show the N worst-mispriced contracts (by absolute mid error). Default 15.")]
	public int Worst { get; set; } = 15;

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (Date == null || !DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error("--date YYYY-MM-DD is required");
		return ValidationResult.Success();
	}
}

internal sealed class OptionsRepriceCommand : AsyncCommand<OptionsRepriceSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private sealed record Cmp(string Symbol, string Source, decimal Moneyness, int Dte, decimal SnapMid, decimal ModelMid, decimal? SnapIv, decimal? ModelIv);

	public override async Task<int> ExecuteAsync(CommandContext context, OptionsRepriceSettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		var date = DateTime.ParseExact(settings.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var path = Program.ResolvePath(Path.Combine("data", "chain-snapshots", ticker, $"{settings.Date}.jsonl"));
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"Error: no snapshot at {path}");
			return 1;
		}

		// Same pricing stack the backtest uses.
		var bars = new HistoricalBarCache();
		var smile = new SmileIndexCache(offline: true);
		var ivProvider = new BacktestIVProvider(bars, smile: smile);
		var optionBars = new HistoricalOptionBarCache();
		var dividendsByRoot = await new HistoricalDividendCache().BuildScheduleMapAsync(new[] { ticker }, cancellation);
		var quotes = new BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036, optionBars: optionBars, dividendsByRoot: dividendsByRoot);
		var tickerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker };
		var closeUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.Date.AddHours(16), NyTz), TimeSpan.Zero);

		var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
		var selected = settings.MaxSnapshots > 0 && settings.MaxSnapshots < lines.Count
			? Enumerable.Range(0, settings.MaxSnapshots).Select(i => lines[(int)((long)i * (lines.Count - 1) / Math.Max(1, settings.MaxSnapshots - 1))]).Distinct().ToList()
			: lines;

		AnsiConsole.MarkupLine($"[bold]Reprice[/] {ticker} {settings.Date}: {selected.Count}/{lines.Count} snapshot minutes vs the backtest pricing model");

		var cmps = new List<Cmp>();
		var noModel = 0;
		foreach (var line in selected)
		{
			cancellation.ThrowIfCancellationRequested();
			using var doc = JsonDocument.Parse(line);
			var root = doc.RootElement;
			if (!root.TryGetProperty("tsEt", out var tsEl) || !DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var tsEt)) continue;
			if (!root.TryGetProperty("underlyingPrice", out var upEl)) continue;
			var spot = upEl.GetDecimal();
			if (spot <= 0m || !root.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array) continue;

			var minuteEt = DateTime.SpecifyKind(tsEt, DateTimeKind.Unspecified);
			var minuteUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(minuteEt, NyTz), TimeSpan.Zero);
			var minutesToClose = Math.Max(1.0, (closeUtc - minuteUtc).TotalMinutes);
			var overrides = new QuoteOverrides(Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spot }, ZeroDteTimeYears: minutesToClose / 60.0 / 24.0 / 365.0);

			var snapByeSym = new Dictionary<string, (decimal mid, decimal? iv)>(StringComparer.OrdinalIgnoreCase);
			var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var o in opts.EnumerateArray())
			{
				var sym = o.GetProperty("symbol").GetString();
				if (string.IsNullOrWhiteSpace(sym)) continue;
				var bid = o.TryGetProperty("bid", out var b) ? b.GetDecimal() : 0m;
				var ask = o.TryGetProperty("ask", out var a) ? a.GetDecimal() : 0m;
				if (ask <= 0m) continue; // no real quote to compare against
				decimal? iv = o.TryGetProperty("iv", out var ivel) && ivel.ValueKind == JsonValueKind.Number ? ivel.GetDecimal() : null;
				snapByeSym[sym] = ((bid + ask) / 2m, iv);
				symbols.Add(sym);
			}
			if (symbols.Count == 0) continue;

			var model = await quotes.GetQuotesAsync(minuteEt, symbols, tickerSet, cancellation, overrides);
			foreach (var (sym, snap) in snapByeSym)
			{
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p?.CallPut == null) continue;
				if (!model.Options.TryGetValue(sym, out var mq) || mq.Bid is not { } mb || mq.Ask is not { } ma) { noModel++; continue; }
				var modelMid = (mb + ma) / 2m;
				var moneyness = p.CallPut == "C" ? (spot - p.Strike) / spot : (p.Strike - spot) / spot; // +ITM / -OTM
				var dte = (p.ExpiryDate.Date - date.Date).Days;
				var src = quotes.GetSyntheticSource(sym, date.Date)?.ToString() ?? "real-bar";
				cmps.Add(new Cmp(sym, src, moneyness, dte, snap.mid, modelMid, snap.iv, mq.ImpliedVolatility));
			}
		}

		if (cmps.Count == 0) { Console.Error.WriteLine("No comparable contracts (snapshot had no quotes, or the model priced nothing — check option-bar coverage for this date)."); return 1; }

		RenderBySource(cmps, noModel);
		RenderByMoneyness(cmps);
		RenderWorst(cmps, settings.Worst);
		return 0;
	}

	private static string MoneynessBucket(decimal m) => m switch
	{
		> 0.10m => "deep-ITM",
		> 0.01m => "ITM",
		>= -0.01m => "ATM",
		>= -0.10m => "OTM",
		_ => "deep-OTM",
	};

	// Median absolute mid error in $, median |mid error| as % of snapshot mid (priced >$0.10 only, so penny
	// contracts don't dominate the %), and median |iv error|.
	private static (decimal mae, decimal mapePct, decimal ivMae, int n) Stats(IReadOnlyList<Cmp> g)
	{
		decimal Median(IEnumerable<decimal> xs) { var s = xs.OrderBy(x => x).ToList(); return s.Count == 0 ? 0m : s[s.Count / 2]; }
		var mae = Median(g.Select(c => Math.Abs(c.ModelMid - c.SnapMid)));
		var mape = Median(g.Where(c => c.SnapMid > 0.10m).Select(c => Math.Abs(c.ModelMid - c.SnapMid) / c.SnapMid * 100m));
		var ivMae = Median(g.Where(c => c.SnapIv.HasValue && c.ModelIv.HasValue).Select(c => Math.Abs(c.ModelIv!.Value - c.SnapIv!.Value)));
		return (mae, mape, ivMae, g.Count);
	}

	private static void RenderBySource(IReadOnlyList<Cmp> cmps, int noModel)
	{
		var t = new Table().Border(TableBorder.Rounded).Title("[bold]Mid error by pricing source[/]");
		t.AddColumn("Source"); t.AddColumn(new TableColumn("N").RightAligned()); t.AddColumn(new TableColumn("median |$err|").RightAligned()); t.AddColumn(new TableColumn("median |%err|").RightAligned()); t.AddColumn(new TableColumn("median |iv err|").RightAligned());
		foreach (var grp in cmps.GroupBy(c => c.Source).OrderByDescending(g => g.Count()))
		{
			var (mae, mape, ivMae, n) = Stats(grp.ToList());
			t.AddRow(grp.Key, n.ToString(), $"${mae:F2}", $"{mape:F1}%", $"{ivMae:F3}");
		}
		AnsiConsole.Write(t);
		if (noModel > 0) AnsiConsole.MarkupLine($"[dim]({noModel} snapshot contracts the model produced no bid/ask for — excluded.)[/]");
	}

	private static void RenderByMoneyness(IReadOnlyList<Cmp> cmps)
	{
		var order = new[] { "deep-ITM", "ITM", "ATM", "OTM", "deep-OTM" };
		var t = new Table().Border(TableBorder.Rounded).Title("[bold]Mid error by moneyness[/]");
		t.AddColumn("Moneyness"); t.AddColumn(new TableColumn("N").RightAligned()); t.AddColumn(new TableColumn("median |$err|").RightAligned()); t.AddColumn(new TableColumn("median |%err|").RightAligned()); t.AddColumn(new TableColumn("median |iv err|").RightAligned());
		foreach (var bucket in order)
		{
			var g = cmps.Where(c => MoneynessBucket(c.Moneyness) == bucket).ToList();
			if (g.Count == 0) continue;
			var (mae, mape, ivMae, n) = Stats(g);
			var color = mape > 25m ? "red" : mape > 10m ? "yellow" : "green";
			t.AddRow(bucket, n.ToString(), $"${mae:F2}", $"[{color}]{mape:F1}%[/]", $"{ivMae:F3}");
		}
		AnsiConsole.Write(t);
	}

	private static void RenderWorst(IReadOnlyList<Cmp> cmps, int worst)
	{
		var t = new Table().Border(TableBorder.Rounded).Title($"[bold]{worst} worst-mispriced (by |$err|)[/]");
		t.AddColumn("Symbol"); t.AddColumn("src"); t.AddColumn(new TableColumn("dte").RightAligned()); t.AddColumn(new TableColumn("mny").RightAligned()); t.AddColumn(new TableColumn("snap mid").RightAligned()); t.AddColumn(new TableColumn("model mid").RightAligned()); t.AddColumn(new TableColumn("snap iv").RightAligned()); t.AddColumn(new TableColumn("model iv").RightAligned());
		foreach (var c in cmps.OrderByDescending(c => Math.Abs(c.ModelMid - c.SnapMid)).Take(worst))
			t.AddRow(c.Symbol, c.Source, c.Dte.ToString(), $"{c.Moneyness * 100m:F1}%", $"${c.SnapMid:F2}", $"${c.ModelMid:F2}", c.SnapIv?.ToString("F3") ?? "—", c.ModelIv?.ToString("F3") ?? "—");
		AnsiConsole.Write(t);
	}
}

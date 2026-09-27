using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI.Open.ZeroDte;

internal sealed class AIGateSettings : AISingleTickerSubcommandSettings
{
	[CommandOption("--date <DATE>")]
	[Description("Session to replay, YYYY-MM-DD. Default: today.")]
	public string? Date { get; set; }

	[CommandOption("--since <DATE>")]
	[Description("Replay a RANGE instead of one session: start date YYYY-MM-DD. With --until this prints one row per day (the day's outcome) plus a tally of why days were skipped, which is how you see the gate's day-filter rate over history rather than its minute-by-minute reasoning.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[Description("Range end date YYYY-MM-DD. Defaults to today when --since is given.")]
	public string? Until { get; set; }

	[CommandOption("--every <MINUTES>")]
	[Description("Single-session mode: print a row every N minutes instead of every minute. Default: 15.")]
	public int Every { get; set; } = 15;

	[CommandOption("--all")]
	[Description("Single-session mode: print every minute, ignoring --every.")]
	public bool All { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		foreach (var (name, value) in new[] { ("--date", Date), ("--since", Since), ("--until", Until) })
			if (value != null && !DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
				return ValidationResult.Error($"{name}: must be YYYY-MM-DD, got '{value}'");
		if (Date != null && Since != null) return ValidationResult.Error("--date and --since are mutually exclusive: one session or a range, not both");
		if (Every < 1 || Every > 390) return ValidationResult.Error($"--every: must be in [1, 390], got {Every}");
		return ValidationResult.Success();
	}
}

/// <summary>`wa ai gate` — replays the 0DTE session gate against the stored intraday tape and prints what it
/// decided, minute by minute for one session or day by day over a range.
///
/// <para>This exists because a gate whose job is to sometimes refuse to trade is otherwise unfalsifiable from
/// the outside: a backtest that opens on 40% of days tells you the rate but not the reason, and "no proposals
/// emitted" in the watch log looks identical whether the session was rejected on the gap, on the VWAP hold,
/// on chop, or on a missing tape file. Tuning any threshold without this is guesswork.</para>
///
/// <para>Entirely offline — it reads <c>data/intraday/&lt;TICKER&gt;/</c> and the daily close cache, never the
/// option chain, so it needs no market session, no credentials, and no quote store. The consequence is that
/// it evaluates the SESSION half of the gate (state machine + protective range levels) and not the
/// per-candidate half (GEX-wall placement and the credit floor), which need a priced book.</para></summary>
internal sealed class AIGateCommand : AsyncCommand<AIGateSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan RthStart = new(9, 30, 0);
	private static readonly TimeSpan RthEnd = new(16, 0, 0);

	protected override async Task<int> ExecuteAsync(CommandContext context, AIGateSettings settings, CancellationToken cancellation)
		=> await AITextOutput.RunAsync(settings, "AIGate", async () =>
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;
		TerminalHelper.EnsureTerminalWidthFromConfig();

		var cfg = config.Opener.ZeroDteGate;
		if (!cfg.Enabled)
			AnsiConsole.MarkupLine("[yellow]Note: opener.zeroDteGate.enabled is false in this config, so the live opener ignores the gate. Replaying its settings anyway.[/]");

		// Disk-only bar source: the no-op fetcher makes a missing date read as "no tape" rather than
		// reaching for Webull's 5-day intraday window, so a historical replay is deterministic.
		var bars = new IntradayBarCache((_, _, _, _, _) => Task.FromResult<IReadOnlyList<MinuteBar>>(Array.Empty<MinuteBar>()));
		var closes = new HistoricalPriceCache();

		AnsiConsole.MarkupLine($"[bold]0DTE session gate[/] {config.Ticker}  mode={cfg.Direction.Mode}  window={cfg.EarliestEntryEt ?? "open"}-{cfg.LatestEntryEt ?? "close"} ET  hold={cfg.Direction.MinVwapHoldMinutes}m  maxFlips={cfg.Direction.MaxVwapFlips}  range={cfg.Range.WindowMinutes}m/{cfg.Range.MaxRangePct}%");

		if (settings.Since != null)
			return await RenderRangeAsync(config, cfg, bars, closes, settings, cancellation);
		return await RenderSessionAsync(config, cfg, bars, closes, settings, cancellation);
	});

	/// <summary>One session, minute by minute: the tape facts alongside the state they produce.</summary>
	private async Task<int> RenderSessionAsync(AIConfig config, ZeroDteGateConfig cfg, IntradayBarCache bars, HistoricalPriceCache closes, AIGateSettings settings, CancellationToken cancellation)
	{
		var date = settings.Date != null ? DateTime.ParseExact(settings.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today;
		var sessionBars = await LoadSessionAsync(bars, config.Ticker, date, cancellation);
		if (sessionBars.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]No RTH tape for {config.Ticker} on {date:yyyy-MM-dd}[/] — data/intraday/{config.Ticker}/{date:yyyy-MM-dd}.csv is missing or holds no regular-session bars. The gate fails closed on such a day, so it would trade nothing.");
			return 1;
		}
		var prevClose = await PrevCloseAsync(closes, config.Ticker, date, cancellation);

		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("ET");
		table.AddColumn(new TableColumn("Spot").RightAligned());
		table.AddColumn(new TableColumn("VWAP").RightAligned());
		table.AddColumn(new TableColumn("Side").Centered());
		table.AddColumn(new TableColumn("Held").RightAligned());
		table.AddColumn(new TableColumn("Flips").RightAligned());
		table.AddColumn(new TableColumn("Win rng%").RightAligned());
		table.AddColumn(new TableColumn("Tests").Centered());
		table.AddColumn("State");
		table.AddColumn("Why");

		ZeroDteGateVerdict? firstEntry = null;
		DateTime? firstEntryEt = null;
		var renderedCutoff = false;
		var stateCounts = new Dictionary<ZeroDteGateState, int>();

		for (var i = 0; i < sessionBars.Count; i++)
		{
			// Bars [0..i] are the tape "as of" bar i's minute — the same causal slice the backtest's
			// per-minute opener sees, so a row here is reproducible by the opener at that minute.
			var slice = sessionBars.Take(i + 1).ToList();
			var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(sessionBars[i].Timestamp, NyTz).DateTime, DateTimeKind.Unspecified);
			var verdict = ZeroDteSessionGate.Evaluate(cfg, slice, prevClose, compositeBias: null, minuteEt);
			stateCounts[verdict.State] = stateCounts.GetValueOrDefault(verdict.State) + 1;
			if (firstEntry == null && verdict.AllowsEntry) { firstEntry = verdict; firstEntryEt = minuteEt; }

			// Stop rendering once the day is closed for entries: every later minute repeats the same
			// PastCutoff row and buries the minutes that actually mattered.
			if (verdict.DayClosed && renderedCutoff) continue;
			if (verdict.DayClosed) renderedCutoff = true;
			var show = verdict.DayClosed || settings.All || i % settings.Every == 0 || firstEntryEt == minuteEt;
			if (!show) continue;

			var t = verdict.Tape;
			table.AddRow(
				minuteEt.ToString("HH:mm"),
				t?.Spot.ToString("N2") ?? "—",
				t?.Vwap.ToString("N2") ?? "—",
				t == null ? "—" : t.Side switch { VwapSide.Above => "[green]▲[/]", VwapSide.Below => "[red]▼[/]", _ => "=" },
				t?.MinutesHeldOnSide.ToString() ?? "—",
				t?.VwapFlips.ToString() ?? "—",
				t?.WindowRangePct.ToString("N2") ?? "—",
				t == null ? "—" : $"{t.WindowLowTouches}/{t.WindowHighTouches}",
				StateMarkup(verdict.State),
				Markup.Escape(Truncate(verdict.Summary, 62)));
		}

		AnsiConsole.Write(table);
		var gapTape = ZeroDteSessionGate.Evaluate(cfg, sessionBars, prevClose, null, DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(sessionBars[^1].Timestamp, NyTz).DateTime, DateTimeKind.Unspecified)).Tape;
		AnsiConsole.MarkupLine($"[dim]prev RTH close {(prevClose?.ToString("N2") ?? "unavailable")}, session open {gapTape?.SessionOpen:N2}, gap {gapTape?.GapPct:+0.00;-0.00}% → {gapTape?.Gap}[/]");
		if (firstEntry != null)
			AnsiConsole.MarkupLine($"[green]First entry-eligible minute: {firstEntryEt:HH:mm} ET — {Markup.Escape(firstEntry.Summary)}[/]");
		else
			AnsiConsole.MarkupLine($"[yellow]NO TRADE this session — the gate never confirmed. Minutes by state: {string.Join(", ", stateCounts.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}"))}[/]");
		AnsiConsole.MarkupLine("[dim]Held = consecutive minutes on the current side of the RUNNING VWAP (resets on a cross). Flips = session VWAP side changes. Win rng% = trailing-window high-low as % of spot. Tests = low/high boundary touches in that window. The GEX-wall placement and credit-floor halves of the gate need a priced chain and are not evaluated here.[/]");
		return 0;
	}

	/// <summary>A date range, one row per session: what the gate would have decided that day and when.</summary>
	private async Task<int> RenderRangeAsync(AIConfig config, ZeroDteGateConfig cfg, IntradayBarCache bars, HistoricalPriceCache closes, AIGateSettings settings, CancellationToken cancellation)
	{
		var since = DateTime.ParseExact(settings.Since!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var until = settings.Until != null ? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today;
		if (until < since) { Console.Error.WriteLine("--until must be on or after --since"); return 1; }

		var outcomes = new List<(DateTime Date, ZeroDteGateState State, DateTime? EntryEt, string Summary)>();
		for (var date = since.Date; date <= until.Date; date = date.AddDays(1))
		{
			cancellation.ThrowIfCancellationRequested();
			if (!MarketCalendar.IsOpen(date)) continue;
			var sessionBars = await LoadSessionAsync(bars, config.Ticker, date, cancellation);
			if (sessionBars.Count == 0) { outcomes.Add((date, ZeroDteGateState.NoTape, null, "no RTH tape on disk")); continue; }
			var prevClose = await PrevCloseAsync(closes, config.Ticker, date, cancellation);

			// Walk to the first entry-eligible minute; if none, report the state at the last minute of the
			// entry window, which is the reason the day ended flat.
			ZeroDteGateVerdict? last = null;
			DateTime? entryEt = null;
			for (var i = 0; i < sessionBars.Count; i++)
			{
				var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(sessionBars[i].Timestamp, NyTz).DateTime, DateTimeKind.Unspecified);
				var v = ZeroDteSessionGate.Evaluate(cfg, sessionBars.Take(i + 1).ToList(), prevClose, null, minuteEt);
				if (v.AllowsEntry) { outcomes.Add((date, v.State, minuteEt, v.Summary)); entryEt = minuteEt; break; }
				if (!v.DayClosed) last = v;   // keep the last still-live state, not the trivial PastCutoff one
			}
			if (entryEt == null) outcomes.Add((date, last?.State ?? ZeroDteGateState.NoTape, null, last?.Summary ?? "no decision"));
		}

		if (outcomes.Count == 0) { AnsiConsole.MarkupLine("[yellow]No trading days in range.[/]"); return 0; }

		var traded = outcomes.Where(o => o.EntryEt != null).ToList();
		var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{config.Ticker} gate outcomes {since:yyyy-MM-dd} → {until:yyyy-MM-dd}[/]");
		table.AddColumn("Outcome");
		table.AddColumn(new TableColumn("Days").RightAligned());
		table.AddColumn(new TableColumn("Share").RightAligned());
		foreach (var g in outcomes.GroupBy(o => o.State).OrderByDescending(g => g.Count()))
			table.AddRow(StateMarkup(g.Key), g.Count().ToString(), $"{(decimal)g.Count() / outcomes.Count:P1}");
		AnsiConsole.Write(table);

		AnsiConsole.MarkupLine($"[bold]{traded.Count} of {outcomes.Count} sessions ({(decimal)traded.Count / outcomes.Count:P1}) would have been entry-eligible.[/]");
		if (traded.Count > 0)
		{
			var mins = traded.Select(t => t.EntryEt!.Value.TimeOfDay).OrderBy(t => t).ToList();
			AnsiConsole.MarkupLine($"[dim]Entry time: earliest {mins[0]:hh\\:mm}, median {mins[mins.Count / 2]:hh\\:mm}, latest {mins[^1]:hh\\:mm} ET[/]");
			var byHour = traded.GroupBy(t => t.EntryEt!.Value.Hour).OrderBy(g => g.Key);
			AnsiConsole.MarkupLine($"[dim]By hour: {string.Join(", ", byHour.Select(g => $"{g.Key:00}:xx={g.Count()}"))}[/]");
		}
		var noTape = outcomes.Count(o => o.State == ZeroDteGateState.NoTape);
		if (noTape > 0)
			AnsiConsole.MarkupLine($"[yellow]{noTape} session(s) had no tape on disk. Those are DATA gaps, not strategy decisions — the gate fails closed on them, so a backtest counts them as no-trade days.[/]");
		return 0;
	}

	/// <summary>Today's regular-session bars for the ticker, oldest first. RTH-only, matching the contract
	/// <see cref="SessionTape"/> expects (the cache serves every row on disk, pre-market included).</summary>
	private static async Task<List<MinuteBar>> LoadSessionAsync(IntradayBarCache bars, string ticker, DateTime date, CancellationToken cancellation)
	{
		var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.Date.Add(RthStart), NyTz), TimeSpan.Zero);
		var toUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.Date.Add(RthEnd), NyTz), TimeSpan.Zero);
		var raw = await bars.GetBarsAsync(ticker, fromUtc, toUtc, BarInterval.M1, includeExtended: false, cancellation);
		return raw.Where(b =>
		{
			var tod = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).TimeOfDay;
			return tod >= RthStart && tod < RthEnd;
		}).OrderBy(b => b.Timestamp).ToList();
	}

	/// <summary>Prior session's close, from the same daily cache the opener's gap reads — so the gap shown
	/// here is the gap the gate actually gated on.</summary>
	private static async Task<decimal?> PrevCloseAsync(HistoricalPriceCache closes, string ticker, DateTime date, CancellationToken cancellation)
	{
		var series = await closes.GetRecentClosesAsync(ticker, 4, date, cancellation);
		return series.Count > 0 ? series[^1] : null;
	}

	private static string StateMarkup(ZeroDteGateState state) => state switch
	{
		ZeroDteGateState.BullConfirmed => "[green]BullConfirmed[/]",
		ZeroDteGateState.BearConfirmed => "[green]BearConfirmed[/]",
		ZeroDteGateState.RangeConfirmed => "[green]RangeConfirmed[/]",
		ZeroDteGateState.Choppy => "[red]Choppy[/]",
		ZeroDteGateState.NoTape => "[red]NoTape[/]",
		ZeroDteGateState.PastCutoff => "[yellow]PastCutoff[/]",
		_ => $"[dim]{state}[/]"
	};

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

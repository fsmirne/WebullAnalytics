using System.Globalization;
using Spectre.Console;
using WebullAnalytics.AI.Backtest;

namespace WebullAnalytics.AI;

/// <summary>
/// Console output of <c>wa ai verify</c>, in the backtest's style: one line per checked day, a Metric/Value summary, and with
/// <c>--detail</c> a pick ledger (live and backtest rows per day, each backtest cell carrying its Δ vs live) plus mark commands.
/// Money convention matches the backtest ledger: per-share prices are signed cash (+credit, −debit), P&L is per lot.
/// </summary>
internal static class AIVerifyRenderer
{
	public static void Render(DateTime since, DateTime until, string ticker, string strategy, IReadOnlyList<VerifyRow> rows, DateTime followThrough, bool detail)
	{
		AnsiConsole.WriteLine();
		RenderComparisonTable(since, until, ticker, strategy, rows, followThrough);
		RenderSummary(since, until, ticker, strategy, rows, followThrough);
		if (!detail) return;
		var scored = rows.Where(r => r.SetupLabel == null && (r.LiveProposal != null || r.BacktestOpen != null)).ToList();
		if (scored.Count == 0) return;
		RenderPickLedger(scored);
		RenderMarkCommands(scored);
	}

	private static void RenderComparisonTable(DateTime since, DateTime until, string ticker, string strategy, IReadOnlyList<VerifyRow> rows, DateTime followThrough)
	{
		// Columns that only say something on some runs appear only then: a cascade's setup label, and the structure when it varies
		// (a single-structure strategy names it in the title instead).
		var showSetup = rows.Any(r => r.SetupLabel != null);
		var structures = rows.SelectMany(r => new[] { r.LiveProposal?.Structure, r.BacktestOpen?.StrategyKind }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var showStructure = structures.Count > 1;
		var title = $"[bold]{Markup.Escape(ticker)}.{Markup.Escape(strategy)} live vs backtest[/]{(structures.Count == 1 ? $" [dim]{Markup.Escape(structures[0])}[/]" : "")} [dim]{since:yyyy-MM-dd} → {until:yyyy-MM-dd}[/]";
		var table = new Table().Border(TableBorder.Rounded).Title(title).Expand();
		// Only the note wraps: every value column stays one line, so each check is one row.
		table.AddColumn(new TableColumn("Date").NoWrap());
		if (showSetup) table.AddColumn(new TableColumn("Setup").NoWrap());
		if (showStructure) table.AddColumn(new TableColumn("Structure").NoWrap());
		table.AddColumn(new TableColumn("Live").NoWrap());
		table.AddColumn(new TableColumn("Backtest").NoWrap());
		table.AddColumn(new TableColumn("Net L / B").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("BE L / B").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Live P&L").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("BT P&L").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Δ").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Verdict").NoWrap());
		table.AddColumn("Note");

		foreach (var r in rows)
		{
			var live = r.LiveProposal;
			var bt = r.BacktestOpen;
			var cells = new List<string> { r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
			if (showSetup) cells.Add(Markup.Escape(r.SetupLabel ?? "—"));
			if (showStructure) cells.Add(Markup.Escape(live?.Structure ?? bt?.StrategyKind ?? "—"));
			cells.AddRange([
				live != null ? Markup.Escape(Legs(live)) : "[dim]—[/]",
				bt == null ? "[dim]—[/]" : live != null && AIVerifyCommand.SameLegSet(live.Legs, bt.Legs) ? "[dim]=[/]" : Markup.Escape(Legs(bt)),
				$"{Price(live?.NetPerShare)} / {Price(bt != null ? BtNetPerShare(bt) : null)}",
				$"{Breakevens(live?.Breakevens)} / {Breakevens(bt?.Breakevens)}",
				PathCell(r.LivePickPnl),
				PathCell(r.BacktestPickPnl),
				Money(PathDelta(r)),
				Verdict(r.Verdict),
				Markup.Escape(r.LivePickNote != null ? $"{r.Note}{(r.Note.Length > 0 ? "; " : "")}live replay n/a" : r.Note)]);
			table.AddRow(cells.ToArray());
		}
		AnsiConsole.Write(table);
		if (rows.Any(r => r.LivePickPnl?.Open == true || r.BacktestPickPnl?.Open == true))
			AnsiConsole.MarkupLine($"[dim]* still open at {followThrough:yyyy-MM-dd}: P&L includes that session's closing mark. P&L is per lot; Δ = BT − Live.[/]");
		AnsiConsole.WriteLine();
	}

	private static void RenderSummary(DateTime since, DateTime until, string ticker, string strategy, IReadOnlyList<VerifyRow> rows, DateTime followThrough)
	{
		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Verify summary[/]");
		table.AddColumn(new TableColumn("Metric").NoWrap());
		table.AddColumn(new TableColumn("Value").RightAligned().NoWrap());
		AddSummaryRows(table, since, until, ticker, strategy, rows, followThrough);
		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	private static void AddSummaryRows(Table table, DateTime since, DateTime until, string ticker, string strategy, IReadOnlyList<VerifyRow> rows, DateTime followThrough)
	{
		table.AddRow("Strategy", $"{Markup.Escape(ticker)}.{Markup.Escape(strategy)}");
		table.AddRow("Window", $"{since:yyyy-MM-dd} → {until:yyyy-MM-dd} ({rows.Count} check{(rows.Count == 1 ? "" : "s")})");
		table.AddRow("Picks followed through", $"{followThrough:yyyy-MM-dd} (1 lot each)");

		var matches = CountVerdicts(rows, VerifyVerdict.Match);
		var noData = CountVerdicts(rows, VerifyVerdict.NoBackfill);
		table.AddRow("Verdicts", $"[green]{matches} match[/] · [cyan]{CountVerdicts(rows, VerifyVerdict.NearTie, VerifyVerdict.CadenceOffset)} tie[/] · [yellow]{CountVerdicts(rows, VerifyVerdict.StrikeShift)} shift[/] · [yellow]{CountVerdicts(rows, VerifyVerdict.LegFlip)} flip[/] · [red]{CountVerdicts(rows, VerifyVerdict.Mismatch)} mismatch[/]{(noData > 0 ? $" · [dim]{noData} no data[/]" : "")}");
		var checkable = rows.Count - noData;
		table.AddRow("Match rate", checkable > 0 ? $"{matches} / {checkable} ({matches * 100m / checkable:F0}%)" : "—");

		var paired = rows.Where(r => PathDelta(r).HasValue).ToList();
		if (paired.Count > 0)
		{
			// Split on the legs themselves, not the verdict: an early-open day can be a mismatch with identical legs.
			var sameLegRows = paired.Where(r => AIVerifyCommand.SameLegSet(r.LiveProposal!.Legs, r.BacktestOpen!.Legs)).ToList();
			var divergentRows = paired.Except(sameLegRows).ToList();
			table.AddRow("[bold]Live picks P&L/lot[/]", PathTotal(paired.Select(r => r.LivePickPnl!)));
			table.AddRow("[bold]BT picks P&L/lot[/]", PathTotal(paired.Select(r => r.BacktestPickPnl!)));
			table.AddRow("[bold]Δ BT − Live[/]", Money(paired.Sum(r => PathDelta(r)!.Value)));
			table.AddRow("  same legs (entry price, exit timing)", $"{Money(sameLegRows.Sum(r => PathDelta(r)!.Value))} ({Days(sameLegRows.Count)})");
			table.AddRow("  divergent picks", $"{Money(divergentRows.Sum(r => PathDelta(r)!.Value))} ({Days(divergentRows.Count)})");

			// The same per-lot paths scaled to each day's proposed size, so the gap reads in the dollars the proposal would have meant.
			var lots = paired.Sum(r => ProposedLots(r)!.Value);
			table.AddRow("[bold]Live picks P&L at proposed size[/]", $"{Money(paired.Sum(r => r.LivePickPnl!.PerLot * ProposedLots(r)!.Value))} ({lots} lots over {Days(paired.Count)})");
			table.AddRow("[bold]BT picks P&L at proposed size[/]", Money(paired.Sum(r => r.BacktestPickPnl!.PerLot * ProposedLots(r)!.Value)));
			table.AddRow("[bold]Δ BT − Live at proposed size[/]", Money(paired.Sum(r => PathDelta(r)!.Value * ProposedLots(r)!.Value)));
		}


		var worst = rows.Select(r => r.Verdict).DefaultIfEmpty(VerifyVerdict.Match).Max();
		table.AddRow("Exit code", $"{AIVerifyCommand.ExitCodeFor(worst)} ({Verdict(worst)})");
	}

	/// <summary>Three aligned one-line rows per day — Live, BT, and Δ (BT − Live) — plus the unpinned BT on an early-open day, so
	/// every metric reads down a column the way the backtest's fill ledger does.</summary>
	private static void RenderPickLedger(IReadOnlyList<VerifyRow> rows)
	{
		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Pick ledger[/]").Expand();
		table.AddColumn(new TableColumn("Date").NoWrap());
		table.AddColumn(new TableColumn("Side").NoWrap());
		table.AddColumn(new TableColumn("Time").NoWrap());
		table.AddColumn(new TableColumn("Legs").NoWrap());
		table.AddColumn(new TableColumn("Leg mids").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Spot").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Net/sh").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Score").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Rep IV").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("POP").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("EV").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("P&L/lot").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Exit").NoWrap());

		foreach (var r in rows)
		{
			var live = r.LiveProposal;
			var bt = r.BacktestOpen;
			var date = r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			if (live != null)
			{
				table.AddRow(date, "Live", $"{live.TimestampEt:HH:mm:ss}", Markup.Escape(Legs(live)), LegMids(live.Legs.Select(l => (l.Action, l.Symbol, live.LegQuotes.GetValueOrDefault(l.Symbol)?.Mid))),
					live.Spot > 0m ? $"{live.Spot:0.00}" : "—", Price(live.NetPerShare), Score(live.FinalScore), Pct(live.RepIv),
					live.Pop.HasValue ? $"{live.Pop.Value:P0}" : "—", live.Ev.HasValue ? $"${live.Ev.Value:N0}" : "—",
					r.LivePickNote != null ? "[dim]n/a[/]" : PathCell(r.LivePickPnl), PathExit(r.LivePickPnl));
				date = "";
			}
			if (bt != null)
			{
				table.AddRow(date, "BT", $"{bt.Date:HH:mm:ss}", BtLegs(bt, live), LegMids(BtLegQuotes(bt)),
					$"{bt.Spot:0.00}", Price(BtNetPerShare(bt)), Score(bt.FinalScore), Pct(bt.RepIv), "—", "—",
					PathCell(r.BacktestPickPnl), PathExit(r.BacktestPickPnl));
				date = "";
			}
			if (live != null && bt != null)
				table.AddRow("", "[dim]Δ[/]", Dim(MinutesDelta(live.TimestampEt, bt.Date)), Dim(Markup.Escape(r.Note)), "",
					Dim(Delta(live.Spot > 0m ? bt.Spot - live.Spot : null, "+0.00;-0.00;0.00")),
					Dim(Delta(BtNetPerShare(bt) - live.NetPerShare, "+0.00;-0.00;0.00")),
					Dim(Delta(Diff(live.FinalScore, bt.FinalScore), "+0.00000;-0.00000;0")),
					Dim(Delta(Diff(live.RepIv, bt.RepIv) * 100m, "+0.00pt;-0.00pt;0pt")),
					"", "", Dim(Delta(PathDelta(r), "+$0.00;-$0.00;$0.00")), "");
			if (r.BacktestEarlyOpen is { } early)
				table.AddRow("", "[red]BT early[/]", $"{early.Date:HH:mm:ss}", BtLegs(early, live), LegMids(BtLegQuotes(early)),
					$"{early.Spot:0.00}", Price(BtNetPerShare(early)), Score(early.FinalScore), Pct(early.RepIv), "—", "—", "[dim]—[/]", "[red]opened before live[/]");
		}
		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	private static void RenderMarkCommands(IReadOnlyList<VerifyRow> rows)
	{
		// Only a pick still held is worth marking now: a finished one is already marked at its exit in the ledger, and marking its legs at
		// today's quotes (one may have expired) prices a position nobody holds.
		var paired = rows.Where(r => r.LiveProposal != null && r.BacktestOpen != null && (r.LivePickPnl?.Open == true || r.BacktestPickPnl?.Open == true)).ToList();
		if (paired.Count == 0) return;
		AnsiConsole.MarkupLine("[bold]Mark the open picks at current quotes[/] [dim](both at the proposal's size, at each side's entry price)[/]");
		foreach (var r in paired)
			AnsiConsole.MarkupLine($"  [dim]{r.Date:yyyy-MM-dd} x{ProposedLots(r)}[/]  {Markup.Escape(MarkCommand(r.LiveProposal!, r.BacktestOpen!, ProposedLots(r)!.Value))}");
		AnsiConsole.WriteLine();
	}

	private static int CountVerdicts(IReadOnlyList<VerifyRow> rows, params VerifyVerdict[] verdicts) => rows.Count(r => verdicts.Contains(r.Verdict));

	/// <summary>The size the live proposal sized the day's pick at; null when live proposed nothing.</summary>
	private static int? ProposedLots(VerifyRow row) => row.LiveProposal?.Qty;

	/// <summary>bt pick − live pick, per lot, when both picks were followed.</summary>
	internal static decimal? PathDelta(VerifyRow row) => row.LivePickPnl != null && row.BacktestPickPnl != null ? row.BacktestPickPnl.PerLot - row.LivePickPnl.PerLot : null;

	/// <summary>An <c>analyze trade --compare</c> command pricing both trades at <paramref name="lots"/> from each side's entry (live =
	/// captured mid, bt = sim fill), so their marks at current quotes compare at one size. Every opener structure is 1:1 per leg, so
	/// each leg carries the position's quantity.</summary>
	internal static string MarkCommand(LiveWatchProposal live, BacktestFill bt, int lots)
	{
		var liveSpec = TradeSpec(live.Legs.Select(l => (l.Action.ToLowerInvariant(), l.Symbol, live.LegQuotes.GetValueOrDefault(l.Symbol)?.Mid)), lots);
		var btSpec = TradeSpec(bt.Legs.Select(l => (l.Side == Side.Buy ? "buy" : "sell", l.Symbol, (decimal?)l.PricePerShare)), lots);
		return $"wa analyze trade \"{liveSpec};{btSpec}\" --compare";
	}

	private static string TradeSpec(IEnumerable<(string Action, string Symbol, decimal? Price)> legs, int lots) =>
		string.Join(",", legs.OrderBy(l => l.Action == "sell" ? 0 : 1).Select(l => $"{l.Action}:{l.Symbol}:{lots}@{(l.Price.HasValue ? l.Price.Value.ToString("0.00", CultureInfo.InvariantCulture) : "MID")}"));

	private static string Legs(LiveWatchProposal live) => BacktestSummaryRenderer.FormatLegDetail(live.Legs.Select(l => l.Symbol));

	private static string Legs(BacktestFill bt) => BacktestSummaryRenderer.FormatLegDetail(bt.Legs.Select(l => l.Symbol));

	private static string BtLegs(BacktestFill bt, LiveWatchProposal? live) =>
		live != null && AIVerifyCommand.SameLegSet(live.Legs, bt.Legs) ? "[dim]= live[/]" : Markup.Escape(Legs(bt));

	private static IEnumerable<(string Action, string Symbol, decimal? Price)> BtLegQuotes(BacktestFill bt) =>
		bt.Legs.Select(l => (l.Side == Side.Buy ? "buy" : "sell", l.Symbol, (decimal?)l.PricePerShare));

	/// <summary>Per-leg prices as signed cash (sell +, buy −) in the leg format's order (by expiry, then strike), so they sum to Net/sh.</summary>
	private static string LegMids(IEnumerable<(string Action, string Symbol, decimal? Price)> legs) =>
		string.Join(" ", legs
			.Select(l => (l.Action, l.Price, Opt: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.OrderBy(l => l.Opt?.ExpiryDate).ThenBy(l => l.Opt?.Strike)
			.Select(l => l.Price.HasValue ? Signed(l.Action.Equals("sell", StringComparison.OrdinalIgnoreCase) ? l.Price.Value : -l.Price.Value, "0.00") : "?"));

	/// <summary>The backtest's entry vs live's, in whole minutes; blank when they share the minute (the backtest fills at the bar, live
	/// seconds into it).</summary>
	private static string MinutesDelta(DateTime live, DateTime bt)
	{
		var minutes = (int)(new DateTime(bt.Year, bt.Month, bt.Day, bt.Hour, bt.Minute, 0) - new DateTime(live.Year, live.Month, live.Day, live.Hour, live.Minute, 0)).TotalMinutes;
		return minutes != 0 ? $"{minutes:+0;-0}m" : "";
	}

	/// <summary>The backtest open's net per share, signed (+credit, −debit), including its slippage model.</summary>
	private static decimal BtNetPerShare(BacktestFill bt) => bt.Qty > 0 ? bt.NetCashFlow / (bt.Qty * 100m) : 0m;

	private static string PathTotal(IEnumerable<PathPnl> pnls)
	{
		var list = pnls.ToList();
		var open = list.Count(p => p.Open);
		return $"{Money(list.Sum(p => p.PerLot))} ({list.Count - open} closed, {open} open)";
	}

	private static string PathCell(PathPnl? pnl) => pnl == null ? "[dim]—[/]" : $"{Money(pnl.PerLot)}{(pnl.Open ? "*" : "")}";

	private static string PathExit(PathPnl? pnl) =>
		pnl == null ? "[dim]—[/]" : pnl.Open ? "[dim]open[/]" : $"{pnl.ExitEt:MM/dd HH:mm} {Markup.Escape(BacktestSummaryRenderer.ShortRule(pnl.ExitRule))}";

	private static string Verdict(VerifyVerdict verdict) => verdict switch
	{
		VerifyVerdict.Match => "[green]MATCH[/]",
		VerifyVerdict.NearTie => "[cyan]TIE[/]",
		VerifyVerdict.CadenceOffset => "[cyan]CADENCE[/]",
		VerifyVerdict.StrikeShift => "[yellow]SHIFT[/]",
		VerifyVerdict.LegFlip => "[yellow]FLIP[/]",
		VerifyVerdict.NoBackfill => "[dim]NO DATA[/]",
		_ => "[red]MISMATCH[/]"
	};

	private static string Delta(decimal? delta, string format) => delta.HasValue ? delta.Value.ToString(format, CultureInfo.InvariantCulture) : "";

	private static string Dim(string markup) => markup.Length > 0 ? $"[dim]{markup}[/]" : "";

	private static decimal? Diff(decimal? live, decimal? bt) => live.HasValue && bt.HasValue ? bt.Value - live.Value : null;

	private static string Breakevens(IReadOnlyList<decimal>? bes) =>
		bes == null || bes.Count == 0 ? "—" : string.Join("–", bes.Select(b => b.ToString("0.0", CultureInfo.InvariantCulture)));

	private static string Price(decimal? perShare) => perShare.HasValue ? Signed(perShare.Value, "0.00") : "—";

	private static string Score(decimal? score) => score.HasValue ? score.Value.ToString("0.00000", CultureInfo.InvariantCulture) : "—";

	private static string Pct(decimal? fraction) => fraction.HasValue ? $"{fraction.Value * 100m:0.00}%" : "—";

	private static string Days(int n) => $"{n} day{(n == 1 ? "" : "s")}";

	private static string Signed(decimal value, string format) => value.ToString($"+{format};-{format};{format}", CultureInfo.InvariantCulture);

	private static string Money(decimal? value) =>
		!value.HasValue ? "[dim]—[/]"
		: value.Value >= 0m ? $"[green]+${value.Value:N2}[/]"
		: $"[red]-${Math.Abs(value.Value):N2}[/]";
}

using Spectre.Console;
using Spectre.Console.Rendering;
using WebullAnalytics.AI.Output;
using WebullAnalytics.Analyze;

namespace WebullAnalytics.AI.Open.Cascade;

/// <summary>
/// The watch loop's full-width subpanel at the bottom of the cascade panel: what became of each enabled setup today (placed or logged
/// with its legs, skipped with the reason, proposed and awaiting the executor, or nothing yet), the reasons a due setup produced no trade
/// on this read, and, on the read a setup is opening, the trade (setup and score, structure, size, credit or debit, max loss, legs) with
/// its <c>wa trade place</c> / <c>wa analyze trade</c> / <c>wa analyze risk</c> commands, one per line so each copies cleanly. A cascade
/// proposal has no panel of its own; the risk diagnostic is one command away rather than printed.
/// </summary>
internal static class CascadeTradeSection
{
	/// <summary>A proposal and the setup label shown with it (e.g. "BULL TRAP 76/100").</summary>
	internal sealed record Trade(OpenProposal Proposal, string Label);

	/// <summary>A setup's (or neutral side's) latest ledger entry of the day in this mode; null when it has none.</summary>
	internal sealed record Outcome(string Label, CascadeSignalLedger.Entry? Entry);

	public static Panel Build(IReadOnlyList<Trade> trades, IReadOnlyList<string> notices, IReadOnlyList<Outcome> today, bool live)
	{
		var rows = new List<IRenderable>();
		if (today.Count > 0) rows.Add(TodayGrid(today, live));
		foreach (var notice in notices) rows.Add(new Markup(notice));
		foreach (var trade in trades)
		{
			rows.Add(new Text(""));
			AddTrade(rows, trade);
		}
		if (rows.Count == 0) rows.Add(new Markup("[dim]no setup enabled[/]"));
		var title = trades.Count > 0 ? $"TRADE · TODAY ({(live ? "live" : "dry run")})" : $"TODAY ({(live ? "live" : "dry run")})";
		return CascadePanel.SubPanel(title, new Rows(rows));
	}

	private static void AddTrade(List<IRenderable> rows, Trade trade)
	{
		var p = trade.Proposal;
		var perShare = p.DebitOrCreditPerContract / 100m;
		var cash = perShare >= 0m ? $"credit ${perShare:F2}" : $"debit ${-perShare:F2}";
		rows.Add(new Markup($"[bold green]▶ {Markup.Escape(trade.Label)}[/] · [bold]{p.StructureKind}[/] {Markup.Escape(p.Ticker)} x{p.Qty} · {cash} · max loss ${-p.MaxLossPerContract:N0} per contract"));
		rows.Add(new Markup(Markup.Escape(p.Legs.Describe())));
		if (!string.IsNullOrWhiteSpace(p.PricingWarning)) rows.Add(new Markup($"[yellow]{Markup.Escape(p.PricingWarning)}[/]"));
		if (p.CashReserveBlocked && p.CashReserveDetail != null) rows.Add(new Markup($"[yellow]{Markup.Escape(p.CashReserveDetail)}[/]"));
		// Sized to zero and not blocked by the reserve: there is nothing to place.
		if (p.Qty <= 0 && !p.CashReserveBlocked) return;
		var leadIn = WebullAnalytics.IO.TextFileExporter.ReproductionLeadIn(false);
		foreach (var line in ReproductionCommands.Build(p, SuggestionPricing.Mid, includeRisk: true))
			rows.Add(new Markup($"[dim]{leadIn} {Markup.Escape(line)}[/]"));
	}

	private static Grid TodayGrid(IReadOnlyList<Outcome> today, bool live)
	{
		var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn(new GridColumn());
		foreach (var row in today)
			grid.AddRow(new Markup($"[bold]{Markup.Escape(row.Label)}[/]"), new Markup(Status(row.Entry, live)));
		return grid;
	}

	private static string Status(CascadeSignalLedger.Entry? entry, bool live)
	{
		if (entry == null) return "[dim]—[/]";
		var at = entry.TimeEt.Length >= 5 ? entry.TimeEt[..5] : entry.TimeEt;
		var detail = Markup.Escape(entry.Detail);
		return entry.Outcome switch
		{
			CascadeSignalLedger.Placed => $"[green]{at} {(live ? "placed" : "logged (dry run)")}[/] · {detail}",
			CascadeSignalLedger.Skipped => $"[yellow]{at} no trade[/] · {detail}",
			_ => $"{at} proposed, awaiting the executor · {detail}",
		};
	}
}

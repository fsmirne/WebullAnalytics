using Spectre.Console;
using Spectre.Console.Rendering;
using WebullAnalytics.AI.Output;
using WebullAnalytics.Analyze;

namespace WebullAnalytics.AI.Open.Cascade;

/// <summary>
/// The watch loop's full-width subpanel at the bottom of the cascade panel: what became of each enabled setup today (placed or logged
/// with its legs, skipped with the reason, confirmed and not placed — retrying, or never placed — or nothing yet), the reasons a due setup produced no trade
/// on this read, and, on the read a setup is opening, the trade (setup and score, structure, size, credit or debit, max loss, legs) with
/// its <c>wa trade place</c> / <c>wa analyze trade</c> / <c>wa analyze risk</c> commands, one per line so each copies cleanly. A cascade
/// proposal has no panel of its own; the risk diagnostic is one command away rather than printed.
/// </summary>
internal static class CascadeTradeSection
{
	/// <summary>A proposal and the setup label shown with it (e.g. "BULL TRAP 76/100").</summary>
	internal sealed record Trade(OpenProposal Proposal, string Label);

	/// <summary>A setup's (or neutral side's) latest ledger entry of the day in this mode; null when it has none. <paramref name="RetryUntil"/>
	/// is the ET time-of-day a confirmation not yet placed is proposed again until; null once that window has passed, or for a final entry.</summary>
	internal sealed record Outcome(string Label, CascadeSignalLedger.Entry? Entry, TimeSpan? RetryUntil = null);

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
		var title = trades.Any(t => t.Proposal.Qty >= 1) ? $"TRADE · TODAY ({(live ? "live" : "dry run")})" : $"TODAY ({(live ? "live" : "dry run")})";
		return CascadePanel.SubPanel(title, new Rows(rows));
	}

	private static void AddTrade(List<IRenderable> rows, Trade trade)
	{
		var p = trade.Proposal;
		var perShare = p.DebitOrCreditPerContract / 100m;
		var cash = perShare >= 0m ? $"credit ${perShare:F2}" : $"debit ${-perShare:F2}";
		var placeable = p.Qty >= 1;
		var marker = placeable ? $"[bold green]▶ {Markup.Escape(trade.Label)}[/]" : $"[bold yellow]▷ {Markup.Escape(trade.Label)}[/]";
		var size = placeable ? $" x{p.Qty}" : "";
		rows.Add(new Markup($"{marker} · [bold]{p.StructureKind}[/] {Markup.Escape(p.Ticker)}{size} · {cash} · max loss ${-p.MaxLossPerContract:N0} per contract"));
		// The sizing caps zeroed it: its legs are the unscaled one-lot skeleton, so they are shown per contract and nothing is placed.
		rows.Add(new Markup(placeable ? Markup.Escape(p.Legs.Describe()) : $"[dim]per contract:[/] {Markup.Escape(p.Legs.Describe())}"));
		if (!string.IsNullOrWhiteSpace(p.PricingWarning)) rows.Add(new Markup($"[yellow]{Markup.Escape(p.PricingWarning)}[/]"));
		if (!placeable)
		{
			rows.Add(new Markup($"[yellow]not placed: sized to 0 contracts — {Markup.Escape(p.CashReserveDetail ?? "the sizing caps allow none")}[/]"));
			return;
		}
		var leadIn = WebullAnalytics.IO.TextFileExporter.ReproductionLeadIn(false);
		foreach (var line in ReproductionCommands.Build(p, SuggestionPricing.Mid, includeRisk: true))
			rows.Add(new Markup($"[dim]{leadIn} {Markup.Escape(line)}[/]"));
	}

	private static Grid TodayGrid(IReadOnlyList<Outcome> today, bool live)
	{
		var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn(new GridColumn());
		foreach (var row in today)
			grid.AddRow(new Markup($"[bold]{Markup.Escape(row.Label)}[/]"), new Markup(Status(row.Entry, row.RetryUntil, live)));
		return grid;
	}

	private static string Status(CascadeSignalLedger.Entry? entry, TimeSpan? retryUntil, bool live)
	{
		if (entry == null) return "[dim]—[/]";
		var at = entry.TimeEt.Length >= 5 ? entry.TimeEt[..5] : entry.TimeEt;
		var detail = Describe(entry);
		return entry.Outcome switch
		{
			CascadeSignalLedger.Placed => $"[green]{at} {(live ? "placed" : "logged (dry run)")}[/] · {detail}",
			CascadeSignalLedger.Skipped => $"[yellow]{at} no trade[/] · {detail}",
			_ => retryUntil is TimeSpan until ? $"{at} confirmed, retrying until {until:hh\\:mm} · {detail}" : $"[yellow]{at} confirmed, never placed[/] · {detail}",
		};
	}

	/// <summary>One line: the entry's trade (structure, legs at their size, the price it was proposed at, why the sizing caps allowed no
	/// contracts) or why there was none, then the reading it came from (spot, score). Entries written before the trade fields were recorded
	/// show their legs alone.</summary>
	private static string Describe(CascadeSignalLedger.Entry entry)
	{
		var parts = new List<string> { Markup.Escape(entry.Structure != null ? $"{entry.Structure} {entry.Detail}" : entry.Detail) };
		if (entry.NetPerShare is decimal net) parts.Add(net >= 0m ? $"credit ${net:F2}" : $"debit ${-net:F2}");
		if (entry.Note != null) parts.Add($"[yellow]{Markup.Escape(entry.Note)}[/]");
		parts.Add($"[dim]spot ${entry.Spot:N2} · score {entry.Score}[/]");
		return string.Join(" · ", parts);
	}
}

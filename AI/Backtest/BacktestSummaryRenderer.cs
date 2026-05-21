using Spectre.Console;

namespace WebullAnalytics.AI.Backtest;

internal static class BacktestSummaryRenderer
{
	public static void Render(BacktestResult result, bool showFills)
	{
		AnsiConsole.MarkupLine("[yellow bold]Backtest assumptions:[/]");
		AnsiConsole.MarkupLine("[dim]  • Quotes are Black-Scholes synthesized: SPX-family (SPY/SPX/SPXW/XSP) uses VIX as ATM IV; other tickers use 30-day realized HV × premium.[/]");
		AnsiConsole.MarkupLine("[dim]  • Opens/closes/rolls price off the day's bar.Open (stamped 09:30 ET); expirations settle at bar.Close intrinsic (stamped 16:00 ET).[/]");
		AnsiConsole.MarkupLine("[dim]  • Intraday rule triggering: TakeProfit + StopLoss re-price each position at bar.High and bar.Low using mid-session TTE for 0DTE. SL is evaluated before TP (conservative whipsaw assumption — assumes the adverse extreme came first on days where both could fire). Other rules (rolls, closeBeforeShortExpiry) only fire at EOD.[/]");
		AnsiConsole.MarkupLine("[dim]  • Assignment / early exercise not modeled — expiring positions settle at intrinsic.[/]");
		AnsiConsole.WriteLine();

		if (showFills) RenderFillsTable(result);
		RenderSummaryPanel(result);
		RenderPerTickerBreakdown(result);
	}

	private static void RenderFillsTable(BacktestResult result)
	{
		if (result.Fills.Count == 0) { AnsiConsole.MarkupLine("[dim]No fills.[/]"); return; }

		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Fill ledger[/]");
		table.AddColumn("When (ET)");
		table.AddColumn("Ticker");
		table.AddColumn("Kind");
		table.AddColumn("Strategy");
		table.AddColumn("Strikes / Exp");
		table.AddColumn(new TableColumn("Qty").RightAligned());
		table.AddColumn(new TableColumn("Price").RightAligned());
		table.AddColumn(new TableColumn("Net/Ct").RightAligned());
		table.AddColumn(new TableColumn("Total").RightAligned());
		table.AddColumn(new TableColumn("Fees").RightAligned());
		table.AddColumn(new TableColumn("Cash").RightAligned());
		table.AddColumn("Rule");

		var runningCash = result.StartingCash;
		foreach (var f in result.Fills)
		{
			runningCash += f.NetCashFlow - f.Fees;

			// Net per-contract (signed): positive = credit received, negative = debit paid.
			var perContract = f.Qty != 0 ? f.NetCashFlow / f.Qty : 0m;
			// Per-share price = per-contract / 100 (option multiplier); matches the limit price on a broker ticket.
			var perShare = perContract / 100m;
			var perShareLabel = perShare >= 0m ? $"+${perShare:N2}" : $"-${-perShare:N2}";
			var perCtLabel = perContract >= 0m ? $"+${perContract:N2}" : $"-${-perContract:N2}";
			var totalLabel = f.NetCashFlow >= 0m ? $"+${f.NetCashFlow:N2}" : $"-${-f.NetCashFlow:N2}";
			var cashColor = f.NetCashFlow >= 0m ? "green" : "red";
			var runningColor = runningCash >= result.StartingCash ? "green" : "red";

			table.AddRow(
				f.Date.ToString("yyyy-MM-dd HH:mm"),
				Markup.Escape(f.Ticker),
				f.Kind.ToString(),
				Markup.Escape(f.StrategyKind),
				Markup.Escape(FormatLegDetail(f.Legs)),
				f.Qty.ToString(),
				$"[{cashColor}]{perShareLabel}[/]",
				$"[{cashColor}]{perCtLabel}[/]",
				$"[{cashColor}]{totalLabel}[/]",
				$"${f.Fees:N2}",
				$"[{runningColor}]${runningCash:N2}[/]",
				Markup.Escape(f.RuleName ?? "—"));
		}
		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	/// <summary>Compact summary of the leg set: unique sorted strikes, then earliest/latest expiry.
	/// Single-expiry structures (iron condors, butterflies, verticals) show one date; multi-expiry
	/// structures (calendars, diagonals) show "shortExp→longExp".
	/// Examples: "685 @ 01/09→02/13" (LongCalendar), "680/685/690 @ 01/16" (IronButterfly).</summary>
	private static string FormatLegDetail(IReadOnlyList<BacktestLegFill> legs)
	{
		var parsed = legs
			.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol))
			.Where(p => p != null)
			.Cast<OptionParsed>()
			.ToList();
		if (parsed.Count == 0) return "";

		var strikes = parsed.Select(p => p.Strike).Distinct().OrderBy(s => s).ToList();
		var expiries = parsed.Select(p => p.ExpiryDate.Date).Distinct().OrderBy(d => d).ToList();

		var strikesStr = string.Join("/", strikes.Select(FormatStrike));
		var expStr = expiries.Count == 1
			? expiries[0].ToString("MM/dd")
			: $"{expiries[0]:MM/dd}→{expiries[^1]:MM/dd}";

		return $"{strikesStr} @ {expStr}";
	}

	/// <summary>Drops trailing zeros: 685.00 → "685", 24.50 → "24.5", 686.25 → "686.25".</summary>
	private static string FormatStrike(decimal strike)
	{
		if (strike == Math.Floor(strike)) return strike.ToString("F0");
		return strike.ToString("0.##");
	}

	private static void RenderSummaryPanel(BacktestResult result)
	{
		var realized = result.RealizedPnL;
		var unrealized = result.UnrealizedPnL;
		var total = result.TotalPnL;
		var endingEquity = result.EndingEquity;
		var (wins, losses) = result.LifecycleWinLoss();
		var totalClosedLifecycles = wins + losses;

		var realizedPct = result.StartingCash > 0m ? realized / result.StartingCash * 100m : 0m;
		var unrealizedPct = result.StartingCash > 0m ? unrealized / result.StartingCash * 100m : 0m;
		var totalPct = result.StartingCash > 0m ? total / result.StartingCash * 100m : 0m;
		// Drawdown is the standard peak-to-trough equity drop, expressed as a percentage of peak equity.
		// (Earlier we expressed it as a % of starting cash, which is meaningless once equity has grown
		// past starting cash — a 40% drop from a $30k peak looked like 120% of starting $10k.)
		var ddPct = result.PeakEquity > 0m ? result.MaxDrawdown / result.PeakEquity * 100m : 0m;
		var winRate = totalClosedLifecycles > 0 ? wins * 100m / totalClosedLifecycles : 0m;

		var openMtm = endingEquity - result.EndingCash;

		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Backtest summary[/]");
		table.AddColumn("Metric");
		table.AddColumn(new TableColumn("Value").RightAligned());

		table.AddRow("Starting cash", $"${result.StartingCash:N2}");
		table.AddRow("Ending cash", $"${result.EndingCash:N2}");
		table.AddRow("Open positions MTM", $"${openMtm:N2}");
		table.AddRow("Ending equity", $"[{(endingEquity >= result.StartingCash ? "green" : "red")}]${endingEquity:N2}[/]");
		table.AddRow("[bold]Realized P&L[/] (closed lifecycles)", $"[{(realized >= 0 ? "green" : "red")}]${realized:N2} ({realizedPct:F2}%)[/]");
		table.AddRow("[bold]Unrealized P&L[/] (open lifecycles)", $"[{(unrealized >= 0 ? "green" : "red")}]${unrealized:N2} ({unrealizedPct:F2}%)[/]");
		table.AddRow("[bold]Total P&L[/]", $"[{(total >= 0 ? "green" : "red")}]${total:N2} ({totalPct:F2}%)[/]");
		table.AddRow("Total fees", $"${result.TotalFees:N2}");

		// Peak/trough are read off the equity curve so they carry their date; falling back to PeakEquity
		// from the result keeps the row populated when the curve is empty (zero-step runs).
		if (result.EquityCurve.Count > 0)
		{
			var peak = result.EquityCurve.Aggregate((a, b) => b.Equity > a.Equity ? b : a);
			var trough = result.EquityCurve.Aggregate((a, b) => b.Equity < a.Equity ? b : a);
			table.AddRow("Peak equity", $"${peak.Equity:N2} ({peak.Date:yyyy-MM-dd})");
			var troughColor = trough.Equity < result.StartingCash ? "red" : "green";
			table.AddRow("Trough equity", $"[{troughColor}]${trough.Equity:N2} ({trough.Date:yyyy-MM-dd})[/]");
		}
		else
		{
			table.AddRow("Peak equity", $"${result.PeakEquity:N2}");
		}
		table.AddRow("Max drawdown", $"[red]${result.MaxDrawdown:N2} ({ddPct:F2}% of peak)[/]");
		table.AddRow("Opens", result.OpenFills.ToString());
		table.AddRow("Closes (rules)", result.CloseFills.ToString());
		table.AddRow("Rolls", result.RollFills.ToString());
		table.AddRow("Expirations", result.ExpireFills.ToString());
		table.AddRow("Win rate (closed lifecycles)", totalClosedLifecycles > 0 ? $"{winRate:F1}% ({wins} W / {losses} L)" : "—");

		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	private static void RenderPerTickerBreakdown(BacktestResult result)
	{
		var byTicker = result.Fills.GroupBy(f => f.Ticker).OrderBy(g => g.Key).ToList();
		if (byTicker.Count <= 1) return;

		var table = new Table().Border(TableBorder.Rounded).Title("[bold]Per-ticker[/]");
		table.AddColumn("Ticker");
		table.AddColumn(new TableColumn("Opens").RightAligned());
		table.AddColumn(new TableColumn("Closes").RightAligned());
		table.AddColumn(new TableColumn("Rolls").RightAligned());
		table.AddColumn(new TableColumn("Expires").RightAligned());
		table.AddColumn(new TableColumn("Realized P&L").RightAligned());
		table.AddColumn(new TableColumn("Fees").RightAligned());

		foreach (var g in byTicker)
		{
			var fills = g.ToList();
			var pnl = fills.Sum(f => f.NetCashFlow - f.Fees);
			table.AddRow(
				Markup.Escape(g.Key),
				fills.Count(f => f.Kind == BacktestFillKind.Open).ToString(),
				fills.Count(f => f.Kind == BacktestFillKind.Close).ToString(),
				fills.Count(f => f.Kind == BacktestFillKind.Roll).ToString(),
				fills.Count(f => f.Kind == BacktestFillKind.Expire).ToString(),
				$"[{(pnl >= 0 ? "green" : "red")}]${pnl:N2}[/]",
				$"${fills.Sum(f => f.Fees):N2}");
		}
		AnsiConsole.Write(table);
	}
}

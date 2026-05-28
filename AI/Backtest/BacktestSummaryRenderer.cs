using Spectre.Console;

namespace WebullAnalytics.AI.Backtest;

internal static class BacktestSummaryRenderer
{
	public static void Render(BacktestResult result, bool showFills)
	{
		AnsiConsole.MarkupLine("[yellow bold]Backtest assumptions:[/]");
		AnsiConsole.MarkupLine("[dim]  • Quotes prefer captured per-minute option bars when available (from `wa ai history <ticker> --options`); legs without captured data fall back to Black-Scholes synthesized — SPX-family uses VIX as ATM IV, other tickers use 30-day realized HV × premium. Bid/ask is always synthesized with a per-ticker half-spread (the captured endpoint reports trade prints, not NBBO).[/]");
		AnsiConsole.MarkupLine("[dim]  • Opens/closes/rolls price off the day's bar.Open at the first RTH minute (stamped 09:30 ET under our normalized start-of-bar convention — Webull's native end-of-bar timestamps are shifted -60s at parse to match Polygon/ToS/TradingView); expirations settle at bar.Close intrinsic (stamped 16:00 ET).[/]");
		AnsiConsole.MarkupLine("[dim]  • Intraday rule triggering: StopLoss / TakeProfit / LegInShort walk the day's minute bars chronologically, re-pricing the position at each minute's spot (remaining-session TTE for 0DTE) and firing on the first qualifying minute. SL is evaluated before TP when both cross at the same minute (conservative whipsaw assumption). LegInShort runs before SL/TP per minute; when it fires the position converts to a vertical and intraday SL/TP stops for the day. Other rules (rolls, closeBeforeShortExpiry) only fire at EOD.[/]");
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
		table.AddColumn(new TableColumn("P&L %").RightAligned());
		table.AddColumn(new TableColumn("Fees").RightAligned());
		table.AddColumn(new TableColumn("Cash").RightAligned());
		table.AddColumn(new TableColumn("Return").RightAligned());
		table.AddColumn("Rule");

		// Pre-index by lineage so we can compute cumulative realized P&L at any closing fill.
		// Each lineage's "basis" is the absolute opening cash flow (debit paid or credit received);
		// the P&L % shown on Close/Expire/Roll rows is cumulative-lineage-P&L / basis.
		var openCashByLineage = result.Fills
			.Where(f => f.Kind == BacktestFillKind.Open)
			.GroupBy(f => f.LineageId)
			.ToDictionary(g => g.Key, g => g.First().NetCashFlow);
		var fillsByLineage = result.Fills
			.GroupBy(f => f.LineageId)
			.ToDictionary(g => g.Key, g => g.OrderBy(f => f.Date).ToList());

		var runningCash = result.StartingCash;
		// Cumulative realized P&L mirrors BacktestResult.RealizedPnL semantics — it only changes
		// when a lineage finalizes via Close or Expire. Open fills don't move it (the debit is
		// paid but no P&L is realized yet; equity dips on Open and recovers on Expire, but realized
		// return is unchanged until the lineage finalizes).
		decimal cumulativeRealized = 0m;
		foreach (var f in result.Fills)
		{
			runningCash += f.NetCashFlow - f.Fees;
			if (f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire)
			{
				if (fillsByLineage.TryGetValue(f.LineageId, out var lineageFillsForRealized))
					cumulativeRealized += lineageFillsForRealized.Where(x => x.Date <= f.Date).Sum(x => x.NetCashFlow - x.Fees);
			}

			// Net per-contract (signed): positive = credit received, negative = debit paid.
			var perContract = f.Qty != 0 ? f.NetCashFlow / f.Qty : 0m;
			// Per-share price = per-contract / 100 (option multiplier); matches the limit price on a broker ticket.
			var perShare = perContract / 100m;
			var perShareLabel = perShare >= 0m ? $"+${perShare:N2}" : $"-${-perShare:N2}";
			var perCtLabel = perContract >= 0m ? $"+${perContract:N2}" : $"-${-perContract:N2}";
			var totalLabel = f.NetCashFlow >= 0m ? $"+${f.NetCashFlow:N2}" : $"-${-f.NetCashFlow:N2}";
			var cashColor = f.NetCashFlow >= 0m ? "green" : "red";
			var runningColor = runningCash >= result.StartingCash ? "green" : "red";

			// Lineage P&L %: realized P&L through this fill divided by the absolute opening cash
			// flow. Long-debit positions: a winner shows positive (e.g. +124% means realized P&L =
			// 1.24× the debit paid). Short-credit positions: 100% is the cap (kept the full credit),
			// negative values can exceed -100% when realized loss is larger than the credit. Shown
			// only on rows that close a position (Close, Expire, Roll); Open rows show "—".
			string pnlPctLabel = "—";
			string pnlPctColor = "dim";
			if (f.Kind != BacktestFillKind.Open
				&& openCashByLineage.TryGetValue(f.LineageId, out var openCash)
				&& fillsByLineage.TryGetValue(f.LineageId, out var lineageFills))
			{
				var basis = Math.Abs(openCash);
				if (basis > 0m)
				{
					var cumPnl = lineageFills.Where(x => x.Date <= f.Date).Sum(x => x.NetCashFlow - x.Fees);
					var pct = cumPnl / basis * 100m;
					pnlPctLabel = pct >= 0m ? $"+{pct:F1}%" : $"{pct:F1}%";
					pnlPctColor = pct >= 0m ? "green" : "red";
				}
			}

			// Cumulative realized return vs starting cash. Only displayed on Close/Expire rows
			// — Open rows show "—" because no P&L is realized until the position finalizes.
			// (Showing the running-cash delta on Open was misleading: cash dips by the debit but
			// the position will likely recover that on Expire; the user reads the dip as a loss.)
			string returnLabel = "—";
			string returnColor = "dim";
			if ((f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire) && result.StartingCash > 0m)
			{
				var pct = cumulativeRealized / result.StartingCash * 100m;
				returnLabel = pct >= 0m ? $"+{pct:F1}%" : $"{pct:F1}%";
				returnColor = pct >= 0m ? "green" : "red";
			}

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
				$"[{pnlPctColor}]{pnlPctLabel}[/]",
				$"${f.Fees:N2}",
				$"[{runningColor}]${runningCash:N2}[/]",
				$"[{returnColor}]{returnLabel}[/]",
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

	/// <summary>Longest runs of consecutive <em>trade</em> days with positive ("win") and negative ("loss")
	/// daily P&L, read off the equity curve as the day-over-day equity change (the first day is measured
	/// against starting cash). A flat day — no equity change, i.e. a day we didn't trade — is ignored: it
	/// neither extends nor breaks a run, so only days that actually won or lost money count. Each streak
	/// carries the date range of its first and last trade day; counts are 0 when the curve is empty.</summary>
	private static (int WinDays, DateTime WinStart, DateTime WinEnd, int LossDays, DateTime LossStart, DateTime LossEnd) ComputeDailyStreaks(BacktestResult result)
	{
		int bestWin = 0, bestLoss = 0, curWin = 0, curLoss = 0;
		DateTime winStart = default, winEnd = default, lossStart = default, lossEnd = default, curWinStart = default, curLossStart = default;
		var prev = result.StartingCash;
		foreach (var (date, equity) in result.EquityCurve)
		{
			var delta = equity - prev;
			prev = equity;
			if (delta > 0m)
			{
				if (curWin == 0) curWinStart = date;
				curWin++; curLoss = 0;
				if (curWin > bestWin) { bestWin = curWin; winStart = curWinStart; winEnd = date; }
			}
			else if (delta < 0m)
			{
				if (curLoss == 0) curLossStart = date;
				curLoss++; curWin = 0;
				if (curLoss > bestLoss) { bestLoss = curLoss; lossStart = curLossStart; lossEnd = date; }
			}
			// Flat day (no equity change — a no-trade day): ignored. It neither extends nor breaks a
			// streak, so runs span only the days we actually traded.
		}
		return (bestWin, winStart, winEnd, bestLoss, lossStart, lossEnd);
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
		table.AddRow("Trading days", result.EquityCurve.Count.ToString());
		var streaks = ComputeDailyStreaks(result);
		table.AddRow("Longest win streak", streaks.WinDays > 0 ? $"[green]{streaks.WinDays} day{(streaks.WinDays == 1 ? "" : "s")} ({streaks.WinStart:yyyy-MM-dd} → {streaks.WinEnd:yyyy-MM-dd})[/]" : "—");
		table.AddRow("Longest loss streak", streaks.LossDays > 0 ? $"[red]{streaks.LossDays} day{(streaks.LossDays == 1 ? "" : "s")} ({streaks.LossStart:yyyy-MM-dd} → {streaks.LossEnd:yyyy-MM-dd})[/]" : "—");
		var openPctOfDays = result.EquityCurve.Count > 0 ? result.OpenFills * 100m / result.EquityCurve.Count : 0m;
		table.AddRow("Opens", result.EquityCurve.Count > 0 ? $"{result.OpenFills} ({openPctOfDays:F1}% of days)" : result.OpenFills.ToString());
		table.AddRow("Closes (rules)", result.CloseFills.ToString());
		table.AddRow("Rolls", result.RollFills.ToString());
		table.AddRow("Leg-ins", result.LegInFills.ToString());
		table.AddRow("Expirations", result.ExpireFills.ToString());
		table.AddRow("Win rate (closed lifecycles)", totalClosedLifecycles > 0 ? $"{winRate:F1}% ({wins} W / {losses} L)" : "—");

		// Per-trade edge stats. Unlike terminal P&L (which compounds with position sizing), these are
		// the sizing-robust read on whether the strategy has an edge: expectancy is the average $ per
		// trade, and profit factor (gross win / gross loss) is dimensionless — both meaningful even when
		// terminal equity is dominated by the compounding feedback loop. Under `--lots N` they are the
		// primary metric, since terminal P&L is then just their additive sum.
		var pnls = result.LifecyclePnLs();
		if (pnls.Count > 0)
		{
			var winPnls = pnls.Where(p => p > 0m).ToList();
			var lossPnls = pnls.Where(p => p <= 0m).ToList();
			var avgWin = winPnls.Count > 0 ? winPnls.Average() : 0m;
			var avgLoss = lossPnls.Count > 0 ? lossPnls.Average() : 0m;
			var expectancy = pnls.Average();
			var grossLoss = Math.Abs(lossPnls.Sum());
			var profitFactor = grossLoss > 0m ? winPnls.Sum() / grossLoss : 0m;
			table.AddRow("Avg win / loss (per trade)", $"[green]${avgWin:N2}[/] / [red]${avgLoss:N2}[/]");
			table.AddRow("Expectancy (per trade)", $"[{(expectancy >= 0 ? "green" : "red")}]${expectancy:N2}[/]");
			table.AddRow("Profit factor", profitFactor > 0m ? $"{profitFactor:F2}" : "—");
		}

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

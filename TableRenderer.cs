using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
	private const string LegPrefix = "  └─ ";

	public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount = 0m, bool simplified = false, decimal? iv = null, decimal range = 2, string displayMode = "pnl")
	{
		var console = AnsiConsole.Console;

		console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, simplified: simplified));
		console.WriteLine();

		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, simplified: simplified));
			console.WriteLine();

			var maxGridColumns = ComputeMaxGridColumns(displayMode);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, iv, range, maxGridColumns);
			foreach (var result in breakEvens)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(result, displayMode: displayMode));
				console.WriteLine();
			}
		}
		else
		{
			console.WriteLine("No open positions.");
		}

		var totalFees = rows.Where(r => !r.IsStrategyLeg).Sum(r => r.Fees);
		console.Write($"Total fees: ");
		console.Write(Formatters.FormatMoney(totalFees, decimal.MaxValue));
		console.WriteLine();

		console.Write("Final realized P&L: ");
		console.Write(Formatters.FormatPnL(running));
		console.Write(Formatters.FormatPnLPercent(running, initialAmount));
		console.WriteLine();

		console.Write("Final amount: ");
		console.Write(Formatters.FormatMoney(initialAmount + running, initialAmount));
		console.WriteLine();
	}

	/// <summary>
	/// Computes the maximum number of date columns that fit in the terminal width.
	/// Layout: panel borders (4) + table outer borders (2) + price column (11) + N × date column (15 for pnl, 10 for value).
	/// Each Spectre table column = content + 2 padding + 1 separator.
	/// </summary>
	private static int ComputeMaxGridColumns(string displayMode)
	{
		int terminalWidth;
		try { terminalWidth = Console.WindowWidth; }
		catch { return 7; }

		// panel left/right border+padding (4) + table outer left+right borders (2) + price column (content 8 + pad 2 + sep 1 = 11)
		const int fixedOverhead = 4 + 2 + 11;
		var colWidth = displayMode == "pnl" ? 15 : 10; // pnl: "$+1,520.00" (10) + 2 pad + 1 sep; value: "$25.38" (6) + 2 pad + 1 sep
		var available = terminalWidth - fixedOverhead;
		var maxCols = Math.Max(3, available / colWidth); // minimum 3: today, expiry open, at exp
		return maxCols;
	}
}

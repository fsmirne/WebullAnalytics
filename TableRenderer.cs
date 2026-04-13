using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
	private const string LegPrefix = "  └─ ";

	public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount, bool simplified, AnalysisOptions opts, decimal range = 2, string displayMode = "pnl", List<PriceBreakdown>? adjustmentBreakdowns = null, bool showLegs = false)
	{
		var console = AnsiConsole.Console;

		console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, simplified: simplified));
		console.WriteLine();

		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, simplified: simplified));
			console.WriteLine();

			if (adjustmentBreakdowns != null && adjustmentBreakdowns.Count > 0)
			{
				foreach (var b in adjustmentBreakdowns)
				{
					console.Write(TableBuilder.BuildAdjustmentPanel(b));
					console.WriteLine();
				}
			}

			var maxGridColumns = ComputeMaxGridColumns(displayMode, showLegs);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			foreach (var result in breakEvens)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(result, displayMode: displayMode, showLegs: showLegs));
				console.WriteLine();
			}
		}
		else
		{
			console.WriteLine("No open positions.");
		}

		var unrealizedPnL = TableBuilder.ComputeUnrealizedPnL(positions, opts);
		TableBuilder.RenderSummary(console, rows, running, initialAmount, unrealizedPnL);
	}

	/// <summary>
	/// Computes the maximum number of date columns that fit in the terminal width.
	/// </summary>
	private static int ComputeMaxGridColumns(string displayMode, bool showLegs)
	{
		int terminalWidth;
		try { terminalWidth = Console.WindowWidth; }
		catch { return 7; }
		return TableBuilder.ComputeMaxGridColumns(terminalWidth, displayMode, showLegs);
	}
}

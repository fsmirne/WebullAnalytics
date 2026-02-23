using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
	private const string LegPrefix = "  └─ ";

	public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount = 0m, bool simplified = false, decimal? iv = null)
	{
		var console = AnsiConsole.Console;

		console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, simplified: simplified));
		console.WriteLine();

		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, simplified: simplified));
			console.WriteLine();

			var breakEvens = BreakEvenAnalyzer.Analyze(positions, iv);
			foreach (var result in breakEvens)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(result));
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
		console.WriteLine();

		console.Write("Final amount: ");
		console.Write(Formatters.FormatMoney(initialAmount + running, initialAmount));
		console.WriteLine();
	}
}

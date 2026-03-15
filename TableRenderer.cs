using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
	private const string LegPrefix = "  └─ ";

	public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount = 0m, bool simplified = false, decimal? ivLong = null, decimal? ivShort = null, decimal range = 2, string displayMode = "pnl", IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol = null, IReadOnlyDictionary<string, decimal>? underlyingPrices = null, IReadOnlyDictionary<string, decimal>? underlyingPriceOverrides = null, bool theoretical = false, IReadOnlyDictionary<string, List<decimal>>? extraNotablePrices = null)
	{
		var console = AnsiConsole.Console;

		console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, simplified: simplified));
		console.WriteLine();

		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, simplified: simplified));
			console.WriteLine();

			var maxGridColumns = ComputeMaxGridColumns(displayMode);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, ivLong, ivShort, range, maxGridColumns, optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, theoretical, extraNotablePrices);
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

		TableBuilder.RenderSummary(console, rows, running, initialAmount);
	}

	/// <summary>
	/// Computes the maximum number of date columns that fit in the terminal width.
	/// </summary>
	private static int ComputeMaxGridColumns(string displayMode)
	{
		int terminalWidth;
		try { terminalWidth = Console.WindowWidth; }
		catch { return 7; }
		return TableBuilder.ComputeMaxGridColumns(terminalWidth, displayMode);
	}
}

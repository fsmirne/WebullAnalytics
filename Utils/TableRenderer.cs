using Spectre.Console;
using WebullAnalytics.Report;

namespace WebullAnalytics.Utils;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
	private const string LegPrefix = "  └─ ";

	public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, Dictionary<string, List<Lot>> lotsByMatchKey, decimal running, decimal initialAmount, bool simplified, AnalysisOptions opts, decimal range = 2, string displayMode = "pnl", List<PriceBreakdown>? adjustmentBreakdowns = null, bool showLegs = false)
	{
		var console = AnsiConsole.Console;

		if (rows.Count > 0)
		{
			console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, simplified: simplified));
			console.WriteLine();
		}
		else
		{
			console.WriteLine("No trades.");
		}

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
			var combined = CombinedBreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			var combinedByTicker = new Dictionary<string, BreakEvenResult>(StringComparer.Ordinal);
			foreach (var c in combined)
			{
				var ticker = ExtractTickerFromCombinedTitle(c.Title);
				if (ticker != null) combinedByTicker[ticker] = c;
			}

			string? lastTicker = null;
			foreach (var result in breakEvens)
			{
				var ticker = ExtractTickerFromTitle(result.Title);
				if (lastTicker != null && ticker != lastTicker && combinedByTicker.TryGetValue(lastTicker, out var prevCombined))
				{
					console.Write(TableBuilder.BuildBreakEvenPanel(prevCombined, displayMode: displayMode, showLegs: showLegs));
					console.WriteLine();
					combinedByTicker.Remove(lastTicker);
				}
				console.Write(TableBuilder.BuildBreakEvenPanel(result, displayMode: displayMode, showLegs: showLegs));
				console.WriteLine();
				lastTicker = ticker;
			}
			if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(finalCombined, displayMode: displayMode, showLegs: showLegs));
				console.WriteLine();
			}
		}
		else
		{
			console.WriteLine("No open positions.");
		}

		var unrealizedPnL = TableBuilder.ComputeUnrealizedPnL(lotsByMatchKey, opts);
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

	/// <summary>
	/// Extracts the ticker from an individual break-even result title.
	/// Titles begin with the ticker followed by a space (e.g., "GME Long Call $25").
	/// </summary>
	private static string? ExtractTickerFromTitle(string title)
	{
		var space = title.IndexOf(' ');
		return space <= 0 ? null : title[..space];
	}

	/// <summary>
	/// Extracts the ticker from a combined-panel title: "&lt;TICKER&gt; Combined — ...".
	/// </summary>
	private static string? ExtractTickerFromCombinedTitle(string title)
	{
		var space = title.IndexOf(' ');
		return space <= 0 ? null : title[..space];
	}
}

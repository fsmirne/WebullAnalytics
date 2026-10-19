using Spectre.Console;
using WebullAnalytics.Analyze;
using WebullAnalytics.Report;

namespace WebullAnalytics.Utils;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
	public const string LegPrefix = "  └─ ";

	public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, Dictionary<string, List<Lot>> lotsByMatchKey, decimal running, decimal initialAmount, bool simplified, AnalysisOptions opts, decimal range = 2, string displayMode = "pnl", List<PriceBreakdown>? adjustmentBreakdowns = null, bool showLegs = false)
	{
		var console = AnsiConsole.Console;

		if (rows.Count > 0)
		{
			console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, TableBorder.Rounded, simplified));
			console.WriteLine();
		}
		else
		{
			console.WriteLine("No trades.");
		}

		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, TableBorder.Rounded, simplified));
			console.WriteLine();

			if (adjustmentBreakdowns != null && adjustmentBreakdowns.Count > 0)
			{
				foreach (var b in adjustmentBreakdowns)
				{
					console.Write(TableBuilder.BuildAdjustmentPanel(b, BoxBorder.Rounded, TableBorder.Rounded));
					console.WriteLine();
				}
			}

			int terminalWidth;
			try { terminalWidth = Console.WindowWidth; }
			catch { terminalWidth = 200; }
			WriteBreakEvenPanels(console, positions, opts, range, terminalWidth, displayMode, showLegs, ascii: false);
		}
		else
		{
			console.WriteLine("No open positions.");
		}

		var openMarketValue = TableBuilder.ComputeOpenPositionsMarketValue(lotsByMatchKey, opts);
		TableBuilder.RenderSummary(console, rows, running, initialAmount, openMarketValue);
	}

	/// <summary>The time-decay break-even panel of every position, each ticker's combined panel following its last position —
	/// the grid section shared by `report` (console and text) and `analyze trade`, which renders nothing else.</summary>
	internal static void WriteBreakEvenPanels(IAnsiConsole console, List<PositionRow> positions, AnalysisOptions opts, decimal range, int terminalWidth, string displayMode, bool showLegs, bool ascii)
	{
		var box = ascii ? BoxBorder.Ascii : BoxBorder.Rounded;
		var border = ascii ? TableBorder.Ascii : TableBorder.Rounded;
		// Grids render with an outer-bordered table, whose left/right borders cost 2 chars; the width estimate must
		// account for them or the last date column wraps on tight terminals.
		var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, terminalWidth, displayMode, showLegs, gridTableHasBorder: true);
		var combined = CombinedBreakEvenAnalyzer.Analyze(positions, opts, range, terminalWidth, displayMode, showLegs, gridTableHasBorder: true, individualResults: breakEvens);
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
			if (lastTicker != null && ticker != lastTicker && combinedByTicker.Remove(lastTicker, out var prevCombined))
				WritePanel(console, prevCombined, box, border, ascii, displayMode, showLegs);
			WritePanel(console, result, box, border, ascii, displayMode, showLegs);
			lastTicker = ticker;
		}
		if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
			WritePanel(console, finalCombined, box, border, ascii, displayMode, showLegs);
	}

	private static void WritePanel(IAnsiConsole console, BreakEvenResult result, BoxBorder box, TableBorder border, bool ascii, string displayMode, bool showLegs)
	{
		console.Write(TableBuilder.BuildBreakEvenPanel(result, box, border, ascii: ascii, displayMode: displayMode, showLegs: showLegs));
		console.WriteLine();
	}

	// Max-grid-columns is computed per panel inside the analyzers based on each panel's leg count.

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
	/// Extracts the ticker from a combined-panel title: "<TICKER> Combined — ...".
	/// </summary>
	private static string? ExtractTickerFromCombinedTitle(string title)
	{
		var space = title.IndexOf(' ');
		return space <= 0 ? null : title[..space];
	}
}

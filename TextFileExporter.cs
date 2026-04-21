using Spectre.Console;
using System.Text.RegularExpressions;

namespace WebullAnalytics;

public static partial class TextFileExporter
{
	private const string LegPrefix = "    L- ";

	[GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]")]
	private static partial Regex AnsiEscapeRegex();

	public static void ExportToTextFile(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount, string outputPath, bool simplified, AnalysisOptions opts, decimal range = 2, string displayMode = "pnl", List<PriceBreakdown>? adjustmentBreakdowns = null, bool showLegs = false)
	{
		var stringWriter = new StringWriter();

		var settings = new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.Yes,
			ColorSystem = ColorSystemSupport.Standard,
			Out = new AnsiConsoleOutput(stringWriter),
			Interactive = InteractionSupport.No
		};

		var console = AnsiConsole.Create(settings);
		console.Profile.Width = 200;

		console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, TableBorder.Ascii, simplified));
		console.WriteLine();

		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, TableBorder.Ascii, simplified));
			console.WriteLine();

			if (adjustmentBreakdowns != null && adjustmentBreakdowns.Count > 0)
			{
				foreach (var b in adjustmentBreakdowns)
				{
					console.Write(TableBuilder.BuildAdjustmentPanel(b, Spectre.Console.BoxBorder.Ascii, TableBorder.Ascii, ascii: true));
					console.WriteLine();
				}
			}

			var maxGridColumns = TableBuilder.ComputeMaxGridColumns(200, displayMode, showLegs);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			var combined = CombinedBreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			var combinedByTicker = new Dictionary<string, BreakEvenResult>(StringComparer.Ordinal);
			foreach (var c in combined)
			{
				var sp = c.Title.IndexOf(' ');
				if (sp > 0) combinedByTicker[c.Title[..sp]] = c;
			}

			void WriteResult(BreakEvenResult r)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(r, Spectre.Console.BoxBorder.Ascii, TableBorder.Ascii, ascii: true, displayMode: displayMode, showLegs: showLegs));
				console.WriteLine();
			}

			string? lastTicker = null;
			foreach (var result in breakEvens)
			{
				var sp = result.Title.IndexOf(' ');
				var ticker = sp > 0 ? result.Title[..sp] : null;
				if (lastTicker != null && ticker != lastTicker && combinedByTicker.TryGetValue(lastTicker, out var prev))
				{
					WriteResult(prev);
					combinedByTicker.Remove(lastTicker);
				}
				WriteResult(result);
				lastTicker = ticker;
			}
			if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
				WriteResult(finalCombined);
		}
		else
		{
			console.WriteLine("No open positions.");
		}

		var unrealizedPnL = TableBuilder.ComputeUnrealizedPnL(positions, opts);
		TableBuilder.RenderSummary(console, rows, running, initialAmount, unrealizedPnL);

		var output = stringWriter.ToString();
		var cleanOutput = StripAnsiCodes(output);

		File.WriteAllText(outputPath, cleanOutput);
		Console.WriteLine($"Text report exported to: {outputPath}");
	}

	private static string StripAnsiCodes(string text) => AnsiEscapeRegex().Replace(text, string.Empty);
}

using Spectre.Console;
using System.Text.RegularExpressions;
using WebullAnalytics.Analyze;
using WebullAnalytics.Report;
using WebullAnalytics.Utils;

namespace WebullAnalytics.IO;

public static partial class TextFileExporter
{
	private const string LegPrefix = "    L- ";

	[GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]")]
	private static partial Regex AnsiEscapeRegex();

	[GeneratedRegex(@"^(Webull|Yahoo): .*(\r?\n)?", RegexOptions.Multiline)]
	private static partial Regex ProviderStatusLineRegex();

	public static IAnsiConsole CreateTextConsole(StringWriter stringWriter, int width = 200)
	{
		var settings = new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.No,
			ColorSystem = ColorSystemSupport.NoColors,
			Out = new AnsiConsoleOutput(stringWriter),
			Interactive = InteractionSupport.No
		};

		var console = AnsiConsole.Create(settings);
		console.Profile.Width = width;
		return console;
	}

	public static void WriteConsoleOutputToTextFile(StringWriter stringWriter, string outputPath, string exportMessage)
	{
		var directory = Path.GetDirectoryName(outputPath);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);
		var output = stringWriter.ToString();
		var cleanOutput = StripProviderStatusLines(StripAnsiCodes(output));
		File.WriteAllText(outputPath, cleanOutput);
		Console.WriteLine($"{exportMessage}: {outputPath}");
	}

	public static void ExportToTextFile(List<ReportRow> rows, List<PositionRow> positions, Dictionary<string, List<Lot>> lotsByMatchKey, decimal running, decimal initialAmount, string outputPath, bool simplified, AnalysisOptions opts, decimal range = 2, string displayMode = "pnl", List<PriceBreakdown>? adjustmentBreakdowns = null, bool showLegs = false)
	{
		var stringWriter = new StringWriter();
		var console = CreateTextConsole(stringWriter);

		if (rows.Count > 0)
		{
			console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, TableBorder.Ascii, simplified));
			console.WriteLine();
		}
		else
		{
			console.WriteLine("No trades.");
		}

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

			const int terminalWidth = 200;
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, terminalWidth, displayMode, showLegs, gridTableHasBorder: true);
			var combined = CombinedBreakEvenAnalyzer.Analyze(positions, opts, range, terminalWidth, displayMode, showLegs, gridTableHasBorder: true, individualResults: breakEvens);
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

		var unrealizedPnL = TableBuilder.ComputeUnrealizedPnL(lotsByMatchKey, opts);
		TableBuilder.RenderSummary(console, rows, running, initialAmount, unrealizedPnL);

		WriteConsoleOutputToTextFile(stringWriter, outputPath, "Text report exported to");
	}

	private static string StripAnsiCodes(string text) => AnsiEscapeRegex().Replace(text, string.Empty);
	private static string StripProviderStatusLines(string text) => ProviderStatusLineRegex().Replace(text, string.Empty);
}

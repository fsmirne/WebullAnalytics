using Spectre.Console;
using System.Text.RegularExpressions;

namespace WebullAnalytics;

public static partial class TextFileExporter
{
	private const string LegPrefix = "    L- ";

	[GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]")]
	private static partial Regex AnsiEscapeRegex();

	public static void ExportToTextFile(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount, string outputPath, bool simplified = false, decimal? ivLong = null, decimal? ivShort = null, decimal range = 2, string displayMode = "pnl", IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol = null, IReadOnlyDictionary<string, decimal>? underlyingPrices = null, IReadOnlyDictionary<string, decimal>? underlyingPriceOverrides = null, bool theoretical = false, IReadOnlyDictionary<string, List<decimal>>? extraNotablePrices = null)
	{
		// Create a string writer to capture the console output
		var stringWriter = new StringWriter();

		// Create an ANSI console that writes to our string writer
		var settings = new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.Yes,
			ColorSystem = ColorSystemSupport.Standard,
			Out = new AnsiConsoleOutput(stringWriter),
			Interactive = InteractionSupport.No
		};

		var console = AnsiConsole.Create(settings);

		// Set a larger width to prevent wrapping
		console.Profile.Width = 200;

		// Render report table
		console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, TableBorder.Ascii, simplified));
		console.WriteLine();

		// Render positions table
		if (positions.Count > 0)
		{
			console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, TableBorder.Ascii, simplified));
			console.WriteLine();

			// Text export uses 200-char width; compute max columns to fit
			var maxGridColumns = TableBuilder.ComputeMaxGridColumns(200, displayMode);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, ivLong, ivShort, range, maxGridColumns, optionQuotesBySymbol, underlyingPrices, underlyingPriceOverrides, theoretical, extraNotablePrices);
			foreach (var result in breakEvens)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(result, Spectre.Console.BoxBorder.Ascii, TableBorder.Ascii, ascii: true, displayMode: displayMode));
				console.WriteLine();
			}
		}
		else
		{
			console.WriteLine("No open positions.");
		}

		TableBuilder.RenderSummary(console, rows, running, initialAmount);

		// Get the rendered output and strip ANSI codes
		var output = stringWriter.ToString();
		var cleanOutput = StripAnsiCodes(output);

		// Write to file
		File.WriteAllText(outputPath, cleanOutput);
		Console.WriteLine($"Text report exported to: {outputPath}");
	}

	private static string StripAnsiCodes(string text) => AnsiEscapeRegex().Replace(text, string.Empty);
}

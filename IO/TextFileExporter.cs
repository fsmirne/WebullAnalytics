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
		var cleanOutput = NormalizeAsciiSafe(StripProviderStatusLines(StripAnsiCodes(output)));
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

	/// <summary>Single source of truth for the reproduction-command lead-in glyph. Console output uses
	/// the unicode hooked-arrow `↪`; text-file output uses `L-` because `↪` (U+21AA) is one of the few
	/// glyphs that Spectre measures as width 1 but many monospace fonts draw as a 2-column emoji,
	/// breaking right-border alignment in saved files. Multi-char substitution must happen at render
	/// time so Spectre measures the longer string — the post-process safety net cannot expand chars
	/// without shifting content past the already-drawn right border.</summary>
	public static string ReproductionLeadIn(bool ascii) => ascii ? "L-" : "↪";

	/// <summary>Replaces `↪` with `L-` in a free-form text string for the ascii=true path. Pass-through
	/// otherwise. Use this when the lead-in is embedded in data (e.g. `Scenario.Rationale`) instead of
	/// a literal site we control.</summary>
	public static string NormalizeArrows(bool ascii, string text)
	{
		if (!ascii || string.IsNullOrEmpty(text)) return text;
		return text.Contains('↪', StringComparison.Ordinal)
			? text.Replace("↪", "L-", StringComparison.Ordinal)
			: text;
	}

	/// <summary>Substitutes the Unicode glyphs known to misalign because their rendering width differs
	/// from Spectre's measurement. Spectre uses East Asian Width tables and assigns width 1 to all of
	/// these, but the actual rendered width depends on the font/viewer:
	/// - `★`, `⚠`: emoji-presentation-prone — many fonts (Cascadia, anything with emoji fallback) draw
	///   them as 2-column glyphs.
	/// - `│`: box-drawing char used inline as a column separator. EAW=Ambiguous, so terminals/viewers
	///   under Asian-width rules render it as 2 columns. Easy to hit because we use it 3× per scenario
	///   row in analyze-position panels — three 1-column slips = 3 cols of right-border drift.
	/// Other Unicode we emit (Greek letters, `→`, `←`, `—`, `•`) measures and renders as width 1 in
	/// every common monospace font, so it's preserved. Each substitution is one code point for one,
	/// so the surrounding panel widths Spectre already laid out stay correct.</summary>
	private static string NormalizeAsciiSafe(string text)
	{
		if (string.IsNullOrEmpty(text)) return text;
		var sb = new System.Text.StringBuilder(text.Length);
		foreach (var ch in text)
		{
			sb.Append(ch switch
			{
				'★' => '*',
				'⚠' => '!',
				'│' => '|',
				_ => ch,
			});
		}
		return sb.ToString();
	}
}

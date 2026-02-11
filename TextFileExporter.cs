using Spectre.Console;
using System.Text.RegularExpressions;

namespace WebullAnalytics;

public static class TextFileExporter
{
    private const string LegPrefix = "    L- ";

    public static void ExportToTextFile(List<ReportRow> rows, List<PositionRow> positions, decimal running, decimal initialAmount, string outputPath)
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
        console.Write(TableBuilder.BuildReportTable(rows, LegPrefix, initialAmount, TableBorder.Ascii));
        console.WriteLine();

        // Render positions table
        if (positions.Count > 0)
        {
            console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix, TableBorder.Ascii));
            console.WriteLine();
        }
        else
        {
            console.WriteLine("No open positions.");
        }

        // Total fees
        var totalFees = rows.Where(r => !r.IsStrategyLeg).Sum(r => r.Fees);
        console.WriteLine($"Total fees: {totalFees:0.00}");

        // Final P&L
        console.Write("Final realized P&L: ");
        console.Write(Formatters.FormatPnL(running));
        console.WriteLine();

        // Final amount
        console.Write("Final amount: ");
        console.Write(Formatters.FormatMoney(initialAmount + running, initialAmount));
        console.WriteLine();

        // Get the rendered output and strip ANSI codes
        var output = stringWriter.ToString();
        var cleanOutput = StripAnsiCodes(output);

        // Write to file
        File.WriteAllText(outputPath, cleanOutput);
        Console.WriteLine($"Text report exported to: {outputPath}");
    }

    private static string StripAnsiCodes(string text)
    {
        // Remove ANSI escape sequences
        return Regex.Replace(text, @"\x1B\[[0-9;]*[a-zA-Z]", string.Empty);
    }
}

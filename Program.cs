using System;
using System.Globalization;
using System.IO;

namespace WebullAnalytics;

/// <summary>
/// Entry point for WebullAnalytics CLI.
/// Generates realized P&L reports from Webull CSV order exports.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        var options = ParseArguments(args);

        if (options == null)
            return 1;

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        return Execute(options);
    }

    /// <summary>
    /// Command-line options for the application.
    /// </summary>
    private record Options(
        string DataDir,
        DateTime SinceDate,
        string OutputFormat,
        string? ExcelPath,
        string? TextPath,
        bool ShowHelp = false
    );

    /// <summary>
    /// Parses command-line arguments into an Options record.
    /// Returns null if there was a parsing error.
    /// </summary>
    private static Options? ParseArguments(string[] args)
    {
        var dataDir = "data";
        var sinceDate = DateTime.MinValue;
        var outputFormat = "console";
        string? excelPath = null;
        string? textPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;

                case "--since" when i + 1 < args.Length:
                    if (!DateTime.TryParseExact(args[++i], "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out sinceDate))
                    {
                        Console.WriteLine("Error: --since must be in YYYY-MM-DD format");
                        return null;
                    }
                    break;

                case "--output" when i + 1 < args.Length:
                    outputFormat = args[++i].ToLowerInvariant();
                    if (outputFormat is not ("console" or "excel" or "text"))
                    {
                        Console.WriteLine("Error: --output must be 'console', 'excel', or 'text'");
                        return null;
                    }
                    break;

                case "--excel-path" when i + 1 < args.Length:
                    excelPath = args[++i];
                    break;

                case "--text-path" when i + 1 < args.Length:
                    textPath = args[++i];
                    break;

                case "--help" or "-h":
                    return new Options(dataDir, sinceDate, outputFormat, excelPath, textPath, ShowHelp: true);
            }
        }

        return new Options(dataDir, sinceDate, outputFormat, excelPath, textPath);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            WebullAnalytics - Generate a realized P&L report from Webull CSV order exports

            Usage: WebullAnalytics [options]

            Options:
              --data-dir <path>    Directory containing CSV order exports (default: data)
              --since <date>       Include only trades on or after this date in YYYY-MM-DD format (default: all trades)
              --output <format>    Output format: 'console', 'excel', or 'text' (default: console)
              --excel-path <path>  Path for Excel output file (default: WebullAnalytics_YYYYMMDD.xlsx)
              --text-path <path>   Path for text output file (default: WebullAnalytics_YYYYMMDD.txt)
              --help, -h           Show this help message
            """);
    }

    private static int Execute(Options options)
    {
        if (!Directory.Exists(options.DataDir))
        {
            Console.WriteLine($"Error: Data directory '{options.DataDir}' does not exist.");
            return 1;
        }

        var trades = PositionTracker.LoadTrades(options.DataDir);

        if (trades.Count == 0)
        {
            Console.WriteLine("No trades found.");
            return 0;
        }

        var (rows, positions, running) = PositionTracker.ComputeReport(trades, options.SinceDate);
        var tradeIndex = PositionTracker.BuildTradeIndex(trades);
        var positionRows = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

        var dateStr = DateTime.Now.ToString("yyyyMMdd");

        switch (options.OutputFormat)
        {
            case "excel":
                var excelPath = options.ExcelPath ?? $"WebullAnalytics_{dateStr}.xlsx";
                ExcelExporter.ExportToExcel(rows, positionRows, trades, running, excelPath);
                break;

            case "text":
                var textPath = options.TextPath ?? $"WebullAnalytics_{dateStr}.txt";
                TextFileExporter.ExportToTextFile(rows, positionRows, running, textPath);
                break;

            default:
                TableRenderer.RenderReport(rows, positionRows, running);
                break;
        }

        return 0;
    }
}

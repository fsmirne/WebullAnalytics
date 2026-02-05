using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WebullAnalytics;

class Program
{
    static int Main(string[] args)
    {
        // Parse command-line arguments
        var dataDir = "data";
        var sinceDate = DateTime.MinValue;
        var outputFormat = "console";
        string? excelPath = null;
        string? textPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                dataDir = args[++i];
            }
            else if (args[i] == "--since" && i + 1 < args.Length)
            {
                if (DateTime.TryParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    sinceDate = parsed;
                }
                else
                {
                    Console.WriteLine("Error: --since must be in YYYY-MM-DD format");
                    return 1;
                }
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputFormat = args[++i].ToLower();
                if (outputFormat != "console" && outputFormat != "excel" && outputFormat != "text")
                {
                    Console.WriteLine("Error: --output must be 'console', 'excel', or 'text'");
                    return 1;
                }
            }
            else if (args[i] == "--excel-path" && i + 1 < args.Length)
            {
                excelPath = args[++i];
            }
            else if (args[i] == "--text-path" && i + 1 < args.Length)
            {
                textPath = args[++i];
            }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                PrintHelp();
                return 0;
            }
        }

        return Execute(dataDir, sinceDate, outputFormat, excelPath, textPath);
    }

    static void PrintHelp()
    {
        Console.WriteLine("WebullAnalytics - Generate a realized P&L report from Webull CSV order exports");
        Console.WriteLine();
        Console.WriteLine("Usage: WebullAnalytics [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --data-dir <path>    Directory containing CSV order exports (default: data)");
        Console.WriteLine("  --since <date>       Include only trades on or after this date in YYYY-MM-DD format (default: all trades)");
        Console.WriteLine("  --output <format>    Output format: 'console', 'excel', or 'text' (default: console)");
        Console.WriteLine("  --excel-path <path>  Path for Excel output file (default: WebullAnalytics_YYYYMMDD.xlsx)");
        Console.WriteLine("  --text-path <path>   Path for text output file (default: WebullAnalytics_YYYYMMDD.txt)");
        Console.WriteLine("  --help, -h           Show this help message");
    }

    static int Execute(string dataDirPath, DateTime sinceDate, string outputFormat, string? excelPath, string? textPath)
    {
        if (!Directory.Exists(dataDirPath))
        {
            Console.WriteLine($"Error: Data directory '{dataDirPath}' does not exist.");
            return 1;
        }

        var trades = PositionTracker.LoadTrades(dataDirPath);

        if (trades.Count == 0)
        {
            Console.WriteLine("No trades found.");
            return 0;
        }

        var (rows, positions, running) = PositionTracker.ComputeReport(trades, sinceDate);
        var tradeIndex = PositionTracker.BuildTradeIndex(trades);
        var positionRows = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

        if (outputFormat == "excel")
        {
            // Generate default filename if not provided
            if (string.IsNullOrEmpty(excelPath))
            {
                var dateStr = DateTime.Now.ToString("yyyyMMdd");
                excelPath = $"WebullAnalytics_{dateStr}.xlsx";
            }

            ExcelExporter.ExportToExcel(rows, positionRows, trades, running, excelPath);
        }
        else if (outputFormat == "text")
        {
            // Generate default filename if not provided
            if (string.IsNullOrEmpty(textPath))
            {
                var dateStr = DateTime.Now.ToString("yyyyMMdd");
                textPath = $"WebullAnalytics_{dateStr}.txt";
            }

            TextFileExporter.ExportToTextFile(rows, positionRows, running, textPath);
        }
        else
        {
            TableRenderer.RenderReport(rows, positionRows, running);
        }

        return 0;
    }
}

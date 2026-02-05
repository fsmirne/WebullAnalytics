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
        var asOf = DateTime.Today;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                dataDir = args[++i];
            }
            else if (args[i] == "--as-of" && i + 1 < args.Length)
            {
                if (DateTime.TryParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    asOf = parsed;
                }
                else
                {
                    Console.WriteLine("Error: --as-of must be in YYYY-MM-DD format");
                    return 1;
                }
            }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                PrintHelp();
                return 0;
            }
        }

        return Execute(dataDir, asOf);
    }

    static void PrintHelp()
    {
        Console.WriteLine("WebullAnalytics - Generate a realized P&L report from Webull CSV order exports");
        Console.WriteLine();
        Console.WriteLine("Usage: WebullAnalytics [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --data-dir <path>    Directory containing CSV order exports (default: data)");
        Console.WriteLine("  --as-of <date>       Include expirations on or before this date in YYYY-MM-DD format (default: today)");
        Console.WriteLine("  --help, -h           Show this help message");
    }

    static int Execute(string dataDirPath, DateTime asOf)
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

        var (rows, positions, running) = PositionTracker.ComputeReport(trades, asOf);
        var tradeIndex = PositionTracker.BuildTradeIndex(trades);
        var positionRows = PositionTracker.BuildPositionRows(positions, tradeIndex);

        TableRenderer.RenderReport(rows, positionRows, running);
        return 0;
    }
}

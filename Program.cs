using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;

namespace WebullAnalytics;

/// <summary>
/// Entry point for WebullAnalytics CLI.
/// Generates realized P&L reports from Webull CSV order exports.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        var app = new CommandApp<ReportCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("WebullAnalytics");
        });
        return app.Run(args);
    }
}

/// <summary>
/// Command-line settings for the report command.
/// </summary>
class ReportSettings : CommandSettings
{
    [Description("Directory containing CSV order exports (default: data)")]
    [CommandOption("--data-dir")]
    public string? DataDir { get; set; }

    [Description("Path to a single CSV order export file")]
    [CommandOption("--data-file")]
    public string? DataFile { get; set; }

    [Description("Include only trades on or after this date (YYYY-MM-DD format)")]
    [CommandOption("--since")]
    public string? Since { get; set; }

    [Description("Output format: 'console', 'excel', or 'text'")]
    [CommandOption("--output")]
    [DefaultValue("console")]
    public string OutputFormat { get; set; } = "console";

    [Description("Path for Excel output file")]
    [CommandOption("--excel-path")]
    public string? ExcelPath { get; set; }

    [Description("Path for text output file")]
    [CommandOption("--text-path")]
    public string? TextPath { get; set; }

    [Description("Initial portfolio amount in dollars (default: 0)")]
    [CommandOption("--initial-amount")]
    [DefaultValue(0)]
    public decimal InitialAmount { get; set; } = 0m;

    public DateTime SinceDate => Since != null ? DateTime.ParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.MinValue;

    public override ValidationResult Validate()
    {
        if (DataDir != null && DataFile != null)
        {
            return ValidationResult.Error("--data-dir and --data-file are mutually exclusive");
        }

        if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return ValidationResult.Error("--since must be in YYYY-MM-DD format");
        }

        var format = OutputFormat.ToLowerInvariant();
        if (format is not ("console" or "excel" or "text"))
        {
            return ValidationResult.Error("--output must be 'console', 'excel', or 'text'");
        }

        return ValidationResult.Success();
    }
}

/// <summary>
/// Default command that generates the P&L report.
/// </summary>
class ReportCommand : Command<ReportSettings>
{
    public override int Execute(CommandContext context, ReportSettings settings, CancellationToken cancellation)
    {
        List<Trade> trades;

        if (settings.DataFile != null)
        {
            if (!File.Exists(settings.DataFile))
            {
                Console.WriteLine($"Error: Data file '{settings.DataFile}' does not exist.");
                return 1;
            }

            trades = PositionTracker.LoadTradesFromFile(settings.DataFile);
        }
        else
        {
            var dataDir = settings.DataDir ?? "data";
            if (!Directory.Exists(dataDir))
            {
                Console.WriteLine($"Error: Data directory '{dataDir}' does not exist.");
                return 1;
            }

            trades = PositionTracker.LoadTrades(dataDir);
        }

        if (trades.Count == 0)
        {
            Console.WriteLine("No trades found.");
            return 0;
        }

        var initialAmount = settings.InitialAmount;
        var (rows, positions, running) = PositionTracker.ComputeReport(trades, settings.SinceDate, initialAmount);
        var tradeIndex = PositionTracker.BuildTradeIndex(trades);
        var positionRows = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

        var dateStr = DateTime.Now.ToString("yyyyMMdd");

        switch (settings.OutputFormat.ToLowerInvariant())
        {
            case "excel":
                var excelPath = settings.ExcelPath ?? $"WebullAnalytics_{dateStr}.xlsx";
                ExcelExporter.ExportToExcel(rows, positionRows, trades, running, initialAmount, excelPath);
                break;

            case "text":
                var textPath = settings.TextPath ?? $"WebullAnalytics_{dateStr}.txt";
                TextFileExporter.ExportToTextFile(rows, positionRows, running, initialAmount, textPath);
                break;

            default:
                TableRenderer.RenderReport(rows, positionRows, running, initialAmount);
                break;
        }

        return 0;
    }
}

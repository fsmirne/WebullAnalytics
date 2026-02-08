using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace WebullAnalytics;

public static class TextFileExporter
{
    public static void ExportToTextFile(List<ReportRow> rows, List<PositionRow> positions, decimal running, string outputPath)
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
        var reportTable = BuildReportTable(rows);
        console.Write(reportTable);
        console.WriteLine();

        // Render positions table
        var positionsTable = BuildPositionsTable(positions);
        if (positionsTable != null)
        {
            console.Write(positionsTable);
            console.WriteLine();
        }
        else
        {
            console.WriteLine("No open positions.");
        }

        // Final P&L
        console.Write("Final realized P&L: ");
        console.Write(Formatters.FormatPnL(running));
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

    private static Table BuildReportTable(List<ReportRow> rows)
    {
        var table = new Table
        {
            Title = new TableTitle("Realized P&L by Transaction"),
            Border = TableBorder.Ascii  // Use ASCII characters for better compatibility
        };

        table.AddColumn(new TableColumn("Date").LeftAligned());
        table.AddColumn(new TableColumn("Instrument").LeftAligned());
        table.AddColumn(new TableColumn("Asset").LeftAligned());
        table.AddColumn(new TableColumn("Option").LeftAligned());
        table.AddColumn(new TableColumn("Side").LeftAligned());
        table.AddColumn(new TableColumn("Qty").RightAligned());
        table.AddColumn(new TableColumn("Price").RightAligned());
        table.AddColumn(new TableColumn("Closed Qty").RightAligned());
        table.AddColumn(new TableColumn("Realized P&L").RightAligned());
        table.AddColumn(new TableColumn("Running P&L").RightAligned());

        foreach (var row in rows)
        {
            if (row.IsStrategyLeg)
            {
                // This is a strategy leg - show with indentation and no P&L
                table.AddRow(
                    new Text(""),  // Empty timestamp for legs
                    new Text($"    L- {row.Instrument}"),  // Indented instrument with ASCII chars
                    new Text(row.Asset),
                    new Text(row.OptionKind),
                    new Text(row.Side),
                    new Text(Formatters.FormatQty(row.Qty)),
                    new Text(Formatters.FormatPrice(row.Price, row.Asset)),
                    new Text("-"),  // No closed qty for legs
                    new Text("-"),  // No realized P&L for legs
                    new Text("")   // No running P&L for legs
                );
            }
            else
            {
                // Regular trade or strategy parent
                table.AddRow(
                    new Text(row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                    new Text(row.Instrument),
                    new Text(row.Asset),
                    new Text(row.OptionKind),
                    new Text(row.Side),
                    new Text(Formatters.FormatQty(row.Qty)),
                    new Text(Formatters.FormatPrice(row.Price, row.Asset)),
                    new Text(Formatters.FormatQty(row.ClosedQty)),
                    Formatters.FormatPnL(row.Realized),
                    Formatters.FormatPnL(row.Running)
                );
            }
        }

        return table;
    }

    private static Table? BuildPositionsTable(List<PositionRow> rows)
    {
        if (rows.Count == 0)
            return null;

        var table = new Table
        {
            Title = new TableTitle("Open Positions"),
            Border = TableBorder.Ascii  // Use ASCII characters for better compatibility
        };

        table.AddColumn(new TableColumn("Instrument").LeftAligned());
        table.AddColumn(new TableColumn("Asset").LeftAligned());
        table.AddColumn(new TableColumn("Option").LeftAligned());
        table.AddColumn(new TableColumn("Side").LeftAligned());
        table.AddColumn(new TableColumn("Qty").RightAligned());
        table.AddColumn(new TableColumn("Init Price").RightAligned());
        table.AddColumn(new TableColumn("Adj Price").RightAligned());
        table.AddColumn(new TableColumn("Expiry").RightAligned());

        foreach (var row in rows)
        {
            // Format prices - show both initial and adjusted if available
            var initPrice = row.InitialAvgPrice.HasValue ? Formatters.FormatPrice(row.InitialAvgPrice.Value, row.Asset) : Formatters.FormatPrice(row.AvgPrice, row.Asset);

            var adjPrice = row.AdjustedAvgPrice.HasValue ? Formatters.FormatPrice(row.AdjustedAvgPrice.Value, row.Asset) : "-";

            if (row.IsStrategyLeg)
            {
                // This is a strategy leg - show with indentation
                table.AddRow(
                    new Text($"    L- {row.Instrument}"),  // ASCII indentation
                    new Text(row.Asset),
                    new Text(row.OptionKind),
                    new Text(row.Side),
                    new Text(Formatters.FormatQty(row.Qty)),
                    new Text(initPrice),
                    new Text(adjPrice),
                    new Text(Formatters.FormatExpiry(row.Expiry))
                );
            }
            else
            {
                // Regular position or strategy parent
                table.AddRow(
                    new Text(row.Instrument),
                    new Text(row.Asset),
                    new Text(row.OptionKind),
                    new Text(row.Side),
                    new Text(Formatters.FormatQty(row.Qty)),
                    new Text(initPrice),
                    new Text(adjPrice),
                    new Text(Formatters.FormatExpiry(row.Expiry))
                );
            }
        }

        return table;
    }
}

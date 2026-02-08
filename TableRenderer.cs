using System.Collections.Generic;
using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
    public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, decimal running)
    {
        var console = AnsiConsole.Console;

        console.Write(BuildReportTable(rows));
        console.WriteLine();

        if (positions.Count > 0)
        {
            console.Write(BuildPositionsTable(positions));
            console.WriteLine();
        }
        else
        {
            console.WriteLine("No open positions.");
        }

        console.Write("Final realized P&L: ");
        console.Write(Formatters.FormatPnL(running));
        console.WriteLine();
    }

    private static Table BuildReportTable(List<ReportRow> rows)
    {
        var table = new Table { Title = new TableTitle("Realized P&L by Transaction") };

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
                AddStrategyLegRow(table, row);
            }
            else
            {
                AddTradeRow(table, row);
            }
        }

        return table;
    }

    private static void AddStrategyLegRow(Table table, ReportRow row)
    {
        table.AddRow(
            new Text(""),
            new Text($"  └─ {row.Instrument}"),
            new Text(row.Asset),
            new Text(row.OptionKind),
            new Text(row.Side),
            new Text(Formatters.FormatQty(row.Qty)),
            new Text(Formatters.FormatPrice(row.Price, row.Asset)),
            new Text("-"),
            new Text("-"),
            new Text("")
        );
    }

    private static void AddTradeRow(Table table, ReportRow row)
    {
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

    private static Table BuildPositionsTable(List<PositionRow> rows)
    {
        var table = new Table { Title = new TableTitle("Open Positions") };

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
            var initPrice = Formatters.FormatPrice(row.InitialAvgPrice ?? row.AvgPrice, row.Asset);
            var adjPrice = row.AdjustedAvgPrice.HasValue ? Formatters.FormatPrice(row.AdjustedAvgPrice.Value, row.Asset) : "-";

            var instrument = row.IsStrategyLeg ? $"  └─ {row.Instrument}" : row.Instrument;

            table.AddRow(
                new Text(instrument),
                new Text(row.Asset),
                new Text(row.OptionKind),
                new Text(row.Side),
                new Text(Formatters.FormatQty(row.Qty)),
                new Text(initPrice),
                new Text(adjPrice),
                new Text(Formatters.FormatExpiry(row.Expiry))
            );
        }

        return table;
    }
}

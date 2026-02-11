using System.Collections.Generic;
using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Builds Spectre.Console tables for report and position data.
/// Shared by TableRenderer (console output) and TextFileExporter (text file output).
/// </summary>
public static class TableBuilder
{
    private static string FormatFee(decimal fees) => fees > 0 ? fees.ToString("0.00") : "-";

    public static Table BuildReportTable(List<ReportRow> rows, string legPrefix, decimal initialAmount = 0m, TableBorder? border = null)
    {
        var table = new Table { Title = new TableTitle("Realized P&L by Transaction") };
        if (border != null)
            table.Border = border;

        table.Expand = true;

        table.AddColumn(new TableColumn("Date").LeftAligned());
        table.AddColumn(new TableColumn("Instrument").LeftAligned());
        table.AddColumn(new TableColumn("Asset").LeftAligned());
        table.AddColumn(new TableColumn("Option").LeftAligned());
        table.AddColumn(new TableColumn("Side").LeftAligned());
        table.AddColumn(new TableColumn("Qty").RightAligned());
        table.AddColumn(new TableColumn("Price").RightAligned());
        table.AddColumn(new TableColumn("Fees").RightAligned());
        table.AddColumn(new TableColumn("Closed Qty").RightAligned());
        table.AddColumn(new TableColumn("Realized P&L").RightAligned());
        table.AddColumn(new TableColumn("Running P&L").RightAligned());
        table.AddColumn(new TableColumn("Cash").RightAligned());
        table.AddColumn(new TableColumn("Total").RightAligned());

        foreach (var row in rows)
        {
            if (row.IsStrategyLeg)
            {
                table.AddRow(
                    new Text(""),
                    new Text($"{legPrefix}{row.Instrument}"),
                    new Text(row.Asset),
                    new Text(row.OptionKind),
                    new Text(row.Side),
                    new Text(Formatters.FormatQty(row.Qty)),
                    new Text(Formatters.FormatPrice(row.Price, row.Asset)),
                    new Text(FormatFee(row.Fees)),
                    new Text("-"),
                    new Text("-"),
                    new Text(""),
                    new Text(""),
                    new Text("")
                );
            }
            else
            {
                table.AddRow(
                    new Text(row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                    new Text(row.Instrument),
                    new Text(row.Asset),
                    new Text(row.OptionKind),
                    new Text(row.Side),
                    new Text(Formatters.FormatQty(row.Qty)),
                    new Text(Formatters.FormatPrice(row.Price, row.Asset)),
                    new Text(FormatFee(row.Fees)),
                    new Text(Formatters.FormatQty(row.ClosedQty)),
                    Formatters.FormatPnL(row.Realized),
                    Formatters.FormatPnL(row.Running),
                    Formatters.FormatMoney(row.Cash, 0m),
                    Formatters.FormatMoney(row.Total, initialAmount)
                );
            }
        }

        return table;
    }

    public static Table BuildPositionsTable(List<PositionRow> rows, string legPrefix, TableBorder? border = null)
    {
        var table = new Table { Title = new TableTitle("Open Positions") };
        if (border != null)
            table.Border = border;

        table.Expand = true;

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
            var instrument = row.IsStrategyLeg ? $"{legPrefix}{row.Instrument}" : row.Instrument;

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

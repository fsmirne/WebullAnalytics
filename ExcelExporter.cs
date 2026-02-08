using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;

namespace WebullAnalytics;

public static class ExcelExporter
{
    public static void ExportToExcel(List<ReportRow> reportRows, List<PositionRow> positionRows, List<Trade> allTrades, decimal finalPnL, string outputPath)
    {
        // EPPlus requires a license context
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();

        // Create worksheets
        var transactionSheet = package.Workbook.Worksheets.Add("Transactions");
        var positionsSheet = package.Workbook.Worksheets.Add("Open Positions");
        var dailyPnLSheet = package.Workbook.Worksheets.Add("Daily P&L");

        // Export transaction report
        ExportTransactions(transactionSheet, reportRows, finalPnL);

        // Export open positions
        ExportPositions(positionsSheet, positionRows);

        // Export daily P&L with chart
        ExportDailyPnL(dailyPnLSheet, reportRows);

        // Save the file
        var file = new FileInfo(outputPath);
        package.SaveAs(file);

        Console.WriteLine($"Excel report exported to: {outputPath}");
    }

    private static void ExportTransactions(ExcelWorksheet sheet, List<ReportRow> rows, decimal finalPnL)
    {
        // Headers
        sheet.Cells[1, 1].Value = "Date";
        sheet.Cells[1, 2].Value = "Instrument";
        sheet.Cells[1, 3].Value = "Asset";
        sheet.Cells[1, 4].Value = "Option";
        sheet.Cells[1, 5].Value = "Side";
        sheet.Cells[1, 6].Value = "Qty";
        sheet.Cells[1, 7].Value = "Price";
        sheet.Cells[1, 8].Value = "Closed Qty";
        sheet.Cells[1, 9].Value = "Realized P&L";
        sheet.Cells[1, 10].Value = "Running P&L";

        // Format headers
        using (var range = sheet.Cells[1, 1, 1, 10])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
        }

        // Data rows (only non-leg rows)
        int row = 2;
        foreach (var reportRow in rows.Where(r => !r.IsStrategyLeg))
        {
            sheet.Cells[row, 1].Value = reportRow.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            sheet.Cells[row, 2].Value = reportRow.Instrument;
            sheet.Cells[row, 3].Value = reportRow.Asset;
            sheet.Cells[row, 4].Value = reportRow.OptionKind;
            sheet.Cells[row, 5].Value = reportRow.Side;
            sheet.Cells[row, 6].Value = (double)reportRow.Qty;
            sheet.Cells[row, 7].Value = (double)reportRow.Price;
            sheet.Cells[row, 8].Value = (double)reportRow.ClosedQty;
            sheet.Cells[row, 9].Value = (double)reportRow.Realized;
            sheet.Cells[row, 10].Value = (double)reportRow.Running;

            // Format currency columns
            sheet.Cells[row, 7].Style.Numberformat.Format = "#,##0.00";
            sheet.Cells[row, 9].Style.Numberformat.Format = "#,##0.00";
            sheet.Cells[row, 10].Style.Numberformat.Format = "#,##0.00";

            // Color code P&L
            if (reportRow.Realized > 0)
                sheet.Cells[row, 9].Style.Font.Color.SetColor(System.Drawing.Color.Green);
            else if (reportRow.Realized < 0)
                sheet.Cells[row, 9].Style.Font.Color.SetColor(System.Drawing.Color.Red);

            if (reportRow.Running > 0)
                sheet.Cells[row, 10].Style.Font.Color.SetColor(System.Drawing.Color.Green);
            else if (reportRow.Running < 0)
                sheet.Cells[row, 10].Style.Font.Color.SetColor(System.Drawing.Color.Red);

            row++;
        }

        // Add final P&L
        row++;
        sheet.Cells[row, 9].Value = "Final P&L:";
        sheet.Cells[row, 9].Style.Font.Bold = true;
        sheet.Cells[row, 10].Value = (double)finalPnL;
        sheet.Cells[row, 10].Style.Numberformat.Format = "#,##0.00";
        sheet.Cells[row, 10].Style.Font.Bold = true;
        if (finalPnL > 0)
            sheet.Cells[row, 10].Style.Font.Color.SetColor(System.Drawing.Color.Green);
        else if (finalPnL < 0)
            sheet.Cells[row, 10].Style.Font.Color.SetColor(System.Drawing.Color.Red);

        // Auto-fit columns
        sheet.Cells.AutoFitColumns();
    }

    private static void ExportPositions(ExcelWorksheet sheet, List<PositionRow> rows)
    {
        // Headers
        sheet.Cells[1, 1].Value = "Instrument";
        sheet.Cells[1, 2].Value = "Asset";
        sheet.Cells[1, 3].Value = "Option";
        sheet.Cells[1, 4].Value = "Side";
        sheet.Cells[1, 5].Value = "Qty";
        sheet.Cells[1, 6].Value = "Initial Price";
        sheet.Cells[1, 7].Value = "Adjusted Price";
        sheet.Cells[1, 8].Value = "Expiry";

        // Format headers
        using (var range = sheet.Cells[1, 1, 1, 8])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
        }

        // Data rows
        int row = 2;
        foreach (var posRow in rows)
        {
            var indent = posRow.IsStrategyLeg ? "  └─ " : "";
            sheet.Cells[row, 1].Value = indent + posRow.Instrument;
            sheet.Cells[row, 2].Value = posRow.Asset;
            sheet.Cells[row, 3].Value = posRow.OptionKind;
            sheet.Cells[row, 4].Value = posRow.Side;
            sheet.Cells[row, 5].Value = (double)posRow.Qty;

            var initPrice = posRow.InitialAvgPrice ?? posRow.AvgPrice;
            sheet.Cells[row, 6].Value = (double)initPrice;
            sheet.Cells[row, 6].Style.Numberformat.Format = "#,##0.00";

            if (posRow.AdjustedAvgPrice.HasValue)
            {
                sheet.Cells[row, 7].Value = (double)posRow.AdjustedAvgPrice.Value;
                sheet.Cells[row, 7].Style.Numberformat.Format = "#,##0.00";
            }
            else
            {
                sheet.Cells[row, 7].Value = "-";
            }

            sheet.Cells[row, 8].Value = posRow.Expiry.HasValue ? posRow.Expiry.Value.ToString("dd MMM yyyy") : "-";

            row++;
        }

        // Auto-fit columns
        sheet.Cells.AutoFitColumns();
    }

    private static void ExportDailyPnL(ExcelWorksheet sheet, List<ReportRow> rows)
    {
        // Calculate daily P&L (group by date)
        var dailyData = rows.Where(r => !r.IsStrategyLeg && r.Realized != 0).GroupBy(r => r.Timestamp.Date).Select(g => new { Date = g.Key, DailyPnL = g.Sum(r => r.Realized), EndOfDayRunning = g.Last().Running }).OrderBy(d => d.Date).ToList();

        // Headers
        sheet.Cells[1, 1].Value = "Date";
        sheet.Cells[1, 2].Value = "Daily P&L";
        sheet.Cells[1, 3].Value = "Cumulative P&L";

        // Format headers
        using (var range = sheet.Cells[1, 1, 1, 3])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
        }

        // Data rows
        int row = 2;
        foreach (var day in dailyData)
        {
            sheet.Cells[row, 1].Value = day.Date;
            sheet.Cells[row, 1].Style.Numberformat.Format = "yyyy-mm-dd";

            sheet.Cells[row, 2].Value = (double)day.DailyPnL;
            sheet.Cells[row, 2].Style.Numberformat.Format = "#,##0.00";

            sheet.Cells[row, 3].Value = (double)day.EndOfDayRunning;
            sheet.Cells[row, 3].Style.Numberformat.Format = "#,##0.00";

            // Color code P&L
            if (day.DailyPnL > 0)
                sheet.Cells[row, 2].Style.Font.Color.SetColor(System.Drawing.Color.Green);
            else if (day.DailyPnL < 0)
                sheet.Cells[row, 2].Style.Font.Color.SetColor(System.Drawing.Color.Red);

            if (day.EndOfDayRunning > 0)
                sheet.Cells[row, 3].Style.Font.Color.SetColor(System.Drawing.Color.Green);
            else if (day.EndOfDayRunning < 0)
                sheet.Cells[row, 3].Style.Font.Color.SetColor(System.Drawing.Color.Red);

            row++;
        }

        // Auto-fit columns
        sheet.Cells.AutoFitColumns();

        // Create chart
        if (dailyData.Count > 0)
        {
            var chart = sheet.Drawings.AddLineChart("Daily P&L Chart", eLineChartType.Line);
            chart.Title.Text = "Cumulative P&L Over Time";
            chart.SetPosition(1, 0, 4, 0);
            chart.SetSize(800, 400);

            // Add series for cumulative P&L (values = col 3, x-axis = col 1)
            var series = chart.Series.Add(sheet.Cells[2, 3, row - 1, 3], sheet.Cells[2, 1, row - 1, 1]);

            series.Header = "Cumulative P&L";

            // Format chart
            chart.Legend.Position = eLegendPosition.Bottom;
            chart.XAxis.Title.Text = "Date";
            chart.YAxis.Title.Text = "P&L ($)";
            chart.YAxis.Format = "#,##0.00";
        }
    }
}

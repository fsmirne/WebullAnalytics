using System.Collections.Generic;
using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Renders P&L reports and position tables to the console using Spectre.Console.
/// </summary>
public static class TableRenderer
{
    private const string LegPrefix = "  └─ ";

    public static void RenderReport(List<ReportRow> rows, List<PositionRow> positions, decimal running)
    {
        var console = AnsiConsole.Console;

        console.Write(TableBuilder.BuildReportTable(rows, LegPrefix));
        console.WriteLine();

        if (positions.Count > 0)
        {
            console.Write(TableBuilder.BuildPositionsTable(positions, LegPrefix));
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
}

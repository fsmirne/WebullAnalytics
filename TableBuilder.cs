using Spectre.Console;
using Spectre.Console.Rendering;

namespace WebullAnalytics;

/// <summary>
/// Builds Spectre.Console tables for report and position data.
/// Shared by TableRenderer (console output) and TextFileExporter (text file output).
/// </summary>
public static class TableBuilder
{
	private static string FormatFee(decimal fees) => fees > 0 ? fees.ToString("0.00") : "-";

	public static Table BuildReportTable(List<ReportRow> rows, string legPrefix, decimal initialAmount = 0m, TableBorder? border = null, bool simplified = false)
	{
		var table = new Table { Title = new TableTitle("Realized P&L by Transaction") };
		if (border != null)
			table.Border = border;

		table.Expand = true;

		table.AddColumn(new TableColumn("Date").LeftAligned());
		table.AddColumn(new TableColumn("Instrument").LeftAligned());
		if (!simplified) table.AddColumn(new TableColumn("Asset").LeftAligned());
		if (!simplified) table.AddColumn(new TableColumn("Option").LeftAligned());
		table.AddColumn(new TableColumn("Side").LeftAligned());
		table.AddColumn(new TableColumn("Qty").RightAligned());
		table.AddColumn(new TableColumn("Price").RightAligned());
		table.AddColumn(new TableColumn("Fees").RightAligned());
		if (!simplified) table.AddColumn(new TableColumn("Closed Qty").RightAligned());
		table.AddColumn(new TableColumn("Realized P&L").RightAligned());
		if (!simplified) table.AddColumn(new TableColumn("Running P&L").RightAligned());
		table.AddColumn(new TableColumn("Cash").RightAligned());
		table.AddColumn(new TableColumn("Total").RightAligned());

		foreach (var row in rows)
		{
			if (row.IsStrategyLeg)
			{
				var cells = new List<IRenderable>
				{
					new Text(""),
					new Text($"{legPrefix}{row.Instrument}"),
				};
				if (!simplified) cells.Add(new Text(row.Asset.DisplayName()));
				if (!simplified) cells.Add(new Text(row.OptionKind));
				cells.Add(new Text(row.Side.ToString()));
				cells.Add(new Text(Formatters.FormatQty(row.Qty)));
				cells.Add(new Text(Formatters.FormatPrice(row.Price, row.Asset)));
				cells.Add(new Text(FormatFee(row.Fees)));
				if (!simplified) cells.Add(new Text("-"));
				cells.Add(new Text(""));
				if (!simplified) cells.Add(new Text(""));
				cells.Add(new Text(""));
				cells.Add(new Text(""));
				table.AddRow(cells.ToArray());
			}
			else
			{
				var cells = new List<IRenderable>
				{
					new Text(row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
					new Text(row.Instrument),
				};
				if (!simplified) cells.Add(new Text(row.Asset.DisplayName()));
				if (!simplified) cells.Add(new Text(row.OptionKind));
				cells.Add(new Text(row.Side.ToString()));
				cells.Add(new Text(Formatters.FormatQty(row.Qty)));
				cells.Add(new Text(Formatters.FormatPrice(row.Price, row.Asset)));
				cells.Add(new Text(FormatFee(row.Fees)));
				if (!simplified) cells.Add(new Text(Formatters.FormatQty(row.ClosedQty)));
				cells.Add(Formatters.FormatPnL(row.Realized));
				if (!simplified) cells.Add(Formatters.FormatPnL(row.Running));
				cells.Add(Formatters.FormatMoney(row.Cash, 0m));
				cells.Add(Formatters.FormatMoney(row.Total, initialAmount));
				table.AddRow(cells.ToArray());
			}
		}

		return table;
	}

	public static Table BuildPositionsTable(List<PositionRow> rows, string legPrefix, TableBorder? border = null, bool simplified = false)
	{
		var table = new Table { Title = new TableTitle("Open Positions") };
		if (border != null)
			table.Border = border;

		table.Expand = true;

		table.AddColumn(new TableColumn("Instrument").LeftAligned());
		if (!simplified) table.AddColumn(new TableColumn("Asset").LeftAligned());
		if (!simplified) table.AddColumn(new TableColumn("Option").LeftAligned());
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

			var cells = new List<IRenderable> { new Text(instrument) };
			if (!simplified) cells.Add(new Text(row.Asset.DisplayName()));
			if (!simplified) cells.Add(new Text(row.OptionKind));
			cells.Add(new Text(row.Side.ToString()));
			cells.Add(new Text(Formatters.FormatQty(row.Qty)));
			cells.Add(new Text(initPrice));
			cells.Add(new Text(adjPrice));
			cells.Add(new Text(Formatters.FormatExpiry(row.Expiry)));
			table.AddRow(cells.ToArray());
		}

		return table;
	}
}

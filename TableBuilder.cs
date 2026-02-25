using System.Globalization;
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

	public static Panel BuildBreakEvenPanel(BreakEvenResult result, BoxBorder? panelBorder = null, TableBorder? tableBorder = null, bool ascii = false, string displayMode = "pnl")
	{
		var dteText = result.DaysToExpiry.HasValue ? result.DaysToExpiry.Value.ToString() : (ascii ? "-" : "—");
		var sep = ascii ? "|" : "│";
		var legPrefix = ascii ? "  L- " : "  └─ ";

		var items = new List<IRenderable>
		{
			new Markup($"{Markup.Escape(result.Details)} {sep} DTE: {dteText}"),
		};

		if (result.Legs != null)
		{
			foreach (var leg in result.Legs)
				items.Add(new Markup($"{legPrefix}{Markup.Escape(leg)}"));
		}

		if (result.PriceLadder.Count > 0)
		{
			// Summary line with break-even, max profit/loss
			var beText = result.BreakEvens.Count > 0 ? string.Join(", ", result.BreakEvens.Select(be => $"${be.ToString("N2", CultureInfo.InvariantCulture)}")) : "N/A";
			var maxProfitText = result.MaxProfit.HasValue ? $"[green]${result.MaxProfit.Value.ToString("N2", CultureInfo.InvariantCulture)}[/]" : "Unlimited";
			var maxLossText = result.MaxLoss.HasValue ? $"[red]-${result.MaxLoss.Value.ToString("N2", CultureInfo.InvariantCulture)}[/]" : "Unlimited";
			items.Add(new Markup($"Break-even: {Markup.Escape(beText)} {sep} Max Profit: {maxProfitText} {sep} Max Loss: {maxLossText}"));

			if (result.EarlyExercise != null)
			{
				var ex = result.EarlyExercise;
				var direction = ex.IsCall ? "above" : "below";
				var transitionDate = DateTime.Today.AddDays(ex.TransitionDays).ToString("dd MMM yyyy");
				items.Add(new Markup($"[yellow]Early Exercise: {direction} ${ex.BoundaryNear.ToString("N2", CultureInfo.InvariantCulture)} until {Markup.Escape(transitionDate)}, then {direction} ${ex.BoundaryFar.ToString("N2", CultureInfo.InvariantCulture)}[/]"));
			}

			items.Add(new Text(""));

			if (result.Grid != null)
			{
				items.Add(BuildTimeDecayGridTable(result.Grid, result.BreakEvens, displayMode, tableBorder));
			}
			else
			{
				// 1D price ladder fallback
				var table = new Table();
				if (tableBorder != null) table.Border = tableBorder;
				table.AddColumn(new TableColumn("Price").RightAligned());
				table.AddColumn(new TableColumn("Value").RightAligned());
				table.AddColumn(new TableColumn("P&L").RightAligned());

				foreach (var point in result.PriceLadder)
				{
					var isBreakEven = result.BreakEvens.Any(be => Math.Abs(point.UnderlyingPrice - be) < 0.005m);
					var pricePrefix = isBreakEven ? "*" : " ";
					var priceText = $"{pricePrefix}${point.UnderlyingPrice.ToString("N2", CultureInfo.InvariantCulture)}";
					var valueText = point.ContractValue.HasValue ? $"${point.ContractValue.Value.ToString("N2", CultureInfo.InvariantCulture)}" : "-";
					var pnlColor = point.PnL >= 0 ? "green" : "red";
					var pnlText = FormatLadderPnL(point.PnL);
					table.AddRow(new Text(priceText), new Text(valueText), new Markup($"[{pnlColor}]{Markup.Escape(pnlText)}[/]"));
				}

				items.Add(table);
			}
		}

		if (result.Note != null)
			items.Add(new Markup($"[italic]{Markup.Escape(result.Note)}[/]"));

		var panel = new Panel(new Rows(items)) { Header = new PanelHeader(result.Title), Expand = true };
		if (panelBorder != null) panel.Border = panelBorder;
		return panel;
	}

	private static Table BuildTimeDecayGridTable(TimeDecayGrid grid, List<decimal> breakEvens, string displayMode, TableBorder? tableBorder)
	{
		var showPnL = displayMode == "pnl";
		var table = new Table();
		if (tableBorder != null) table.Border = tableBorder;

		table.AddColumn(new TableColumn("Price").RightAligned());
		foreach (var date in grid.DateColumns)
		{
			var isExpiryClose = date == grid.DateColumns[^1] && date.TimeOfDay != TimeSpan.Zero;
			var label = isExpiryClose ? "At Exp" : date.ToString("dd MMM", CultureInfo.InvariantCulture);
			table.AddColumn(new TableColumn(label).RightAligned());
		}

		for (int pi = 0; pi < grid.PriceRows.Count; pi++)
		{
			var price = grid.PriceRows[pi];
			var isBreakEven = breakEvens.Any(be => Math.Abs(price - be) < 0.005m);
			var pricePrefix = isBreakEven ? "*" : " ";
			var priceText = $"{pricePrefix}${price.ToString("N2", CultureInfo.InvariantCulture)}";

			var cells = new List<IRenderable> { new Text(priceText) };
			for (int di = 0; di < grid.DateColumns.Count; di++)
			{
				var cellValue = showPnL ? grid.PnLs[pi, di] : grid.Values[pi, di];
				string cellText;
				string color;

				if (showPnL)
				{
					cellText = FormatLadderPnL(cellValue);
					color = cellValue >= 0 ? "green" : "red";
				}
				else
				{
					cellText = $"${cellValue.ToString("N2", CultureInfo.InvariantCulture)}";
					color = grid.PnLs[pi, di] >= 0 ? "green" : "red";
				}

				cells.Add(new Markup($"[{color}]{Markup.Escape(cellText)}[/]"));
			}
			table.AddRow(cells.ToArray());
		}

		return table;
	}

	private static string FormatLadderPnL(decimal value)
	{
		if (value == 0) return "$0.00";
		return value > 0 ? $"+${value.ToString("N2", CultureInfo.InvariantCulture)}" : $"-${Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture)}";
	}
}

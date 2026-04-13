using System.Globalization;
using System.Text.RegularExpressions;
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
				items.Add(BuildLegMarkup(legPrefix, leg, ascii));
		}

		if (result.PriceLadder.Count > 0)
		{
			// Summary line with current price, break-even, max profit/loss
			string spotText;
			if (result.UnderlyingPrice.HasValue && result.OriginalUnderlyingPrice.HasValue)
			{
				var originalText = $"${result.OriginalUnderlyingPrice.Value.ToString("N2", CultureInfo.InvariantCulture)}";
				var overrideText = $"${result.UnderlyingPrice.Value.ToString("N2", CultureInfo.InvariantCulture)}";
				spotText = ascii ? $"Current Price: {overrideText} {sep} " : $"Current Price: [strikethrough dim]{Markup.Escape(originalText)}[/] {Markup.Escape(overrideText)} {sep} ";
			}
			else if (result.UnderlyingPrice.HasValue)
				spotText = $"Current Price: {Markup.Escape($"${result.UnderlyingPrice.Value.ToString("N2", CultureInfo.InvariantCulture)}")} {sep} ";
			else
				spotText = "";
			var beText = result.BreakEvens.Count > 0 ? string.Join(", ", result.BreakEvens.Select(be => $"${be.ToString("N2", CultureInfo.InvariantCulture)}")) : "N/A";
			var maxProfitText = result.MaxProfit.HasValue ? (result.MaxProfit.Value >= 0 ? $"[green]${result.MaxProfit.Value.ToString("N2", CultureInfo.InvariantCulture)}[/]" : $"[red]-${Math.Abs(result.MaxProfit.Value).ToString("N2", CultureInfo.InvariantCulture)}[/]") : "Unlimited";
			var maxLossText = result.MaxLoss.HasValue ? $"[red]-${result.MaxLoss.Value.ToString("N2", CultureInfo.InvariantCulture)}[/]" : "Unlimited";
			items.Add(new Markup($"{spotText}Break-even: {Markup.Escape(beText)} {sep} Max Profit: {maxProfitText} {sep} Max Loss: {maxLossText}"));

			if (result.EarlyExercise != null)
			{
				var ex = result.EarlyExercise;
				var direction = ex.IsCall ? "above" : "below";
				var transitionDate = EvaluationDate.Today.AddDays(ex.TransitionDays).ToString("dd MMM yyyy");
				items.Add(new Markup($"[yellow]Early Exercise: {direction} ${ex.BoundaryNear.ToString("N2", CultureInfo.InvariantCulture)} until {Markup.Escape(transitionDate)}, then {direction} ${ex.BoundaryFar.ToString("N2", CultureInfo.InvariantCulture)}[/]"));
			}

			items.Add(new Text(""));

			if (result.Grid != null)
			{
				items.Add(BuildTimeDecayGridTable(result.Grid, result.BreakEvens, result.UnderlyingPrice, displayMode, tableBorder));
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
					var isCurrentPrice = result.UnderlyingPrice.HasValue && Math.Abs(point.UnderlyingPrice - result.UnderlyingPrice.Value) < 0.005m;
					var pricePrefix = isBreakEven ? "*" : isCurrentPrice ? ">" : " ";
					var priceText = $"{pricePrefix}${point.UnderlyingPrice.ToString("N2", CultureInfo.InvariantCulture)}";
					var valueText = point.ContractValue.HasValue ? $"${point.ContractValue.Value.ToString("N2", CultureInfo.InvariantCulture)}" : "-";
					var pnlColor = point.PnL >= 0 ? "green" : "red";
					var pnlText = FormatLadderPnL(point.PnL);
					if (isCurrentPrice)
						table.AddRow(new Markup($"[bold yellow]{Markup.Escape(priceText)}[/]"), new Markup($"[bold yellow]{Markup.Escape(valueText)}[/]"), new Markup($"[bold yellow]{Markup.Escape(pnlText)}[/]"));
					else
						table.AddRow(new Text(priceText), new Text(valueText), new Markup($"[{pnlColor}]{Markup.Escape(pnlText)}[/]"));
				}

				items.Add(table);
			}
		}

		if (result.Note != null)
			items.Add(new Markup($"[italic]{Markup.Escape(result.Note)}[/]"));

		var panel = new Panel(new Rows(items)) { Header = new PanelHeader(Markup.Escape(result.Title)), Expand = true };
		if (panelBorder != null) panel.Border = panelBorder;
		return panel;
	}

	private static IRenderable BuildLegMarkup(string legPrefix, string leg, bool ascii)
	{
		// In ASCII/text mode, strip ~...~ markers entirely (the override value follows, so only it remains).
		// In console mode, render ~...~ as strikethrough to show the original Yahoo IV crossed out.
		var processed = ascii ? Regex.Replace(leg, @"~[^~]+~ ", "") : leg;

		// Colorize the "Chg ..." portion (from Yahoo option-chain data) without allowing arbitrary markup.
		const string token = "Chg ";
		var start = processed.IndexOf(token, StringComparison.Ordinal);

		string markup;
		if (start < 0)
		{
			markup = $"{legPrefix}{Markup.Escape(processed)}";
		}
		else
		{
			var end = processed.IndexOf(" | ", start, StringComparison.Ordinal);
			if (end < 0) end = processed.Length;

			var before = processed[..start];
			var chgPart = processed[start..end];
			var after = processed[end..];

			var chgValue = chgPart[token.Length..];
			var color = chgPart.Contains("Chg -", StringComparison.Ordinal) ? "red" : chgPart.Contains("Chg +", StringComparison.Ordinal) ? "green" : "white";
			markup = $"{legPrefix}{Markup.Escape(before)}{Markup.Escape(token)}[{color}]{Markup.Escape(chgValue)}[/]{Markup.Escape(after)}";
		}

		if (!ascii)
		{
			markup = Regex.Replace(markup, @"~([^~]+)~", "[strikethrough dim]$1[/]");
			markup = Regex.Replace(markup, @"\{cheap\}([^{]+)\{/cheap\}", "[green]$1[/]");
			markup = Regex.Replace(markup, @"\{rich\}([^{]+)\{/rich\}", "[red]$1[/]");
		}
		else
		{
			markup = Regex.Replace(markup, @"\{cheap\}([^{]+)\{/cheap\}", "$1");
			markup = Regex.Replace(markup, @"\{rich\}([^{]+)\{/rich\}", "$1");
		}

		return new Markup(markup);
	}

	private static Table BuildTimeDecayGridTable(TimeDecayGrid grid, List<decimal> breakEvens, decimal? underlyingPrice, string displayMode, TableBorder? tableBorder)
	{
		var showPnL = displayMode == "pnl";
		var table = new Table();
		if (tableBorder != null) table.Border = tableBorder;

		table.AddColumn(new TableColumn("Price").RightAligned());
		foreach (var date in grid.DateColumns)
		{
			var label = date == grid.DateColumns[^1] ? "At Exp" : date.ToString("dd MMM", CultureInfo.InvariantCulture);
			table.AddColumn(new TableColumn(label).RightAligned());
		}

		for (int pi = 0; pi < grid.PriceRows.Count; pi++)
		{
			var price = grid.PriceRows[pi];
			var isBreakEven = breakEvens.Any(be => Math.Abs(price - be) < 0.005m);
			var isCurrentPrice = underlyingPrice.HasValue && Math.Abs(price - underlyingPrice.Value) < 0.005m;
			var pricePrefix = isBreakEven ? "*" : isCurrentPrice ? ">" : " ";
			var priceText = $"{pricePrefix}${price.ToString("N2", CultureInfo.InvariantCulture)}";

			var cells = new List<IRenderable> { isCurrentPrice ? new Markup($"[bold yellow]{Markup.Escape(priceText)}[/]") : new Text(priceText) };
			for (int di = 0; di < grid.DateColumns.Count; di++)
			{
				var cellValue = showPnL ? grid.PnLs[pi, di] : grid.Values[pi, di];
				string cellText;

				if (showPnL)
					cellText = FormatLadderPnL(cellValue);
				else
					cellText = $"${cellValue.ToString("N2", CultureInfo.InvariantCulture)}";

				if (isCurrentPrice)
					cells.Add(new Markup($"[bold yellow]{Markup.Escape(cellText)}[/]"));
				else
				{
					var pnl = grid.PnLs[pi, di];
					var color = pnl >= 0 ? "green" : "red";
					cells.Add(new Markup($"[{color}]{Markup.Escape(cellText)}[/]"));
				}
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

	public static Panel BuildAdjustmentPanel(PriceBreakdown b, BoxBorder? panelBorder = null, TableBorder? tableBorder = null, bool ascii = false)
	{
		var items = new List<IRenderable>();
		var sep = ascii ? "|" : "│";

		if (b.CostSteps != null && b.CostSteps.Count > 0)
		{
			var table = new Table();
			if (tableBorder != null) table.Border = tableBorder;
			table.AddColumn(new TableColumn("Date").LeftAligned());
			table.AddColumn(new TableColumn("Side").LeftAligned());
			table.AddColumn(new TableColumn("Qty").RightAligned());
			table.AddColumn(new TableColumn("Price").RightAligned());
			table.AddColumn(new TableColumn("Position").RightAligned());
			table.AddColumn(new TableColumn("Avg").RightAligned());

			foreach (var step in b.CostSteps)
			{
				table.AddRow(step.Timestamp.ToString("yyyy-MM-dd HH:mm"), step.Side.ToString(), step.TradeQty.ToString(), Formatters.FormatPrice(step.Price, b.Asset), step.RunningQty.ToString(), Formatters.FormatPrice(step.RunningAvg, b.Asset));
			}
			items.Add(table);
		}

		if (b.Credits != null && b.Credits.Count > 0)
		{
			items.Add(new Text(""));
			items.Add(ascii ? new Text("Strategy adjustments:") : new Markup("[bold]Strategy adjustments:[/]"));
			foreach (var c in b.Credits)
			{
				var credit = (c.LotPrice - c.ParentPrice) * c.Qty;
				var lotText = Formatters.FormatPrice(c.LotPrice, b.Asset);
				var parentText = Formatters.FormatPrice(c.ParentPrice, b.Asset);
				var creditText = credit.ToString("N2", CultureInfo.InvariantCulture);
				items.Add(new Text($"  {c.Instrument}: (${lotText} - ${parentText}) x {c.Qty} = ${creditText}"));
			}
		}

		if (b.NetDebitTrades != null && b.NetDebitTrades.Count > 0)
		{
			if (b.LastFlatTime.HasValue)
				items.Add(ascii ? new Text($"Trades since {b.LastFlatTime.Value:yyyy-MM-dd} (last flat):") : new Markup($"Trades since [bold]{b.LastFlatTime.Value:yyyy-MM-dd}[/] (last flat):"));
			else
				items.Add(new Text("All trades:"));

			items.Add(new Text(""));

			var table = new Table();
			if (tableBorder != null) table.Border = tableBorder;
			table.AddColumn(new TableColumn("Date").LeftAligned());
			table.AddColumn(new TableColumn("Instrument").LeftAligned());
			table.AddColumn(new TableColumn("Side").LeftAligned());
			table.AddColumn(new TableColumn("Qty").RightAligned());
			table.AddColumn(new TableColumn("Price").RightAligned());
			table.AddColumn(new TableColumn("Cash").RightAligned());

			foreach (var t in b.NetDebitTrades)
			{
				var cashText = t.CashImpact >= 0 ? $"+${t.CashImpact.ToString("N2", CultureInfo.InvariantCulture)}" : $"-${Math.Abs(t.CashImpact).ToString("N2", CultureInfo.InvariantCulture)}";
				IRenderable cashCell = ascii ? new Text(cashText) : new Markup(t.CashImpact >= 0 ? $"[green]{Markup.Escape(cashText)}[/]" : $"[red]{Markup.Escape(cashText)}[/]");
				table.AddRow(new Text(t.Timestamp.ToString("yyyy-MM-dd HH:mm")), new Text(t.Instrument), new Text(t.Side.ToString()), new Text(t.Qty.ToString()), new Text(Formatters.FormatPrice(t.Price, Asset.Option)), cashCell);
			}
			items.Add(table);
		}

		// Summary line
		items.Add(new Text(""));
		var initText = Formatters.FormatPrice(b.InitPrice, b.Asset);

		if (b.AdjPrice.HasValue && b.AdjPrice.Value != b.InitPrice)
		{
			var adjText = Formatters.FormatPrice(b.AdjPrice.Value, b.Asset);

			if (b.TotalNetDebit.HasValue)
			{
				var initDebit = b.InitNetDebit ?? (b.PositionSide == Side.Buy ? 1m : -1m) * b.InitPrice * b.Qty * 100m;
				var initQty = b.InitNetDebit.HasValue && b.InitPrice != 0 ? (int)Math.Round(Math.Abs(b.InitNetDebit.Value) / (b.InitPrice * 100m)) : b.Qty;
				var initDebitLabel = initDebit >= 0 ? "Init Net Debit" : "Init Net Credit";
				var initDebitText = Math.Abs(initDebit).ToString("N2", CultureInfo.InvariantCulture);
				items.Add(new Text($"{initDebitLabel}: ${initDebitText} {(ascii ? "/" : "÷")} ({initQty} x $100) = ${initText}/contract"));

				var netDebit = b.TotalNetDebit.Value;
				var adjLabel = netDebit >= 0 ? "Adj Net Debit" : "Adj Net Credit";
				var debitText = Math.Abs(netDebit).ToString("N2", CultureInfo.InvariantCulture);
				items.Add(new Text($"{adjLabel}: ${debitText} {(ascii ? "/" : "÷")} ({b.Qty} x $100) = ${adjText}/contract"));

				var perContractDiff = b.AdjPrice.Value - b.InitPrice;
				var diff = perContractDiff * b.Qty * 100m;
				var diffText = Math.Abs(diff).ToString("N2", CultureInfo.InvariantCulture);
				var diffSign = diff >= 0 ? "+" : "-";
				var diffAmount = $"{diffSign}${diffText}";
				if (ascii)
					items.Add(new Text($"Difference: {diffAmount}"));
				else
				{
					var diffColor = diff <= 0 ? "green" : "red";
					items.Add(new Markup($"Difference: [{diffColor}]{Markup.Escape(diffAmount)}[/]"));
				}
			}

			if (b.Credits != null && b.Credits.Count > 0)
			{
				var totalAdj = (b.InitPrice - b.AdjPrice.Value) * b.Qty;
				var adjAmount = totalAdj.ToString("N2", CultureInfo.InvariantCulture);
				items.Add(new Text($"Adj = ${initText} - ${adjAmount} {(ascii ? "/" : "÷")} {b.Qty} = ${adjText}"));
			}
		}
		else if (b.TotalNetDebit.HasValue)
		{
			var netDebit = b.TotalNetDebit.Value;
			var label = netDebit >= 0 ? "Net Debit" : "Net Credit";
			var debitText = Math.Abs(netDebit).ToString("N2", CultureInfo.InvariantCulture);
			items.Add(new Text($"{label}: ${debitText} {(ascii ? "/" : "÷")} ({b.Qty} x $100) = ${initText}/contract"));
		}
		else
		{
			items.Add(new Text($"Avg: ${initText} (no adjustment)"));
		}

		var sideText = b.OptionKind ?? (b.PositionSide == Side.Buy ? "Long" : "Short");
		var title = $"{b.Instrument} ({sideText} {b.Qty}x)";
		var panel = new Panel(new Rows(items)) { Header = new PanelHeader(title), Expand = true };
		if (panelBorder != null) panel.Border = panelBorder;
		return panel;
	}

	/// <summary>
	/// Renders the summary footer (total fees, final P&amp;L, final amount) to the given console.
	/// </summary>
	public static void RenderSummary(IAnsiConsole console, List<ReportRow> rows, decimal running, decimal initialAmount, decimal? unrealizedPnL = null)
	{
		var totalFees = rows.Where(r => !r.IsStrategyLeg).Sum(r => r.Fees);
		console.Write("Total fees: ");
		console.Write(Formatters.FormatMoney(totalFees, decimal.MaxValue));
		console.WriteLine();

		console.Write("Final P&L (realized): ");
		console.Write(Formatters.FormatPnL(running));
		console.Write(Formatters.FormatPnLPercent(running, initialAmount));
		console.WriteLine();

		console.Write("Final amount (realized): ");
		console.Write(Formatters.FormatMoney(initialAmount + running, initialAmount));
		console.WriteLine();

		if (unrealizedPnL.HasValue)
		{
			var totalPnL = running + unrealizedPnL.Value;

			console.Write("Final P&L (unrealized): ");
			console.Write(Formatters.FormatPnL(totalPnL));
			console.Write(Formatters.FormatPnLPercent(totalPnL, initialAmount));
			console.WriteLine();

			console.Write("Final amount (unrealized): ");
			console.Write(Formatters.FormatMoney(initialAmount + totalPnL, initialAmount));
			console.WriteLine();
		}
	}

	/// <summary>
	/// Computes unrealized P&L for all open positions. When theoretical mode is active and IV
	/// is available, uses Black-Scholes pricing; otherwise uses market mid prices from Yahoo quotes.
	/// Returns null if no pricing source is available.
	/// </summary>
	public static decimal? ComputeUnrealizedPnL(List<PositionRow> positions, AnalysisOptions opts)
	{
		if (positions.Count == 0)
			return null;

		var now = EvaluationDate.Now;
		decimal total = 0;
		bool anyPriced = false;

		foreach (var pos in positions)
		{
			// Skip strategy parents — their value is captured via legs
			if (pos.Asset == Asset.OptionStrategy)
				continue;

			if (pos.MatchKey == null || !MatchKeys.TryGetOptionSymbol(pos.MatchKey, out var symbol))
				continue;

			var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
			if (parsed == null)
				continue;

			decimal currentValue;
			if (opts.Theoretical)
			{
				var iv = OptionMath.GetLegIv(pos.Side, symbol, opts);
				var spot = opts.UnderlyingPriceOverrides != null && opts.UnderlyingPriceOverrides.TryGetValue(parsed.Root, out var op) ? op : opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(parsed.Root, out var up) ? up : (decimal?)null;
				if (!iv.HasValue || !spot.HasValue)
					continue;
				var expirationTime = parsed.ExpiryDate.Date + OptionMath.MarketClose;
				var timeYears = Math.Max(0, (expirationTime - now).TotalDays / 365.0);
				currentValue = OptionMath.BlackScholes(spot.Value, parsed.Strike, timeYears, OptionMath.RiskFreeRate, iv.Value, parsed.CallPut);
			}
			else
			{
				if (opts.OptionQuotes == null || !opts.OptionQuotes.TryGetValue(symbol, out var quote) || !quote.Bid.HasValue || !quote.Ask.HasValue)
					continue;
				currentValue = (quote.Bid.Value + quote.Ask.Value) / 2m;
			}

			var premium = pos.InitialAvgPrice ?? pos.AvgPrice;
			var multiplier = pos.Asset == Asset.Stock ? Trade.StockMultiplier : Trade.OptionMultiplier;
			var unrealized = pos.Side == Side.Buy ? (currentValue - premium) * pos.Qty * multiplier : (premium - currentValue) * pos.Qty * multiplier;
			total += unrealized;
			anyPriced = true;
		}

		return anyPriced ? total : null;
	}

	/// <summary>
	/// Computes the maximum number of date columns that fit in a given total width.
	/// Layout: panel borders (4) + table outer borders (2) + price column (11) + N × date column (15 for pnl, 10 for value).
	/// Each Spectre table column = content + 2 padding + 1 separator.
	/// </summary>
	public static int ComputeMaxGridColumns(int totalWidth, string displayMode)
	{
		// panel left/right border+padding (4) + table outer left+right borders (2) + price column (content 8 + pad 2 + sep 1 = 11)
		const int fixedOverhead = 4 + 2 + 11;
		var colWidth = displayMode == "pnl" ? 15 : 10; // pnl: "$+1,520.00" (10) + 2 pad + 1 sep; value: "$25.38" (6) + 2 pad + 1 sep
		var available = totalWidth - fixedOverhead;
		return Math.Max(3, available / colWidth); // minimum 3: today, expiry open, at exp
	}
}


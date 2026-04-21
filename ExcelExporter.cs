using OfficeOpenXml;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;

namespace WebullAnalytics;

public static class ExcelExporter
{
	public static void ExportToExcel(List<ReportRow> reportRows, List<PositionRow> positionRows, List<Trade> allTrades, decimal finalPnL, decimal initialAmount, string outputPath, AnalysisOptions opts)
	{
		// EPPlus requires a license context
		ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

		using var package = new ExcelPackage();

		// Create worksheets
		var transactionSheet = package.Workbook.Worksheets.Add("Transactions");
		var positionsSheet = package.Workbook.Worksheets.Add("Open Positions");
		var dailyPnLSheet = package.Workbook.Worksheets.Add("Daily P&L");
		var breakEvenSheet = package.Workbook.Worksheets.Add("Break-Even Analysis");

		// Export transaction report
		var unrealizedPnL = TableBuilder.ComputeUnrealizedPnL(positionRows, opts);
		ExportTransactions(transactionSheet, reportRows, finalPnL, initialAmount, unrealizedPnL);

		// Export open positions
		ExportPositions(positionsSheet, positionRows, opts);

		// Export daily P&L with chart
		ExportDailyPnL(dailyPnLSheet, reportRows);

		// Export break-even analysis
		ExportBreakEven(breakEvenSheet, positionRows, opts);

		// Save the file
		var file = new FileInfo(outputPath);
		package.SaveAs(file);

		Console.WriteLine($"Excel report exported to: {outputPath}");
	}

	private static void ExportTransactions(ExcelWorksheet sheet, List<ReportRow> rows, decimal finalPnL, decimal initialAmount, decimal? unrealizedPnL)
	{
		// Headers
		sheet.Cells[1, 1].Value = "Date";
		sheet.Cells[1, 2].Value = "Instrument";
		sheet.Cells[1, 3].Value = "Asset";
		sheet.Cells[1, 4].Value = "Option";
		sheet.Cells[1, 5].Value = "Side";
		sheet.Cells[1, 6].Value = "Qty";
		sheet.Cells[1, 7].Value = "Price";
		sheet.Cells[1, 8].Value = "Fees";
		sheet.Cells[1, 9].Value = "Closed Qty";
		sheet.Cells[1, 10].Value = "Realized P&L";
		sheet.Cells[1, 11].Value = "Running P&L";
		sheet.Cells[1, 12].Value = "Cash";
		sheet.Cells[1, 13].Value = "Total";

		FormatHeaderRow(sheet, 1, 1, 13);

		// Data rows (only non-leg rows)
		int row = 2;
		foreach (var reportRow in rows.Where(r => !r.IsStrategyLeg))
		{
			sheet.Cells[row, 1].Value = reportRow.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
			sheet.Cells[row, 2].Value = reportRow.Instrument;
			sheet.Cells[row, 3].Value = reportRow.Asset.DisplayName();
			sheet.Cells[row, 4].Value = reportRow.OptionKind;
			sheet.Cells[row, 5].Value = reportRow.Side;
			sheet.Cells[row, 6].Value = (double)reportRow.Qty;
			sheet.Cells[row, 7].Value = (double)reportRow.Price;
			sheet.Cells[row, 8].Value = (double)reportRow.Fees;
			sheet.Cells[row, 8].Style.Numberformat.Format = "#,##0.00";
			sheet.Cells[row, 9].Value = (double)reportRow.ClosedQty;
			sheet.Cells[row, 10].Value = (double)reportRow.Realized;
			sheet.Cells[row, 11].Value = (double)reportRow.Running;
			sheet.Cells[row, 12].Value = (double)reportRow.Cash;
			sheet.Cells[row, 13].Value = (double)reportRow.Total;

			// Format currency columns
			sheet.Cells[row, 7].Style.Numberformat.Format = "#,##0.00";
			sheet.Cells[row, 10].Style.Numberformat.Format = "#,##0.00";
			sheet.Cells[row, 11].Style.Numberformat.Format = "#,##0.00";
			sheet.Cells[row, 12].Style.Numberformat.Format = "$#,##0.00";
			sheet.Cells[row, 13].Style.Numberformat.Format = "$#,##0.00";

			// Color code P&L
			ColorCodePnL(sheet.Cells[row, 10], reportRow.Realized);
			ColorCodePnL(sheet.Cells[row, 11], reportRow.Running);

			row++;
		}

		// Add total fees
		row++;
		var totalFees = rows.Where(r => !r.IsStrategyLeg).Sum(r => r.Fees);
		sheet.Cells[row, 7].Value = "Total fees:";
		sheet.Cells[row, 7].Style.Font.Bold = true;
		sheet.Cells[row, 8].Value = (double)totalFees;
		sheet.Cells[row, 8].Style.Numberformat.Format = "#,##0.00";
		sheet.Cells[row, 8].Style.Font.Color.SetColor(System.Drawing.Color.Red);
		sheet.Cells[row, 8].Style.Font.Bold = true;

		// Add final P&L (realized)
		row++;
		sheet.Cells[row, 10].Value = "Final P&L (realized):";
		sheet.Cells[row, 10].Style.Font.Bold = true;
		sheet.Cells[row, 11].Value = (double)finalPnL;
		sheet.Cells[row, 11].Style.Numberformat.Format = "#,##0.00";
		sheet.Cells[row, 11].Style.Font.Bold = true;
		ColorCodePnL(sheet.Cells[row, 11], finalPnL);
		sheet.Cells[row, 12].Value = initialAmount == 0 ? 0d : (double)(finalPnL / initialAmount);
		sheet.Cells[row, 12].Style.Numberformat.Format = "0.00%";
		sheet.Cells[row, 12].Style.Font.Bold = true;
		ColorCodePnL(sheet.Cells[row, 12], finalPnL);

		// Add final amount (realized)
		sheet.Cells[row, 13].Value = "Realized:";
		sheet.Cells[row, 13].Style.Font.Bold = true;
		sheet.Cells[row, 14].Value = (double)(initialAmount + finalPnL);
		sheet.Cells[row, 14].Style.Numberformat.Format = "$#,##0.00";
		sheet.Cells[row, 14].Style.Font.Bold = true;

		// Add unrealized P&L and amount if quotes are available
		if (unrealizedPnL.HasValue)
		{
			var totalPnL = finalPnL + unrealizedPnL.Value;

			row++;
			sheet.Cells[row, 10].Value = "Final P&L (unrealized):";
			sheet.Cells[row, 10].Style.Font.Bold = true;
			sheet.Cells[row, 11].Value = (double)totalPnL;
			sheet.Cells[row, 11].Style.Numberformat.Format = "#,##0.00";
			sheet.Cells[row, 11].Style.Font.Bold = true;
			ColorCodePnL(sheet.Cells[row, 11], totalPnL);
			sheet.Cells[row, 12].Value = initialAmount == 0 ? 0d : (double)(totalPnL / initialAmount);
			sheet.Cells[row, 12].Style.Numberformat.Format = "0.00%";
			sheet.Cells[row, 12].Style.Font.Bold = true;
			ColorCodePnL(sheet.Cells[row, 12], totalPnL);

			sheet.Cells[row, 13].Value = "Unrealized:";
			sheet.Cells[row, 13].Style.Font.Bold = true;
			sheet.Cells[row, 14].Value = (double)(initialAmount + totalPnL);
			sheet.Cells[row, 14].Style.Numberformat.Format = "$#,##0.00";
			sheet.Cells[row, 14].Style.Font.Bold = true;
			ColorCodePnL(sheet.Cells[row, 14], totalPnL);
		}

		// Auto-fit columns
		sheet.Cells.AutoFitColumns();
	}

	private static void ExportPositions(ExcelWorksheet sheet, List<PositionRow> rows, AnalysisOptions opts)
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
		sheet.Cells[1, 9].Value = "IV";
		sheet.Cells[1, 10].Value = "HV";
		sheet.Cells[1, 11].Value = "IV5";

		FormatHeaderRow(sheet, 1, 1, 11);

		// Data rows
		int row = 2;
		foreach (var posRow in rows)
		{
			var indent = posRow.IsStrategyLeg ? "  └─ " : "";
			sheet.Cells[row, 1].Value = indent + posRow.Instrument;
			sheet.Cells[row, 2].Value = posRow.Asset.DisplayName();
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

			if (posRow.MatchKey != null && MatchKeys.TryGetOptionSymbol(posRow.MatchKey, out var sym) && opts.OptionQuotes != null && opts.OptionQuotes.TryGetValue(sym, out var q))
			{
				FormatIvCell(sheet.Cells[row, 9], q.ImpliedVolatility);
				FormatIvCell(sheet.Cells[row, 10], q.HistoricalVolatility);
				FormatIvCell(sheet.Cells[row, 11], q.ImpliedVolatility5Day);
			}
			else
			{
				sheet.Cells[row, 9].Value = "-";
				sheet.Cells[row, 10].Value = "-";
				sheet.Cells[row, 11].Value = "-";
			}

			row++;
		}

		// Auto-fit columns
		sheet.Cells.AutoFitColumns();
	}

	private static void FormatIvCell(ExcelRange cell, decimal? iv)
	{
		if (iv.HasValue)
		{
			cell.Value = (double)(iv.Value * 100m);
			cell.Style.Numberformat.Format = "0.0\"%\"";
		}
		else
		{
			cell.Value = "-";
		}
	}

	private static void ExportDailyPnL(ExcelWorksheet sheet, List<ReportRow> rows)
	{
		// Calculate daily P&L (group by date)
		var dailyData = rows.Where(r => !r.IsStrategyLeg && r.Realized != 0).GroupBy(r => r.Timestamp.Date).Select(g => new { Date = g.Key, DailyPnL = g.Sum(r => r.Realized), EndOfDayRunning = g.Last().Running }).OrderBy(d => d.Date).ToList();

		// Headers
		sheet.Cells[1, 1].Value = "Date";
		sheet.Cells[1, 2].Value = "Daily P&L";
		sheet.Cells[1, 3].Value = "Cumulative P&L";

		FormatHeaderRow(sheet, 1, 1, 3);

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
			ColorCodePnL(sheet.Cells[row, 2], day.DailyPnL);
			ColorCodePnL(sheet.Cells[row, 3], day.EndOfDayRunning);

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

	private static void ExportBreakEven(ExcelWorksheet sheet, List<PositionRow> positionRows, AnalysisOptions opts)
	{
		var results = BreakEvenAnalyzer.Analyze(positionRows, opts);
		if (results.Count == 0)
		{
			sheet.Cells[1, 1].Value = "No positions to analyze.";
			return;
		}

		int row = 1;
		int chartIndex = 0;

		void WriteResult(BreakEvenResult result)
		{
			int sectionStartRow = row;

			// Title row
			sheet.Cells[row, 1].Value = result.Title;
			sheet.Cells[row, 1].Style.Font.Bold = true;
			sheet.Cells[row, 1].Style.Font.Size = 12;
			row++;

			// Details row
			sheet.Cells[row, 1].Value = result.Details;
			var dteText = result.DaysToExpiry.HasValue ? result.DaysToExpiry.Value.ToString() : "N/A";
			sheet.Cells[row, 2].Value = $"DTE: {dteText}";
			row++;

			// Leg descriptions
			if (result.Legs != null)
			{
				foreach (var leg in result.Legs)
				{
					WriteRichTextWithStrikethrough(sheet.Cells[row, 1], $"  └─ {leg}");
					row++;
				}
			}

			// Note
			if (result.Note != null)
			{
				sheet.Cells[row, 1].Value = result.Note;
				sheet.Cells[row, 1].Style.Font.Italic = true;
				row++;
			}

			if (result.PriceLadder.Count > 0)
			{
				// Current price row
				if (result.UnderlyingPrice.HasValue)
				{
					sheet.Cells[row, 1].Value = "Current Price:";
					if (result.OriginalUnderlyingPrice.HasValue)
					{
						sheet.Cells[row, 2].Value = (double)result.OriginalUnderlyingPrice.Value;
						sheet.Cells[row, 2].Style.Numberformat.Format = "$#,##0.00";
						sheet.Cells[row, 2].Style.Font.Strike = true;
						sheet.Cells[row, 2].Style.Font.Color.SetColor(System.Drawing.Color.Gray);
						sheet.Cells[row, 3].Value = (double)result.UnderlyingPrice.Value;
						sheet.Cells[row, 3].Style.Numberformat.Format = "$#,##0.00";
						sheet.Cells[row, 3].Style.Font.Bold = true;
					}
					else
					{
						sheet.Cells[row, 2].Value = (double)result.UnderlyingPrice.Value;
						sheet.Cells[row, 2].Style.Numberformat.Format = "$#,##0.00";
						sheet.Cells[row, 2].Style.Font.Bold = true;
					}
					row++;
				}

				// Summary row
				sheet.Cells[row, 1].Value = "Break-even:";
				if (result.BreakEvens.Count > 0)
				{
					for (int i = 0; i < result.BreakEvens.Count; i++)
					{
						sheet.Cells[row, 2 + i].Value = (double)result.BreakEvens[i];
						sheet.Cells[row, 2 + i].Style.Numberformat.Format = "$#,##0.00";
						sheet.Cells[row, 2 + i].Style.Font.Bold = true;
					}
				}
				else
				{
					sheet.Cells[row, 2].Value = "N/A";
				}
				row++;

				sheet.Cells[row, 1].Value = "Max Profit:";
				if (result.MaxProfit.HasValue)
				{
					sheet.Cells[row, 2].Value = (double)result.MaxProfit.Value;
					sheet.Cells[row, 2].Style.Numberformat.Format = "$#,##0.00";
					sheet.Cells[row, 2].Style.Font.Color.SetColor(System.Drawing.Color.Green);
				}
				else
				{
					sheet.Cells[row, 2].Value = "Unlimited";
				}
				sheet.Cells[row, 3].Value = "Max Loss:";
				if (result.MaxLoss.HasValue)
				{
					sheet.Cells[row, 4].Value = -(double)result.MaxLoss.Value;
					sheet.Cells[row, 4].Style.Numberformat.Format = "$#,##0.00";
					sheet.Cells[row, 4].Style.Font.Color.SetColor(System.Drawing.Color.Red);
				}
				else
				{
					sheet.Cells[row, 4].Value = "Unlimited";
				}
				row++;

				if (result.EarlyExercise != null)
				{
					var ex = result.EarlyExercise;
					var direction = ex.IsCall ? "above" : "below";
					var transitionDate = EvaluationDate.Today.AddDays(ex.TransitionDays).ToString("dd MMM yyyy");
					sheet.Cells[row, 1].Value = "Early Exercise:";
					sheet.Cells[row, 2].Value = $"{direction} ${ex.BoundaryNear:N2} until {transitionDate}";
					sheet.Cells[row, 3].Value = "then:";
					sheet.Cells[row, 4].Value = $"{direction} ${ex.BoundaryFar:N2}";
					sheet.Cells[row, 1].Style.Font.Bold = true;
					row++;
				}
			}

			// Chart data in columns 8-10, chart starts at column 12 (one empty column gap)
			var chartData = result.ChartData ?? result.PriceLadder;
			if (chartData.Count > 0)
			{
				int dataStartRow = sectionStartRow;
				sheet.Cells[dataStartRow, 8].Value = "Price";
				sheet.Cells[dataStartRow, 9].Value = "Value";
				sheet.Cells[dataStartRow, 10].Value = "P&L";
				sheet.Cells[dataStartRow, 8].Style.Font.Bold = true;
				sheet.Cells[dataStartRow, 9].Style.Font.Bold = true;
				sheet.Cells[dataStartRow, 10].Style.Font.Bold = true;
				dataStartRow++;

				foreach (var point in chartData)
				{
					sheet.Cells[dataStartRow, 8].Value = (double)point.UnderlyingPrice;
					sheet.Cells[dataStartRow, 8].Style.Numberformat.Format = "$#,##0.00";
					
					if (point.ContractValue.HasValue)
					{
						sheet.Cells[dataStartRow, 9].Value = (double)point.ContractValue.Value;
						sheet.Cells[dataStartRow, 9].Style.Numberformat.Format = "$#,##0.00";
					}
					else
					{
						sheet.Cells[dataStartRow, 9].Value = "-";
					}

					sheet.Cells[dataStartRow, 10].Value = (double)point.PnL;
					sheet.Cells[dataStartRow, 10].Style.Numberformat.Format = "$#,##0.00";
					dataStartRow++;
				}

				// Create scatter chart (column 12, skipping column 11 as gap)
				var chart = sheet.Drawings.AddScatterChart($"PnL_Chart_{chartIndex}", eScatterChartType.XYScatterSmoothNoMarkers);
				chart.Title.Text = result.Title;
				chart.SetPosition(sectionStartRow - 1, 0, 11, 0); // column L onward
				chart.SetSize(600, 350);

				var xRange = sheet.Cells[sectionStartRow + 1, 8, dataStartRow - 1, 8]; // Price column (skip header)
				var yRange = sheet.Cells[sectionStartRow + 1, 10, dataStartRow - 1, 10]; // P&L column (skip header)
				var series = chart.Series.Add(yRange, xRange);
				series.Header = "P&L at Expiration";

				chart.XAxis.Title.Text = "Underlying Price";
				chart.XAxis.Format = "$#,##0.00";
				chart.YAxis.Title.Text = "P&L";
				chart.YAxis.Format = "$#,##0.00";
				chart.Legend.Position = eLegendPosition.Bottom;

				chartIndex++;

				// Ensure row advances past chart data
				if (dataStartRow > row) row = dataStartRow;
			}

			row += 2; // blank separator rows
		}

		var combined = CombinedBreakEvenAnalyzer.Analyze(positionRows, opts);
		var combinedByTicker = new Dictionary<string, BreakEvenResult>(StringComparer.Ordinal);
		foreach (var c in combined)
		{
			var sp = c.Title.IndexOf(' ');
			if (sp > 0) combinedByTicker[c.Title[..sp]] = c;
		}

		string? lastTicker = null;
		foreach (var result in results)
		{
			var sp = result.Title.IndexOf(' ');
			var ticker = sp > 0 ? result.Title[..sp] : null;
			if (lastTicker != null && ticker != lastTicker && combinedByTicker.TryGetValue(lastTicker, out var prev))
			{
				WriteResult(prev);
				combinedByTicker.Remove(lastTicker);
			}
			WriteResult(result);
			lastTicker = ticker;
		}
		if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
			WriteResult(finalCombined);

		sheet.Cells.AutoFitColumns();
	}

	private static void FormatHeaderRow(ExcelWorksheet sheet, int row, int fromCol, int toCol)
	{
		using var range = sheet.Cells[row, fromCol, row, toCol];
		range.Style.Font.Bold = true;
		range.Style.Fill.PatternType = ExcelFillStyle.Solid;
		range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
	}

	/// <summary>
	/// Writes text to a cell, converting ~...~ markers into strikethrough rich text segments.
	/// </summary>
	private static void WriteRichTextWithStrikethrough(ExcelRange cell, string text)
	{
		var idx = text.IndexOf('~');
		if (idx < 0)
		{
			cell.Value = text;
			return;
		}

		var rt = cell.RichText;
		var pos = 0;
		while (pos < text.Length)
		{
			var start = text.IndexOf('~', pos);
			if (start < 0)
			{
				var normal = rt.Add(text[pos..]);
				normal.Strike = false;
				normal.Color = System.Drawing.Color.Black;
				break;
			}
			var end = text.IndexOf('~', start + 1);
			if (end < 0)
			{
				var normal = rt.Add(text[pos..]);
				normal.Strike = false;
				normal.Color = System.Drawing.Color.Black;
				break;
			}
			if (start > pos)
			{
				var normal = rt.Add(text[pos..start]);
				normal.Strike = false;
				normal.Color = System.Drawing.Color.Black;
			}
			var struck = rt.Add(text[(start + 1)..end]);
			struck.Strike = true;
			struck.Color = System.Drawing.Color.Gray;
			pos = end + 1;
		}
	}

	private static void ColorCodePnL(ExcelRange cell, decimal value)
	{
		if (value > 0)
			cell.Style.Font.Color.SetColor(System.Drawing.Color.Green);
		else if (value < 0)
			cell.Style.Font.Color.SetColor(System.Drawing.Color.Red);
	}
}

using WebullAnalytics.Positions;
using WebullAnalytics.Report;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Positions;

public class PositionReplayTests
{
	[Fact]
	public void Execute_SplitsSharedLongLegIntoCalendarAndDiagonal()
	{
		var backExpiry = new DateTime(2026, 6, 19);
		var frontExpiry = new DateTime(2026, 5, 15);
		var longSymbol = MatchKeys.OccSymbol("GME", backExpiry, 25m, "P");
		var calendarShortSymbol = MatchKeys.OccSymbol("GME", frontExpiry, 25m, "P");
		var diagonalShortSymbol = MatchKeys.OccSymbol("GME", frontExpiry, 25.5m, "P");
		var timestamp = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, "GME 01 May 2026", "strategy:Diagonal:GME:2026-05-01:P25,P25.5", Asset.OptionStrategy, "Diagonal", Side.Buy, 244, 0.47m, Trade.OptionMultiplier, backExpiry),
			new(2, timestamp, Formatters.FormatOptionDisplay("GME", backExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Buy, 244, 0.61m, Trade.OptionMultiplier, backExpiry, 1),
			new(3, timestamp, Formatters.FormatOptionDisplay("GME", frontExpiry, 25m), MatchKeys.Option(calendarShortSymbol), Asset.Option, "Put", Side.Sell, 122, 0.33m, Trade.OptionMultiplier, frontExpiry, 1),
			new(4, timestamp, Formatters.FormatOptionDisplay("GME", frontExpiry, 25.5m), MatchKeys.Option(diagonalShortSymbol), Asset.Option, "Put", Side.Sell, 122, 0.45m, Trade.OptionMultiplier, frontExpiry, 1),
		};

		var (rows, _, _) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);
		var parents = rows.Where(r => r.Asset == Asset.OptionStrategy).ToList();

		Assert.Equal(2, parents.Count);
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 122);
		Assert.Contains(parents, row => row.OptionKind == "Diagonal" && row.Qty == 122);
		Assert.DoesNotContain(rows, row => row.Asset == Asset.OptionStrategy && row.OptionKind == "Butterfly");
		Assert.DoesNotContain(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longSymbol));
	}

	[Fact]
	public void Execute_MergesLeftoverSingleLegsIntoDiagonal()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var longExpiry = new DateTime(2026, 5, 22);
		var shortCalendarSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25m, "P");
		var longCalendarSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25m, "P");
		var shortDiagonalSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25.5m, "P");
		var timestamp = new DateTime(2026, 4, 27, 12, 37, 2, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 474, 0.71m, Trade.OptionMultiplier, longExpiry),
			new(2, timestamp, Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortCalendarSymbol), Asset.Option, "Put", Side.Sell, 474, 0.36m, Trade.OptionMultiplier, shortExpiry, 1),
			new(3, timestamp, Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longCalendarSymbol), Asset.Option, "Put", Side.Buy, 474, 1.07m, Trade.OptionMultiplier, longExpiry, 1),
			new(4, timestamp.AddSeconds(1), Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortCalendarSymbol), Asset.Option, "Put", Side.Buy, 126, 0.28m, Trade.OptionMultiplier, shortExpiry),
			new(5, timestamp.AddSeconds(2), Formatters.FormatOptionDisplay("GME", shortExpiry, 25.5m), MatchKeys.Option(shortDiagonalSymbol), Asset.Option, "Put", Side.Sell, 126, 0.47m, Trade.OptionMultiplier, shortExpiry),
		};

		var (rows, _, _) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);
		var parents = rows.Where(r => r.Asset == Asset.OptionStrategy).ToList();
		var diagonalLong = rows.Single(r => r.Asset == Asset.Option && r.IsStrategyLeg && r.MatchKey == MatchKeys.Option(longCalendarSymbol) && r.Qty == 126);
		var diagonalShort = rows.Single(r => r.Asset == Asset.Option && r.IsStrategyLeg && r.MatchKey == MatchKeys.Option(shortDiagonalSymbol) && r.Qty == 126);
		var diagonalParent = parents.Single(row => row.OptionKind == "Diagonal" && row.Qty == 126);

		Assert.Equal(2, parents.Count);
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 348);
		Assert.Equal(0.71m, decimal.Round(diagonalParent.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.52m, decimal.Round(diagonalParent.AdjustedAvgPrice!.Value, 2));
		Assert.Equal(1.07m, decimal.Round(diagonalLong.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.99m, decimal.Round(diagonalLong.AdjustedAvgPrice!.Value, 2));
		Assert.Equal(0.47m, decimal.Round(diagonalShort.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.47m, decimal.Round(diagonalShort.AdjustedAvgPrice!.Value, 2));
		Assert.DoesNotContain(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortDiagonalSymbol));
		Assert.DoesNotContain(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longCalendarSymbol));
	}

	[Fact]
	public void Execute_StandaloneAddToCalendarKeepsNewDiagonalOnItsOwnBasis()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var longExpiry = new DateTime(2026, 5, 22);
		var shortCalendarSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25m, "P");
		var longCalendarSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25m, "P");
		var diagonalLongSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25.5m, "P");
		var timestamp = new DateTime(2026, 4, 27, 12, 37, 2, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 474, 0.71m, Trade.OptionMultiplier, longExpiry),
			new(2, timestamp, Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortCalendarSymbol), Asset.Option, "Put", Side.Sell, 474, 0.36m, Trade.OptionMultiplier, shortExpiry, 1),
			new(3, timestamp, Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longCalendarSymbol), Asset.Option, "Put", Side.Buy, 474, 1.07m, Trade.OptionMultiplier, longExpiry, 1),
			new(4, timestamp.AddSeconds(1), Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortCalendarSymbol), Asset.Option, "Put", Side.Sell, 152, 0.32m, Trade.OptionMultiplier, shortExpiry),
			new(5, timestamp.AddSeconds(2), Formatters.FormatOptionDisplay("GME", longExpiry, 25.5m), MatchKeys.Option(diagonalLongSymbol), Asset.Option, "Put", Side.Buy, 152, 1.255m, Trade.OptionMultiplier, longExpiry),
		};

		var (rows, _, _) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);
		var parents = rows.Where(r => r.Asset == Asset.OptionStrategy).ToList();
		var calendarParent = parents.Single(row => row.OptionKind == "Calendar" && row.Qty == 474);
		var diagonalParent = parents.Single(row => row.OptionKind == "Diagonal" && row.Qty == 152);
		var diagonalLong = rows.Single(r => r.Asset == Asset.Option && r.IsStrategyLeg && r.MatchKey == MatchKeys.Option(diagonalLongSymbol) && r.Qty == 152);
		var diagonalShort = rows.Single(r => r.Asset == Asset.Option && r.IsStrategyLeg && r.MatchKey == MatchKeys.Option(shortCalendarSymbol) && r.Qty == 152 && r.Side == Side.Sell);

		Assert.Equal(2, parents.Count);
		Assert.Equal(0.71m, decimal.Round(calendarParent.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.71m, decimal.Round(calendarParent.AdjustedAvgPrice!.Value, 2));
	  Assert.Equal(0.32m, decimal.Round(diagonalParent.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.935m, decimal.Round(diagonalParent.AdjustedAvgPrice!.Value, 3));
		Assert.Equal(1.255m, decimal.Round(diagonalLong.InitialAvgPrice!.Value, 3));
		Assert.Equal(1.255m, decimal.Round(diagonalLong.AdjustedAvgPrice!.Value, 3));
		Assert.Equal(0.32m, decimal.Round(diagonalShort.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.32m, decimal.Round(diagonalShort.AdjustedAvgPrice!.Value, 2));
	}

	[Fact]
	public void Execute_GroupedDiagonalAddToCalendarKeepsPositionsSeparate()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var longExpiry = new DateTime(2026, 5, 22);
		var shortCalendarSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25m, "P");
		var longCalendarSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25m, "P");
		var diagonalLongSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25.5m, "P");
		var timestamp = new DateTime(2026, 4, 27, 12, 37, 2, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 474, 0.71m, Trade.OptionMultiplier, longExpiry),
			new(2, timestamp, Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortCalendarSymbol), Asset.Option, "Put", Side.Sell, 474, 0.36m, Trade.OptionMultiplier, shortExpiry, 1),
			new(3, timestamp, Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longCalendarSymbol), Asset.Option, "Put", Side.Buy, 474, 1.07m, Trade.OptionMultiplier, longExpiry, 1),
			new(4, timestamp.AddSeconds(1), "GME 22 May 2026", "strategy:Diagonal:GME:2026-05-22:P25,P25.5", Asset.OptionStrategy, "Diagonal", Side.Buy, 152, 0.93m, Trade.OptionMultiplier, longExpiry),
			new(5, timestamp.AddSeconds(1), Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortCalendarSymbol), Asset.Option, "Put", Side.Sell, 152, 0.32m, Trade.OptionMultiplier, shortExpiry, 4),
			new(6, timestamp.AddSeconds(1), Formatters.FormatOptionDisplay("GME", longExpiry, 25.5m), MatchKeys.Option(diagonalLongSymbol), Asset.Option, "Put", Side.Buy, 152, 1.255m, Trade.OptionMultiplier, longExpiry, 4),
		};

		var (rows, _, _) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);
		var parents = rows.Where(r => r.Asset == Asset.OptionStrategy).ToList();
		var calendarParent = parents.Single(row => row.OptionKind == "Calendar" && row.Qty == 474);
		var diagonalParent = parents.Single(row => row.OptionKind == "Diagonal" && row.Qty == 152);

		Assert.Equal(2, parents.Count);
		Assert.Equal(0.71m, decimal.Round(calendarParent.InitialAvgPrice!.Value, 2));
		Assert.Equal(0.71m, decimal.Round(calendarParent.AdjustedAvgPrice!.Value, 2));
		Assert.Equal(0.935m, decimal.Round(diagonalParent.InitialAvgPrice!.Value, 3));
		Assert.Equal(0.935m, decimal.Round(diagonalParent.AdjustedAvgPrice!.Value, 3));
		Assert.DoesNotContain(rows, row => row.Asset == Asset.OptionStrategy && row.Qty == 152 && row.MatchKey == null && row.OptionKind == "Butterfly");
		Assert.DoesNotContain(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortCalendarSymbol));
		Assert.DoesNotContain(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longCalendarSymbol));
	}

	[Fact]
	public void Execute_DoesNotMergeSingleLegOrphansWhenContractCountsDoNotMatch()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var longExpiry = new DateTime(2026, 5, 22);
		var shortSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25.5m, "P");
		var longSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25m, "P");
		var timestamp = new DateTime(2026, 4, 27, 12, 37, 2, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Buy, 126, 0.71m, Trade.OptionMultiplier, longExpiry),
			new(2, timestamp.AddSeconds(1), Formatters.FormatOptionDisplay("GME", shortExpiry, 25.5m), MatchKeys.Option(shortSymbol), Asset.Option, "Put", Side.Sell, 100, 0.47m, Trade.OptionMultiplier, shortExpiry),
		};

		var (rows, _, _) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);

		Assert.DoesNotContain(rows, row => row.Asset == Asset.OptionStrategy);
		Assert.Contains(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longSymbol) && row.Qty == 126);
		Assert.Contains(rows, row => row.Asset == Asset.Option && !row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 100);
	}

	[Fact]
	public void Execute_SplitsMixedQuantitySharedShortIntoSeparateCalendars()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var midExpiry = new DateTime(2026, 5, 15);
		var longExpiry = new DateTime(2026, 5, 22);
		var shortSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25m, "P");
		var midSymbol = MatchKeys.OccSymbol("GME", midExpiry, 25m, "P");
		var longSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25m, "P");
		var timestamp = new DateTime(2026, 4, 27, 12, 37, 2, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 474, 0.71m, Trade.OptionMultiplier, longExpiry),
			new(2, timestamp, Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortSymbol), Asset.Option, "Put", Side.Sell, 474, 0.36m, Trade.OptionMultiplier, shortExpiry, 1),
			new(3, timestamp, Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Buy, 474, 1.07m, Trade.OptionMultiplier, longExpiry, 1),
			new(4, timestamp.AddDays(2), "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 115, 0.53m, Trade.OptionMultiplier, longExpiry),
			new(5, timestamp.AddDays(2), Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortSymbol), Asset.Option, "Put", Side.Sell, 115, 0.8942m, Trade.OptionMultiplier, shortExpiry, 4),
			new(6, timestamp.AddDays(2), Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Buy, 115, 1.42m, Trade.OptionMultiplier, longExpiry, 4),
			new(7, timestamp.AddDays(2).AddHours(1), "GME 15 May 2026", "strategy:Calendar:GME:2026-05-15:P25", Asset.OptionStrategy, "Calendar", Side.Sell, 300, 0.130134m, Trade.OptionMultiplier, midExpiry),
			new(8, timestamp.AddDays(2).AddHours(1), Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Sell, 300, 1.36m, Trade.OptionMultiplier, longExpiry, 7),
			new(9, timestamp.AddDays(2).AddHours(1), Formatters.FormatOptionDisplay("GME", midExpiry, 25m), MatchKeys.Option(midSymbol), Asset.Option, "Put", Side.Buy, 300, 1.23m, Trade.OptionMultiplier, midExpiry, 7),
		};

		var (rows, _, _) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);
		var parents = rows.Where(r => r.Asset == Asset.OptionStrategy).OrderBy(r => r.Qty).ToList();

		Assert.Equal(2, parents.Count);
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 289 && row.Instrument == "GME 22 May 2026");
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 300 && row.Instrument == "GME 15 May 2026");
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 289 && decimal.Round(row.InitialAvgPrice!.Value, 2) == 0.71m && row.OpenQty == 474);
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 300 && decimal.Round(row.InitialAvgPrice!.Value, 2) == 0.71m && row.OpenQty == 474);
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 289 && decimal.Round(row.AdjustedAvgPrice!.Value, 3) == 0.674m);
		Assert.Contains(parents, row => row.OptionKind == "Calendar" && row.Qty == 300 && decimal.Round(row.AdjustedAvgPrice!.Value, 3) == 0.544m);
		Assert.DoesNotContain(rows, row => row.Asset == Asset.OptionStrategy && row.OptionKind == "Butterfly");
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 289 && decimal.Round(row.InitialAvgPrice!.Value, 2) == 0.36m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 300 && decimal.Round(row.InitialAvgPrice!.Value, 2) == 0.36m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longSymbol) && row.Qty == 289 && decimal.Round(row.InitialAvgPrice!.Value, 2) == 1.07m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(midSymbol) && row.Qty == 300 && decimal.Round(row.InitialAvgPrice!.Value, 2) == 1.23m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 289 && decimal.Round(row.AdjustedAvgPrice!.Value, 3) == 0.464m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 300 && decimal.Round(row.AdjustedAvgPrice!.Value, 3) == 0.464m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longSymbol) && row.Qty == 289 && decimal.Round(row.AdjustedAvgPrice!.Value, 3) == 1.138m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(midSymbol) && row.Qty == 300 && decimal.Round(row.AdjustedAvgPrice!.Value, 3) == 1.008m);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 289);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(shortSymbol) && row.Qty == 300);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(longSymbol) && row.Qty == 289);
		Assert.Contains(rows, row => row.Asset == Asset.Option && row.IsStrategyLeg && row.MatchKey == MatchKeys.Option(midSymbol) && row.Qty == 300);
	}

	[Fact]
	public void BuildAdjustmentReport_FiltersInheritedRollTradesFromUnchangedSiblingPanel()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var midExpiry = new DateTime(2026, 5, 15);
		var longExpiry = new DateTime(2026, 5, 22);
		var shortSymbol = MatchKeys.OccSymbol("GME", shortExpiry, 25m, "P");
		var midSymbol = MatchKeys.OccSymbol("GME", midExpiry, 25m, "P");
		var longSymbol = MatchKeys.OccSymbol("GME", longExpiry, 25m, "P");
		var timestamp = new DateTime(2026, 4, 27, 12, 37, 2, DateTimeKind.Utc);

		var trades = new List<Trade>
		{
			new(1, timestamp, "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 474, 0.71m, Trade.OptionMultiplier, longExpiry),
			new(2, timestamp, Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortSymbol), Asset.Option, "Put", Side.Sell, 474, 0.36m, Trade.OptionMultiplier, shortExpiry, 1),
			new(3, timestamp, Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Buy, 474, 1.07m, Trade.OptionMultiplier, longExpiry, 1),
			new(4, timestamp.AddDays(2), "GME 22 May 2026", "strategy:Calendar:GME:2026-05-22:P25", Asset.OptionStrategy, "Calendar", Side.Buy, 115, 0.53m, Trade.OptionMultiplier, longExpiry),
			new(5, timestamp.AddDays(2), Formatters.FormatOptionDisplay("GME", shortExpiry, 25m), MatchKeys.Option(shortSymbol), Asset.Option, "Put", Side.Sell, 115, 0.8942m, Trade.OptionMultiplier, shortExpiry, 4),
			new(6, timestamp.AddDays(2), Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Buy, 115, 1.42m, Trade.OptionMultiplier, longExpiry, 4),
			new(7, timestamp.AddDays(2).AddHours(1), "GME 15 May 2026", "strategy:Calendar:GME:2026-05-15:P25", Asset.OptionStrategy, "Calendar", Side.Sell, 300, 0.130134m, Trade.OptionMultiplier, midExpiry),
			new(8, timestamp.AddDays(2).AddHours(1), Formatters.FormatOptionDisplay("GME", longExpiry, 25m), MatchKeys.Option(longSymbol), Asset.Option, "Put", Side.Sell, 300, 1.36m, Trade.OptionMultiplier, longExpiry, 7),
			new(9, timestamp.AddDays(2).AddHours(1), Formatters.FormatOptionDisplay("GME", midExpiry, 25m), MatchKeys.Option(midSymbol), Asset.Option, "Put", Side.Buy, 300, 1.23m, Trade.OptionMultiplier, midExpiry, 7),
		};

		var (rows, adjustments, singleLegStandalones) = PositionReplay.Execute(new Dictionary<string, List<Lot>>(), new Dictionary<string, Trade>(), trades);
		var breakdowns = AdjustmentReportBuilder.Build(rows, trades, new Dictionary<string, List<Lot>>(), adjustments, singleLegStandalones);

		var unchangedPanel = breakdowns.Single(b => b.Instrument == "GME 22 May 2026");
		var rolledPanel = breakdowns.Single(b => b.Instrument == "GME 15 May 2026");

		Assert.Equal("Basis trades:", unchangedPanel.NetDebitTradesLabel);
		Assert.NotNull(unchangedPanel.NetDebitTrades);
		Assert.Equal(4, unchangedPanel.NetDebitTrades!.Count);
		Assert.All(unchangedPanel.NetDebitTrades, t => Assert.DoesNotContain("15 May 2026", t.Instrument));
		Assert.All(unchangedPanel.NetDebitTrades, t => Assert.DoesNotContain("1.36", t.Price.ToString()));
		Assert.Null(rolledPanel.NetDebitTradesLabel);
		Assert.NotNull(rolledPanel.NetDebitTrades);
		Assert.Equal(6, rolledPanel.NetDebitTrades!.Count);
		Assert.Contains(rolledPanel.NetDebitTrades, t => t.Instrument.Contains("15 May 2026") && t.Side == Side.Buy);
	}
}

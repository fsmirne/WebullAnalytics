using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public class MarginAnalysisTests
{
	[Fact]
	public void ComputeLegMargin_CalendarHasNoMargin()
	{
		var shortLeg = new OptionParsed("GME", DateTime.Today.AddDays(7), "C", 25.50m);
		var longLeg = new OptionParsed("GME", DateTime.Today.AddDays(28), "C", 25.50m);

		var margin = AnalyzeCommon.ComputeLegMargin(shortLeg, 454, 25.50m, 0.63m, longLeg, null, 454, 1.27m, false);

		Assert.Equal(0m, margin.Total);
	}

	[Fact]
	public void ComputeLegMargin_CreditVerticalRequiresWidthCollateral()
	{
		var shortLeg = new OptionParsed("GME", DateTime.Today.AddDays(7), "C", 25.50m);
		var longLeg = new OptionParsed("GME", DateTime.Today.AddDays(7), "C", 26.50m);

		var margin = AnalyzeCommon.ComputeLegMargin(shortLeg, 2, 25.50m, 0.80m, longLeg, null, 2, 0.30m, false);

		Assert.Equal(200m, margin.Total);
	}

	[Fact]
	public void ComputeLegMargin_InvertedDiagonalRequiresStrikeLossPlusDebit()
	{
		var shortLeg = new OptionParsed("GME", DateTime.Today.AddDays(7), "C", 25.50m);
		var longLeg = new OptionParsed("GME", DateTime.Today.AddDays(28), "C", 26.00m);

		var margin = AnalyzeCommon.ComputeLegMargin(shortLeg, 1, 25.50m, 0.50m, longLeg, null, 1, 1.00m, false);

		Assert.Equal(100m, margin.Total);
	}

	[Fact]
	public void BreakEvenAnalyzer_ShowsZeroMarginForCalendar()
	{
		var shortSymbol = MatchKeys.OccSymbol("GME", DateTime.Today.AddDays(7), 25.50m, "C");
		var longSymbol = MatchKeys.OccSymbol("GME", DateTime.Today.AddDays(28), 25.50m, "C");
		var positions = new List<PositionRow>
		{
			new("GME Calendar", Asset.OptionStrategy, "Calendar", Side.Buy, 454, 0.64m, DateTime.Today.AddDays(28)),
			new("GME Call", Asset.Option, "Call", Side.Buy, 454, 1.27m, DateTime.Today.AddDays(28), IsStrategyLeg: true, MatchKey: MatchKeys.Option(longSymbol)),
			new("GME Call", Asset.Option, "Call", Side.Sell, 454, 0.63m, DateTime.Today.AddDays(7), IsStrategyLeg: true, MatchKey: MatchKeys.Option(shortSymbol)),
		};

		var opts = new AnalysisOptions(
			UnderlyingPriceOverrides: new Dictionary<string, decimal> { ["GME"] = 25.50m },
			IvOverrides: new Dictionary<string, decimal>
			{
				[shortSymbol] = 0.50m,
				[longSymbol] = 0.50m,
			});

		var result = Assert.Single(BreakEvenAnalyzer.Analyze(positions, opts, padding: 2m, maxGridColumns: 4));

		Assert.Equal(0m, result.Margin);
	}

	[Fact]
	public void BreakEvenAnalyzer_InvertedDiagonalShowsMaxLossAndMargin()
	{
		var shortSymbol = MatchKeys.OccSymbol("GME", DateTime.Today.AddDays(7), 25.50m, "C");
		var longSymbol = MatchKeys.OccSymbol("GME", DateTime.Today.AddDays(28), 26.00m, "C");
		var positions = new List<PositionRow>
		{
			new("GME Diagonal", Asset.OptionStrategy, "Diagonal", Side.Buy, 1, 0.50m, DateTime.Today.AddDays(28)),
			new("GME Call", Asset.Option, "Call", Side.Buy, 1, 1.00m, DateTime.Today.AddDays(28), IsStrategyLeg: true, MatchKey: MatchKeys.Option(longSymbol)),
			new("GME Call", Asset.Option, "Call", Side.Sell, 1, 0.50m, DateTime.Today.AddDays(7), IsStrategyLeg: true, MatchKey: MatchKeys.Option(shortSymbol)),
		};

		var opts = new AnalysisOptions(
			UnderlyingPriceOverrides: new Dictionary<string, decimal> { ["GME"] = 25.50m },
			IvOverrides: new Dictionary<string, decimal>
			{
				[shortSymbol] = 0.50m,
				[longSymbol] = 0.50m,
			});

		var result = Assert.Single(BreakEvenAnalyzer.Analyze(positions, opts, padding: 2m, maxGridColumns: 4));

		Assert.Equal(100m, result.MaxLoss);
		Assert.Equal(100m, result.Margin);
	}
}

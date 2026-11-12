using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public class MarginAnalysisTests
{
	private static OptionParsed Opt(string root, int days, string cp, decimal strike) => new(root, DateTime.Today.AddDays(days), cp, strike);

	private static decimal Margin(decimal spot, params MarginLeg[] legs) => MarginCalculator.Requirement(legs, spot).Total;

	[Fact]
	public void Calendar_HoldsNoMargin() =>
		Assert.Equal(0m, Margin(25.50m, new(Opt("GME", 7, "C", 25.50m), 454, IsLong: false, 0.63m), new(Opt("GME", 28, "C", 25.50m), 454, IsLong: true)));

	[Fact]
	public void CreditVertical_HoldsItsWidth() =>
		Assert.Equal(200m, Margin(25.50m, new(Opt("GME", 7, "C", 25.50m), 2, IsLong: false, 0.80m), new(Opt("GME", 7, "C", 26.50m), 2, IsLong: true)));

	/// <summary>The 2026-11-12 fill: short 760P 11/17, long 759P 11/27, x100. The pair can owe the $1 strike gap at the short's expiry;
	/// the $2.75 debit was paid in cash and is not collateral → $1 × 100 × 100.</summary>
	[Fact]
	public void InvertedDiagonal_HoldsTheStrikeLossNotTheDebit() =>
		Assert.Equal(10_000m, Margin(757.69m, new(Opt("SPY", 5, "P", 760m), 100, IsLong: false, 5.69m), new(Opt("SPY", 15, "P", 759m), 100, IsLong: true)));

	[Fact]
	public void CoveredDiagonal_HoldsNoMargin() =>
		Assert.Equal(0m, Margin(757.77m, new(Opt("SPY", 5, "C", 760m), 100, IsLong: false, 3.85m), new(Opt("SPY", 15, "C", 759m), 100, IsLong: true)));

	[Fact]
	public void LongExpiringFirst_DoesNotCover()
	{
		var naked = MarginCalculator.NakedShortPerContract(100m, 100m, "P", 2m);
		Assert.Equal(naked, Margin(100m, new(Opt("SPY", 15, "P", 100m), 1, IsLong: false, 2m), new(Opt("SPY", 5, "P", 100m), 1, IsLong: true)));
	}

	/// <summary>Naked: the highest of premium + 20% of the underlying − OTM, the call minimum (premium + 10% of the underlying), the
	/// put minimum (premium + 15% of the strike, Webull) and $250.</summary>
	[Fact]
	public void NakedShort_TakesTheHighestRequirement()
	{
		Assert.Equal((1m + 0.20m * 100m - 5m) * 100m, MarginCalculator.NakedShortPerContract(100m, 105m, "C", 1m));    // 20% base wins
		Assert.Equal((0.5m + 0.10m * 100m) * 100m, MarginCalculator.NakedShortPerContract(100m, 115m, "C", 0.5m));    // call minimum
		Assert.Equal((0.5m + 0.15m * 80m) * 100m, MarginCalculator.NakedShortPerContract(100m, 80m, "P", 0.5m));      // Webull put minimum
		Assert.Equal(250m, MarginCalculator.NakedShortPerContract(10m, 15m, "C", 0.05m));                                // $250 floor
	}

	[Fact]
	public void IronCondor_HoldsTheGreaterSide() =>
		Assert.Equal(500m, Margin(100m,
			new(Opt("SPY", 7, "P", 95m), 1, IsLong: false), new(Opt("SPY", 7, "P", 92m), 1, IsLong: true),
			new(Opt("SPY", 7, "C", 105m), 1, IsLong: false), new(Opt("SPY", 7, "C", 110m), 1, IsLong: true)));

	[Fact]
	public void PartialCover_LeavesTheRestNaked()
	{
		var naked = MarginCalculator.NakedShortPerContract(100m, 105m, "C", 1m);
		Assert.Equal(naked, Margin(100m, new(Opt("SPY", 7, "C", 105m), 2, IsLong: false, 1m), new(Opt("SPY", 7, "C", 106m), 1, IsLong: true)) - 100m);
	}

	[Fact]
	public void ShortCall_CoveredByStock()
	{
		var legs = new[] { new MarginLeg(Opt("SPY", 7, "C", 105m), 2, IsLong: false, 1m) };
		Assert.Equal(0m, MarginCalculator.Requirement(legs, 100m, longShares: 200).Total);
		Assert.Equal(MarginCalculator.NakedShortPerContract(100m, 105m, "C", 1m), MarginCalculator.Requirement(legs, 100m, longShares: 150).Total);
	}

	[Fact]
	public void ShortPut_IsNotCoveredByStock()
	{
		var legs = new[] { new MarginLeg(Opt("SPY", 7, "P", 95m), 1, IsLong: false, 1m) };
		Assert.Equal(MarginCalculator.NakedShortPerContract(100m, 95m, "P", 1m), MarginCalculator.Requirement(legs, 100m, longShares: 100).Total);
	}

	[Fact]
	public void SpxCoversSpxw() =>
		Assert.Equal(500m, Margin(6000m, new(Opt("SPXW", 7, "P", 6000m), 1, IsLong: false, 10m), new(Opt("SPX", 7, "P", 5995m), 1, IsLong: true)));

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

		Assert.Equal(100m, result.MaxLoss);   // strike loss + debit is what can be lost
		Assert.Equal(50m, result.Margin);     // only the strike loss is collateral
	}
}

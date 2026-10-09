using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerConfigValidationTests
{
	private static AIConfig MinimalValidConfig()
	{
		var c = new AIConfig
		{
			Ticker = "GME",
			Indicators = new IndicatorsConfig { IvDefaultPct = 0.4m, StrikeStep = 0.50m },
		};
		c.Opener.Indicators = c.Indicators;
		return c;
	}

	[Fact]
	public void DefaultConfigIsValid()
	{
		var cfg = MinimalValidConfig();
		Assert.Null(AIConfigLoader.Validate(cfg));
	}

	[Fact]
	public void TopNPerTickerMustBePositive()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.TopNPerTicker = 0;
		Assert.Contains("opener.topNPerTicker", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void DirectionalFitWeightMustBeNonNegative()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Weights.DirectionalFit = -0.1m;
		Assert.Contains("opener.weights.directionalFit", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void ShortVerticalDeltaBoundsInRange()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Structures.ShortVertical.ShortDeltaMin = -0.1m;
		Assert.Contains("shortVertical.shortDeltaMin", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void LongCalendarDteMaxMustBeAtLeastMin()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Structures.LongCalendar.ShortDteMin = 10;
		cfg.Opener.Structures.LongCalendar.ShortDteMax = 5;
		Assert.Contains("longCalendar.shortDteMax", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void VolatilityFitWeightMustBeNonNegative()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Weights.VolatilityFit = -0.1m;
		Assert.Contains("opener.weights.volatilityFit", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void GammaRegimeWeightMustBeNonNegative()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Weights.GammaRegime = -0.1m;
		Assert.Contains("opener.weights.gammaRegime", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void StatArbWeightMustBeNonNegative()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Weights.StatArb = -0.1m;
		Assert.Contains("opener.weights.statArb", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void StrikeStepMustBePositive()
	{
		var cfg = MinimalValidConfig();
		cfg.Indicators.StrikeStep = 0m;
		Assert.Contains("indicators.strikeStep", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void DoubleCalendarWidthStepsMustBePresent()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Structures.DoubleCalendar.WidthSteps.Clear();
		Assert.Contains("doubleCalendar.widthSteps", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void IronButterflyWingStepsMustBePresent()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Structures.IronButterfly.WingSteps.Clear();
		Assert.Contains("ironButterfly.wingSteps", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void IronCondorWidthStepsMustBePresent()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Structures.IronCondor.WidthSteps.Clear();
		Assert.Contains("ironCondor.widthSteps", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void DoubleDiagonalLongWingStepsMustBePresent()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.Structures.DoubleDiagonal.LongWingSteps.Clear();
		Assert.Contains("doubleDiagonal.longWingSteps", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Theory]
	[InlineData(null, null, "13:04", true)]       // no window: always inside
	[InlineData(null, "11:00", "11:00", true)]    // inclusive at the latest end (the backtest's `> latest` skip)
	[InlineData(null, "11:00", "11:01", false)]
	[InlineData(null, "11:00", "13:04", false)]   // 2026-10-08: DC2's 13:04 crossing was past its 11:00 cutoff
	[InlineData("10:15", null, "10:14", false)]   // the earliest end, previously enforced by the backtest only
	[InlineData("10:15", null, "10:15", true)]
	[InlineData("10:15", "13:30", "12:00", true)]
	public void IsWithinEntryWindow_MatchesTheBacktestWindow(string? earliest, string? latest, string time, bool expected)
	{
		var opener = new OpenerConfig { EarliestEntryTimeEt = earliest, LatestEntryTimeEt = latest };
		Assert.Equal(expected, opener.IsWithinEntryWindow(TimeSpan.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));
	}
}

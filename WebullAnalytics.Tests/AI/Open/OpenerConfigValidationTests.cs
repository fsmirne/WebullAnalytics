using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerConfigValidationTests
{
	private static AIConfig MinimalValidConfig() => new()
	{
		Tickers = new() { "GME" },
		Opener = new OpenerConfig
		{
			StrikeSteps = new() { ["GME"] = 0.50m }
		}
	};

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
		cfg.Opener.DirectionalFitWeight = -0.1m;
		Assert.Contains("opener.directionalFitWeight", AIConfigLoader.Validate(cfg) ?? "");
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
		cfg.Opener.VolatilityFitWeight = -0.1m;
		Assert.Contains("opener.volatilityFitWeight", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void MaxPainWeightMustBeNonNegative()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.MaxPainWeight = -0.1m;
		Assert.Contains("opener.maxPainWeight", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void StatArbWeightMustBeNonNegative()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.StatArbWeight = -0.1m;
		Assert.Contains("opener.statArbWeight", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void TickerStrikeStepsMustBePositive()
	{
		var cfg = MinimalValidConfig();
		cfg.Opener.StrikeSteps["GME"] = 0m;
		Assert.Contains("opener.strikeSteps.GME", AIConfigLoader.Validate(cfg) ?? "");
	}

	[Fact]
	public void TickerStrikeStepsMustExistForAllConfiguredTickers()
	{
		var cfg = MinimalValidConfig();
		cfg.Tickers.Add("SPY");
		Assert.Contains("opener.strikeSteps.SPY", AIConfigLoader.Validate(cfg) ?? "");
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
}

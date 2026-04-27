using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerConfigValidationTests
{
	private static AIConfig MinimalValidConfig() => new()
	{
		Tickers = new() { "GME" }
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
}

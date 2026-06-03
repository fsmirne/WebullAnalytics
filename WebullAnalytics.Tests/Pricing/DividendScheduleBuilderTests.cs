using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

public class DividendScheduleBuilderTests
{
	private static readonly DateTime ExDate = new(2026, 6, 19);

	private static TickerEvents Events(DateTime? exDate, decimal? amount) =>
		new("SPY", NextEarningsDate: null, EarningsTime: null, NextExDividendDate: exDate, DividendAmount: amount);

	[Fact]
	public void UsesYahooAmountWhenPresent()
	{
		var schedule = DividendScheduleBuilder.BuildForTicker(Events(ExDate, 1.75m), spot: 754m, cfg: null);
		Assert.NotNull(schedule);
		Assert.Single(schedule!);
		Assert.Equal(ExDate.Date, schedule[0].ExDate);
		Assert.Equal(1.75m, schedule[0].Amount);
	}

	[Fact]
	public void FallsBackToConfigYieldWhenAmountMissing()
	{
		var cfg = new OpenerEventsConfig { DividendYield = 0.012m, DividendFrequency = 4 };
		var schedule = DividendScheduleBuilder.BuildForTicker(Events(ExDate, amount: null), spot: 800m, cfg: cfg);
		Assert.NotNull(schedule);
		Assert.Equal(800m * 0.012m / 4m, schedule![0].Amount); // 2.40
	}

	[Fact]
	public void NoExDate_ReturnsNull()
	{
		Assert.Null(DividendScheduleBuilder.BuildForTicker(Events(exDate: null, amount: 1.75m), spot: 800m, cfg: null));
		Assert.Null(DividendScheduleBuilder.BuildForTicker(events: null, spot: 800m, cfg: null));
	}

	[Fact]
	public void ExDateButNoAmountAndNoConfig_ReturnsNull()
	{
		Assert.Null(DividendScheduleBuilder.BuildForTicker(Events(ExDate, amount: null), spot: 800m, cfg: null));
		Assert.Null(DividendScheduleBuilder.BuildForTicker(Events(ExDate, amount: null), spot: 800m, cfg: new OpenerEventsConfig()));
	}
}

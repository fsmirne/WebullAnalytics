using WebullAnalytics.AI.Sources;
using Xunit;

namespace WebullAnalytics.Tests.AI.Sources;

public class OvernightSessionTests
{
	// 2026-11-22 is a Sunday; ET is UTC-5 (EST).
	private static DateTimeOffset Et(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-5));

	[Theory]
	[InlineData(22, 20, 0, true)]   // overnight open
	[InlineData(22, 23, 59, true)]
	[InlineData(23, 0, 0, true)]
	[InlineData(23, 3, 59, true)]   // last overnight minute
	[InlineData(23, 4, 0, false)]   // pre-market open
	[InlineData(23, 9, 30, false)]  // RTH
	[InlineData(20, 19, 59, false)] // Friday's last post-market minute
	public void ClassifiesBarStartsByEtClock(int day, int hour, int minute, bool expected) =>
		Assert.Equal(expected, WebullIntradayBars.IsOvernightSession(Et(day, hour, minute)));

	[Fact]
	public void UsesEtNotTheInstantsOwnOffset() =>
		Assert.True(WebullIntradayBars.IsOvernightSession(new DateTimeOffset(2026, 11, 23, 2, 0, 0, TimeSpan.Zero))); // 21:00 ET Sunday
}

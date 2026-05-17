using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerExpiryHelpersTests
{
	[Fact]
	public void ThirdFridayInApril2026Is2026_04_17()
	{
		Assert.Equal(new DateTime(2026, 4, 17), OpenerExpiryHelpers.ThirdFridayInMonth(2026, 4));
	}

	[Fact]
	public void ThirdFridayInMay2026Is2026_05_15()
	{
		Assert.Equal(new DateTime(2026, 5, 15), OpenerExpiryHelpers.ThirdFridayInMonth(2026, 5));
	}

	[Fact]
	public void NextWeeklyExpiriesInRangeReturnsFridaysOnly()
	{
		var asOf = new DateTime(2026, 4, 20); // Monday
		var result = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte: 3, maxDte: 10).ToList();
		Assert.All(result, d => Assert.Equal(DayOfWeek.Friday, d.DayOfWeek));
		Assert.All(result, d => Assert.InRange((d - asOf.Date).Days, 3, 10));
	}

	[Fact]
	public void NextWeeklyExpiriesInRangeFromMondayIncludesFriday()
	{
		var asOf = new DateTime(2026, 4, 20); // Monday; Friday = 2026-04-24, DTE = 4
		var result = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte: 3, maxDte: 10).ToList();
		Assert.Contains(new DateTime(2026, 4, 24), result);
	}

	[Fact]
	public void MonthlyExpiriesInRangeReturnsThirdFridays()
	{
		var asOf = new DateTime(2026, 4, 1);
		var result = OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, minDte: 0, maxDte: 60).ToList();
		Assert.Contains(new DateTime(2026, 4, 17), result);
		Assert.Contains(new DateTime(2026, 5, 15), result);
	}

	[Fact]
	public void NextDailyExpiriesInRangeIncludesTodayWhenMinDteIsZero()
	{
		var asOf = new DateTime(2026, 4, 20); // Monday — market open
		var result = OpenerExpiryHelpers.NextDailyExpiriesInRange(asOf, minDte: 0, maxDte: 4).ToList();
		Assert.Contains(new DateTime(2026, 4, 20), result); // today
		Assert.Contains(new DateTime(2026, 4, 21), result); // Tue
		Assert.Contains(new DateTime(2026, 4, 22), result); // Wed
		Assert.Contains(new DateTime(2026, 4, 23), result); // Thu
		Assert.Contains(new DateTime(2026, 4, 24), result); // Fri
		Assert.DoesNotContain(new DateTime(2026, 4, 25), result); // Sat
	}

	[Fact]
	public void NextDailyExpiriesInRangeSkipsWeekendsAndHolidays()
	{
		// Good Friday 2026 is April 3 — market closed. Friday/weekend skipped.
		var asOf = new DateTime(2026, 4, 2); // Thursday
		var result = OpenerExpiryHelpers.NextDailyExpiriesInRange(asOf, minDte: 0, maxDte: 5).ToList();
		Assert.Contains(new DateTime(2026, 4, 2), result);
		Assert.DoesNotContain(new DateTime(2026, 4, 3), result); // Good Friday
		Assert.DoesNotContain(new DateTime(2026, 4, 4), result); // Sat
		Assert.DoesNotContain(new DateTime(2026, 4, 5), result); // Sun
		Assert.Contains(new DateTime(2026, 4, 6), result); // Mon
	}

	[Fact]
	public void NextMultiWeeklyExpiriesYieldsOnlyMonWedFri()
	{
		var asOf = new DateTime(2026, 4, 20); // Monday
		var result = OpenerExpiryHelpers.NextMultiWeeklyExpiriesInRange(asOf, minDte: 0, maxDte: 14).ToList();
		Assert.All(result, d => Assert.Contains(d.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }));
		Assert.Contains(new DateTime(2026, 4, 20), result); // Mon
		Assert.Contains(new DateTime(2026, 4, 22), result); // Wed
		Assert.Contains(new DateTime(2026, 4, 24), result); // Fri
		Assert.DoesNotContain(new DateTime(2026, 4, 21), result); // Tue
		Assert.DoesNotContain(new DateTime(2026, 4, 23), result); // Thu
	}

	[Fact]
	public void NextExpiriesForTickerDispatchesByCadence()
	{
		var asOf = new DateTime(2026, 4, 20); // Monday
		var spy = OpenerExpiryHelpers.NextExpiriesForTicker("SPY", asOf, 0, 4).ToList();
		var iwm = OpenerExpiryHelpers.NextExpiriesForTicker("IWM", asOf, 0, 4).ToList();
		var gme = OpenerExpiryHelpers.NextExpiriesForTicker("GME", asOf, 0, 7).ToList();

		Assert.Equal(5, spy.Count); // Mon–Fri all included
		Assert.Equal(3, iwm.Count); // Mon/Wed/Fri only
		Assert.All(gme, d => Assert.Equal(DayOfWeek.Friday, d.DayOfWeek));
	}
}

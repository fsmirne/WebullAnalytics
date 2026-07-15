using System;
using WebullAnalytics;
using Xunit;

namespace WebullAnalytics.Tests.Core;

/// <summary>NYSE regular-hours gate used by the live quote-staleness guard: must know normal sessions,
/// weekends, full holidays, and early-close half-days (so the guard doesn't cry wolf off-hours).</summary>
public class MarketCalendarTests
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	// Build a DateTimeOffset at a given ET wall-clock time, using the correct EST/EDT offset for that date.
	private static DateTimeOffset Et(int y, int mo, int d, int h, int mi)
	{
		var local = new DateTime(y, mo, d, h, mi, 0);
		return new DateTimeOffset(local, NyTz.GetUtcOffset(local));
	}

	[Theory]
	[InlineData(2026, 7, 14, 10, 0, true)]   // Tue mid-session → open
	[InlineData(2026, 7, 14, 9, 30, true)]   // exactly the open
	[InlineData(2026, 7, 14, 16, 0, true)]   // exactly the close
	[InlineData(2026, 7, 14, 8, 0, false)]   // pre-market
	[InlineData(2026, 7, 14, 16, 30, false)] // after close (the off-hours false-positive we gate out)
	[InlineData(2026, 7, 11, 12, 0, false)]  // Saturday
	[InlineData(2026, 12, 25, 11, 0, false)] // Christmas (full holiday)
	public void RegularHours(int y, int mo, int d, int h, int mi, bool expected)
		=> Assert.Equal(expected, MarketCalendar.IsRegularHours(Et(y, mo, d, h, mi)));

	[Fact]
	public void EarlyCloseHalfDayEndsAtOnePmEt()
	{
		// Friday after Thanksgiving 2026-11-27 is a 13:00 ET early close.
		Assert.True(MarketCalendar.IsRegularHours(Et(2026, 11, 27, 12, 0)));   // before 13:00 → open
		Assert.False(MarketCalendar.IsRegularHours(Et(2026, 11, 27, 14, 0)));  // after 13:00 → closed
	}
}

using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Replay;

public class IntradayTapeBackfillTests
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private static DateTime EtToUtc(int y, int mo, int d, int h, int mi) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), NyTz);

	private static MinuteBar Bar(DateTime utc) => new(new DateTimeOffset(utc, TimeSpan.Zero), 100m, 100m, 100m, 100m, 0);

	// RTH bars 09:30 through lastHh:lastMm ET on 2026-10-20 (a Tuesday, regular session).
	private static List<MinuteBar> SessionThrough(int lastHh, int lastMm)
	{
		var bars = new List<MinuteBar>();
		for (var t = new DateTime(2026, 10, 20, 9, 30, 0); t <= new DateTime(2026, 10, 20, lastHh, lastMm, 0); t = t.AddMinutes(1))
			bars.Add(Bar(TimeZoneInfo.ConvertTimeToUtc(t, NyTz)));
		return bars;
	}

	[Fact]
	public void FrozenMidSession_IsBehind()
	{
		// The 2026-10-20 case: the file stopped at 12:23 and nothing wrote it after.
		Assert.True(IntradayTapeBackfill.IsBehind(SessionThrough(12, 23), EtToUtc(2026, 10, 20, 13, 30)));
	}

	[Fact]
	public void LiveWithinOneMinuteGrace_IsNotBehind()
	{
		// At 13:30 the newest published bar is 13:29; 13:28 is within the one-minute publish grace.
		Assert.False(IntradayTapeBackfill.IsBehind(SessionThrough(13, 28), EtToUtc(2026, 10, 20, 13, 30)));
		Assert.True(IntradayTapeBackfill.IsBehind(SessionThrough(13, 27), EtToUtc(2026, 10, 20, 13, 30)));
	}

	[Fact]
	public void AfterTheClose_NeedsTheFinalBar()
	{
		Assert.False(IntradayTapeBackfill.IsBehind(SessionThrough(15, 59), EtToUtc(2026, 10, 20, 18, 0)));
		Assert.True(IntradayTapeBackfill.IsBehind(SessionThrough(15, 30), EtToUtc(2026, 10, 20, 18, 0)));
	}

	[Fact]
	public void BeforeTheFirstBar_NeverBehind()
	{
		Assert.False(IntradayTapeBackfill.IsBehind(new List<MinuteBar>(), EtToUtc(2026, 10, 20, 9, 30)));
		Assert.True(IntradayTapeBackfill.IsBehind(new List<MinuteBar>(), EtToUtc(2026, 10, 20, 9, 45)));
	}

	[Fact]
	public void PreMarketBarsDoNotCount()
	{
		var bars = new List<MinuteBar> { Bar(EtToUtc(2026, 10, 20, 8, 0)), Bar(EtToUtc(2026, 10, 20, 9, 29)) };
		Assert.True(IntradayTapeBackfill.IsBehind(bars, EtToUtc(2026, 10, 20, 10, 0)));
	}

	[Fact]
	public void NonTradingDay_NeverBehind() => Assert.False(IntradayTapeBackfill.IsBehind(new List<MinuteBar>(), EtToUtc(2026, 10, 18, 12, 0)));

	[Fact]
	public void EarlyClose_EndsAt1259()
	{
		// 2026-11-27, day after Thanksgiving, closes 13:00.
		var bars = new List<MinuteBar>();
		for (var t = new DateTime(2026, 11, 27, 9, 30, 0); t <= new DateTime(2026, 11, 27, 12, 59, 0); t = t.AddMinutes(1))
			bars.Add(Bar(TimeZoneInfo.ConvertTimeToUtc(t, NyTz)));
		Assert.False(IntradayTapeBackfill.IsBehind(bars, EtToUtc(2026, 11, 27, 15, 0)));
	}
}

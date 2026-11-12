using WebullAnalytics;
using Xunit;

namespace WebullAnalytics.Tests.Core;

/// <summary>--date's grammar: now, fill, a date (that session at 10:00 ET) or an exact minute inside the regular session.</summary>
public class AsOfTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("now")]
	[InlineData("NOW")]
	public void Now_IsTheLiveMarket(string? text)
	{
		Assert.True(AsOf.TryParse(text, out var value, out _));
		Assert.Equal(AsOfKind.Now, value.Kind);
		Assert.Null(value.Instant);
	}

	[Fact]
	public void Fill_IsResolvedLater()
	{
		Assert.True(AsOf.TryParse("fill", out var value, out _));
		Assert.Equal(AsOfKind.Fill, value.Kind);
		Assert.Null(value.Instant);
	}

	[Fact]
	public void DateAlone_IsTenAmEt()
	{
		Assert.True(AsOf.TryParse("2026-10-09", out var value, out _));
		Assert.Equal(new DateTime(2026, 10, 9, 10, 0, 0), value.Instant);
	}

	[Fact]
	public void DateAlone_IsTenAmOnAHalfDayToo()
	{
		Assert.True(AsOf.TryParse("2026-11-27", out var value, out _)); // the day after Thanksgiving closes at 13:00
		Assert.Equal(new DateTime(2026, 11, 27, 10, 0, 0), value.Instant);
	}

	[Theory]
	[InlineData("2026-10-09T14:30")]
	[InlineData("2026-10-09 14:30")]
	public void DateAndTime_IsThatMinute(string text)
	{
		Assert.True(AsOf.TryParse(text, out var value, out _));
		Assert.Equal(new DateTime(2026, 10, 9, 14, 30, 0), value.Instant);
	}

	[Theory]
	[InlineData("2026-10-09T09:29")]
	[InlineData("2026-10-09T16:01")]
	[InlineData("2026-11-27T13:30")] // after a half-day's 13:00 close
	public void TimeOutsideTheSession_IsRejected(string text)
	{
		Assert.False(AsOf.TryParse(text, out _, out var error));
		Assert.Contains("outside", error);
	}

	[Theory]
	[InlineData("10/09/2026")]
	[InlineData("2026-10-09T10")]
	[InlineData("yesterday")]
	public void Malformed_IsRejected(string text) => Assert.False(AsOf.TryParse(text, out _, out _));

	[Fact]
	public void Fill_NeedsAFill() => Assert.Equal("--date fill needs --fill", AsOf.Validate("fill", fillAllowed: false, spot: null));

	[Fact]
	public void Spot_IsRejectedAtAPastInstant()
	{
		Assert.NotNull(AsOf.Validate("2026-10-09", fillAllowed: false, spot: "SPY:700"));
		Assert.NotNull(AsOf.Validate("fill", fillAllowed: true, spot: "SPY:700"));
		Assert.Null(AsOf.Validate("now", fillAllowed: false, spot: "SPY:700"));
		Assert.Null(AsOf.Validate(DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"), fillAllowed: false, spot: "SPY:700"));
	}
}

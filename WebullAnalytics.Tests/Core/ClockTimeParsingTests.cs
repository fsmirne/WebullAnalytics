using System;
using WebullAnalytics;
using Xunit;

namespace WebullAnalytics.Tests.Core;

/// <summary>Entry-window and gate times are wall-clock HH:mm. TimeSpan.TryParse accepted "0945" as 945 days,
/// which passed validation and silently suppressed every open (`--open-after 0945` → 0 opens).</summary>
public class ClockTimeParsingTests
{
	[Theory]
	[InlineData("09:45", 9, 45)]
	[InlineData("9:45", 9, 45)]
	[InlineData(" 15:30 ", 15, 30)]
	public void AcceptsHourMinute(string text, int h, int m)
	{
		Assert.True(ParsingHelpers.TryParseClockTime(text, out var t));
		Assert.Equal(new TimeSpan(h, m, 0), t);
	}

	[Theory]
	[InlineData("0945")]
	[InlineData("945")]
	[InlineData("25:00")]
	[InlineData("09:60")]
	[InlineData("1.09:45")]
	[InlineData("")]
	[InlineData(null)]
	public void RejectsNonClockValues(string? text)
	{
		Assert.False(ParsingHelpers.TryParseClockTime(text, out _));
	}
}

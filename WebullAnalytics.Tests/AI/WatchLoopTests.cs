using Spectre.Console.Cli;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class WatchLoopTests
{
	[Theory]
	[InlineData("6h", 6 * 3600)]
	[InlineData("90m", 90 * 60)]
	[InlineData("45s", 45)]
	[InlineData("1H", 3600)]
	[InlineData("30M", 1800)]
	[InlineData("10S", 10)]
	public void TryParseDuration_ValidInputs_ParsesCorrectly(string input, int expectedSeconds)
	{
		var ok = AIWatchSettings.TryParseDuration(input, out var span);
		Assert.True(ok);
		Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), span);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData(null)]
	[InlineData("abc")]
	[InlineData("0h")]
	[InlineData("-5m")]
	[InlineData("10x")]
	public void TryParseDuration_InvalidInputs_ReturnsFalse(string? input)
	{
		var ok = AIWatchSettings.TryParseDuration(input!, out var span);
		Assert.False(ok);
		Assert.Equal(TimeSpan.Zero, span);
	}

	[Fact]
	public void Validate_TickRange_ValidatesCorrectly()
	{
		var s1 = new AIWatchSettings { Tick = 0 };
		Assert.False(s1.Validate().Successful);

		var s2 = new AIWatchSettings { Tick = 3601 };
		Assert.False(s2.Validate().Successful);

		var s3 = new AIWatchSettings { Tick = 15 };
		Assert.True(s3.Validate().Successful);
	}

	[Fact]
	public void Validate_TifAndDuration_ValidatesCorrectly()
	{
		var s1 = new AIWatchSettings { Tif = "invalid" };
		Assert.False(s1.Validate().Successful);

		var s2 = new AIWatchSettings { Tif = "DAY" };
		Assert.True(s2.Validate().Successful);

		var s3 = new AIWatchSettings { Tif = "gtc" };
		Assert.True(s3.Validate().Successful);

		var s4 = new AIWatchSettings { Duration = "invalid" };
		Assert.False(s4.Validate().Successful);

		var s5 = new AIWatchSettings { Start = "invalid" };
		Assert.False(s5.Validate().Successful);

		var s6 = new AIWatchSettings { Start = "09:30:00" };
		Assert.True(s6.Validate().Successful);
	}
}

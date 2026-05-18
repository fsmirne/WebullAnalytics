using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class IntradayTapeIndicatorsTests
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private static DateTimeOffset NyMoment(int hour, int minute) =>
		new DateTimeOffset(new DateTime(2026, 5, 18, hour, minute, 0, DateTimeKind.Unspecified), NyTz.GetUtcOffset(new DateTime(2026, 5, 18, hour, minute, 0))).ToUniversalTime();

	private static MinuteBar Bar(DateTimeOffset ts, decimal close, long volume = 0) =>
		new MinuteBar(ts, close, close + 0.5m, close - 0.5m, close, volume);

	[Fact]
	public void Compute_NoBars_ReturnsNull()
	{
		var cfg = new IntradayTapeConfig();
		var result = IntradayTapeIndicators.Compute(Array.Empty<MinuteBar>(), prevClose: 100m, NyMoment(10, 0), cfg);
		Assert.Null(result);
	}

	[Fact]
	public void Compute_FewerThanMinBars_ReturnsNull()
	{
		var cfg = new IntradayTapeConfig { MinBars = 5 };
		var bars = new[]
		{
			Bar(NyMoment(9, 30), 5100m),
			Bar(NyMoment(9, 31), 5101m),
		};
		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 32), cfg);
		Assert.Null(result);
	}

	[Fact]
	public void Compute_FlatSession_ZeroScore()
	{
		var cfg = new IntradayTapeConfig();
		var bars = Enumerable.Range(30, 10).Select(m => Bar(NyMoment(9, m), 5100m)).ToList();
		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 40), cfg);

		Assert.NotNull(result);
		Assert.Equal(0m, result.Score);
		Assert.Equal(0m, result.GapScore);
		Assert.Equal(0m, result.OpenToNowScore);
	}

	[Fact]
	public void Compute_OneHalfPercentRally_PositiveOpenToNowScore()
	{
		var cfg = new IntradayTapeConfig();
		// Open at 9:30 = 5100; bars climb to 5125.50 (+0.5%) by 9:40.
		var bars = new List<MinuteBar>();
		for (int m = 30; m <= 40; m++)
			bars.Add(Bar(NyMoment(9, m), 5100m + (m - 30) * 2.55m));

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 40), cfg);

		Assert.NotNull(result);
		// Open-to-now: (5125.5 - 5100) / 5100 * 100 = 0.5
		Assert.Equal(0.5m, result.OpenToNowScore, 2);
		Assert.True(result.Score > 0.2m, $"Expected positive composite score, got {result.Score}");
	}

	[Fact]
	public void Compute_NegativeGap_NegativeGapScore()
	{
		var cfg = new IntradayTapeConfig();
		// Prev close 5100, today opens at 5074.50 (-0.5%), then flat.
		var bars = Enumerable.Range(30, 10).Select(m => Bar(NyMoment(9, m), 5074.50m)).ToList();

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 40), cfg);

		Assert.NotNull(result);
		Assert.Equal(-0.5m, result.GapScore, 2);
		Assert.Equal(0m, result.OpenToNowScore);
	}

	[Fact]
	public void Compute_LargeMoveClamps_ScoreAtOne()
	{
		var cfg = new IntradayTapeConfig();
		// A 3% intraday move should clamp at +1.
		var bars = new List<MinuteBar>();
		bars.Add(Bar(NyMoment(9, 30), 5100m));
		for (int m = 31; m <= 40; m++) bars.Add(Bar(NyMoment(9, m), 5253m)); // +3%

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 40), cfg);

		Assert.NotNull(result);
		Assert.Equal(1m, result.OpenToNowScore);
	}

	[Fact]
	public void Compute_NoPrevClose_GapScoreZero()
	{
		var cfg = new IntradayTapeConfig();
		var bars = Enumerable.Range(30, 10).Select(m => Bar(NyMoment(9, m), 5100m + (m - 30) * 0.5m)).ToList();

		var result = IntradayTapeIndicators.Compute(bars, prevClose: null, NyMoment(9, 40), cfg);

		Assert.NotNull(result);
		Assert.Equal(0m, result.GapScore);
		Assert.True(result.OpenToNowScore > 0m);
	}

	[Fact]
	public void Compute_VwapDeviation_WithVolume_PicksVwap()
	{
		var cfg = new IntradayTapeConfig();
		// Heavy volume at low prices, light volume at high prices → VWAP is closer to low.
		var bars = new List<MinuteBar>
		{
			new MinuteBar(NyMoment(9, 30), 5100m, 5100.5m, 5099.5m, 5100m, 10000),
			new MinuteBar(NyMoment(9, 31), 5100m, 5100.5m, 5099.5m, 5100m, 10000),
			new MinuteBar(NyMoment(9, 32), 5100m, 5100.5m, 5099.5m, 5100m, 10000),
			new MinuteBar(NyMoment(9, 33), 5100m, 5100.5m, 5099.5m, 5100m, 10000),
			new MinuteBar(NyMoment(9, 34), 5110m, 5110.5m, 5109.5m, 5110m, 100),
		};

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 34), cfg);

		Assert.NotNull(result);
		// VWAP is heavily weighted toward 5100; current close 5110 is significantly above → positive deviation.
		Assert.True(result.VwapDeviationScore > 0m, $"Expected positive vwap dev, got {result.VwapDeviationScore}");
	}

	[Fact]
	public void Compute_ZeroVolume_FallsBackToTwap()
	{
		var cfg = new IntradayTapeConfig();
		// All zero-volume (cash index). TWAP of the typical prices ≈ 5102.5; last close 5105 is above.
		var bars = new List<MinuteBar>
		{
			Bar(NyMoment(9, 30), 5100m, 0),
			Bar(NyMoment(9, 31), 5100m, 0),
			Bar(NyMoment(9, 32), 5102m, 0),
			Bar(NyMoment(9, 33), 5104m, 0),
			Bar(NyMoment(9, 34), 5105m, 0),
		};

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 34), cfg);

		Assert.NotNull(result);
		Assert.NotEqual(0m, result.VwapDeviationScore);
	}

	[Fact]
	public void Compute_ZeroWeights_ReturnsNull()
	{
		var cfg = new IntradayTapeConfig { GapWeight = 0m, OpenToNowWeight = 0m, VwapDeviationWeight = 0m };
		var bars = Enumerable.Range(30, 10).Select(m => Bar(NyMoment(9, m), 5100m)).ToList();

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 40), cfg);

		Assert.Null(result);
	}

	[Fact]
	public void Compute_FiltersOutOtherDates()
	{
		var cfg = new IntradayTapeConfig { MinBars = 3 };
		// Mix today's bars with yesterday's bars.
		var bars = new List<MinuteBar>
		{
			Bar(new DateTimeOffset(2026, 5, 15, 14, 30, 0, TimeSpan.Zero), 5050m), // Friday in UTC = Friday ET
			Bar(new DateTimeOffset(2026, 5, 15, 14, 31, 0, TimeSpan.Zero), 5050m),
			Bar(NyMoment(9, 30), 5100m), // Monday
			Bar(NyMoment(9, 31), 5101m),
			Bar(NyMoment(9, 32), 5102m),
		};

		var result = IntradayTapeIndicators.Compute(bars, prevClose: 5100m, NyMoment(9, 32), cfg);

		Assert.NotNull(result);
		Assert.Equal(3, result.BarCount); // Only today's bars counted.
	}
}

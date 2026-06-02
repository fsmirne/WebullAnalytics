using WebullAnalytics.AI.Analysis;
using Xunit;

namespace WebullAnalytics.Tests.AI.Analysis;

public class SeriesIndicatorsTests
{
	[Fact]
	public void Ema_ConstantSeries_EqualsConstantAfterSeed()
	{
		var ema = SeriesIndicators.Ema(Enumerable.Repeat(10m, 8).ToList(), 3);
		Assert.Null(ema[1]);          // before seed
		Assert.Equal(10m, ema[2]);    // seed = SMA of first 3
		Assert.Equal(10m, ema[7]);    // stays constant
	}

	[Fact]
	public void Rsi_MonotonicUp_Is100_MonotonicDown_Is0()
	{
		var up = SeriesIndicators.Rsi(Enumerable.Range(1, 20).Select(i => (decimal)i).ToList());
		var down = SeriesIndicators.Rsi(Enumerable.Range(1, 20).Select(i => (decimal)(21 - i)).ToList());
		Assert.Null(up[13]);          // warm-up: need period+1 closes
		Assert.Equal(100m, up[14]);   // only gains → RSI 100
		Assert.Equal(0m, down[14]);   // only losses → RSI 0
	}

	[Fact]
	public void BollingerLower_KnownWindow()
	{
		// Classic σ example: mean 5, population σ 2 → lower = 5 − 2·2 = 1.
		var closes = new decimal[] { 2, 4, 4, 4, 5, 5, 7, 9 };
		var lower = SeriesIndicators.BollingerLower(closes, period: 8, k: 2m);
		Assert.Null(lower[6]);
		Assert.Equal(1m, lower[7]!.Value, precision: 6);
	}

	[Fact]
	public void Macd_ConstantSeries_AllZero()
	{
		var (line, signal, hist) = SeriesIndicators.Macd(Enumerable.Repeat(50m, 60).ToList());
		Assert.Equal(0m, line[59]!.Value, precision: 6);   // fast EMA == slow EMA
		Assert.Equal(0m, signal[59]!.Value, precision: 6);
		Assert.Equal(0m, hist[59]!.Value, precision: 6);
	}

	[Fact]
	public void Macd_RisingTrend_PositiveLineAndHistogram()
	{
		// Strictly increasing series: fast EMA leads slow EMA, so MACD line is positive.
		var (line, _, hist) = SeriesIndicators.Macd(Enumerable.Range(1, 80).Select(i => (decimal)i).ToList());
		Assert.True(line[79]!.Value > 0m);
		Assert.NotNull(hist[79]);
	}
}

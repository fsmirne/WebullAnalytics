using WebullAnalytics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>The time-decay grid's leading columns: the observation anchor, plus the instant a future --date evaluates to, else (pre-open)
/// today's open — each exactly once.</summary>
[Collection("EvaluationDate")]
public class BuildDateColumnsTests : IDisposable
{
	public void Dispose() => EvaluationDate.Reset();

	// A guaranteed open trading day, derived relatively so the test never goes stale.
	private static DateTime TradingDay() => MarketCalendar.NextOpenOnOrAfter(DateTime.Today.AddDays(14));

	/// <summary>The regression: live during RTH the anchor and the eval instant are both "now"; read from two clock calls the second landed
	/// microseconds later, read as a future instant, and the grid showed two columns for today.</summary>
	[Fact]
	public void LiveDuringRth_HasOneColumnForToday()
	{
		EvaluationDate.Reset();
		var now = TradingDay() + new TimeSpan(11, 41, 7);
		var columns = TimeDecayGridBuilder.BuildDateColumns(now.Date.AddDays(30), 12, now);
		Assert.Equal(now, columns[0]);
		Assert.Single(columns, c => c.Date == now.Date);
	}

	[Fact]
	public void LivePreOpen_AddsTodaysOpenAfterTheLastClose()
	{
		EvaluationDate.Reset();
		var now = TradingDay() + new TimeSpan(8, 0, 0);
		var columns = TimeDecayGridBuilder.BuildDateColumns(now.Date.AddDays(30), 12, now);
		Assert.True(columns[0] < now.Date, $"the anchor is the prior session's close, got {columns[0]:yyyy-MM-dd HH:mm}");
		Assert.Equal(now.Date + OptionMath.MarketOpen, columns[1]);
	}

	[Fact]
	public void FutureDate_AddsTheEvaluationInstant()
	{
		var now = TradingDay() + new TimeSpan(11, 41, 7);
		var pin = MarketCalendar.NextOpenOnOrAfter(now.Date.AddDays(7)) + AsOf.DefaultTime;
		EvaluationDate.Set(pin);
		var columns = TimeDecayGridBuilder.BuildDateColumns(pin.Date.AddDays(30), 12, now);
		Assert.Equal(now, columns[0]);
		Assert.Single(columns, c => c.Date == pin.Date);
		Assert.Contains(pin, columns);
	}
}

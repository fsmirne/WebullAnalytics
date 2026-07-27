using WebullAnalytics;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

/// <summary>Pins the observation-anchor semantics for a pinned evaluation date. A PAST/TODAY --date
/// (historical replay, report --until, proposal-snapshot replay, backtest) anchors quotes at that date's
/// session open. A FUTURE --date is a forward what-if on quotes struck NOW, so the anchor must stay at the
/// real observation moment — otherwise calibration/greeks use the future date's shorter DTE and forward
/// pricing shows zero elapsed decay.</summary>
[Collection("EvaluationDate")]
public class ObservationInstantTests : IDisposable
{
	public void Dispose() => EvaluationDate.Reset();

	[Fact]
	public void PastOverride_AnchorsAtThatDatesSessionOpen()
	{
		var past = DateTime.Today.AddDays(-30);
		EvaluationDate.Set(past);
		Assert.Equal(past + OptionMath.MarketOpen, OptionMath.ObservationInstant());
	}

	[Fact]
	public void TodayOverride_UsesLiveInstant_NotPinnedOpen()
	{
		// A same-day --date is NOT historical: it must price at the live run-time instant (RTH now / last
		// close off-hours), identical to no override — not a phantom 09:30 that ignores the day's elapsed theta.
		EvaluationDate.Reset();
		var live = OptionMath.ObservationInstant();
		EvaluationDate.Set(DateTime.Today);
		var pinned = OptionMath.ObservationInstant();
		Assert.Equal(live.Date, pinned.Date);
		Assert.True(Math.Abs((pinned - live).TotalMinutes) < 1, $"today-override {pinned} should track the live instant {live}, not pin to 09:30");
	}

	[Fact]
	public void FutureOverride_AnchorsAtRealObservationMoment_NotTheFutureDate()
	{
		var future = DateTime.Today.AddDays(7);
		EvaluationDate.Set(future);
		var anchor = OptionMath.ObservationInstant();

		// Must NOT anchor at the future date (that would zero the elapsed decay); the quotes were struck now.
		Assert.NotEqual(future + OptionMath.MarketOpen, anchor);
		Assert.True(anchor.Date <= DateTime.Today, $"future-date anchor should be at/before today, got {anchor:yyyy-MM-dd HH:mm}");
	}
}

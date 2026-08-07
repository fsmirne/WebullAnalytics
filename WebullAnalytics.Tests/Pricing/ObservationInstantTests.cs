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

	// EvaluationInstant is the eval-TO instant (where the mark is projected), distinct from the quote anchor
	// above. `now` is injected so the branch logic is deterministic regardless of the wall clock at test time.

	[Fact]
	public void EvaluationInstant_FutureDate_IsThatDaysOpen()
	{
		var now = MondayNoon();
		EvaluationDate.Set(now.Date.AddDays(7));
		Assert.Equal(now.Date.AddDays(7) + OptionMath.MarketOpen, OptionMath.EvaluationInstant(now));
	}

	[Fact]
	public void EvaluationInstant_ExplicitToday_PreOpenTradingDay_ProjectsToTodaysOpen()
	{
		// The regression this guards: a --date that was "future" at 23:59 must not collapse to the previous
		// close one minute after midnight. Pre-open on the same trading day → today's 09:30 open, not last close.
		var preOpen = MondayNoon().Date + new TimeSpan(2, 0, 0); // 02:00 on a trading day
		EvaluationDate.Set(preOpen.Date);
		Assert.Equal(preOpen.Date + OptionMath.MarketOpen, OptionMath.EvaluationInstant(preOpen));
	}

	[Fact]
	public void EvaluationInstant_ExplicitToday_AfterOpen_UsesObservationInstant_NotPhantomOpen()
	{
		// Once RTH begins the explicit same-day --date must track the run time (ObservationInstant), not pin 09:30.
		EvaluationDate.Reset();
		var live = OptionMath.ObservationInstant();
		EvaluationDate.Set(DateTime.Today);
		var afterOpenNow = DateTime.Today + new TimeSpan(12, 0, 0);
		// Compare within a tolerance: during RTH ObservationInstant() returns the live wall-clock, so two calls
		// differ by microseconds — a phantom 09:30 would be off by hours, which this still catches.
		var expected = OptionMath.ObservationInstant();
		Assert.True(Math.Abs((OptionMath.EvaluationInstant(afterOpenNow) - expected).TotalSeconds) < 2, "explicit-today after-open must track ObservationInstant (run-time now), not a phantom 09:30");
	}

	[Fact]
	public void EvaluationInstant_NoOverride_PreOpen_StaysObservationInstant_NotTodaysOpen()
	{
		// The pre-open→today's-open projection is scoped to an EXPLICIT same-day --date. With no --date the
		// "current" value must remain the honest last-known mark (ObservationInstant), never a phantom 09:30.
		EvaluationDate.Reset();
		var preOpen = MondayNoon().Date + new TimeSpan(2, 0, 0);
		// Tolerance for the same reason as above: RTH ObservationInstant() is the live clock, not a fixed value.
		var expected = OptionMath.ObservationInstant();
		Assert.True(Math.Abs((OptionMath.EvaluationInstant(preOpen) - expected).TotalSeconds) < 2, "no-override must track ObservationInstant, never a phantom today's-open");
	}

	// A guaranteed open trading day at noon, derived relatively so the test never goes stale.
	private static DateTime MondayNoon()
	{
		return MarketCalendar.NextOpenOnOrAfter(DateTime.Today.AddDays(14)) + new TimeSpan(12, 0, 0);
	}
}

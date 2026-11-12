using WebullAnalytics;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

/// <summary>Pins the observation-anchor semantics for a pinned evaluation instant (<see cref="AsOf"/>). A PAST instant (--date, a fill,
/// report --until, proposal-snapshot replay) anchors quotes at that instant — or at the minute its books were struck. A FUTURE instant is a
/// forward what-if on quotes struck NOW, so the anchor must stay at the real observation moment — otherwise calibration/greeks use the
/// future instant's shorter life and forward pricing shows zero elapsed decay — while the eval-TO instant is the pinned one.</summary>
[Collection("EvaluationDate")]
public class ObservationInstantTests : IDisposable
{
	public void Dispose()
	{
		EvaluationDate.Reset();
		OptionMath.HistoricalQuotesStruckAt = null;
	}

	[Fact]
	public void PastOverride_AnchorsAtThePinnedInstant()
	{
		var past = DateTime.Today.AddDays(-30) + AsOf.DefaultTime;
		EvaluationDate.Set(past);
		Assert.Equal(past, OptionMath.ObservationInstant());
	}

	/// <summary>A fill is read at the minute boundary nearest it: the anchor is that boundary, so its IV solve runs on the books' own clock. A
	/// struck-at instant on another date is stale and ignored.</summary>
	[Fact]
	public void PastOverride_AnchorsAtTheHistoricalQuotesInstantWhenSetForThatDate()
	{
		var fill = DateTime.Today.AddDays(-30) + new TimeSpan(14, 7, 41);
		EvaluationDate.Set(fill);
		OptionMath.HistoricalQuotesStruckAt = fill.Date + new TimeSpan(14, 8, 0);
		Assert.Equal(fill.Date + new TimeSpan(14, 8, 0), OptionMath.ObservationInstant());

		OptionMath.HistoricalQuotesStruckAt = fill.Date.AddDays(-1) + new TimeSpan(16, 0, 0);
		Assert.Equal(fill, OptionMath.ObservationInstant());
	}

	[Fact]
	public void FutureOverride_AnchorsAtRealObservationMoment_NotTheFutureInstant()
	{
		var future = DateTime.Today.AddDays(7) + AsOf.DefaultTime;
		EvaluationDate.Set(future);
		var anchor = OptionMath.ObservationInstant();

		// Must NOT anchor at the future instant (that would zero the elapsed decay); the quotes were struck now.
		Assert.NotEqual(future, anchor);
		Assert.True(anchor.Date <= DateTime.Today, $"future-instant anchor should be at/before today, got {anchor:yyyy-MM-dd HH:mm}");
	}

	// EvaluationInstant is the eval-TO instant (where the mark is projected), distinct from the quote anchor
	// above. `now` is injected so the branch logic is deterministic regardless of the wall clock at test time.

	[Fact]
	public void EvaluationInstant_FutureInstant_IsThatInstant()
	{
		var now = MondayNoon();
		var future = now.Date.AddDays(7) + new TimeSpan(14, 30, 0);
		EvaluationDate.Set(future);
		Assert.Equal(future, OptionMath.EvaluationInstant(now));
	}

	[Fact]
	public void EvaluationInstant_LaterToday_IsThatInstant()
	{
		// A --date for later today (run pre-open, or before the pinned minute) is still ahead: the live market projected forward to it.
		var preOpen = MondayNoon().Date + new TimeSpan(2, 0, 0);
		EvaluationDate.Set(preOpen.Date + AsOf.DefaultTime);
		Assert.Equal(preOpen.Date + AsOf.DefaultTime, OptionMath.EvaluationInstant(preOpen));
	}

	[Fact]
	public void EvaluationInstant_PastInstant_IsTheObservationInstant()
	{
		var past = MondayNoon().AddDays(-7);
		EvaluationDate.Set(past);
		Assert.Equal(past, OptionMath.EvaluationInstant(MondayNoon()));
	}

	[Fact]
	public void EvaluationInstant_NoOverride_PreOpen_StaysObservationInstant_NotTodaysOpen()
	{
		// With no --date the "current" value must remain the honest last-known mark (ObservationInstant), never a phantom 09:30.
		EvaluationDate.Reset();
		var preOpen = MondayNoon().Date + new TimeSpan(2, 0, 0);
		Assert.Equal(OptionMath.ObservationInstant(preOpen, OptionMath.LiveQuotesStruckAt), OptionMath.EvaluationInstant(preOpen));
	}

	// Live quote-stamp branch (pre-open). The stamp decides whether a premarket book is today's (priced for the
	// open → anchor 09:30) or a prior session's leftover (anchor that session's close).

	[Fact]
	public void PreOpen_BookStampedToday_AnchorsAtTodaysOpen()
	{
		var tradingDay = MondayNoon().Date;
		var now = tradingDay + new TimeSpan(8, 47, 0);
		var struck = MarketTime.EtToInstant(tradingDay + new TimeSpan(8, 46, 30));
		Assert.Equal(tradingDay + OptionMath.MarketOpen, OptionMath.ObservationInstant(now, struck));
	}

	[Fact]
	public void PreOpen_BookStampedPriorSession_AnchorsAtPriorClose()
	{
		var tradingDay = MondayNoon().Date;
		var priorSession = MarketCalendar.PreviousOpenOnOrBefore(tradingDay.AddDays(-1));
		var now = tradingDay + new TimeSpan(8, 47, 0);
		var struck = MarketTime.EtToInstant(priorSession + new TimeSpan(16, 14, 0));
		Assert.Equal(priorSession + OptionMath.MarketClose, OptionMath.ObservationInstant(now, struck));
	}

	[Fact]
	public void PreOpen_Unstamped_AnchorsAtPriorClose()
	{
		var tradingDay = MondayNoon().Date;
		var now = tradingDay + new TimeSpan(8, 47, 0);
		Assert.Equal(MarketCalendar.PreviousOpenOnOrBefore(tradingDay.AddDays(-1)) + OptionMath.MarketClose, OptionMath.ObservationInstant(now, null));
	}

	[Fact]
	public void NonTradingDay_IgnoresStamp_AnchorsAtLastClose()
	{
		// A weekend run has no session to open today: the last close stays the anchor whatever the stamp says.
		var tradingDay = MondayNoon().Date;
		var saturday = tradingDay.AddDays(DayOfWeek.Saturday - tradingDay.DayOfWeek + (tradingDay.DayOfWeek == DayOfWeek.Sunday ? -7 : 0));
		var now = saturday + new TimeSpan(10, 0, 0);
		var struck = MarketTime.EtToInstant(saturday + new TimeSpan(9, 0, 0));
		Assert.Equal(MarketCalendar.PreviousOpenOnOrBefore(saturday) + OptionMath.MarketClose, OptionMath.ObservationInstant(now, struck));
	}

	[Fact]
	public void Rth_IgnoresStamp_UsesNow()
	{
		var now = MondayNoon();
		Assert.Equal(now, OptionMath.ObservationInstant(now, MarketTime.EtToInstant(now.Date + new TimeSpan(8, 0, 0))));
	}

	// A guaranteed open trading day at noon, derived relatively so the test never goes stale.
	private static DateTime MondayNoon()
	{
		return MarketCalendar.NextOpenOnOrAfter(DateTime.Today.AddDays(14)) + new TimeSpan(12, 0, 0);
	}
}

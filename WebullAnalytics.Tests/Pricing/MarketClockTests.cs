using WebullAnalytics.Pricing;
using WebullAnalytics.Pricing.Clock;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

/// <summary>Pins the trading-time clock: the calendar weights reproduce the old (to − from)/365 exactly, a fitted clock
/// weights session / overnight / closed-day hours as specified (half-days included), and Black-Scholes on an
/// <see cref="OptionTime"/> equals plain calendar Black-Scholes with the vol rescaled to the same total variance.</summary>
public class MarketClockTests
{
	private static readonly DateTime Friday = new(2026, 10, 30);        // regular session
	private static readonly DateTime Monday = new(2026, 11, 2);
	private static readonly DateTime BlackFriday = new(2026, 11, 27);   // 13:00 early close

	[Fact]
	public void CalendarWeights_EqualTheOldCalendarYearFraction()
	{
		var from = Friday + new TimeSpan(15, 59, 0);
		var to = new DateTime(2026, 11, 27, 16, 0, 0);
		var t = MarketClock.Between(ClockWeights.Calendar, from, to);
		Assert.Equal((to - from).TotalDays / 365.0, t.VarianceYears);
		Assert.Equal(t.VarianceYears, t.CalendarYears);
	}

	[Fact]
	public void NotAfter_IsZero()
	{
		Assert.True(MarketClock.Between(new ClockWeights(0.15, 0.05), Monday, Monday).IsExpired);
		Assert.True(MarketClock.Between(new ClockWeights(0.15, 0.05), Monday, Friday).IsExpired);
	}

	[Fact]
	public void WeekendClose_ToMondayOpen_WeighsOvernightAndClosedDays()
	{
		var w = new ClockWeights(0.15, 0.05);
		// Fri 16:00 → Mon 09:30: Fri 8h overnight, Sat+Sun 48h closed, Mon 9.5h overnight.
		var hours = MarketClock.WeightedHours(w, Friday + OptionMath.MarketClose, Monday + OptionMath.MarketOpen);
		Assert.Equal(0.15 * (8 + 9.5) + 0.05 * 48, hours, 9);
	}

	[Fact]
	public void SessionHoursCountOne_AndHalfDayClosesAt13()
	{
		var w = new ClockWeights(0.15, 0.05);
		Assert.Equal(6.5, MarketClock.WeightedHours(w, Monday + OptionMath.MarketOpen, Monday + OptionMath.MarketClose), 9);
		// Half-day: 09:30–13:00 is session (3.5h), 13:00–16:00 is overnight-weighted.
		Assert.Equal(3.5 + 0.15 * 3, MarketClock.WeightedHours(w, BlackFriday + OptionMath.MarketOpen, BlackFriday + OptionMath.MarketClose), 9);
	}

	[Fact]
	public void FittedClock_KeepsCalendarYearsForCarry_AndNormalizesVarianceYears()
	{
		var w = new ClockWeights(0.15, 0.05);
		var from = Monday + OptionMath.MarketClose;
		var to = from.AddDays(365);
		var t = MarketClock.Between(w, from, to);
		Assert.Equal(1.0, t.CalendarYears, 9);
		Assert.InRange(t.VarianceYears, 0.97, 1.03);   // a calendar year ≈ one variance year by construction (252 sessions / 113 closed days)
	}

	[Fact]
	public void BlackScholes_OnOptionTime_EqualsCalendarBlackScholesWithRescaledVol()
	{
		var t = new OptionTime(0.010, 0.016);
		var sigmaCal = 0.18 * Math.Sqrt(0.010 / 0.016);
		var onClock = OptionMath.BlackScholes(760m, 760m, t, 0.04, 0.18m, "C");
		var onCalendar = OptionMath.BlackScholes(760m, 760m, OptionTime.Calendar(0.016), 0.04, (decimal)sigmaCal, "C");
		Assert.Equal((double)onCalendar, (double)onClock, 6);
	}

	[Fact]
	public void ImpliedVol_RoundTripsOnAFittedClock()
	{
		var t = new OptionTime(0.010, 0.016);
		var price = OptionMath.BlackScholes(760m, 765m, t, 0.04, 0.155m, "C");
		Assert.Equal(0.155m, Math.Round(OptionMath.ImpliedVol(760m, 765m, t, 0.04, price, "C"), 3));
	}
}

/// <summary>Swaps the process-wide fit lookup, so its tests must not overlap any other test class.</summary>
[CollectionDefinition("MarketClockFits", DisableParallelization = true)]
public class MarketClockFitsCollection { }

/// <summary>The fitted clock in force: lookup by instant (never a fit built on or after the day), and calendar-quoted vols carried onto it.</summary>
[Collection("MarketClockFits")]
public class MarketClockFitLookupTests : IDisposable
{
	private static ClockFit Fit(DateOnly through, double o, double n) => new("SPY", through, through.AddDays(-365), o, n, 200, 40, 0, 50_000, 0.15, 0.19);

	public MarketClockFitLookupTests()
	{
		var fits = new[] { Fit(new DateOnly(2026, 9, 30), 0.15, 0.05), Fit(new DateOnly(2026, 10, 30), 0.22, 0.13) };
		MarketClock.FitLookup = (_, at) => fits.LastOrDefault(f => f.FittedThrough < DateOnly.FromDateTime(at));
		MarketClock.ResetResolved();
	}

	public void Dispose()
	{
		TestClock.UseCalendarClock();
		MarketClock.PinnedDay = null;
		MarketClock.ResetResolved();
	}

	[Fact]
	public void OnePricingContext_OneFit_EvenForIntervalsStartingLater()
	{
		// Simulated 2026-10-15: the 09-30 fit is in force — also for an interval that starts after the 10-30 refit (no look-ahead).
		MarketClock.PinnedDay = new DateOnly(2026, 10, 15);
		var later = MarketClock.Between("SPY", new DateTime(2026, 11, 2, 9, 30, 0), new DateTime(2026, 11, 5, 16, 0, 0));
		Assert.Equal(MarketClock.Between(new ClockWeights(0.15, 0.05), new DateTime(2026, 11, 2, 9, 30, 0), new DateTime(2026, 11, 5, 16, 0, 0)), later);
	}

	[Fact]
	public void UsesTheNewestFitBuiltBeforeTheInstant_ElseCalendar()
	{
		Assert.Equal(new ClockWeights(0.22, 0.13), MarketClock.WeightsFor("SPY", new DateTime(2026, 11, 2, 9, 30, 0)));
		Assert.Equal(new ClockWeights(0.15, 0.05), MarketClock.WeightsFor("SPY", new DateTime(2026, 10, 30, 12, 0, 0)));   // the 10-30 fit isn't usable ON 10-30
		Assert.Equal(ClockWeights.Calendar, MarketClock.WeightsFor("SPY", new DateTime(2026, 9, 1)));
	}

	[Fact]
	public void WeekendDecay_IsMuchSmallerThanCalendar()
	{
		var fridayClose = new DateTime(2026, 10, 30, 16, 0, 0);
		var mondayOpen = new DateTime(2026, 11, 2, 9, 30, 0);
		var expiry = new DateTime(2026, 11, 5, 16, 0, 0);
		var w = new ClockWeights(0.22, 0.13);
		var atClose = MarketClock.Between(w, fridayClose, expiry);
		var atOpen = MarketClock.Between(w, mondayOpen, expiry);
		// Weekend share of the remaining variance: (0.22·17.5 + 0.13·48) / (4·(6.5 + 0.22·17.5) + 0.13·48) ≈ 21%, vs 2.73/6 ≈ 45% calendar.
		Assert.Equal((0.22 * 17.5 + 0.13 * 48) / (4 * (6.5 + 0.22 * 17.5) + 0.13 * 48), 1 - atOpen.VarianceYears / atClose.VarianceYears, 9);
	}

	[Fact]
	public void ClockSigma_InvertsCalendarSigma()
	{
		var t = MarketClock.Between("SPY", new DateTime(2026, 11, 2, 9, 30, 0), new DateTime(2026, 11, 27, 16, 0, 0));
		var onClock = OptionMath.ClockSigma(0.15m, t);
		Assert.Equal(0.15, OptionMath.CalendarSigma((double)onClock, t), 9);
	}
}

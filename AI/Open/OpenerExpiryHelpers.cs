using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

internal static class OpenerExpiryHelpers
{
	// Fraction of a year a full trading session represents (9:30–16:00 ET = 6.5h). Used as the
	// upper bound for 0DTE time-to-expiry when asOf is before the opening bell — matches the
	// backtest's open-of-day stepper.
	public const double ZeroDteTimeYears = 6.5 / 24.0 / 365.0;

	/// <summary>Years-to-expiry for the scorer. For 0DTE, uses the actual time-of-day between
	/// <paramref name="asOf"/> and market close (capped at one full session if asOf is before
	/// the opening bell). For >0DTE, returns <c>days/365</c> — preserves the calendar-day
	/// approximation used across the scenario grid and breakeven analytics. Returns 0 when
	/// already past the close on expiry day. Avoids the legacy <c>Math.Max(1, days)</c> 1-day
	/// floor that priced expiring 0DTE candidates as if they had a full day of vol remaining.</summary>
	public static double TimeYearsToExpiry(DateTime asOf, DateTime expiry)
	{
		var days = (expiry.Date - asOf.Date).Days;
		if (days > 0) return days / 365.0;
		var expirationTime = expiry.Date + OptionMath.MarketClose;
		if (asOf >= expirationTime) return 0.0;
		var openTime = expiry.Date + OptionMath.MarketOpen;
		var effectiveStart = asOf < openTime ? openTime : asOf;
		return (expirationTime - effectiveStart).TotalDays / 365.0;
	}

	// Tickers with Mon-Fri daily option expirations.
	public static readonly IReadOnlySet<string> DailyExpiryTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"SPX", "SPXW", "SPY", "QQQ", "XSP", "NDX"
	};

	// Tickers with Mon/Wed/Fri option expirations (multi-weekly cadence per the 2025 regulatory expansion).
	public static readonly IReadOnlySet<string> MultiWeeklyExpiryTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"IWM", "IBIT", "GLD", "SLV", "USO", "UNG",
		"GOOG", "GOOGL", "AMZN", "AAPL", "META", "MSFT", "NVDA", "TSLA", "AVGO"
	};

	private static DateTime AdjustToPreviousOpen(DateTime date) => MarketCalendar.PreviousOpenOnOrBefore(date);

	/// <summary>Dispatches to the right enumerator based on the ticker's listed expiry cadence:
	/// daily (Mon-Fri) for index ETFs/indices in <see cref="DailyExpiryTickers"/>; Mon/Wed/Fri for mega-caps
	/// and select ETFs in <see cref="MultiWeeklyExpiryTickers"/>; weekly Fridays for everything else.</summary>
	public static IEnumerable<DateTime> NextExpiriesForTicker(string ticker, DateTime asOf, int minDte, int maxDte)
	{
		if (DailyExpiryTickers.Contains(ticker)) return NextDailyExpiriesInRange(asOf, minDte, maxDte);
		if (MultiWeeklyExpiryTickers.Contains(ticker)) return NextMultiWeeklyExpiriesInRange(asOf, minDte, maxDte);
		return NextWeeklyExpiriesInRange(asOf, minDte, maxDte);
	}

	/// <summary>Enumerates every open trading day (Mon-Fri, holidays excluded) whose DTE lands in [minDte, maxDte].</summary>
	public static IEnumerable<DateTime> NextDailyExpiriesInRange(DateTime asOf, int minDte, int maxDte)
	{
		var start = asOf.Date.AddDays(minDte);
		var end = asOf.Date.AddDays(maxDte);
		for (var d = start; d <= end; d = d.AddDays(1))
			if (MarketCalendar.IsOpen(d))
				yield return d;
	}

	/// <summary>Enumerates Mon/Wed/Fri trading days whose DTE lands in [minDte, maxDte]. Holiday-closed days are skipped.</summary>
	public static IEnumerable<DateTime> NextMultiWeeklyExpiriesInRange(DateTime asOf, int minDte, int maxDte)
	{
		foreach (var d in NextDailyExpiriesInRange(asOf, minDte, maxDte))
		{
			if (d.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday)
				yield return d;
		}
	}

	/// <summary>Returns the 3rd-Friday date in the given month. No holiday adjustment — standard monthly expiries.</summary>
	/// <summary>True when <paramref name="expiry"/> is the last open trading day of its Mon–Sun week — a weekly
	/// or monthly expiry, not a Mon–Thu daily. Normally that's Friday, but it correctly keeps a holiday-shifted
	/// expiry (e.g. Thursday 2026-06-18 when Friday 06-19 is Juneteenth).</summary>
	public static bool IsWeekEndingExpiry(DateTime expiry)
	{
		for (var d = expiry.AddDays(1); d.DayOfWeek != DayOfWeek.Monday; d = d.AddDays(1))
			if (MarketCalendar.IsOpen(d)) return false;
		return true;
	}

	/// <summary>True when <paramref name="expiry"/> is the holiday-adjusted 3rd-Friday monthly expiry for its
	/// month (i.e., it equals <see cref="ThirdFridayInMonth"/> for the same year/month).</summary>
	public static bool IsMonthlyExpiry(DateTime expiry) =>
		IsWeekEndingExpiry(expiry) && expiry.Date == ThirdFridayInMonth(expiry.Year, expiry.Month);

	public static DateTime ThirdFridayInMonth(int year, int month)
	{
		var first = new DateTime(year, month, 1);
		var firstFridayOffset = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
		var thirdFriday = first.AddDays(firstFridayOffset + 14);
		return AdjustToPreviousOpen(thirdFriday);
	}

	/// <summary>Enumerates all Fridays strictly after <paramref name="asOf"/> whose DTE lands in [minDte, maxDte].</summary>
	public static IEnumerable<DateTime> NextWeeklyExpiriesInRange(DateTime asOf, int minDte, int maxDte)
	{
		var start = asOf.Date.AddDays(minDte);
		var end = asOf.Date.AddDays(maxDte);
		// Find the first Friday on or after `start`.
		var firstFridayOffset = ((int)DayOfWeek.Friday - (int)start.DayOfWeek + 7) % 7;
		for (var d = start.AddDays(firstFridayOffset); d <= end; d = d.AddDays(7))
		{
			var adjusted = AdjustToPreviousOpen(d);
			if (adjusted >= start && adjusted <= end)
				yield return adjusted;
		}
	}

	/// <summary>Enumerates 3rd-Friday monthlies whose DTE falls in [minDte, maxDte], ordered by date.</summary>
	public static IEnumerable<DateTime> MonthlyExpiriesInRange(DateTime asOf, int minDte, int maxDte)
	{
		var start = asOf.Date.AddDays(minDte);
		var end = asOf.Date.AddDays(maxDte);
		var year = start.Year;
		var month = start.Month;
		// Look at most 3 months ahead of `end` to guarantee coverage.
		var stopYear = end.Year;
		var stopMonth = end.Month + 1;
		if (stopMonth > 12) { stopMonth -= 12; stopYear++; }
		while (year < stopYear || (year == stopYear && month <= stopMonth))
		{
			var third = ThirdFridayInMonth(year, month);
			if (third >= start && third <= end) yield return third;
			month++;
			if (month > 12) { month = 1; year++; }
		}
	}
}

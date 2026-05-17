namespace WebullAnalytics.AI;

internal static class OpenerExpiryHelpers
{
	// Fraction of a year a 0DTE option has remaining at the daily simulator step. Matches the pricer's
	// open-of-day assumption so the scorer's scenario grid agrees with the price the trade actually fills at.
	public const double ZeroDteTimeYears = 6.5 / 24.0 / 365.0;

	/// <summary>Years-to-expiry consistent with the 0DTE pricer: returns a fractional trading day for
	/// expiries on/before <paramref name="asOf"/>, else <c>days/365</c>. Use this anywhere the scorer
	/// needs a time-in-years; avoids the legacy <c>Math.Max(1, days)</c> 1-day floor that doubled the
	/// scenario-grid sigma for 0DTE structures and made every candidate score negative.</summary>
	public static double TimeYearsToExpiry(DateTime asOf, DateTime expiry)
	{
		var days = (expiry.Date - asOf.Date).Days;
		return days <= 0 ? ZeroDteTimeYears : days / 365.0;
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

	private static DateTime AdjustToPreviousOpen(DateTime date)
	{
		var d = date.Date;
		while (!MarketCalendar.IsOpen(d))
			d = d.AddDays(-1);
		return d;
	}

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

namespace WebullAnalytics;

/// <summary>
/// US equity market open/close status for NYSE/CBOE-listed options.
/// Accounts for weekends and the standard NYSE holiday schedule.
/// </summary>
internal static class MarketCalendar
{
	/// <summary>Returns true if US options markets are open for regular trading on the given date.</summary>
	internal static bool IsOpen(DateTime date)
	{
		if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
			return false;
		return !IsHoliday(date);
	}

	/// <summary>The given date if it's a trading day, else the nearest open day before it. Used to map a
	/// nominal Friday expiry that lands on a holiday (e.g. Good Friday) to the day the contract actually
	/// expires (the preceding Thursday) — both the computed expiry helpers and the chain-date filter rely
	/// on this so we never enumerate a contract on a closed session.</summary>
	internal static DateTime PreviousOpenOnOrBefore(DateTime date)
	{
		var d = date.Date;
		while (!IsOpen(d)) d = d.AddDays(-1);
		return d;
	}

	/// <summary>True if <paramref name="date"/> is an NYSE/CBOE early-close (1:00pm ET) half-day: the Friday
	/// after Thanksgiving, July 3 (day before Independence Day), and December 24 (Christmas Eve) — each only
	/// when it is itself a regular trading day AND the adjacent full holiday falls on its own weekday, so a
	/// shifted observance (holiday landing on a weekend) doesn't turn the half-day into a full closure or a
	/// normal session. The rarer holiday-on-weekend early-close variants aren't modeled; add them if a
	/// window needs one. Note: index feeds (VIX) may keep printing ~15 min past the 13:00 equity close.</summary>
	internal static bool IsEarlyClose(DateTime date)
	{
		var d = date.Date;
		if (!IsOpen(d)) return false;
		int y = d.Year;
		if (d == NthWeekday(y, 11, DayOfWeek.Thursday, 4).AddDays(1)) return true;                            // Fri after Thanksgiving
		if (d == new DateTime(y, 7, 3) && Observed(new DateTime(y, 7, 4)) == new DateTime(y, 7, 4)) return true;   // July 3
		if (d == new DateTime(y, 12, 24) && Observed(new DateTime(y, 12, 25)) == new DateTime(y, 12, 25)) return true; // Christmas Eve
		return false;
	}

	// Ad-hoc US market closures outside the recurring NYSE holiday calendar. Add new entries as they
	// happen — the backtest's 2-year lookback only sees recent dates, so the historical 9/11, Hurricane
	// Sandy, and Bush 41 closures aren't tracked here.
	private static readonly HashSet<DateTime> AdHocClosures = new()
	{
		new DateTime(2025, 1, 9), // National Day of Mourning for President Jimmy Carter
	};

	private static bool IsHoliday(DateTime date)
	{
		if (AdHocClosures.Contains(date.Date)) return true;
		int y = date.Year;
		return date.Date == Observed(new DateTime(y, 1, 1))                         // New Year's Day
			|| date.Date == NthWeekday(y, 1, DayOfWeek.Monday, 3)                  // Martin Luther King Jr. Day
			|| date.Date == NthWeekday(y, 2, DayOfWeek.Monday, 3)                  // Presidents' Day
			|| date.Date == GoodFriday(y)                                           // Good Friday
			|| date.Date == LastWeekday(y, 5, DayOfWeek.Monday)                    // Memorial Day
			|| (y >= 2022 && date.Date == Observed(new DateTime(y, 6, 19)))         // Juneteenth (since 2022)
			|| date.Date == Observed(new DateTime(y, 7, 4))                        // Independence Day
			|| date.Date == NthWeekday(y, 9, DayOfWeek.Monday, 1)                  // Labor Day
			|| date.Date == NthWeekday(y, 11, DayOfWeek.Thursday, 4)               // Thanksgiving
			|| date.Date == Observed(new DateTime(y, 12, 25));                     // Christmas
	}

	// If a fixed holiday falls on Saturday, observe on the preceding Friday.
	// If it falls on Sunday, observe on the following Monday.
	private static DateTime Observed(DateTime holiday)
	{
		if (holiday.DayOfWeek == DayOfWeek.Saturday) return holiday.AddDays(-1);
		if (holiday.DayOfWeek == DayOfWeek.Sunday) return holiday.AddDays(1);
		return holiday;
	}

	// nth occurrence of a given weekday in a month (e.g. 3rd Monday of January).
	private static DateTime NthWeekday(int year, int month, DayOfWeek dow, int n)
	{
		var first = new DateTime(year, month, 1);
		int offset = ((int)dow - (int)first.DayOfWeek + 7) % 7;
		return first.AddDays(offset + (n - 1) * 7);
	}

	// Last occurrence of a given weekday in a month (e.g. last Monday of May).
	private static DateTime LastWeekday(int year, int month, DayOfWeek dow)
	{
		var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
		int offset = ((int)last.DayOfWeek - (int)dow + 7) % 7;
		return last.AddDays(-offset);
	}

	// Good Friday = Easter Sunday minus 2 days. Uses the Anonymous Gregorian algorithm.
	private static DateTime GoodFriday(int year)
	{
		int a = year % 19;
		int b = year / 100, c = year % 100;
		int d = b / 4, e = b % 4;
		int f = (b + 8) / 25;
		int g = (b - f + 1) / 3;
		int h = (19 * a + b - d - g + 15) % 30;
		int i = c / 4, k = c % 4;
		int l = (32 + 2 * e + 2 * i - h - k) % 7;
		int m = (a + 11 * h + 22 * l) / 451;
		int month = (h + l - 7 * m + 114) / 31;
		int day = (h + l - 7 * m + 114) % 31 + 1;
		return new DateTime(year, month, day).AddDays(-2);
	}
}

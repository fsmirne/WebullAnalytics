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

	private static bool IsHoliday(DateTime date)
	{
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
		if (holiday.DayOfWeek == DayOfWeek.Sunday)   return holiday.AddDays(1);
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
		int day   = (h + l - 7 * m + 114) % 31 + 1;
		return new DateTime(year, month, day).AddDays(-2);
	}
}

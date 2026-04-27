namespace WebullAnalytics.AI;

internal static class OpenerExpiryHelpers
{
	private static DateTime AdjustToPreviousOpen(DateTime date)
	{
		var d = date.Date;
		while (!MarketCalendar.IsOpen(d))
			d = d.AddDays(-1);
		return d;
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

namespace WebullAnalytics.Pricing;

/// <summary>Time to an option's expiry on the two clocks Black-Scholes needs. <see cref="VarianceYears"/> carries the
/// diffusion (σ²·T, d1/d2, √T): how much price movement the market prices into the interval. <see cref="CalendarYears"/>
/// carries carry (discounting, r·T), which accrues in calendar time whatever the market is doing. Under the calendar
/// clock both are equal; under a fitted trading-time clock an overnight/weekend hour adds far less variance time.</summary>
internal readonly record struct OptionTime(double VarianceYears, double CalendarYears)
{
	internal static readonly OptionTime Zero = new(0.0, 0.0);

	/// <summary>Both clocks at <paramref name="years"/> — the calendar clock (o = n = 1).</summary>
	internal static OptionTime Calendar(double years) => new(years, years);

	/// <summary>True when no diffusion remains (expired, or a zero-variance interval).</summary>
	internal bool IsExpired => VarianceYears <= 0.0;
}

/// <summary>Per-hour variance weights of a trading-time clock, relative to a regular-session hour (weight 1):
/// <see cref="Overnight"/> for the non-session hours of a trading day, <see cref="NonTrading"/> for every hour of a
/// weekend/holiday. (1, 1) is the calendar clock.</summary>
internal readonly record struct ClockWeights(double Overnight, double NonTrading)
{
	internal static readonly ClockWeights Calendar = new(1.0, 1.0);

	internal bool IsCalendar => Overnight == 1.0 && NonTrading == 1.0;

	/// <summary>Weighted hours in a year: 252 trading days of (session + weighted overnight) plus 113 weighted
	/// non-trading days. Normalizes variance time so a calendar clock gives exactly calendar years (8760 h) and
	/// fitted IVs stay on the familiar annualized scale.</summary>
	internal double HoursPerYear => 252.0 * (6.5 + 17.5 * Overnight) + 113.0 * 24.0 * NonTrading;
}

/// <summary>The single place time-to-expiry is computed. Every pricing path — IV back-solve, repricing, greeks, the
/// report grid, the scorer and the backtest — gets its <see cref="OptionTime"/> here, so a solved IV and the price it
/// later projects always share one clock.</summary>
internal static class MarketClock
{
	/// <summary>The clock weights in force for <paramref name="root"/>. Calendar until calibration is wired in.</summary>
	internal static ClockWeights WeightsFor(string root) => ClockWeights.Calendar;

	/// <summary><see cref="OptionTime"/> from <paramref name="from"/> to <paramref name="to"/> (both ET wall-clock) on
	/// <paramref name="root"/>'s clock. Zero when <paramref name="to"/> is not after <paramref name="from"/>.</summary>
	internal static OptionTime Between(string root, DateTime from, DateTime to) => Between(WeightsFor(root), from, to);

	/// <summary><see cref="Between(string, DateTime, DateTime)"/> on explicit <paramref name="weights"/>.</summary>
	internal static OptionTime Between(ClockWeights weights, DateTime from, DateTime to)
	{
		if (to <= from) return OptionTime.Zero;
		var calendarYears = (to - from).TotalDays / 365.0;
		if (weights.IsCalendar) return OptionTime.Calendar(calendarYears);
		return new OptionTime(WeightedHours(weights, from, to) / weights.HoursPerYear, calendarYears);
	}

	/// <summary>Time from <paramref name="fromDate"/>'s 16:00 close to <paramref name="toDate"/>'s, stretched to at least
	/// <paramref name="minDays"/> calendar days — for callers that price on whole-day granularity (a date-to-expiry DTE).</summary>
	internal static OptionTime CloseToClose(string root, DateTime fromDate, DateTime toDate, int minDays = 0)
	{
		var from = fromDate.Date + OptionMath.MarketClose;
		var to = toDate.Date + OptionMath.MarketClose;
		if ((to - from).TotalDays < minDays) to = from.AddDays(minDays);
		return Between(root, from, to);
	}

	/// <summary>The last <paramref name="hours"/> before <paramref name="date"/>'s 16:00 close — a same-day (0DTE) leg's
	/// assumed remaining life when no exact instant is known (6.5 = a full session, 3.25 = mid-session).</summary>
	internal static OptionTime BeforeClose(string root, DateTime date, double hours)
	{
		var close = date.Date + OptionMath.MarketClose;
		return Between(root, close - TimeSpan.FromHours(hours), close);
	}

	/// <summary><paramref name="years"/> of pure calendar time on both clocks — for intervals with no anchoring instant
	/// (a synthetic fixed horizon such as a reference smile), where no trading-time weighting is meaningful.</summary>
	internal static OptionTime CalendarYears(double years) => OptionTime.Calendar(Math.Max(0.0, years));

	/// <summary>Variance hours in [from, to): session hours count 1, the rest of a trading day <see cref="ClockWeights.Overnight"/>,
	/// every hour of a closed day <see cref="ClockWeights.NonTrading"/>.</summary>
	internal static double WeightedHours(ClockWeights weights, DateTime from, DateTime to) => HourMixOf(from, to).Weighted(weights);

	/// <summary>Clock hours in [from, to) split by kind: regular-session hours (09:30 to the day's close, 13:00 on half-days), the
	/// rest of each trading day, and every hour of a weekend/holiday. Weight-independent, so a fit can price many candidate
	/// weights off one split.</summary>
	internal static HourMix HourMixOf(DateTime from, DateTime to)
	{
		double session = 0.0, overnight = 0.0, closed = 0.0;
		for (var day = from.Date; day < to; day = day.AddDays(1))
		{
			var start = from > day ? from : day;
			var end = to < day.AddDays(1) ? to : day.AddDays(1);
			if (end <= start) continue;
			var span = (end - start).TotalHours;
			if (!MarketCalendar.IsOpen(day)) { closed += span; continue; }
			var open = day + OptionMath.MarketOpen;
			var close = day + MarketCalendar.CloseTimeEt(day);
			var sessionStart = start > open ? start : open;
			var sessionEnd = end < close ? end : close;
			var inSession = sessionEnd > sessionStart ? (sessionEnd - sessionStart).TotalHours : 0.0;
			session += inSession;
			overnight += span - inSession;
		}
		return new HourMix(session, overnight, closed);
	}
}

/// <summary>Clock hours of an interval split into regular-session, trading-day overnight and closed-day hours.</summary>
internal readonly record struct HourMix(double Session, double Overnight, double Closed)
{
	internal double Weighted(ClockWeights weights) => Session + weights.Overnight * Overnight + weights.NonTrading * Closed;
}

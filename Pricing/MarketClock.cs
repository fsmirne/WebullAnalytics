using System.Collections.Concurrent;

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
	/// <summary>A live fit older than this many days draws a staleness warning (the daily backfill refits every morning).</summary>
	private const int StaleAfterDays = 7;

	/// <summary>Where fits come from — <see cref="Clock.ClockFits.At"/> in the app. The test assembly pins it to "no fit" so tests price
	/// on the calendar clock regardless of what calibration the machine's data folder holds.</summary>
	internal static Func<string, DateTime, Clock.ClockFit?> FitLookup { get; set; } = Clock.ClockFits.At;

	private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

	// Weights per (root, day): pricing hot loops (the scorer, the backtest) ask millions of times per run.
	private static readonly ConcurrentDictionary<(string Root, DateOnly Day), ClockWeights?> Resolved = new();

	/// <summary>The simulated day a historical run prices on (the backtest pins it every step, like the day's risk-free rate). Null =
	/// live: today, or a past --date (<see cref="EvaluationDate"/>).</summary>
	internal static DateOnly? PinnedDay { get; set; }

	/// <summary>The day whose calibration is in force. ONE fit per pricing context — chosen by when the pricing happens, never by the
	/// interval priced — so an IV solved at an anchor and the projection it later feeds share one clock, and a backtest never reaches a
	/// fit built after its simulated day even when it projects forward.</summary>
	private static DateTime KnowledgeDay =>
		PinnedDay?.ToDateTime(TimeOnly.MinValue) ?? (EvaluationDate.IsOverridden && EvaluationDate.Today < MarketTime.TodayEt() ? EvaluationDate.Today : MarketTime.TodayEt());

	/// <summary>The clock weights in force for <paramref name="root"/> on day <paramref name="at"/>: the newest fit built before that day,
	/// else the calendar clock.</summary>
	internal static ClockWeights WeightsFor(string root, DateTime at) => FittedWeights(root, at) ?? ClockWeights.Calendar;

	/// <summary>The newest fit's weights before day <paramref name="at"/>, or null when <paramref name="root"/> has none. Each thread
	/// remembers its last answer: a pricing loop asks for the same root and day millions of times, and the shared map costs a string hash each time.</summary>
	private static ClockWeights? FittedWeights(string root, DateTime at)
	{
		var day = DateOnly.FromDateTime(at);
		var generation = Volatile.Read(ref _generation);
		var last = _lastResolved;
		if (last != null && last.Generation == generation && last.Day == day && ReferenceEquals(last.Root, root)) return last.Weights;
		var weights = Resolved.GetOrAdd((root, day), static key => Resolve(key.Root, key.Day.ToDateTime(TimeOnly.MinValue)));
		_lastResolved = new LastResolved(root, day, weights, generation);
		return weights;
	}

	private sealed record LastResolved(string Root, DateOnly Day, ClockWeights? Weights, int Generation);

	[ThreadStatic] private static LastResolved? _lastResolved;

	/// <summary>Bumped by <see cref="ResetResolved"/> so every thread's remembered answer goes stale with the shared map.</summary>
	private static int _generation;

	/// <summary>Forgets resolved weights — after a refit in-process, or when a test swaps <see cref="FitLookup"/>.</summary>
	internal static void ResetResolved()
	{
		Resolved.Clear();
		Interlocked.Increment(ref _generation);
	}

	private static ClockWeights? Resolve(string root, DateTime at)
	{
		var fit = FitLookup(root, at);
		if (fit == null) return null;
		var ageDays = DateOnly.FromDateTime(at).DayNumber - fit.FittedThrough.DayNumber;
		if (ageDays > StaleAfterDays && at.Date >= MarketTime.TodayEt().AddDays(-1))
			WarnOnce(root, $"trading-time clock fit for {fit.Root} is {ageDays} days old (through {fit.FittedThrough:yyyy-MM-dd}): run `wa data clock`");
		return fit.Weights;
	}

	private static void WarnOnce(string root, string message)
	{
		// Keyed on the clock root as well as the raw one: SPX and SPXW share a fit, so one run over both would otherwise print the same warning twice.
		var first = Warned.TryAdd(Clock.ClockFits.ClockRoot(root), 0);
		Warned.TryAdd(root, 0);
		if (first) Console.Error.WriteLine($"warning: {message}");
	}

	/// <summary><see cref="OptionTime"/> from <paramref name="from"/> to <paramref name="to"/> (both ET wall-clock) on <paramref name="root"/>'s
	/// clock as calibrated on the pricing context's day (<see cref="KnowledgeDay"/>). Zero when <paramref name="to"/> is not after <paramref name="from"/>.
	/// A root without a fit prices on the calendar clock, warning once — but only when that matters: the interval crosses an overnight,
	/// weekend or holiday (a same-session interval is clock-independent, so same-day roots never need a fit).</summary>
	internal static OptionTime Between(string root, DateTime from, DateTime to)
	{
		if (to <= from) return OptionTime.Zero;
		var day = KnowledgeDay;
		if (FittedWeights(root, day) is { } fitted) return Between(fitted, from, to);
		// Warned is checked first: the message would otherwise be formatted on every call of a hot loop for a root that has no fit.
		if (!Warned.ContainsKey(root) && HourMixOf(from, to) is var mix && mix.Overnight + mix.Closed > 0)
			WarnOnce(root, $"no trading-time clock fit for {Clock.ClockFits.ClockRoot(root)} before {day:yyyy-MM-dd}: pricing across nights/weekends on the calendar clock (run `wa data clock {Clock.ClockFits.ClockRoot(root)}`)");
		return Between(ClockWeights.Calendar, from, to);
	}

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
	internal static HourMix HourMixOf(DateTime from, DateTime to) => to <= from ? default : Cumulative(to) - Cumulative(from);

	/// <summary>First day of the day table; instants outside it are an error (it spans 2000–2049).</summary>
	private static readonly DateTime TableStart = new(2000, 1, 1);
	private const int TableDays = 50 * 366;

	/// <summary>Per calendar day from <see cref="TableStart"/>: cumulative hours of each kind up to its midnight, and its close in hours after
	/// midnight (0 = closed all day). Any interval's split is then two array reads and some arithmetic — no calendar lookups — since the
	/// pricing hot loops (the scorer, the backtest) measure millions of intervals per run.</summary>
	private sealed record DayTable(HourMix[] MidnightTotals, double[] CloseHours);

	private static readonly Lazy<DayTable> Days = new(() =>
	{
		var totals = new HourMix[TableDays + 1];
		var closeHours = new double[TableDays];
		for (var i = 0; i < TableDays; i++)
		{
			var day = TableStart.AddDays(i);
			closeHours[i] = MarketCalendar.IsOpen(day) ? MarketCalendar.CloseTimeEt(day).TotalHours : 0.0;
			totals[i + 1] = totals[i] + IntradayMix(closeHours[i], 24.0);
		}
		return new DayTable(totals, closeHours);
	});

	private static readonly double OpenHours = OptionMath.MarketOpen.TotalHours;

	/// <summary>Hours of each kind from <see cref="TableStart"/> to <paramref name="t"/>.</summary>
	private static HourMix Cumulative(DateTime t)
	{
		var index = (int)((t.Ticks - TableStart.Ticks) / TimeSpan.TicksPerDay);
		if (index < 0 || index >= TableDays) throw new ArgumentOutOfRangeException(nameof(t), t, $"market clock covers {TableStart:yyyy-MM-dd} to {TableStart.AddDays(TableDays):yyyy-MM-dd}");
		var days = Days.Value;
		return days.MidnightTotals[index] + IntradayMix(days.CloseHours[index], t.TimeOfDay.TotalHours);
	}

	/// <summary>The split of a day's first <paramref name="hours"/> hours: session runs 09:30 to <paramref name="closeHours"/> (13:00 on
	/// half-days); the rest of a trading day is overnight; a closed day (<paramref name="closeHours"/> = 0) is all closed.</summary>
	private static HourMix IntradayMix(double closeHours, double hours)
	{
		if (closeHours == 0.0) return new HourMix(0.0, 0.0, hours);
		var inSession = Math.Clamp(hours, OpenHours, closeHours) - OpenHours;
		return new HourMix(inSession, hours - inSession, 0.0);
	}
}

/// <summary>Clock hours of an interval split into regular-session, trading-day overnight and closed-day hours.</summary>
internal readonly record struct HourMix(double Session, double Overnight, double Closed)
{
	internal double Weighted(ClockWeights weights) => Session + weights.Overnight * Overnight + weights.NonTrading * Closed;

	public static HourMix operator +(HourMix a, HourMix b) => new(a.Session + b.Session, a.Overnight + b.Overnight, a.Closed + b.Closed);
	public static HourMix operator -(HourMix a, HourMix b) => new(a.Session - b.Session, a.Overnight - b.Overnight, a.Closed - b.Closed);
}

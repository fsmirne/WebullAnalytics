namespace WebullAnalytics.Pricing.Clock;

/// <summary>A fitted trading-time clock for one root, with what produced it. <see cref="FittedThrough"/> is the last session whose
/// 09:45 book entered the fit; a fit is only ever used for instants after that day, so a backtest never sees a fit built on its future.</summary>
internal sealed record ClockFit(
	string Root,
	DateOnly FittedThrough,
	DateOnly WindowStart,
	double Overnight,
	double NonTrading,
	int Nights,
	int ClosedDayNights,
	int SkippedNights,
	int Options,
	double MaeFitted,
	double MaeCalendar)
{
	internal ClockWeights Weights => new(Overnight, NonTrading);
}

/// <summary>Fits a root's <see cref="ClockWeights"/> from consecutive (16:00 close, next session's 09:45) book pairs: each option's
/// Black-76 total variance is read off its closing mid (sticky strike, actual forward), carried to the next morning on the candidate
/// clock, and repriced at the next morning's forward; the weights minimizing the mean absolute error against the 09:45 mids win.
/// Holding the strike's vol fixed means the closing total variance v0 = σ²·T0 is clock-independent — a candidate clock only scales it
/// by T1/T0 — so the vol is solved once per option and each candidate costs one Black evaluation per option.</summary>
internal static class ClockFitter
{
	/// <summary>Minimum evidence for a fit: nights overall, nights spanning a weekend/holiday, and priced options.</summary>
	internal const int MinNights = 60, MinClosedDayNights = 20, MinOptions = 5000;

	/// <summary>A night is skipped when the at-the-money vol of an expiry at least <see cref="JumpMinDte"/> days out moved more than
	/// <see cref="JumpMaxRelative"/> (relative) between the close and the 09:45 book — an earnings crush or a market shock, not decay.
	/// That far out a weekend moves the vol only a few percent, so normal nights pass.</summary>
	internal const int JumpMinDte = 20;
	internal const double JumpMaxRelative = 0.15;

	/// <summary>Only options at least this many calendar days from expiry at the 09:45 book enter the objective (expiry-day pricing is
	/// dominated by the intraday path, not the overnight clock).</summary>
	internal const int MinDteAtOpen = 2;

	/// <summary>Cap on priced options per fit (evenly subsampled) to keep a fit to seconds.</summary>
	private const int MaxRows = 40_000;

	private readonly record struct Row(double F1, double K, bool IsCall, double V0, HourMix ToExpiryAtClose, HourMix ToExpiryAtOpen, double Mid1);

	/// <summary>Fits the weights over the pairs available in <paramref name="days"/> (any order; consecutive trading days are paired).
	/// Null when the evidence falls short of <see cref="MinNights"/> / <see cref="MinClosedDayNights"/> / <see cref="MinOptions"/>.</summary>
	internal static ClockFit? Fit(string root, IReadOnlyList<ClockDay> days)
	{
		var byDate = days.ToDictionary(d => d.Date);
		var rows = new List<Row>();
		int nights = 0, closedNights = 0, skipped = 0;
		foreach (var d0 in days.OrderBy(d => d.Date))
		{
			var next = DateOnly.FromDateTime(MarketCalendar.NextOpenAfter(d0.Date.ToDateTime(TimeOnly.MinValue)));
			if (!byDate.TryGetValue(next, out var d1) || d0.Close.Count == 0 || d1.Open.Count == 0) continue;
			var nightRows = PairRows(d0, d1, out var jumped);
			if (jumped) { skipped++; continue; }
			if (nightRows.Count == 0) continue;
			nights++;
			if (next.DayNumber - d0.Date.DayNumber > 1) closedNights++;
			rows.AddRange(nightRows);
		}
		if (nights < MinNights || closedNights < MinClosedDayNights || rows.Count < MinOptions) return null;

		var sample = rows.Count <= MaxRows ? rows : Enumerable.Range(0, MaxRows).Select(i => rows[(int)((long)i * rows.Count / MaxRows)]).ToList();
		var (best, _) = Search(sample, 0.5, 0.5, 0.5, 0.05);
		var (fine, bestMae) = Search(sample, best.Overnight, best.NonTrading, 0.05, 0.01);
		best = fine;
		var calendarMae = Mae(sample, ClockWeights.Calendar);
		var ordered = days.Select(d => d.Date).OrderBy(d => d).ToList();
		return new ClockFit(root, ordered[^1], ordered[0], best.Overnight, best.NonTrading, nights, closedNights, skipped, rows.Count, bestMae, calendarMae);
	}

	/// <summary>Grid search over (overnight, non-trading) within ±<paramref name="span"/> of the center (clamped to [0, 1]) at <paramref name="step"/>.</summary>
	private static (ClockWeights Best, double Mae) Search(IReadOnlyList<Row> rows, double centerO, double centerN, double span, double step)
	{
		var best = ClockWeights.Calendar;
		var bestMae = double.MaxValue;
		for (var o = Math.Max(0.0, centerO - span); o <= Math.Min(1.0, centerO + span) + 1e-9; o += step)
			for (var n = Math.Max(0.0, centerN - span); n <= Math.Min(1.0, centerN + span) + 1e-9; n += step)
			{
				var w = new ClockWeights(Math.Round(o, 4), Math.Round(n, 4));
				var mae = Mae(rows, w);
				if (mae < bestMae) (best, bestMae) = (w, mae);
			}
		return (best, bestMae);
	}

	/// <summary>Mean absolute 09:45 repricing error on clock <paramref name="w"/>.</summary>
	private static double Mae(IReadOnlyList<Row> rows, ClockWeights w)
	{
		var sum = 0.0;
		foreach (var r in rows)
		{
			var t0 = r.ToExpiryAtClose.Weighted(w);
			var v1 = t0 > 0 ? r.V0 * r.ToExpiryAtOpen.Weighted(w) / t0 : 0.0;
			sum += Math.Abs(Black(r.F1, r.K, v1, r.IsCall) - r.Mid1);
		}
		return sum / rows.Count;
	}

	/// <summary>Undiscounted Black-76 value at total variance <paramref name="variance"/> (σ²·T).</summary>
	private static double Black(double f, double k, double variance, bool isCall) =>
		variance <= 0 ? Math.Max(0, isCall ? f - k : k - f) : OptionMath.BlackScholesValue(f, k, OptionTime.Calendar(1.0), 0.0, Math.Sqrt(variance), isCall);

	/// <summary>The fit rows of one night (d0 close → d1 09:45), or empty with <paramref name="jumped"/> set when the night's
	/// longer-dated ATM vol moved more than <see cref="JumpMaxRelative"/>.</summary>
	private static List<Row> PairRows(ClockDay d0, ClockDay d1, out bool jumped)
	{
		jumped = false;
		var closeAt = d0.Date.ToDateTime(ClockStore.CloseTime);
		var openAt = d1.Date.ToDateTime(ClockStore.OpenTime);
		var openBooks = d1.Open.ToDictionary(b => b.Expiry);
		var rows = new List<Row>();
		foreach (var b0 in d0.Close)
		{
			if (!openBooks.TryGetValue(b0.Expiry, out var b1) || b0.Expiry < d1.Date) continue;
			var expiryAt = b0.Expiry.ToDateTime(TimeOnly.MinValue) + MarketCalendar.CloseTimeEt(b0.Expiry.ToDateTime(TimeOnly.MinValue));
			var calendar0 = (expiryAt - closeAt).TotalDays / 365.0;
			var dteAtOpen = b0.Expiry.DayNumber - d1.Date.DayNumber;
			if (dteAtOpen >= JumpMinDte && AtmVolMoved(b0, b1, calendar0, (expiryAt - openAt).TotalDays / 365.0)) { jumped = true; return []; }
			if (dteAtOpen < MinDteAtOpen) continue;
			var mixClose = MarketClock.HourMixOf(closeAt, expiryAt);
			var mixOpen = MarketClock.HourMixOf(openAt, expiryAt);
			var opens = b1.Options.ToDictionary(q => (q.Strike, q.IsCall));
			foreach (var q0 in b0.Options)
			{
				if (!opens.TryGetValue((q0.Strike, q0.IsCall), out var q1)) continue;
				var mid0 = q0.Mid;
				var mid1 = q1.Mid;
				if (mid0 < 0.05m || mid1 < 0.05m) continue;
				if (q1.Ask - q1.Bid > Math.Max(0.10m * mid1, 0.05m)) continue;
				var intrinsic0 = Math.Max(0m, q0.IsCall ? b0.Forward - q0.Strike : q0.Strike - b0.Forward);
				if (mid0 <= intrinsic0 + 0.01m) continue;
				var sigma = OptionMath.ImpliedVol(b0.Forward, q0.Strike, OptionTime.Calendar(calendar0), 0.0, mid0, q0.IsCall ? "C" : "P");
				if (sigma <= 0.011m || sigma >= 4.99m) continue;
				rows.Add(new Row((double)b1.Forward, (double)q0.Strike, q0.IsCall, (double)(sigma * sigma) * calendar0, mixClose, mixOpen, (double)mid1));
			}
		}
		return rows;
	}

	/// <summary>True when the at-the-money vol (option nearest the forward) moved more than <see cref="JumpMaxRelative"/> between the books.</summary>
	private static bool AtmVolMoved(ClockExpiryBook b0, ClockExpiryBook b1, double years0, double years1)
	{
		var v0 = AtmVol(b0, years0);
		var v1 = AtmVol(b1, years1);
		return v0 is > 0.011m && v1 is > 0.011m && Math.Abs((double)(v1.Value / v0.Value) - 1.0) > JumpMaxRelative;
	}

	/// <summary>Calendar-clock implied vol of the option nearest the forward, or null for an empty book / no time left.</summary>
	private static decimal? AtmVol(ClockExpiryBook b, double years)
	{
		if (b.Options.Count == 0 || years <= 0) return null;
		var q = b.Options.MinBy(o => Math.Abs(o.Strike - b.Forward));
		return OptionMath.ImpliedVol(b.Forward, q.Strike, OptionTime.Calendar(years), 0.0, q.Mid, q.IsCall ? "C" : "P");
	}
}

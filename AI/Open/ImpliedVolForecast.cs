using System.Collections.Concurrent;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI;

/// <summary>
/// Rescales <see cref="RealizedVolModel"/>'s level with the market's own short-dated implied vol, calibrated against what was realized.
/// The trailing EWMA reacts to a volatility shock a day late and keeps forecasting the old level for days after it fades; the prior
/// close of VIX1D (one-day) and VIX9D (nine-day) already price the new level. Two log-linear fits, each on up to three trailing years of
/// the ticker's daily bars strictly before the forecast date, every target fully realized before that date (so a backtest never scores
/// itself on its own training data):
/// <list type="bullet">
/// <item><description><b>Today</b> — ln(gap² + Parkinson range variance) of a session on ln(its EWMA gap + session variance) and ln(prior-close VIX1D²).</description></item>
/// <item><description><b>Forward</b> — ln(realized variance over that session and the next four) on ln(5 × EWMA) and ln(prior-close VIX9D²).</description></item>
/// </list>
/// Each fit's prediction is turned back into a variance with Duan's smearing factor (the mean of exp(residual), so fat-tailed log
/// residuals don't bias it), and the scale is that forecast over the EWMA's own forecast for the same day. A fit with fewer than
/// <see cref="MinTrainingSessions"/> rows, or a missing implied close for the forecast day, leaves its scale at 1.
///
/// Study (scripts/realized_vol_forecast_study.py, SPY 2022-01..2026-11, QLIKE vs the EWMA alone, each year predicted from earlier years
/// only, two-year minimum training window): next-5-session variance improved 31% (2024), 42% (2025), 38% (2026) from the EWMA + VIX9D;
/// variance from 09:45 to the close improved 32–57% a year from VIX1D. Memoized per (ticker, date): the fit depends only on history before
/// the date, and the opener rescores every minute.
/// </summary>
internal static class ImpliedVolForecast
{
	/// <summary>Trailing sessions a fit may use: three years.</summary>
	internal const int TrainingSessions = 756;

	/// <summary>Fewer usable rows than this (about two years) leaves the scale at 1: a one-year fit carries that year's vol level into the next (a 2022-only fit over-forecast 2023 by 0.85 in log terms).</summary>
	internal const int MinTrainingSessions = 500;

	private const int ForwardSessions = 5;

	private static readonly ConcurrentDictionary<(string Ticker, DateTime Date), (double Today, double Forward)> Scales = new();

	internal static async Task<RealizedVolModel> ApplyAsync(RealizedVolModel model, HistoricalPriceCache priceCache, string ticker, DateTime asOf, CancellationToken cancellation)
	{
		var key = (ticker.ToUpperInvariant(), asOf.Date);
		if (!Scales.TryGetValue(key, out var scales))
			Scales[key] = scales = await FitAsync(priceCache, ticker, asOf, cancellation);
		return model with { TodayScale = scales.Today, ForwardScale = scales.Forward };
	}

	private static async Task<(double Today, double Forward)> FitAsync(HistoricalPriceCache priceCache, string ticker, DateTime asOf, CancellationToken cancellation)
	{
		var count = TrainingSessions + RealizedVolModel.HistorySessions + ForwardSessions + 1;
		var bars = await priceCache.GetRecentBarsAsync(ticker, count, asOf, cancellation);
		if (bars.Count < RealizedVolModel.HistorySessions + MinTrainingSessions) return (1.0, 1.0);
		var vix1d = await ClosesByDateAsync(priceCache, "VIX1D", count, asOf, cancellation);
		var vix9d = await ClosesByDateAsync(priceCache, "VIX9D", count, asOf, cancellation);

		// Per session i (oldest-first): the EWMA known before it opened, its realized pieces, and the implied closes of the session before.
		var n = bars.Count;
		var ewma = new double?[n + 1];
		for (var i = RealizedVolModel.HistorySessions; i <= n; i++)
			ewma[i] = RealizedVolModel.FromBars(Slice(bars, i - RealizedVolModel.HistorySessions - 1, i)) is { } m ? m.OvernightVariance + m.SessionVariance : null;
		var day = new double[n];
		var today = new double[n];
		for (var i = 1; i < n; i++)
		{
			var gap = Math.Pow(Math.Log((double)bars[i].Open / (double)bars[i - 1].Close), 2);
			day[i] = gap + Math.Pow(Math.Log((double)bars[i].Close / (double)bars[i].Open), 2);
			today[i] = gap + Math.Pow(Math.Log((double)bars[i].High / (double)bars[i].Low), 2) / (4.0 * Math.Log(2.0));
		}

		var todayRows = new List<(double Y, double X1, double X2)>();
		var forwardRows = new List<(double Y, double X1, double X2)>();
		for (var i = RealizedVolModel.HistorySessions; i < n; i++)
		{
			if (ewma[i] is not > 0) continue;
			var prior = bars[i - 1].Date.Date;
			if (today[i] > 0 && vix1d.TryGetValue(prior, out var v1) && v1 > 0)
				todayRows.Add((Math.Log(today[i]), Math.Log(ewma[i]!.Value), ImpliedLogVariance(v1)));
			if (i + ForwardSessions <= n && vix9d.TryGetValue(prior, out var v9) && v9 > 0)
			{
				var rv = 0.0;
				for (var k = 0; k < ForwardSessions; k++) rv += day[i + k];
				if (rv > 0) forwardRows.Add((Math.Log(rv), Math.Log(ForwardSessions * ewma[i]!.Value), ImpliedLogVariance(v9)));
			}
		}

		var lastPrior = bars[^1].Date.Date;
		var asOfEwma = ewma[n];
		if (asOfEwma is not > 0) return (1.0, 1.0);
		var todayScale = vix1d.TryGetValue(lastPrior, out var t1) && t1 > 0 ? Scale(todayRows, Math.Log(asOfEwma.Value), ImpliedLogVariance(t1), asOfEwma.Value) : 1.0;
		var forwardScale = vix9d.TryGetValue(lastPrior, out var t9) && t9 > 0 ? Scale(forwardRows, Math.Log(ForwardSessions * asOfEwma.Value), ImpliedLogVariance(t9), ForwardSessions * asOfEwma.Value) : 1.0;
		return (todayScale, forwardScale);
	}

	/// <summary>The fitted forecast over the EWMA's own (<paramref name="ewmaForecast"/>) for the forecast day; 1 when the fit has too few rows or is degenerate.</summary>
	private static double Scale(List<(double Y, double X1, double X2)> rows, double x1, double x2, double ewmaForecast)
	{
		var recent = rows.Count > TrainingSessions ? rows.GetRange(rows.Count - TrainingSessions, TrainingSessions) : rows;
		if (recent.Count < MinTrainingSessions || LeastSquares(recent) is not { } b) return 1.0;
		var smear = recent.Average(r => Math.Exp(r.Y - (b.B0 + b.B1 * r.X1 + b.B2 * r.X2)));
		var forecast = Math.Exp(b.B0 + b.B1 * x1 + b.B2 * x2) * smear;
		var scale = forecast / ewmaForecast;
		return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
	}

	/// <summary>A VIX-family close (annualized vol points) as the log of its one-session variance.</summary>
	private static double ImpliedLogVariance(double volPoints) => Math.Log(Math.Pow(volPoints / 100.0, 2) / 252.0);

	/// <summary>OLS of Y on [1, X1, X2] via the 3×3 normal equations; null when singular.</summary>
	private static (double B0, double B1, double B2)? LeastSquares(List<(double Y, double X1, double X2)> rows)
	{
		double n = rows.Count, s1 = 0, s2 = 0, s11 = 0, s12 = 0, s22 = 0, sy = 0, s1y = 0, s2y = 0;
		foreach (var (y, x1, x2) in rows)
		{
			s1 += x1; s2 += x2; s11 += x1 * x1; s12 += x1 * x2; s22 += x2 * x2; sy += y; s1y += x1 * y; s2y += x2 * y;
		}
		var a = new[,] { { n, s1, s2 }, { s1, s11, s12 }, { s2, s12, s22 } };
		var c = new[] { sy, s1y, s2y };
		var det = Det(a);
		if (Math.Abs(det) < 1e-12) return null;
		return (Cramer(a, c, 0, det), Cramer(a, c, 1, det), Cramer(a, c, 2, det));
	}

	/// <summary>Cramer's rule: coefficient <paramref name="col"/> of a·x = c.</summary>
	private static double Cramer(double[,] a, double[] c, int col, double det)
	{
		var m = (double[,])a.Clone();
		for (var r = 0; r < 3; r++) m[r, col] = c[r];
		return Det(m) / det;
	}

	private static double Det(double[,] m) =>
		m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

	private static List<YahooOptionsClient.HistoricalBar> Slice(IReadOnlyList<YahooOptionsClient.HistoricalBar> bars, int from, int to)
	{
		var slice = new List<YahooOptionsClient.HistoricalBar>(to - from);
		for (var i = Math.Max(0, from); i < to; i++) slice.Add(bars[i]);
		return slice;
	}

	private static async Task<Dictionary<DateTime, double>> ClosesByDateAsync(HistoricalPriceCache priceCache, string ticker, int count, DateTime asOf, CancellationToken cancellation) =>
		(await priceCache.GetRecentBarsAsync(ticker, count + 30, asOf, cancellation)).ToDictionary(b => b.Date.Date, b => (double)b.Close);
}

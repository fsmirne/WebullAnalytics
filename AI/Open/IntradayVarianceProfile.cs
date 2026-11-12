using System.Collections.Concurrent;
using System.Globalization;
using WebullAnalytics.AI.Replay;

namespace WebullAnalytics.AI;

/// <summary>
/// How a regular session's open→close variance is spread across the day: <see cref="RemainingShare"/> is the fraction of it still to
/// come at a point in the session. Variance is front-loaded (the open trades far more of it than midday), so spreading it evenly over
/// the minutes — the fallback when a ticker has no profile — overstates what a 0DTE trade has left to experience after the open: on SPY
/// 2022-01..2026-11, 70% of the session's variance remained at 10:38 against 83% of its minutes.
///
/// Estimated from the ticker's minute tape over the <see cref="TrainingSessions"/> full sessions strictly before the day it serves (no
/// look-ahead): at each <see cref="StepMinutes"/> checkpoint g, Σ ln(close / price at g)² over Σ ln(close / open)², the same open→close
/// quantity <see cref="RealizedVolModel.SessionVariance"/> forecasts, made non-increasing. Endpoint returns rather than summed minute
/// returns, so bid/ask bounce doesn't inflate the busy minutes. A half-day uses the same shape over its shorter session.
/// </summary>
internal sealed class IntradayVarianceProfile
{
	internal const int SessionMinutes = 390;
	internal const int StepMinutes = 15;
	private const int Steps = SessionMinutes / StepMinutes;

	/// <summary>About two years: enough sessions that a few crash days don't set the shape on their own.</summary>
	internal const int TrainingSessions = 504;

	/// <summary>Fewer full sessions than this before the day → no profile (the even spread).</summary>
	internal const int MinSessions = 250;

	/// <summary>RemainingShare at checkpoints 0, 15, …, 390 minutes after the open: 1 at the open, 0 at the close, non-increasing.</summary>
	private readonly double[] _remaining;

	internal IntradayVarianceProfile(IReadOnlyList<double> remainingAtSteps)
	{
		if (remainingAtSteps.Count != Steps + 1) throw new ArgumentException($"expected {Steps + 1} checkpoints", nameof(remainingAtSteps));
		_remaining = new double[Steps + 1];
		var floor = 1.0;
		for (var i = 0; i <= Steps; i++) _remaining[i] = floor = Math.Clamp(Math.Min(floor, remainingAtSteps[i]), 0.0, 1.0);
		_remaining[0] = 1.0;
		_remaining[Steps] = 0.0;
	}

	/// <summary>Share of the session's variance left at <paramref name="elapsedFraction"/> of the session (0 = open, 1 = close).</summary>
	internal double RemainingShare(double elapsedFraction)
	{
		var x = Math.Clamp(elapsedFraction, 0.0, 1.0) * Steps;
		var i = Math.Min((int)x, Steps - 1);
		return _remaining[i] + (_remaining[i + 1] - _remaining[i]) * (x - i);
	}

	/// <summary>Inverse of <see cref="RemainingShare"/>: the earliest elapsed fraction at which only <paramref name="remainingShare"/> is left.</summary>
	internal double ElapsedAt(double remainingShare)
	{
		var share = Math.Clamp(remainingShare, 0.0, 1.0);
		for (var i = 0; i < Steps; i++)
		{
			if (share < _remaining[i + 1]) continue;
			var drop = _remaining[i] - _remaining[i + 1];
			var within = drop > 0 ? (_remaining[i] - share) / drop : 0.0;
			return (i + Math.Clamp(within, 0.0, 1.0)) / Steps;
		}
		return 1.0;
	}

	/// <summary>The profile in force for <paramref name="ticker"/> at <paramref name="asOf"/>, or null. Tests pin it to null so the
	/// machine's tape never shapes an expected number.</summary>
	internal static Func<string, DateTime, IntradayVarianceProfile?> Lookup { get; set; } = For;

	private static readonly ConcurrentDictionary<string, Lazy<SortedList<DateOnly, double[]>>> Sessions = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<(string Ticker, DateOnly Day), IntradayVarianceProfile?> Profiles = new();

	/// <summary>The profile from the <see cref="TrainingSessions"/> full sessions before <paramref name="asOf"/>'s date; null when the tape
	/// has fewer than <see cref="MinSessions"/> of them.</summary>
	internal static IntradayVarianceProfile? For(string ticker, DateTime asOf) =>
		Profiles.GetOrAdd((ticker.ToUpperInvariant(), DateOnly.FromDateTime(asOf)), static key => Fit(SessionsFor(key.Ticker), key.Day));

	internal static IntradayVarianceProfile? Fit(SortedList<DateOnly, double[]> sessions, DateOnly day)
	{
		var end = 0;
		while (end < sessions.Count && sessions.Keys[end] < day) end++;
		var start = Math.Max(0, end - TrainingSessions);
		if (end - start < MinSessions) return null;
		var sums = new double[Steps];
		for (var s = start; s < end; s++)
		{
			var v = sessions.Values[s];
			for (var i = 0; i < Steps; i++) sums[i] += v[i];
		}
		if (sums[0] <= 0) return null;
		return new IntradayVarianceProfile(Enumerable.Range(0, Steps + 1).Select(i => i < Steps ? sums[i] / sums[0] : 0.0).ToList());
	}

	/// <summary>Squared log returns from each checkpoint to the close, per full session: <c>data/vol-profile/{TICKER}.csv</c> plus any
	/// completed tape day it hasn't seen yet (appended, so each tape file is read once). A day that isn't a full session is recorded
	/// with no values so it isn't re-read.</summary>
	private static SortedList<DateOnly, double[]> SessionsFor(string ticker) => Sessions.GetOrAdd(ticker, static t => new Lazy<SortedList<DateOnly, double[]>>(() => LoadSessions(t))).Value;

	private static SortedList<DateOnly, double[]> LoadSessions(string ticker)
	{
		var sessions = new SortedList<DateOnly, double[]>();
		var tapeDir = Program.ResolvePath($"data/intraday/{ticker}");
		if (!Directory.Exists(tapeDir)) return sessions;
		var cachePath = Program.ResolvePath($"data/vol-profile/{ticker}.csv");
		var seen = new HashSet<DateOnly>();
		if (File.Exists(cachePath))
			foreach (var line in File.ReadLines(cachePath))
			{
				var parts = line.Split(',');
				if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
				seen.Add(d);
				if (parts.Length == Steps + 1) sessions[d] = parts.Skip(1).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
			}

		var today = DateOnly.FromDateTime(MarketTime.TodayEt());
		var added = new List<string>();
		foreach (var path in Directory.EnumerateFiles(tapeDir, "*.csv").Order(StringComparer.Ordinal))
		{
			if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d >= today || seen.Contains(d)) continue;
			var values = SessionReturns(d, IntradayTapeBackfill.ReadIntradayCsv(path));
			if (values != null) sessions[d] = values;
			added.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (values == null ? "" : "," + string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));
		}
		if (added.Count > 0)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
			File.AppendAllLines(cachePath, added);
		}
		return sessions;
	}

	/// <summary>ln(close / price at each checkpoint)² for a full regular session, or null when <paramref name="day"/> is a half-day or its
	/// tape is missing minutes. START-of-bar labels: the price at checkpoint g is the open of the first bar at g = 0, else the close of bar g−1.</summary>
	internal static double[]? SessionReturns(DateOnly day, IReadOnlyList<MinuteBar> bars)
	{
		if (MarketCalendar.IsEarlyClose(day.ToDateTime(TimeOnly.MinValue))) return null;
		var rth = new Dictionary<int, MinuteBar>();
		foreach (var b in bars)
		{
			var et = MarketTime.ToEt(b.Timestamp.UtcDateTime);
			if (DateOnly.FromDateTime(et) != day) continue;
			var m = et.Hour * 60 + et.Minute - 570;
			if (m is >= 0 and < SessionMinutes) rth[m] = b;
		}
		if (rth.Count < SessionMinutes - 10 || !rth.ContainsKey(0) || !rth.TryGetValue(SessionMinutes - 1, out var last)) return null;
		var close = (double)last.Close;
		var values = new double[Steps];
		for (var i = 0; i < Steps; i++)
		{
			var g = i * StepMinutes;
			double price;
			if (g == 0) price = (double)rth[0].Open;
			else if (rth.TryGetValue(g - 1, out var bar)) price = (double)bar.Close;
			else return null;
			if (price <= 0 || close <= 0) return null;
			values[i] = Math.Pow(Math.Log(close / price), 2);
		}
		return values;
	}
}

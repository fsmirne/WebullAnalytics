namespace WebullAnalytics.AI.Analysis;

/// <summary>One aggregated 5-minute RTH candle. <see cref="EtStart"/> is the bar's start in Eastern time
/// (so <c>EtStart.Date</c> is the session day used to bound intraday forward returns).</summary>
internal readonly record struct IntradayBar(DateTime EtStart, decimal Open, decimal High, decimal Low, decimal Close, long Volume)
{
	public DateTime Day => EtStart.Date;
}

/// <summary>The "buy the dip" branch of the source TradingView "MACD+RSI+BB" indicator's black trigger:
/// on the same 5-minute bar the MACD histogram is negative (<c>hist &lt; 0</c>, the down-branch gate),
/// RSI is below <see cref="RsiLow"/>, AND the close is below the lower Bollinger band. The band uses an EMA
/// basis (the indicator's "Basis MA Type" = EMA) with σ around the SMA. MACD/BB/RSI lengths mirror the
/// indicator defaults. (The symmetric top branch is hist≥0 ∧ RSI&gt;RsiHigh ∧ close&gt;upper band.)</summary>
internal sealed record DipParams(int RsiPeriod = 14, decimal RsiLow = 30m, int BbPeriod = 20, decimal BbK = 2m, int MacdFast = 12, int MacdSlow = 26, int MacdSignal = 9);

/// <summary>A fired dip signal and the underlying's forward returns from the entry (next bar's open). Returns
/// are fractions (0.012 = +1.2%); null when the horizon runs past the session close. Positive = price rose
/// after entry (a win for a dip-buy).</summary>
internal sealed record DipSignal(DateTime EntryEt, decimal EntryPrice, decimal Rsi, decimal Close, decimal LowerBand, decimal MacdHist, decimal? Ret30, decimal? Ret60, decimal RetEod);

/// <summary>How often each leg of the conjunction (and all) holds across evaluable bars — makes a rare signal
/// count interpretable and shows that the <c>hist&lt;0</c> gate is near-always satisfied inside a dip.</summary>
internal sealed record ConditionCounts(int Evaluable, int HistNeg, int Rsi, int Band, int All);

internal sealed record DipAnalysisResult(int BarCount, int SessionCount, IReadOnlyList<DipSignal> Signals, ConditionCounts Counts);

/// <summary>Aggregates 1-minute RTH bars to 5-minute candles, runs the RSI+Bollinger+MACD-sign "dip" trigger
/// over the CONTINUOUS series (indicators roll across sessions, standard intraday-chart behavior), and records
/// forward returns at +30m / +60m / session close. Entry is the open of the bar AFTER the signal bar; the
/// 30m/60m horizons stay within the same session (null if the session ends first).</summary>
internal static class DipSignalAnalyzer
{
	/// <summary>Buckets RTH 1-minute bars (already filtered to 09:30–16:00 ET, sorted) into clock-aligned
	/// <paramref name="intervalMinutes"/>-minute candles (anchored to midnight, so 1/5/10/15/30 all align with
	/// the 09:30 open). <paramref name="toEt"/> converts a UTC bar timestamp to Eastern time.</summary>
	public static List<IntradayBar> AggregateToMinutes(IEnumerable<MinuteBar> rthOneMin, Func<DateTimeOffset, DateTime> toEt, int intervalMinutes)
	{
		var buckets = new SortedDictionary<DateTime, List<MinuteBar>>();
		foreach (var b in rthOneMin)
		{
			var et = toEt(b.Timestamp);
			var bucketStart = et.Date.AddMinutes(Math.Floor(et.TimeOfDay.TotalMinutes / intervalMinutes) * intervalMinutes);
			if (!buckets.TryGetValue(bucketStart, out var list)) buckets[bucketStart] = list = new List<MinuteBar>();
			list.Add(b);
		}
		var result = new List<IntradayBar>(buckets.Count);
		foreach (var (start, mins) in buckets)
		{
			mins.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
			result.Add(new IntradayBar(start, mins[0].Open, mins.Max(m => m.High), mins.Min(m => m.Low), mins[^1].Close, mins.Sum(m => m.Volume)));
		}
		return result;
	}

	public static DipAnalysisResult Analyze(IReadOnlyList<IntradayBar> bars, DipParams p, int intervalMinutes)
	{
		// Forward-return horizons are fixed wall-clock windows, so express them in bars from the interval.
		var bars30 = Math.Max(1, (int)Math.Round(30.0 / intervalMinutes));
		var bars60 = Math.Max(1, (int)Math.Round(60.0 / intervalMinutes));
		var signals = new List<DipSignal>();
		var sessions = bars.Select(b => b.Day).Distinct().Count();
		if (bars.Count == 0) return new DipAnalysisResult(0, 0, signals, new ConditionCounts(0, 0, 0, 0, 0));

		var closes = bars.Select(b => b.Close).ToArray();
		var rsi = SeriesIndicators.Rsi(closes, p.RsiPeriod);
		var lower = SeriesIndicators.BollingerLower(closes, p.BbPeriod, p.BbK, emaBasis: true);
		var (_, _, hist) = SeriesIndicators.Macd(closes, p.MacdFast, p.MacdSlow, p.MacdSignal);

		int evaluable = 0, cH = 0, cR = 0, cB = 0, cAll = 0;
		for (var i = 0; i < bars.Count; i++)
		{
			if (rsi[i] is not { } r || lower[i] is not { } lb || hist[i] is not { } h) continue;
			evaluable++;
			bool condH = h < 0m, condR = r < p.RsiLow, condB = closes[i] < lb;
			if (condH) cH++;
			if (condR) cR++;
			if (condB) cB++;
			if (!(condH && condR && condB)) continue;
			cAll++;

			// Entry = open of the next bar, but only if it's a real intraday continuation (same session).
			var e = i + 1;
			if (e >= bars.Count || bars[e].Day != bars[i].Day) continue;
			var entry = bars[e].Open;
			if (entry <= 0m) continue;

			signals.Add(new DipSignal(
				EntryEt: bars[e].EtStart,
				EntryPrice: entry,
				Rsi: r, Close: closes[i], LowerBand: lb, MacdHist: h,
				Ret30: ForwardReturn(bars, e, bars30, entry),
				Ret60: ForwardReturn(bars, e, bars60, entry),
				RetEod: (SessionClose(bars, e) - entry) / entry));
		}
		return new DipAnalysisResult(bars.Count, sessions, signals, new ConditionCounts(evaluable, cH, cR, cB, cAll));
	}

	/// <summary>Return from <paramref name="entry"/> to the close <paramref name="barsAhead"/> bars after the
	/// entry bar, provided that bar is in the same session; otherwise null (horizon ran past the close).</summary>
	private static decimal? ForwardReturn(IReadOnlyList<IntradayBar> bars, int entryIdx, int barsAhead, decimal entry)
	{
		var j = entryIdx + barsAhead;
		if (j >= bars.Count || bars[j].Day != bars[entryIdx].Day) return null;
		return (bars[j].Close - entry) / entry;
	}

	private static decimal SessionClose(IReadOnlyList<IntradayBar> bars, int entryIdx)
	{
		var day = bars[entryIdx].Day;
		var close = bars[entryIdx].Close;
		for (var j = entryIdx; j < bars.Count && bars[j].Day == day; j++) close = bars[j].Close;
		return close;
	}
}

using WebullAnalytics.AI;
using WebullAnalytics.AI.Analysis;
using Xunit;

namespace WebullAnalytics.Tests.AI.Analysis;

public class DipSignalAnalyzerTests
{
	// toEt that treats the bar's UTC clock as already-ET, so bucketing is deterministic without TZ math.
	private static DateTime AsEt(DateTimeOffset ts) => ts.UtcDateTime;

	[Fact]
	public void AggregateTo5Min_BucketsByClockAndAggregatesOhlcv()
	{
		var day = new DateTime(2025, 1, 2, 14, 30, 0, DateTimeKind.Utc); // 14:30 "ET"
		var oneMin = new List<MinuteBar>();
		for (var m = 0; m < 5; m++) // 14:30..14:34 → one 5-min bucket
			oneMin.Add(new MinuteBar(new DateTimeOffset(day.AddMinutes(m)), 100m + m, 110m + m, 90m - m, 101m + m, 10));
		oneMin.Add(new MinuteBar(new DateTimeOffset(day.AddMinutes(5)), 200m, 201m, 199m, 200m, 7)); // 14:35 → next bucket

		var bars = DipSignalAnalyzer.AggregateToMinutes(oneMin, AsEt, 5);

		Assert.Equal(2, bars.Count);
		Assert.Equal(100m, bars[0].Open);          // first minute's open
		Assert.Equal(114m, bars[0].High);          // max high (110+4)
		Assert.Equal(86m, bars[0].Low);            // min low (90-4)
		Assert.Equal(105m, bars[0].Close);         // last minute's close (101+4)
		Assert.Equal(50, bars[0].Volume);          // 5×10
		Assert.Equal(new DateTime(2025, 1, 2, 14, 30, 0), bars[0].EtStart);
		Assert.Equal(new DateTime(2025, 1, 2, 14, 35, 0), bars[1].EtStart);
	}

	[Fact]
	public void Analyze_WiresEntryAndForwardReturnsCorrectly()
	{
		// Two 09:30-based sessions of 5-min bars. Force RSI + Bollinger to always pass (RsiLow huge, negative
		// band width → band sits above price), so signals reduce to the "hist < 0" gate — enough to exercise the
		// entry/forward-return indexing, which is the bug-prone wiring this test guards. Expected signals are
		// recomputed from SeriesIndicators (independently unit-tested) so the assertion isn't self-fulfilling.
		var bars = BuildTwoSessions();
		var p = new DipParams(RsiLow: 1000m, BbK: -1000m);

		var result = DipSignalAnalyzer.Analyze(bars, p, intervalMinutes: 5);

		var closes = bars.Select(b => b.Close).ToArray();
		var rsi = SeriesIndicators.Rsi(closes, p.RsiPeriod);
		var lower = SeriesIndicators.BollingerLower(closes, p.BbPeriod, p.BbK, emaBasis: true);
		var (_, _, hist) = SeriesIndicators.Macd(closes, p.MacdFast, p.MacdSlow, p.MacdSignal);

		var expected = new List<int>();
		for (var i = 0; i < bars.Count; i++)
		{
			if (rsi[i] is not { } r || lower[i] is not { } lb || hist[i] is not { } h) continue;
			if (!(h < 0m && r < p.RsiLow && closes[i] < lb)) continue;
			if (i + 1 >= bars.Count || bars[i + 1].Day != bars[i].Day) continue; // same-session entry only
			expected.Add(i);
		}

		Assert.NotEmpty(expected); // the crafted data must actually trigger, else the test proves nothing
		Assert.Equal(expected.Count, result.Signals.Count);

		for (var k = 0; k < expected.Count; k++)
		{
			var i = expected[k];
			var e = i + 1;
			var sig = result.Signals[k];
			Assert.Equal(bars[e].Open, sig.EntryPrice);
			Assert.Equal(bars[e].EtStart, sig.EntryEt);

			// +30m = close 6 bars after entry, same session; else null.
			var expect30 = e + 6 < bars.Count && bars[e + 6].Day == bars[e].Day ? (bars[e + 6].Close - bars[e].Open) / bars[e].Open : (decimal?)null;
			Assert.Equal(expect30, sig.Ret30);

			// EOD = last close of the entry's session.
			var eod = bars.Where(b => b.Day == bars[e].Day).Last().Close;
			Assert.Equal((eod - bars[e].Open) / bars[e].Open, sig.RetEod);
		}
	}

	[Fact]
	public void Analyze_NoSignalWhenNextBarIsNewSession()
	{
		// A trigger on the LAST bar of a session must not fire — there's no intraday continuation bar to enter
		// on. Equivalently: an entry is bar i+1 of the SAME session as signal bar i, so an entry can never be a
		// session's first bar (that would mean the signal sat on the prior session's last bar).
		var bars = BuildTwoSessions();
		var result = DipSignalAnalyzer.Analyze(bars, new DipParams(RsiLow: 1000m, BbK: -1000m), intervalMinutes: 5);
		Assert.NotEmpty(result.Signals);
		Assert.All(result.Signals, s =>
		{
			var sessionStart = bars.Where(b => b.Day == s.EntryEt.Date).Min(b => b.EtStart);
			Assert.True(s.EntryEt > sessionStart, $"entry {s.EntryEt:HH:mm} is the first bar of its session");
		});
	}

	private static List<IntradayBar> BuildTwoSessions()
	{
		var bars = new List<IntradayBar>();
		AddSession(bars, new DateTime(2025, 1, 2));
		AddSession(bars, new DateTime(2025, 1, 3));
		return bars;
	}

	// 50 five-min bars from 09:30, price path: up-ramp (warm MACD), V-dip, recovery — produces histogram-rising bars.
	private static void AddSession(List<IntradayBar> bars, DateTime day)
	{
		var start = day.AddHours(9).AddMinutes(30);
		for (var i = 0; i < 50; i++)
		{
			decimal price = i < 25 ? 100m + i * 0.5m         // gentle uptrend
				: i < 32 ? 112.5m - (i - 25) * 2m            // sharp drop
				: 98.5m + (i - 32) * 1.2m;                   // recovery
			var et = start.AddMinutes(i * 5);
			bars.Add(new IntradayBar(et, Open: price, High: price + 0.3m, Low: price - 0.3m, Close: price, Volume: 100));
		}
	}
}

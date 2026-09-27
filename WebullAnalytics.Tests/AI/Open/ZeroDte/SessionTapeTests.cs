using WebullAnalytics.AI;
using WebullAnalytics.AI.Open.ZeroDte;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open.ZeroDte;

// SessionTape turns a minute series into the discrete facts the gate asks about. The two properties worth
// locking hardest are (1) the VWAP comparison is against the RUNNING VWAP, not the window's final one —
// otherwise "held above VWAP for N minutes" is look-ahead and meaningless — and (2) the flip count ignores
// exactly-at-VWAP bars so a bar passing through does not register as two flips.
public class SessionTapeTests
{
	private static readonly DateTimeOffset Open = new(2026, 9, 23, 13, 30, 0, TimeSpan.Zero); // 09:30 ET

	private static MinuteBar Bar(int minute, decimal close, decimal? high = null, decimal? low = null, long volume = 1000)
		=> new(Open.AddMinutes(minute), close, high ?? close, low ?? close, close, volume);

	/// <summary>Bars that rise monotonically sit above the running VWAP from the second bar on, so the held
	/// streak is barCount − 1 with no flips. The first bar is definitionally AT its own VWAP (a one-bar VWAP
	/// equals that bar's typical price), so the streak starts at bar 1 — which is why a "held N minutes"
	/// threshold is effectively measured from the second minute of the session, not the first.</summary>
	[Fact]
	public void RisingSeries_HoldsAboveRunningVwap_FromSecondBarOn()
	{
		var bars = Enumerable.Range(0, 40).Select(i => Bar(i, 100m + i * 0.1m)).ToList();

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: 0.05m, rangeWindowMinutes: 20, boundaryTouchTolerancePct: 0.03m);

		Assert.NotNull(tape);
		Assert.Equal(VwapSide.Above, tape!.Side);
		Assert.Equal(39, tape.MinutesHeldOnSide);
		// The At → Above transition off bar 0 is not a flip: a bar sitting exactly on VWAP belongs to
		// neither side, so crossing through it must not be double-counted.
		Assert.Equal(0, tape.VwapFlips);
	}

	/// <summary>A falling series is the mirror image: below the running VWAP throughout, no flips.</summary>
	[Fact]
	public void FallingSeries_HoldsBelowRunningVwap()
	{
		var bars = Enumerable.Range(0, 40).Select(i => Bar(i, 100m - i * 0.1m)).ToList();

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: 0.05m, rangeWindowMinutes: 20, boundaryTouchTolerancePct: 0.03m);

		Assert.Equal(VwapSide.Below, tape!.Side);
		Assert.Equal(39, tape.MinutesHeldOnSide);
		Assert.Equal(0, tape.VwapFlips);
	}

	/// <summary>The hold streak RESETS on a side change, so a long run followed by a reclaim reports only
	/// the minutes since the reclaim. This is the property the Class #02 "reclaim AND hold" rule rides on:
	/// without the reset, a one-minute poke back above VWAP would inherit the earlier streak and confirm.</summary>
	[Fact]
	public void HoldStreak_ResetsOnSideChange()
	{
		// 30 bars falling (below VWAP), then a sharp recovery that lifts the last 3 closes above the
		// running mean.
		var bars = new List<MinuteBar>();
		for (var i = 0; i < 30; i++) bars.Add(Bar(i, 100m - i * 0.05m));
		for (var i = 30; i < 33; i++) bars.Add(Bar(i, 120m));

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: 0.05m, rangeWindowMinutes: 20, boundaryTouchTolerancePct: 0.03m)!;

		Assert.Equal(VwapSide.Above, tape.Side);
		Assert.Equal(3, tape.MinutesHeldOnSide);
		Assert.Equal(1, tape.VwapFlips);
	}

	/// <summary>Gap bucketing is by magnitude: a move under minGapPct is Flat, not a direction. A flat open
	/// carries no initial bias, which is what makes the gapAndVwap mode decline to trade on it.</summary>
	[Theory]
	[InlineData(100.0, 0.05, (int)GapDirection.Flat)]   // 0.00% gap
	[InlineData(100.5, 0.05, (int)GapDirection.Bull)]   // +0.50%
	[InlineData(99.5, 0.05, (int)GapDirection.Bear)]    // −0.50%
	[InlineData(100.02, 0.05, (int)GapDirection.Flat)]  // +0.02%, under the 0.05% floor
	public void GapDirection_BucketsByMagnitude(double open, double minGapPct, int expected)
	{
		var bars = Enumerable.Range(0, 30).Select(i => Bar(i, (decimal)open)).ToList();

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: (decimal)minGapPct, rangeWindowMinutes: 20, boundaryTouchTolerancePct: 0.03m)!;

		Assert.Equal((GapDirection)expected, tape.Gap);
	}

	/// <summary>No prior close = no gap claim. The gate must not read "flat gap" as a fact when the
	/// reference price is simply missing.</summary>
	[Fact]
	public void MissingPrevClose_YieldsFlatGapAndZeroPct()
	{
		var bars = Enumerable.Range(0, 30).Select(i => Bar(i, 100m)).ToList();

		var tape = SessionTape.Build(bars, prevClose: null, minGapPct: 0.05m, rangeWindowMinutes: 20, boundaryTouchTolerancePct: 0.03m)!;

		Assert.Equal(GapDirection.Flat, tape.Gap);
		Assert.Equal(0m, tape.GapPct);
		Assert.Null(tape.PrevClose);
	}

	/// <summary>The range window is TRAILING: an early spike outside the window must not widen it, or a
	/// session that opened wild and then coiled would never read as balanced.</summary>
	[Fact]
	public void RangeWindow_IsTrailing_AndExcludesEarlierSpikes()
	{
		var bars = new List<MinuteBar>();
		bars.Add(Bar(0, 100m, high: 130m, low: 70m));                       // violent opening bar
		for (var i = 1; i < 60; i++) bars.Add(Bar(i, 100m, high: 100.2m, low: 99.8m));

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: 0.05m, rangeWindowMinutes: 30, boundaryTouchTolerancePct: 0.03m)!;

		Assert.Equal(30, tape.WindowBars);
		Assert.Equal(100.2m, tape.WindowHigh);
		Assert.Equal(99.8m, tape.WindowLow);
		// The session extremes still remember the spike — only the window forgets it.
		Assert.Equal(130m, tape.SessionHigh);
		Assert.Equal(70m, tape.SessionLow);
	}

	/// <summary>Boundary "touches" count bars whose extreme reached the window edge within tolerance. A
	/// single spike to a high is one touch, not a level.</summary>
	[Fact]
	public void BoundaryTouches_CountBarsReachingTheEdge()
	{
		var bars = new List<MinuteBar>();
		for (var i = 0; i < 30; i++)
		{
			// Two bars tag the high, one tags the low; the rest sit mid-range.
			var high = i is 5 or 20 ? 101m : 100.5m;
			var low = i is 12 ? 99m : 99.5m;
			bars.Add(Bar(i, 100m, high: high, low: low));
		}

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: 0.05m, rangeWindowMinutes: 30, boundaryTouchTolerancePct: 0.001m)!;

		Assert.Equal(101m, tape.WindowHigh);
		Assert.Equal(99m, tape.WindowLow);
		Assert.Equal(2, tape.WindowHighTouches);
		Assert.Equal(1, tape.WindowLowTouches);
	}

	/// <summary>Zero-volume bars (cash indexes on some feeds) must fall back to a TWAP rather than divide by
	/// zero or silently report a VWAP of 0 — which would make every close read as "above VWAP".</summary>
	[Fact]
	public void ZeroVolumeBars_FallBackToTwap()
	{
		var bars = Enumerable.Range(0, 30).Select(i => Bar(i, 100m + i * 0.1m, volume: 0)).ToList();

		var tape = SessionTape.Build(bars, prevClose: 100m, minGapPct: 0.05m, rangeWindowMinutes: 20, boundaryTouchTolerancePct: 0.03m)!;

		// TWAP of the typical prices sits inside the traversed range, and the last close is above it.
		Assert.InRange(tape.Vwap, 100m, 102.9m);
		Assert.Equal(VwapSide.Above, tape.Side);
	}

	[Fact]
	public void EmptySeries_ReturnsNull()
		=> Assert.Null(SessionTape.Build(Array.Empty<MinuteBar>(), 100m, 0.05m, 20, 0.03m));
}

// The gate's time-of-day comparisons are only correct if the evaluation instant is converted to ET. The three
// callers supply three different DateTimeKinds and conflating them silently shifts every entry-window
// boundary by the machine's UTC offset — which on a non-ET machine would make a no-trade day indistinguishable
// from a normal one.
public class EasternWallClockTests
{
	[Fact]
	public void UnspecifiedIsTreatedAsAlreadyEastern()
	{
		var minuteEt = new DateTime(2026, 9, 23, 10, 15, 0, DateTimeKind.Unspecified);

		Assert.Equal(minuteEt, OpenCandidateEvaluator.ToEasternWallClock(minuteEt));
	}

	[Fact]
	public void UtcIsConvertedToEastern()
	{
		// 2026-09-23 14:15 UTC = 10:15 ET (EDT, UTC−4).
		var utc = new DateTime(2026, 9, 23, 14, 15, 0, DateTimeKind.Utc);

		var et = OpenCandidateEvaluator.ToEasternWallClock(utc);

		Assert.Equal(new TimeSpan(10, 15, 0), et.TimeOfDay);
		Assert.Equal(DateTimeKind.Unspecified, et.Kind);
	}

	/// <summary>A local instant must round-trip through its own offset, not be read as ET. Constructed from a
	/// known UTC moment so the assertion holds whatever timezone the test host is set to.</summary>
	[Fact]
	public void LocalIsConvertedThroughItsOwnOffset()
	{
		var local = new DateTime(2026, 9, 23, 14, 15, 0, DateTimeKind.Utc).ToLocalTime();

		var et = OpenCandidateEvaluator.ToEasternWallClock(local);

		Assert.Equal(new TimeSpan(10, 15, 0), et.TimeOfDay);
	}
}

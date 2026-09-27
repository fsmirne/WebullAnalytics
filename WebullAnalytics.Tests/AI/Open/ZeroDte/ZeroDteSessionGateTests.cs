using WebullAnalytics.AI;
using WebullAnalytics.AI.Open.ZeroDte;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open.ZeroDte;

// The state machine's contract: exactly three states permit an entry, everything else withholds one, and a
// session that never satisfies its conditions ends the day at PastCutoff having traded nothing. That last
// property is the whole point of the gate — MinScoreToOpen cannot express it.
public class ZeroDteSessionGateTests
{
	private static readonly DateTimeOffset OpenUtc = new(2026, 9, 23, 13, 30, 0, TimeSpan.Zero); // 09:30 ET
	private static DateTime Et(int hour, int minute) => new(2026, 9, 23, hour, minute, 0, DateTimeKind.Unspecified);

	private static MinuteBar Bar(int minute, decimal close, decimal? high = null, decimal? low = null)
		=> new(OpenUtc.AddMinutes(minute), close, high ?? close, low ?? close, close, 1000);

	private static ZeroDteGateConfig Cfg(Action<ZeroDteGateConfig>? tweak = null)
	{
		var cfg = new ZeroDteGateConfig { Enabled = true };
		tweak?.Invoke(cfg);
		return cfg;
	}

	/// <summary>Monotonic rise from a gapped-up open: gap and VWAP agree and the hold is long, so the
	/// published Class #02 rule confirms bullish and the endorsed structure is the bull put spread.</summary>
	private static List<MinuteBar> BullTrend(int bars = 60) => Enumerable.Range(0, bars).Select(i => Bar(i, 100m + i * 0.02m)).ToList();
	private static List<MinuteBar> BearTrend(int bars = 60) => Enumerable.Range(0, bars).Select(i => Bar(i, 100m - i * 0.02m)).ToList();

	[Fact]
	public void BullGap_HeldAboveVwap_ConfirmsBullPut()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(), BullTrend(), prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.BullConfirmed, v.State);
		Assert.True(v.AllowsEntry);
		Assert.Equal(new[] { OpenStructureKind.ShortPutVertical }, v.AllowedStructures);
	}

	[Fact]
	public void BearGap_HeldBelowVwap_ConfirmsBearCall()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(), BearTrend(), prevClose: 101m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.BearConfirmed, v.State);
		Assert.Equal(new[] { OpenStructureKind.ShortCallVertical }, v.AllowedStructures);
	}

	/// <summary>Class #02 Scenario #2: a bullish gap the market has NOT accepted. Gap says bull, price is
	/// below VWAP — the conjunction fails, so the gate waits rather than trading the gap.</summary>
	[Fact]
	public void BullGap_BelowVwap_Waits_DoesNotFlipBearish()
	{
		// Gapped up from 99 to 100, then sold off all session: below VWAP, but the gap is still bullish.
		var v = ZeroDteSessionGate.Evaluate(Cfg(), BearTrend(), prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.WaitingForConfirmation, v.State);
		Assert.False(v.AllowsEntry);
		Assert.Empty(v.AllowedStructures);
		Assert.Contains("unconfirmed", v.Summary);
	}

	/// <summary>A flat open carries no initial bias, so gapAndVwap has nothing to confirm however clean the
	/// trend is. This is the mode's defining strictness and the reason it matched only 48% of the trader's
	/// own entries — many of his days opened flat or gapped against the side he traded.</summary>
	[Fact]
	public void FlatGap_NeverConfirmsDirectional_InGapAndVwapMode()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(c => c.Range.Enabled = false), BullTrend(), prevClose: 100m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.WaitingForConfirmation, v.State);
		Assert.Contains("flat", v.Summary);
	}

	/// <summary>vwapOnly drops the gap requirement — the same flat-open trend now confirms.</summary>
	[Fact]
	public void VwapOnlyMode_ConfirmsWithoutAGap()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(c => c.Direction.Mode = "vwapOnly"), BullTrend(), prevClose: 100m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.BullConfirmed, v.State);
	}

	/// <summary>netChange reads spot against the prior close and ignores VWAP entirely. Here price is below
	/// VWAP but still up on the day, and the mode calls it bullish — which is exactly how it ends up
	/// agreeing with the trader's posted side 34/40 times where the taught rule agrees 19/40.</summary>
	[Fact]
	public void NetChangeMode_UsesPriorCloseNotVwap()
	{
		// Gapped up hard to 100 then drifted down to ~98.8 — below VWAP, but still above a 95 prior close.
		var v = ZeroDteSessionGate.Evaluate(Cfg(c => c.Direction.Mode = "netChange"), BearTrend(), prevClose: 95m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.BullConfirmed, v.State);
	}

	[Fact]
	public void CompositeMode_FollowsTheBlendedBiasSign()
	{
		var bull = ZeroDteSessionGate.Evaluate(Cfg(c => c.Direction.Mode = "composite"), BearTrend(), prevClose: 101m, compositeBias: 0.4m, Et(10, 30));
		var bear = ZeroDteSessionGate.Evaluate(Cfg(c => c.Direction.Mode = "composite"), BullTrend(), prevClose: 99m, compositeBias: -0.4m, Et(10, 30));

		Assert.Equal(ZeroDteGateState.BullConfirmed, bull.State);
		Assert.Equal(ZeroDteGateState.BearConfirmed, bear.State);
	}

	[Fact]
	public void CompositeMode_WithoutABias_Waits()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(c => { c.Direction.Mode = "composite"; c.Range.Enabled = false; }), BullTrend(), prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.WaitingForConfirmation, v.State);
		Assert.Contains("unavailable", v.Summary);
	}

	/// <summary>The hold requirement is what makes this a CONFIRMATION gate rather than a snapshot: a fresh
	/// reclaim with only a couple of minutes behind it must not confirm.</summary>
	[Fact]
	public void FreshReclaim_BelowHoldThreshold_Waits()
	{
		var bars = new List<MinuteBar>();
		for (var i = 0; i < 40; i++) bars.Add(Bar(i, 100m - i * 0.02m));   // drift down, below VWAP
		for (var i = 40; i < 42; i++) bars.Add(Bar(i, 120m));              // two-bar spike above

		var v = ZeroDteSessionGate.Evaluate(Cfg(c => { c.Direction.MinVwapHoldMinutes = 10; c.Range.Enabled = false; }), bars, prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.WaitingForConfirmation, v.State);
		Assert.Contains("need 10m", v.Summary);
	}

	/// <summary>Too many VWAP flips is the framework's "unstable, choppy or contradictory" tape. Directional
	/// entry is vetoed even when the current side reads cleanly.</summary>
	[Fact]
	public void ExcessiveVwapFlips_VetoDirectional()
	{
		// Alternate around 100 so nearly every bar crosses the running mean.
		var bars = Enumerable.Range(0, 60).Select(i => Bar(i, i % 2 == 0 ? 99.5m : 100.5m)).ToList();

		var v = ZeroDteSessionGate.Evaluate(Cfg(c => { c.Direction.MaxVwapFlips = 5; c.Range.Enabled = false; }), bars, prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.Choppy, v.State);
		Assert.False(v.AllowsEntry);
	}

	/// <summary>A tape that rotates in a tight band with both edges repeatedly tested is the balanced state,
	/// and the endorsed structure is the iron condor. Chop does not block it — chop is what BUILDS it.</summary>
	[Fact]
	public void RotatingTightRange_ConfirmsIronCondor()
	{
		// 60 bars oscillating between 99.8 and 100.2 (a 0.4% band) with both edges tagged many times.
		var bars = Enumerable.Range(0, 60).Select(i => Bar(i, i % 2 == 0 ? 99.8m : 100.2m, high: 100.2m, low: 99.8m)).ToList();

		var cfg = Cfg(c =>
		{
			c.Direction.Mode = "off";
			c.Direction.MaxVwapFlips = 0;
			c.Range.WindowMinutes = 45;
			c.Range.MaxRangePct = 0.6m;
			c.Range.MinBoundaryTouches = 2;
			c.Range.MinVwapCrosses = 2;
		});
		// After the range state's own 12:00 floor.
		var v = ZeroDteSessionGate.Evaluate(cfg, bars, prevClose: 100m, compositeBias: null, Et(12, 30));

		Assert.Equal(ZeroDteGateState.RangeConfirmed, v.State);
		Assert.Equal(new[] { OpenStructureKind.IronCondor }, v.AllowedStructures);
	}

	/// <summary>The balanced state has its own, later time floor: the identical tape that confirms at 12:30
	/// must NOT confirm at 11:30. Without this the range state confirmed at the gate's window floor on 59% of
	/// SPX sessions, which made the gate stop being a gate — measured over 1,187 sessions, the session machine
	/// was entry-eligible on 98.2% of days with a median entry at the 10:15 floor.</summary>
	[Fact]
	public void RangeState_HasItsOwnLaterTimeFloor()
	{
		var bars = Enumerable.Range(0, 60).Select(i => Bar(i, i % 2 == 0 ? 99.8m : 100.2m, high: 100.2m, low: 99.8m)).ToList();
		var cfg = Cfg(c =>
		{
			c.Direction.Mode = "off";
			c.Direction.MaxVwapFlips = 0;   // chop veto off, so the FLOOR is unambiguously what blocks
			c.Range.EarliestEntryEt = "12:00";
			c.Range.WindowMinutes = 45;
			c.Range.MaxRangePct = 0.6m;
			c.Range.MinBoundaryTouches = 2;
			c.Range.MinVwapCrosses = 2;
		});

		var early = ZeroDteSessionGate.Evaluate(cfg, bars, prevClose: 100m, compositeBias: null, Et(11, 30));
		var late = ZeroDteSessionGate.Evaluate(cfg, bars, prevClose: 100m, compositeBias: null, Et(12, 30));

		Assert.Equal(ZeroDteGateState.WaitingForConfirmation, early.State);
		Assert.Contains("not open until 12:00", early.Summary);
		Assert.Equal(ZeroDteGateState.RangeConfirmed, late.State);
	}

	/// <summary>Clearing the floor restores the old behaviour, so the asymmetry is opt-out.</summary>
	[Fact]
	public void RangeFloorCanBeDisabled()
	{
		var bars = Enumerable.Range(0, 60).Select(i => Bar(i, i % 2 == 0 ? 99.8m : 100.2m, high: 100.2m, low: 99.8m)).ToList();
		var cfg = Cfg(c =>
		{
			c.Direction.Mode = "off";
			c.Direction.MaxVwapFlips = 0;
			c.Range.EarliestEntryEt = null;
			c.Range.WindowMinutes = 45;
			c.Range.MaxRangePct = 0.6m;
			c.Range.MinBoundaryTouches = 2;
			c.Range.MinVwapCrosses = 2;
		});

		Assert.Equal(ZeroDteGateState.RangeConfirmed, ZeroDteSessionGate.Evaluate(cfg, bars, prevClose: 100m, compositeBias: null, Et(10, 30)).State);
	}

	/// <summary>A range wider than maxRangePct is a trending or violent session, not a balanced one.</summary>
	[Fact]
	public void WideRange_DoesNotConfirmIronCondor()
	{
		var bars = Enumerable.Range(0, 60).Select(i => Bar(i, i % 2 == 0 ? 97m : 103m, high: 103m, low: 97m)).ToList();

		var cfg = Cfg(c => { c.Direction.Mode = "off"; c.Range.WindowMinutes = 45; c.Range.MaxRangePct = 0.45m; });
		var v = ZeroDteSessionGate.Evaluate(cfg, bars, prevClose: 100m, compositeBias: null, Et(12, 30));

		Assert.NotEqual(ZeroDteGateState.RangeConfirmed, v.State);
		Assert.False(v.AllowsEntry);
	}

	/// <summary>Directional wins the tie: a session that both trends and has coiled is a trending session
	/// that paused, per Class #01's ordered structure classification.</summary>
	[Fact]
	public void DirectionalTakesPrecedenceOverRange()
	{
		var bars = BullTrend(90);   // steady rise; the trailing window is also narrow

		var cfg = Cfg(c => { c.Range.WindowMinutes = 45; c.Range.MaxRangePct = 5m; c.Range.MinBoundaryTouches = 0; c.Range.MinVwapCrosses = 0; c.Range.EarliestEntryEt = null; });
		var v = ZeroDteSessionGate.Evaluate(cfg, bars, prevClose: 99m, compositeBias: null, Et(11, 0));

		Assert.Equal(ZeroDteGateState.BullConfirmed, v.State);
	}

	// ---- window and data-availability states ----

	[Fact]
	public void BeforeEarliestEntry_WithholdsEvenOnAPerfectSetup()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(), BullTrend(), prevClose: 99m, compositeBias: null, Et(9, 45));

		Assert.Equal(ZeroDteGateState.BeforeWindow, v.State);
		Assert.False(v.AllowsEntry);
		Assert.False(v.DayClosed);
	}

	/// <summary>Past the cutoff the day is CLOSED, not merely waiting — callers use this to stop scanning and
	/// record a deliberate no-trade day.</summary>
	[Fact]
	public void PastLatestEntry_ClosesTheDay_EvenOnAPerfectSetup()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(), BullTrend(), prevClose: 99m, compositeBias: null, Et(14, 30));

		Assert.Equal(ZeroDteGateState.PastCutoff, v.State);
		Assert.False(v.AllowsEntry);
		Assert.True(v.DayClosed);
		Assert.Contains("no trade today", v.Summary);
	}

	[Fact]
	public void TooFewBars_WarmsUp()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(c => c.MinBarsBeforeDecision = 30), BullTrend(10), prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.WarmingUp, v.State);
	}

	/// <summary>No tape means the gate cannot certify anything, so it fails CLOSED — but it says so, because
	/// a data gap silently zeroing out a day's trading is indistinguishable from a strategy decision.</summary>
	[Fact]
	public void NoTape_FailsClosed_AndSaysWhy()
	{
		var v = ZeroDteSessionGate.Evaluate(Cfg(), Array.Empty<MinuteBar>(), prevClose: 99m, compositeBias: null, Et(10, 30));

		Assert.Equal(ZeroDteGateState.NoTape, v.State);
		Assert.False(v.AllowsEntry);
		Assert.Contains("no RTH minute tape", v.Summary);
	}
}

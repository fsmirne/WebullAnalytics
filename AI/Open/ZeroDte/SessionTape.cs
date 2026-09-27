namespace WebullAnalytics.AI.Open.ZeroDte;

/// <summary>Which side of the session VWAP the last bar closed on.</summary>
internal enum VwapSide { At = 0, Above = 1, Below = -1 }

/// <summary>Sign of the opening gap (prior RTH close → today's RTH open), bucketed by a minimum
/// magnitude so a flat open is not read as a direction.</summary>
internal enum GapDirection { Flat = 0, Bull = 1, Bear = -1 }

/// <summary>The discrete, class-checklist facts about one 0DTE session, derived from the RTH minute
/// tape up to "now". This is deliberately NOT the continuous <see cref="IntradayBias"/> composite:
/// Class #02 asks yes/no questions ("is price above VWAP?", "has it HELD there?", "has a range been
/// established?") and the gate needs those as booleans and counts, not as a blended score.
///
/// <para><b>Running VWAP, not final VWAP.</b> Every per-bar side test compares that bar's close to the
/// VWAP <i>as of that bar</i> (cumulative from the session open). Comparing the whole day's closes to
/// the end-of-window VWAP would be look-ahead within the day and would make the "held above VWAP for
/// N minutes" test meaningless — a bar that was below the VWAP at the time can sit above the final one.</para>
///
/// <para><b>Look-ahead is the caller's contract.</b> <see cref="Build"/> consumes exactly the bars it is
/// given and never filters by time. Callers must pass RTH-only bars for the session, ending at or before
/// the decision instant — in the backtest that means excluding the forming bar (see
/// <c>OpenCandidateEvaluator.ComputeRegimeComponentsAsync</c>'s <c>includeCurrentBar</c> flag, which this
/// reuses by consuming the same bar list).</para></summary>
internal sealed record SessionTape(
	decimal SessionOpen,
	decimal? PrevClose,
	decimal Spot,
	decimal Vwap,
	decimal GapPct,
	GapDirection Gap,
	VwapSide Side,
	/// <summary>Consecutive bars (≈ minutes) the close has stayed on <see cref="Side"/> of the running VWAP.
	/// This is the Class #02 "reclaim VWAP AND HOLD above it" counter.</summary>
	int MinutesHeldOnSide,
	/// <summary>Session count of VWAP side changes (ignoring exactly-at-VWAP bars). The chop measure:
	/// Class #01 step 2 "clean vs choppy price action".</summary>
	int VwapFlips,
	decimal SessionHigh,
	decimal SessionLow,
	/// <summary>High/low of the trailing range window, and how many times each boundary was tested and
	/// held within it. Class #01's "balanced / range — price rotating between defined levels".</summary>
	decimal WindowHigh,
	decimal WindowLow,
	int WindowHighTouches,
	int WindowLowTouches,
	int WindowBars,
	int MinutesSinceOpen,
	int BarCount)
{
	public decimal SessionRangePct => Spot > 0m ? (SessionHigh - SessionLow) / Spot * 100m : 0m;
	public decimal WindowRangePct => Spot > 0m ? (WindowHigh - WindowLow) / Spot * 100m : 0m;
	public decimal VwapDeviationPct => Vwap > 0m ? (Spot - Vwap) / Vwap * 100m : 0m;

	/// <summary>Builds the tape from this session's RTH bars in chronological order.
	/// <paramref name="prevClose"/> is the prior session's RTH close (for the gap); null when unavailable,
	/// in which case <see cref="Gap"/> is <see cref="GapDirection.Flat"/> and any gap-dependent gate
	/// fails open or shut per its own config. Returns null when there are not enough bars to say anything.</summary>
	public static SessionTape? Build(IReadOnlyList<MinuteBar> rthBars, decimal? prevClose, decimal minGapPct, int rangeWindowMinutes, decimal boundaryTouchTolerancePct)
	{
		if (rthBars.Count == 0) return null;

		var sessionOpen = rthBars[0].Open;
		if (sessionOpen <= 0m) return null;

		decimal notional = 0m, typicalSum = 0m;
		long volume = 0;
		var side = VwapSide.At;
		var held = 0;
		var flips = 0;
		decimal high = decimal.MinValue, low = decimal.MaxValue;
		decimal vwap = sessionOpen;

		for (var n = 0; n < rthBars.Count; n++)
		{
			var bar = rthBars[n];
			var typical = (bar.High + bar.Low + bar.Close) / 3m;
			notional += typical * bar.Volume;
			volume += bar.Volume;
			typicalSum += typical;
			// TWAP fallback for zero-volume bars — cash indexes print no size on some feeds. Same
			// convention as IntradayTapeIndicators.ComputeVwapDeviation so the two never disagree.
			vwap = volume > 0 ? notional / volume : typicalSum / (n + 1);

			var barSide = bar.Close > vwap ? VwapSide.Above : bar.Close < vwap ? VwapSide.Below : VwapSide.At;
			if (barSide == side)
			{
				held++;
			}
			else
			{
				// A flip is a genuine side change; a bar sitting exactly on VWAP is neither side and
				// does not count as a flip (it would double-count on the way through).
				if (side != VwapSide.At && barSide != VwapSide.At) flips++;
				side = barSide;
				held = 1;
			}

			if (bar.High > high) high = bar.High;
			if (bar.Low < low) low = bar.Low;
		}

		var last = rthBars[^1];
		var spot = last.Close;

		// Trailing range window: the last N bars (whole session when shorter). Boundary "touches" are
		// bars whose extreme came within tolerance of the window boundary — the repeated tests that make
		// a level a level rather than a single spike.
		var windowBars = rangeWindowMinutes <= 0 ? rthBars.Count : Math.Min(rangeWindowMinutes, rthBars.Count);
		var windowStart = rthBars.Count - windowBars;
		decimal wHigh = decimal.MinValue, wLow = decimal.MaxValue;
		for (var i = windowStart; i < rthBars.Count; i++)
		{
			if (rthBars[i].High > wHigh) wHigh = rthBars[i].High;
			if (rthBars[i].Low < wLow) wLow = rthBars[i].Low;
		}
		var tol = spot * boundaryTouchTolerancePct / 100m;
		var highTouches = 0;
		var lowTouches = 0;
		for (var i = windowStart; i < rthBars.Count; i++)
		{
			if (rthBars[i].High >= wHigh - tol) highTouches++;
			if (rthBars[i].Low <= wLow + tol) lowTouches++;
		}

		var gapPct = prevClose is > 0m ? (sessionOpen - prevClose.Value) / prevClose.Value * 100m : 0m;
		var gap = gapPct > minGapPct ? GapDirection.Bull : gapPct < -minGapPct ? GapDirection.Bear : GapDirection.Flat;

		return new SessionTape(
			SessionOpen: sessionOpen,
			PrevClose: prevClose,
			Spot: spot,
			Vwap: vwap,
			GapPct: gapPct,
			Gap: gap,
			Side: side,
			MinutesHeldOnSide: held,
			VwapFlips: flips,
			SessionHigh: high,
			SessionLow: low,
			WindowHigh: wHigh,
			WindowLow: wLow,
			WindowHighTouches: highTouches,
			WindowLowTouches: lowTouches,
			WindowBars: windowBars,
			MinutesSinceOpen: (int)Math.Round((last.Timestamp - rthBars[0].Timestamp).TotalMinutes),
			BarCount: rthBars.Count);
	}
}

using System.Globalization;

namespace WebullAnalytics.AI.Open.ZeroDte;

/// <summary>What the session read currently says. Mutually exclusive, and only the three *Confirmed
/// states permit an entry.</summary>
internal enum ZeroDteGateState
{
	/// <summary>No usable RTH minute tape for the session. Fails CLOSED — the gate cannot certify
	/// conditions it cannot see — but is reported so a data gap never looks like a strategy decision.</summary>
	NoTape,
	/// <summary>Before <see cref="ZeroDteGateConfig.EarliestEntryEt"/>.</summary>
	BeforeWindow,
	/// <summary>Inside the window but fewer than <see cref="ZeroDteGateConfig.MinBarsBeforeDecision"/> bars.</summary>
	WarmingUp,
	/// <summary>Conditions readable but not satisfied — the Class #02 "wait for confirmation" state.
	/// The day is still live; a later minute may confirm.</summary>
	WaitingForConfirmation,
	/// <summary>VWAP side has changed too many times: "price action is unstable, choppy or contradictory"
	/// (Class #01 step 3). Directional entry is vetoed; a range entry may still qualify.</summary>
	Choppy,
	BullConfirmed,
	BearConfirmed,
	RangeConfirmed,
	/// <summary>Past <see cref="ZeroDteGateConfig.LatestEntryEt"/> with nothing ever confirmed — the day is
	/// closed for entries. This is the "no trade today" outcome the framework treats as a valid decision.</summary>
	PastCutoff
}

/// <summary>The gate's answer for one minute of one session.</summary>
/// <param name="AllowedStructures">Structures the session read endorses. Empty unless <see cref="AllowsEntry"/>.
/// Intersected with <c>opener.structures</c> downstream — the gate only ever removes candidates.</param>
/// <param name="ProtectiveCallLevel">Resistance the short CALL must sit at or beyond, when the configured
/// level source produced one. Null means "no level located on that side".</param>
/// <param name="ProtectivePutLevel">Support the short PUT must sit at or beyond (i.e. at or below).</param>
/// <param name="Summary">One line naming the state and, when waiting, what is missing — rendered per tick by
/// the watch loop so a flat day is legible rather than silent.</param>
internal sealed record ZeroDteGateVerdict(
	ZeroDteGateState State,
	IReadOnlySet<OpenStructureKind> AllowedStructures,
	decimal? ProtectiveCallLevel,
	decimal? ProtectivePutLevel,
	SessionTape? Tape,
	string Summary)
{
	public bool AllowsEntry => State is ZeroDteGateState.BullConfirmed or ZeroDteGateState.BearConfirmed or ZeroDteGateState.RangeConfirmed;

	/// <summary>True once no later minute today can produce an entry, so callers can stop scanning and
	/// record the day as a deliberate no-trade.</summary>
	public bool DayClosed => State == ZeroDteGateState.PastCutoff;

	private static readonly IReadOnlySet<OpenStructureKind> None = new HashSet<OpenStructureKind>();

	public static ZeroDteGateVerdict Blocked(ZeroDteGateState state, SessionTape? tape, string summary)
		=> new(state, None, null, null, tape, summary);
}

/// <summary>The 0DTE entry state machine: turns the session tape into one of the framework's four
/// environments (bullish / bearish / balanced / unclear) and the protective levels a short strike must
/// hide behind.
///
/// <para><b>Decision order is Class #01's.</b> Directional states are tested before the balanced state,
/// because the framework classifies structure in that order — a day that both trends and has recently
/// coiled is a trending day that has paused, and the more specific claim wins. Consequence: a config with
/// <c>direction.mode = "off"</c> is the only way to make the range state the primary read.</para>
///
/// <para><b>It can say no all day.</b> Unlike <see cref="OpenerConfig.MinScoreToOpen"/>, which ranks a
/// continuous score and therefore almost always finds something at the first legal minute, every state
/// here except the three *Confirmed ones withholds entry outright. A session that never confirms ends
/// <see cref="ZeroDteGateState.PastCutoff"/> and trades nothing.</para></summary>
internal static class ZeroDteSessionGate
{
	/// <summary>Evaluates the gate for the decision instant <paramref name="nowEt"/>.</summary>
	/// <param name="rthBars">This session's RTH minute bars, chronological, ending at or before the decision
	/// instant. Look-ahead avoidance is the caller's contract — see <see cref="SessionTape"/>.</param>
	/// <param name="prevClose">Prior session's RTH close, for the gap. Null disables gap-dependent modes.</param>
	/// <param name="compositeBias">The engine's blended <see cref="IntradayBias"/> score, for
	/// <c>direction.mode = "composite"</c>. Null in the other modes.</param>
	public static ZeroDteGateVerdict Evaluate(ZeroDteGateConfig cfg, IReadOnlyList<MinuteBar> rthBars, decimal? prevClose, decimal? compositeBias, DateTime nowEt)
	{
		var tod = nowEt.TimeOfDay;

		var tape = SessionTape.Build(rthBars, prevClose, cfg.Direction.MinGapPct, cfg.Range.WindowMinutes, cfg.Range.BoundaryTolerancePct);
		if (tape == null)
			return ZeroDteGateVerdict.Blocked(ZeroDteGateState.NoTape, null, "no RTH minute tape for this session — gate cannot confirm conditions, entry withheld");

		if (ParsingHelpers.TryParseClockTime(cfg.LatestEntryEt, out var latest) && tod > latest)
			return ZeroDteGateVerdict.Blocked(ZeroDteGateState.PastCutoff, tape, $"past latest entry {cfg.LatestEntryEt} ET with no confirmed setup — no trade today");

		if (ParsingHelpers.TryParseClockTime(cfg.EarliestEntryEt, out var earliest) && tod < earliest)
			return ZeroDteGateVerdict.Blocked(ZeroDteGateState.BeforeWindow, tape, $"before earliest entry {cfg.EarliestEntryEt} ET — letting the session reveal its structure");

		if (tape.BarCount < cfg.MinBarsBeforeDecision)
			return ZeroDteGateVerdict.Blocked(ZeroDteGateState.WarmingUp, tape, $"only {tape.BarCount} of {cfg.MinBarsBeforeDecision} bars printed — warming up");

		var choppy = cfg.Direction.MaxVwapFlips > 0 && tape.VwapFlips > cfg.Direction.MaxVwapFlips;

		// ---- Directional states (Class #02 / #03 / #04) ----
		if (!choppy)
		{
			var (dir, missing) = ReadDirection(cfg.Direction, tape, prevClose, compositeBias);
			if (dir != 0)
			{
				var bull = dir > 0;
				var state = bull ? ZeroDteGateState.BullConfirmed : ZeroDteGateState.BearConfirmed;
				var kind = bull ? OpenStructureKind.ShortPutVertical : OpenStructureKind.ShortCallVertical;
				// A directional trade needs a protective level on its OWN side only: a bull put spread is
				// protected by support below, a bear call spread by resistance above.
				return new ZeroDteGateVerdict(
					state,
					new HashSet<OpenStructureKind> { kind },
					ProtectiveCallLevel: bull ? null : RangeLevel(cfg, tape, above: true),
					ProtectivePutLevel: bull ? RangeLevel(cfg, tape, above: false) : null,
					tape,
					$"{(bull ? "BULLISH" : "BEARISH")} confirmed ({DescribeDirection(cfg.Direction, tape)}) → {(bull ? "bull put" : "bear call")} spread");
			}

			// Not directional — fall through to the balanced test, remembering why.
			var rangeVerdict = TryRange(cfg, tape, tod);
			if (rangeVerdict != null) return rangeVerdict;
			return ZeroDteGateVerdict.Blocked(ZeroDteGateState.WaitingForConfirmation, tape, $"waiting — {missing}; no established range either ({DescribeRange(cfg, tape, tod)})");
		}

		// Choppy: directional is vetoed, but chop is what builds a range, so the balanced state still applies.
		var choppyRange = TryRange(cfg, tape, tod);
		if (choppyRange != null) return choppyRange;
		return ZeroDteGateVerdict.Blocked(ZeroDteGateState.Choppy, tape, $"choppy — {tape.VwapFlips} VWAP flips exceeds max {cfg.Direction.MaxVwapFlips}, and no established range ({DescribeRange(cfg, tape, tod)})");
	}

	/// <summary>Balanced/range state per <see cref="ZeroDteRangeConfig"/>. Returns null when the session is
	/// not (yet) rotating inside a defined range.</summary>
	private static ZeroDteGateVerdict? TryRange(ZeroDteGateConfig cfg, SessionTape tape, TimeSpan tod)
	{
		var r = cfg.Range;
		if (!r.Enabled) return null;
		// The balanced state's own (normally later) time floor. A range is only a range once the morning has
		// finished drawing it; without this the state confirms at the gate's window floor on most sessions.
		if (ParsingHelpers.TryParseClockTime(r.EarliestEntryEt, out var rangeFloor) && tod < rangeFloor) return null;
		if (tape.WindowBars < r.WindowMinutes) return null;
		if (r.MaxRangePct > 0m && tape.WindowRangePct > r.MaxRangePct) return null;
		if (tape.WindowHighTouches < r.MinBoundaryTouches) return null;
		if (tape.WindowLowTouches < r.MinBoundaryTouches) return null;
		if (tape.VwapFlips < r.MinVwapCrosses) return null;

		return new ZeroDteGateVerdict(
			ZeroDteGateState.RangeConfirmed,
			new HashSet<OpenStructureKind> { OpenStructureKind.IronCondor },
			ProtectiveCallLevel: RangeLevel(cfg, tape, above: true),
			ProtectivePutLevel: RangeLevel(cfg, tape, above: false),
			tape,
			$"BALANCED confirmed (range {tape.WindowRangePct:F2}% over {tape.WindowBars}m, {tape.WindowLowTouches}/{tape.WindowHighTouches} boundary tests, {tape.VwapFlips} VWAP crosses) → iron condor");
	}

	/// <summary>Reads the configured directional signal. Returns +1 bullish / −1 bearish / 0 unconfirmed,
	/// plus a human-readable reason when unconfirmed.</summary>
	private static (int Direction, string Missing) ReadDirection(ZeroDteDirectionConfig cfg, SessionTape tape, decimal? prevClose, decimal? compositeBias)
	{
		switch ((cfg.Mode ?? "gapAndVwap").ToLowerInvariant())
		{
			case "off":
				return (0, "direction.mode=off");

			case "vwaponly":
			{
				if (tape.Side == VwapSide.At) return (0, "price sitting exactly on VWAP");
				if (tape.MinutesHeldOnSide < cfg.MinVwapHoldMinutes)
					return (0, $"only {tape.MinutesHeldOnSide}m held {Describe(tape.Side)} VWAP, need {cfg.MinVwapHoldMinutes}m");
				return (tape.Side == VwapSide.Above ? 1 : -1, "");
			}

			case "netchange":
			{
				if (prevClose is not > 0m) return (0, "no prior close available for net-change read");
				var net = tape.Spot - prevClose.Value;
				if (net == 0m) return (0, "unchanged on the day");
				return (net > 0m ? 1 : -1, "");
			}

			case "composite":
			{
				if (!compositeBias.HasValue) return (0, "intraday composite bias unavailable");
				if (compositeBias.Value == 0m) return (0, "composite bias exactly neutral");
				return (compositeBias.Value > 0m ? 1 : -1, "");
			}

			default: // "gapAndVwap" — the published Class #02 rule.
			{
				if (tape.Gap == GapDirection.Flat)
					return (0, $"gap {tape.GapPct:+0.00;-0.00}% is flat (< {cfg.MinGapPct}%), no initial bias");
				var wantSide = tape.Gap == GapDirection.Bull ? VwapSide.Above : VwapSide.Below;
				if (tape.Side != wantSide)
					return (0, $"{(tape.Gap == GapDirection.Bull ? "bull" : "bear")} gap {tape.GapPct:+0.00;-0.00}% but price {Describe(tape.Side)} VWAP — unconfirmed, waiting for {(wantSide == VwapSide.Above ? "reclaim" : "break")}");
				if (tape.MinutesHeldOnSide < cfg.MinVwapHoldMinutes)
					return (0, $"gap and VWAP agree but only {tape.MinutesHeldOnSide}m held, need {cfg.MinVwapHoldMinutes}m");
				return (tape.Gap == GapDirection.Bull ? 1 : -1, "");
			}
		}
	}

	/// <summary>The range-boundary protective level, when the configured source uses it. Returns null when
	/// the level source is GEX-only — in that case the per-candidate check reads the wall from the chain.</summary>
	private static decimal? RangeLevel(ZeroDteGateConfig cfg, SessionTape tape, bool above)
	{
		var src = (cfg.Placement.LevelSource ?? "gexWall").ToLowerInvariant();
		if (src is not ("rangeboundary" or "both")) return null;
		return above ? tape.WindowHigh : tape.WindowLow;
	}

	private static string Describe(VwapSide side) => side switch
	{
		VwapSide.Above => "above",
		VwapSide.Below => "below",
		_ => "on"
	};

	private static string DescribeDirection(ZeroDteDirectionConfig cfg, SessionTape tape)
		=> $"gap {tape.GapPct:+0.00;-0.00}%, {Describe(tape.Side)} VWAP {tape.MinutesHeldOnSide}m, {tape.VwapFlips} flips";

	private static string DescribeRange(ZeroDteGateConfig cfg, SessionTape tape, TimeSpan tod)
	{
		if (!cfg.Range.Enabled) return "range state disabled";
		if (ParsingHelpers.TryParseClockTime(cfg.Range.EarliestEntryEt, out var floor) && tod < floor)
			return $"range state not open until {cfg.Range.EarliestEntryEt} ET";
		return $"range {tape.WindowRangePct:F2}% vs max {cfg.Range.MaxRangePct}%, tests {tape.WindowLowTouches}/{tape.WindowHighTouches} vs min {cfg.Range.MinBoundaryTouches}, crosses {tape.VwapFlips} vs min {cfg.Range.MinVwapCrosses}";
	}
}

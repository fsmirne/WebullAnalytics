using System.Text.Json.Serialization;

namespace WebullAnalytics.AI.Open.ZeroDte;

/// <summary>Condition gate for same-day-expiry entries, modelled on the GEXOptionsTrading Academy
/// framework (Class #01 step 8 "wait for clarity", Class #02 gap+VWAP confirmation, Class #05 short-strike
/// placement behind a protective level).
///
/// <para><b>Why this exists.</b> The opener's only pre-existing entry conditions are a wall-clock window
/// (<see cref="OpenerConfig.EarliestEntryTimeEt"/>) and a continuous score floor
/// (<see cref="OpenerConfig.MinScoreToOpen"/>). With a permissive floor the opener therefore fires at the
/// first legal minute of every session — 25/25 days stamped at the window boundary in the 2026-09-05
/// replication run. That is the opposite of the framework, which asks a set of yes/no questions and
/// answers "no trade today" when they are not satisfied. This gate supplies the missing state machine:
/// it can withhold entry all session and end the day flat.</para>
///
/// <para><b>Each condition is an independent knob</b> so its contribution can be measured rather than
/// assumed. That matters here: measured against the trader's own 40 directional entries on real SPX tape,
/// the taught gap+VWAP conjunction agreed with his traded side only 48% of the time, while the day's net
/// change agreed 85% of the time. Defaults are faithful to the published framework
/// (<c>direction.mode = "gapAndVwap"</c>); the alternatives exist to quantify that gap.</para>
///
/// <para>Off by default. Enabling it never widens what the opener may trade — it only removes candidates
/// the session state does not endorse, so it composes with <c>opener.structures</c> as an intersection.</para></summary>
internal sealed class ZeroDteGateConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;

	/// <summary>When true (default) the gate only constrains candidates whose short leg expires TODAY;
	/// longer-dated candidates pass through untouched. Set false to apply the session read to every DTE.
	/// The framework is explicitly a 0DTE framework, hence the default.</summary>
	[JsonPropertyName("zeroDteOnly")] public bool ZeroDteOnly { get; set; } = true;

	/// <summary>Earliest ET wall-clock time an entry may fire, "HH:mm". Distinct from
	/// <see cref="OpenerConfig.EarliestEntryTimeEt"/>, which trims the backtest's minute scan for speed:
	/// this one is a strategy condition and is reported as such in the verdict. The observed record has
	/// zero entries before 10:15 in 65 posted trades, which is the default. Null/empty = no floor.</summary>
	[JsonPropertyName("earliestEntryEt")] public string? EarliestEntryEt { get; set; } = "10:15";

	/// <summary>Latest ET wall-clock time an entry may fire, "HH:mm". Past it the day is declared a
	/// no-trade day. Observed record's latest entry is 13:35. Null/empty = no ceiling.</summary>
	[JsonPropertyName("latestEntryEt")] public string? LatestEntryEt { get; set; } = "13:30";

	/// <summary>Minimum RTH bars that must have printed before the gate will answer anything but "warming
	/// up". Guards the case where the tape is present but too short for a VWAP-hold count to mean anything
	/// (Class #01: "give the market enough time to reveal its structure").</summary>
	[JsonPropertyName("minBarsBeforeDecision")] public int MinBarsBeforeDecision { get; set; } = 30;

	[JsonPropertyName("direction")] public ZeroDteDirectionConfig Direction { get; set; } = new();
	[JsonPropertyName("range")] public ZeroDteRangeConfig Range { get; set; } = new();
	[JsonPropertyName("placement")] public ZeroDtePlacementConfig Placement { get; set; } = new();
	[JsonPropertyName("credit")] public ZeroDteCreditConfig Credit { get; set; } = new();
}

/// <summary>Class #02: how the session's directional state is decided, and what it takes to call it
/// CONFIRMED rather than "wait".</summary>
internal sealed class ZeroDteDirectionConfig
{
	/// <summary>Which read decides the directional state:
	/// <list type="bullet">
	/// <item><description><c>"gapAndVwap"</c> (default, the published Class #02 rule) — gap direction and
	/// VWAP side must AGREE, and the VWAP side must have held for <see cref="MinVwapHoldMinutes"/>.
	/// Bull gap + held above VWAP → bull put; bear gap + held below → bear call; disagreement → wait.</description></item>
	/// <item><description><c>"vwapOnly"</c> — ignore the gap, require only a held VWAP side. Class #02's
	/// Scenario #2/#4 "reclaim and hold" path taken to its logical end.</description></item>
	/// <item><description><c>"netChange"</c> — sign of spot vs the prior session close. This is the read
	/// that best describes the trader's own entries (34/40).</description></item>
	/// <item><description><c>"composite"</c> — sign of the engine's existing <see cref="IntradayBias"/>
	/// blend (gap / open-to-now / VWAP deviation per <c>indicators.intradayTape</c>). Also 34/40.</description></item>
	/// <item><description><c>"off"</c> — no directional state; only the range state can open a trade.</description></item>
	/// </list></summary>
	[JsonPropertyName("mode")] public string Mode { get; set; } = "gapAndVwap";

	/// <summary>Minimum |gap| in percent for the open to count as a directional gap rather than flat.
	/// Only read by <c>"gapAndVwap"</c>. A flat gap yields "wait" in that mode, never a direction.</summary>
	[JsonPropertyName("minGapPct")] public decimal MinGapPct { get; set; } = 0.05m;

	/// <summary>Consecutive minutes the close must have stayed on the required side of the RUNNING VWAP
	/// before the direction is CONFIRMED — the "reclaim VWAP + HOLD above it" requirement that the
	/// pre-existing engine has no representation for. 0 = accept the current side with no persistence.
	/// <para>Default 20 is calibrated, not guessed: measured on 1,129 SPX sessions (2022-01..2026-09) the
	/// hold streak at 10:15 ET runs p25 6m / median 16m / p75 29m, so a 10-minute requirement passes about
	/// two days in three and barely discriminates. 20m sits just above the median.</para></summary>
	[JsonPropertyName("minVwapHoldMinutes")] public int MinVwapHoldMinutes { get; set; } = 20;

	/// <summary>Chop veto: when the session's VWAP side has changed more than this many times, the tape is
	/// "unstable, choppy or contradictory" (Class #01 step 3 → NO TRADE) and no directional state is
	/// confirmed however the current side reads. 0 disables the veto.
	/// <para>Default 8 = the p75 of the same 1,129-session sample (p25 3 / median 5 / p90 10), so it vetoes
	/// roughly the choppiest quarter of sessions. The original 12 sat above p90 and fired on 0.8% of days —
	/// a veto that never vetoes.</para></summary>
	[JsonPropertyName("maxVwapFlips")] public int MaxVwapFlips { get; set; } = 8;
}

/// <summary>Class #01 "balanced / range" state — the environment the framework assigns to an iron condor,
/// and the one the trader described waiting for explicitly ("I want to see whether SPX develops a clear
/// and well-defined range before entering anything", 08/28; "I really need time to see what range
/// establishes", 09/17).</summary>
internal sealed class ZeroDteRangeConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

	/// <summary>Earliest ET time the BALANCED state may confirm, "HH:mm" — separate from, and normally later
	/// than, the gate's own <see cref="ZeroDteGateConfig.EarliestEntryEt"/>. The two states mature at
	/// different times and the framework treats them that way: a gap+VWAP read is legible shortly after the
	/// open, but a range is only a range once the morning has finished drawing it. The trader is explicit
	/// about the asymmetry — his directional entries cluster at 10:15-10:30, while on range days he posts
	/// "No trade expected before 12:30 PM New York time. I want to see whether SPX develops a clear and
	/// well-defined range before entering anything" (08/28) and "No more trades until 12.15PM at least and
	/// maybe 13PM, I really need time to see how market behaves and what range establishes" (09/17).
	/// Null/empty = no separate floor (the gate's own window applies).</summary>
	[JsonPropertyName("earliestEntryEt")] public string? EarliestEntryEt { get; set; } = "12:00";

	/// <summary>Trailing window, in minutes, over which the range must hold.</summary>
	[JsonPropertyName("windowMinutes")] public int WindowMinutes { get; set; } = 45;

	/// <summary>The window's high-low range must be no wider than this percent of spot for the session to
	/// count as balanced.
	/// <para>Default 0.25 is calibrated: across 1,129 SPX sessions the 45-minute trailing range at 10:15 ET
	/// has median 0.43%, so the original 0.45% admitted 53% of days — a coin flip dressed up as a condition.
	/// 0.25% admits 17%, which is what "balanced" ought to mean for a range-bound structure. Share of days
	/// admitted by threshold: 0.20% → 8%, 0.25% → 17%, 0.30% → 26%, 0.35% → 37%, 0.45% → 53%.</para></summary>
	[JsonPropertyName("maxRangePct")] public decimal MaxRangePct { get; set; } = 0.25m;

	/// <summary>Each boundary must have been tested at least this many times inside the window. One spike
	/// to a high is not a level; repeated rejection from it is.
	/// <para>Default 3: at the 0.03% tolerance below, ≥2 touches occur on 68% of sessions, ≥3 on 38%,
	/// ≥4 on 21%.</para></summary>
	[JsonPropertyName("minBoundaryTouches")] public int MinBoundaryTouches { get; set; } = 3;

	/// <summary>How close to the window boundary a bar's extreme must come to count as a test, as a
	/// percent of spot.</summary>
	[JsonPropertyName("boundaryTolerancePct")] public decimal BoundaryTolerancePct { get; set; } = 0.03m;

	/// <summary>Minimum session VWAP crossings for the tape to read as rotation rather than trend —
	/// "neither buyers nor sellers have clear control and price is rotating between defined levels".
	/// Default 3 (median is 5 by 10:15, so this is a light requirement; the range width and boundary-test
	/// counts do most of the discriminating).</summary>
	[JsonPropertyName("minVwapCrosses")] public int MinVwapCrosses { get; set; } = 3;
}

/// <summary>Class #05: the short strike must sit behind a protective level, not at a convenient delta.
/// "If there is no good short strike, there is no good trade."</summary>
internal sealed class ZeroDtePlacementConfig
{
	/// <summary>When true, a candidate is rejected unless a protective level was located on its short
	/// side. When false, placement is unconstrained and only the distance bounds below apply.</summary>
	[JsonPropertyName("requireProtectiveLevel")] public bool RequireProtectiveLevel { get; set; } = true;

	/// <summary>Where the protective level comes from: <c>"gexWall"</c> (default — the side-restricted
	/// max-GEX strike at the traded expiry, i.e. the call wall above spot / put wall below spot, the Class #05
	/// "Call Wall / Put Wall"), <c>"rangeBoundary"</c> (the established range edge from the trailing window),
	/// or <c>"both"</c> — the short must clear BOTH layers, which means sitting beyond whichever level is
	/// FARTHER from spot. <c>"both"</c> is therefore the strictest setting, not a disjunction.</summary>
	[JsonPropertyName("levelSource")] public string LevelSource { get; set; } = "gexWall";

	/// <summary>When true (default) the short strike must be at or BEYOND the protective level — above the
	/// call wall for a short call, below the put wall for a short put. This is the literal Class #05 rule,
	/// which held in 38/51 of the trader's own posted legs. When false the level must merely exist and lie
	/// between spot and the short strike is not required.</summary>
	[JsonPropertyName("shortBeyondLevel")] public bool ShortBeyondLevel { get; set; } = true;

	/// <summary>Minimum short-strike distance from spot as a percent of spot. Guards the near-ATM shorts
	/// the observed record is full of (median 0.19% for calls) from becoming arbitrarily close. 0 disables.</summary>
	[JsonPropertyName("minShortDistancePct")] public decimal MinShortDistancePct { get; set; } = 0.10m;

	/// <summary>Maximum short-strike distance from spot as a percent of spot. Past it the credit is not
	/// worth collecting on a 0DTE. 0 disables.</summary>
	[JsonPropertyName("maxShortDistancePct")] public decimal MaxShortDistancePct { get; set; } = 1.20m;

	/// <summary>If the protective level itself is further from spot than this percent, there is "nowhere
	/// logical to place the short strike" (Class #03/#04's avoid list) and the side is vetoed rather than
	/// placed at an unprotected strike. 0 disables.</summary>
	[JsonPropertyName("maxLevelDistancePct")] public decimal MaxLevelDistancePct { get; set; } = 0.80m;
}

/// <summary>Class #03 §7 / Class #04 §8 "premium vs risk": a well-placed strike is still a pass if the
/// credit does not pay for the width. The trader states an explicit floor on every posted trade ("you can
/// enter until $0.9, less than that is too risky and not worth the premium received").</summary>
internal sealed class ZeroDteCreditConfig
{
	/// <summary>Minimum net credit as a fraction of the spread width. Observed floors: $0.60 on a 5-wide
	/// (12%) and $1.20 on a 10-wide (12%). 0 disables.</summary>
	[JsonPropertyName("minPctOfWidth")] public decimal MinPctOfWidth { get; set; } = 0.12m;

	/// <summary>Maximum net credit as a fraction of the spread width. A very rich credit means the short
	/// is too close to price to be the protected trade the framework describes — the same information the
	/// floor carries, from the other side. Observed maximum is 44% (a $2.20 credit on a 5-wide).
	/// 0 or ≥1 disables.</summary>
	[JsonPropertyName("maxPctOfWidth")] public decimal MaxPctOfWidth { get; set; } = 0.45m;
}

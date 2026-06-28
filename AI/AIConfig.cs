using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class AIConfig
{
	/// <summary>Active ticker for this run. Populated programmatically from the single positional CLI
	/// argument (one ticker per scan/watch/replay/backtest) — not bound to a JSON field.</summary>
	[JsonIgnore] public string Ticker { get; set; } = "";

	/// <summary>Active strategy token for this run (e.g. "DC", "0DTE", "generic"). Populated from the
	/// <c>--strategy</c> CLI option, else from <see cref="DefaultStrategy"/>. Selects the
	/// <c>ai-config.<TICKER>.<STRATEGY>.json</c> layer and segments output files. Not a JSON field.</summary>
	[JsonIgnore] public string Strategy { get; set; } = "";

	/// <summary>Single-element set for the position/quote APIs that still take a ticker set.</summary>
	public HashSet<string> TickerSet() => new(StringComparer.OrdinalIgnoreCase) { Ticker };

	/// <summary>Strategy used when <c>--strategy</c> is omitted. Cascades through the merge: set a global
	/// default in the base config, override per-ticker in <c>ai-config.<TICKER>.json</c>. Empty means
	/// a strategy must be passed explicitly.</summary>
	[JsonPropertyName("defaultStrategy")] public string DefaultStrategy { get; set; } = "";

	[JsonPropertyName("watch")] public WatchConfig Watch { get; set; } = new();
	[JsonPropertyName("cashReserve")] public CashReserveConfig CashReserve { get; set; } = new();
	[JsonPropertyName("log-level")] public string LogLevel { get; set; } = "information"; // error | information | debug
	[JsonPropertyName("indicators")] public IndicatorsConfig Indicators { get; set; } = new();
	[JsonPropertyName("rules")] public RulesConfig Rules { get; set; } = new();
	[JsonPropertyName("execution")] public ExecutionConfig Execution { get; set; } = new();
	[JsonPropertyName("opener")] public OpenerConfig Opener { get; set; } = new();
	[JsonPropertyName("autoExecute")] public AutoExecuteConfig AutoExecute { get; set; } = new();
}

/// <summary>Settings specific to the long-running <c>wa ai watch</c> loop: poll cadence and the
/// optional scheduled first-tick time. Kept separate from <c>autoExecute</c> (which both
/// <c>wa ai scan</c> and <c>wa ai watch</c> consume) so loop-only knobs are easy to find.</summary>
internal sealed class WatchConfig
{
	[JsonPropertyName("tickIntervalSeconds")] public int TickIntervalSeconds { get; set; } = 60;
	/// <summary>Scheduled first-tick time (ET, format "HH:mm" or "HH:mm:ss"). The watch loop sleeps until
	/// this time before evaluating its first tick. Defaults to just after the open (09:30:30) — a global
	/// setting, kept in the base config rather than per-ticker. Set null/empty to tick immediately when
	/// market is open. CLI <c>--start</c> overrides.</summary>
	[JsonPropertyName("startTime")] public string? StartTime { get; set; } = "09:30:30";
}

/// <summary>Pipeline-wide inputs / measurement knobs. Read by both the opener (for macro bias and
/// candidate scoring) and the management rules (for opportunistic-roll guards and roll-strike snapping).
/// Centralized here so duplication is impossible and per-ticker overrides apply uniformly.</summary>
internal sealed class IndicatorsConfig
{
	/// <summary>Fallback implied volatility used when a leg has no live IV. Applies to opener scoring
	/// and rule evaluation alike. Stored as a percentage (e.g. 18 = 18%); sane for index/ETF underlyings,
	/// override per-ticker for higher-vol names.</summary>
	[JsonPropertyName("ivDefaultPct")] public decimal IvDefaultPct { get; set; } = 18m;

	/// <summary>Strike-grid increment for the active ticker, in dollars. Ticker-specific (SPXW=5,
	/// SPY=1, GME=0.5, …). Used by the opener candidate enumerator and the roll-rule strike snappers.
	/// Validator rejects 0 — must be set in the per-ticker config (e.g. ai-config.SPXW.json).</summary>
	[JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0m;

	[JsonPropertyName("technicalFilter")] public TechnicalFilterConfig TechnicalFilter { get; set; } = new();

	[JsonPropertyName("intradayTape")] public OpenerIntradayTapeConfig IntradayTape { get; set; } = new();

	[JsonPropertyName("events")] public OpenerEventsConfig Events { get; set; } = new();
}

/// <summary>
/// Root container for auto-execution settings, consumed by both <c>wa ai watch</c> (every tick) and
/// <c>wa ai scan</c> (one-shot). Two independent paths with parallel security gates:
///   - <c>management</c>: closes/rolls emitted by the rule engine, with tranche scheduling.
///   - <c>opener</c>: new-position opens emitted by the opener, with per-day fingerprint dedup.
/// </summary>
internal sealed class AutoExecuteConfig
{
	[JsonPropertyName("management")] public ManagementAutoExecuteConfig Management { get; set; } = new();
	[JsonPropertyName("opener")] public OpenerAutoExecuteConfig Opener { get; set; } = new();
}

/// <summary>
/// Opt-in execution of opener proposals (new positions). Mirrors the security model of
/// <see cref="ManagementAutoExecuteConfig"/>: <c>enabled</c> turns the executor on, <c>submit</c>
/// flips dry-run logging to real PlaceOrder calls. Off by default on both flags. Per-day fingerprint
/// deduplication prevents the same proposal from firing multiple times across successive ticks.
/// </summary>
internal sealed class OpenerAutoExecuteConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	/// <summary>If false, the executor logs the action it WOULD take but does not call PlaceOrder.</summary>
	[JsonPropertyName("submit")] public bool Submit { get; set; } = false;
	/// <summary>Order time-in-force: "DAY" (in-force for the current session only) or "GTC" (queues
	/// across sessions). Default DAY matches normal in-session operation. Use GTC for off-hours
	/// submission — Webull rejects DAY orders outside 08:00–16:00 ET on trading days with
	/// OAUTH_OPENAPI_OPTION_CAN_NOT_TRADING_FOR_NON_TRADING_HOURS, but accepts GTC.</summary>
	[JsonPropertyName("timeInForce")] public string TimeInForce { get; set; } = "DAY";
	/// <summary>Max LIVE opener submissions across all tickers per trading day. Default 1, matching
	/// the backtest's <c>--top-per-step 1</c> convention. Without this cap a watch session that ticks
	/// every minute can fire the same number of opens as there are distinct proposal fingerprints
	/// the opener emits during the day (e.g. when strike drifts with spot). Dry-run emissions are
	/// NOT capped — only live PlaceOrder calls are counted.
	/// Scope: OPEN proposals only. Management rules (LegInShortRule, StopLoss, TakeProfit, rolls,
	/// etc.) flow through <see cref="ManagementAutoExecutor"/> and are NOT subject to this cap —
	/// closing/managing existing positions should never be throttled by a daily-open limit.
	/// Cross-process enforcement: each successful submit is appended to
	/// <c>data/opener-submissions.jsonl</c> under an OS-level file lock, so concurrent
	/// <c>wa ai scan</c> / <c>wa ai watch</c> invocations on the same machine share the count.
	/// Different machines do not coordinate.</summary>
	[JsonPropertyName("maxOrdersPerDay")] public int MaxOrdersPerDay { get; set; } = 1;
	/// <summary>Allow-list of <c>OpenStructureKind</c> names to auto-execute. Empty = all structures allowed.</summary>
	[JsonPropertyName("structures")] public List<string> Structures { get; set; } = new();
}

/// <summary>
/// Opt-in execution of selected management-rule proposals (closes/rolls). Off by default.
/// The <c>rules</c> allow-list lets users enable one rule at a time as they validate behavior.
/// </summary>
internal sealed class ManagementAutoExecuteConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	/// <summary>If false, the executor logs the action it WOULD take but does not call PlaceOrder.
	/// Use this to validate schedule and pricing against the live tape before flipping to real submission.</summary>
	[JsonPropertyName("submit")] public bool Submit { get; set; } = false;
	/// <summary>Order time-in-force: "DAY" (in-force for the current session only) or "GTC" (queues
	/// across sessions). Default DAY matches normal in-session operation. Use GTC for off-hours
	/// submission — Webull rejects DAY orders outside 08:00–16:00 ET on trading days but accepts GTC.</summary>
	[JsonPropertyName("timeInForce")] public string TimeInForce { get; set; } = "DAY";
	/// <summary>Names of rules whose proposals are eligible for auto-execution. Empty by default — nothing
	/// auto-executes until explicitly allow-listed. Anything not listed still surfaces as a suggestion only.</summary>
	[JsonPropertyName("rules")] public List<string> Rules { get; set; } = new();
	[JsonPropertyName("scaleOut")] public ScaleOutConfig ScaleOut { get; set; } = new();
}

/// <summary>
/// Tranche schedule for scaled close execution. When a Close proposal's qty is at least <c>minQty</c>,
/// the <see cref="ManagementAutoExecutor"/> splits the close into the configured time windows;
/// otherwise it fires a single order immediately. Final tranche (3) always closes whatever remains,
/// so mid-day partial fills still converge to a fully-closed position by the last window.
/// </summary>
internal sealed class ScaleOutConfig
{
	[JsonPropertyName("tz")] public string Tz { get; set; } = "America/New_York";
	[JsonPropertyName("tranche1Start")] public string Tranche1Start { get; set; } = "10:00";
	[JsonPropertyName("tranche1End")] public string Tranche1End { get; set; } = "10:30";
	[JsonPropertyName("tranche2Start")] public string Tranche2Start { get; set; } = "12:30";
	[JsonPropertyName("tranche2End")] public string Tranche2End { get; set; } = "13:00";
	[JsonPropertyName("tranche3Start")] public string Tranche3Start { get; set; } = "15:00";
	[JsonPropertyName("tranche3End")] public string Tranche3End { get; set; } = "15:30";
	[JsonPropertyName("tranche1Fraction")] public decimal Tranche1Fraction { get; set; } = 0.3333m;
	[JsonPropertyName("tranche2Fraction")] public decimal Tranche2Fraction { get; set; } = 0.5m;
	/// <summary>Position quantity at or above which scaling is applied. Smaller closes go in a single order.</summary>
	[JsonPropertyName("minQty")] public int MinQty { get; set; } = 100;
}

internal sealed class CashReserveConfig
{
	[JsonPropertyName("mode")] public string Mode { get; set; } = "percent";   // "percent" or "absolute"
	[JsonPropertyName("value")] public decimal Value { get; set; } = 25m;      // percent of account value, or absolute $
}

/// <summary>Resolves the proposal JSONL path. The file is always <c>data/ai-proposals.<TICKER>.jsonl</c>,
/// derived from the single required ticker argument — the path is not configurable. Per-ticker scoping lets
/// concurrent single-ticker <c>wa ai watch</c> / <c>wa ai scan</c> runs write without sharing a file.</summary>
internal static class ProposalLog
{
	public static string RelativePath(string ticker, string strategy) => $"data/ai-proposals.{ticker.ToUpperInvariant()}.{strategy}.jsonl";
	public static string ResolvedPath(string ticker, string strategy) => Program.ResolvePath(RelativePath(ticker, strategy));

	/// <summary>All proposal logs for a ticker across strategies — <c>data/ai-proposals.<TICKER>.*.jsonl</c>
	/// plus the legacy pre-strategy <c>ai-proposals.<TICKER>.jsonl</c> if present. For readers that need a
	/// ticker's full footprint regardless of strategy (e.g. the options backfill's touched-symbol scan).</summary>
	public static IReadOnlyList<string> AllResolvedPathsForTicker(string ticker)
	{
		var dir = Program.ResolvePath("data");
		var t = ticker.ToUpperInvariant();
		var paths = new List<string>();
		if (Directory.Exists(dir)) paths.AddRange(Directory.GetFiles(dir, $"ai-proposals.{t}.*.jsonl"));
		var legacy = Program.ResolvePath($"data/ai-proposals.{t}.jsonl");
		if (File.Exists(legacy) && !paths.Contains(legacy)) paths.Add(legacy);
		return paths;
	}
}

internal sealed class RulesConfig
{
	[JsonPropertyName("stopLoss")] public StopLossConfig StopLoss { get; set; } = new();
	[JsonPropertyName("opportunisticRoll")] public OpportunisticRollConfig OpportunisticRoll { get; set; } = new();
	[JsonPropertyName("takeProfit")] public TakeProfitConfig TakeProfit { get; set; } = new() { Enabled = false };
	[JsonPropertyName("defensiveRoll")] public DefensiveRollConfig DefensiveRoll { get; set; } = new() { Enabled = false };
	[JsonPropertyName("rollShortOnExpiry")] public RollShortOnExpiryConfig RollShortOnExpiry { get; set; } = new() { Enabled = false };
	[JsonPropertyName("closeBeforeShortExpiry")] public CloseBeforeShortExpiryConfig CloseBeforeShortExpiry { get; set; } = new() { Enabled = false };
	[JsonPropertyName("legInShort")] public LegInShortConfig LegInShort { get; set; } = new();
	[JsonPropertyName("completeCondor")] public CompleteCondorConfig CompleteCondor { get; set; } = new();
}

/// <summary>
/// Converts an ITM long single-leg (LongCall / LongPut) into a vertical by selling a higher-strike
/// call (for calls) or lower-strike put (for puts) at the same expiry. Locks in profit while keeping
/// some delta. Fires only when the long is gamma-saturated (delta high), profit is meaningful but
/// not yet at take-profit threshold, and there's enough DTE for the short to carry premium.
/// </summary>
internal sealed class LegInShortConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	/// <summary>Spot must be at least this percent in-the-money relative to the long strike. Default 1.0%.</summary>
	[JsonPropertyName("minSpotPctITM")] public decimal MinSpotPctITM { get; set; } = 1.0m;
	/// <summary>Absolute delta of the long leg must be at least this. Default 0.65 — gamma-saturated;
	/// additional underlying moves give diminishing premium pickup so capping upside is fair trade.</summary>
	[JsonPropertyName("minLongDelta")] public decimal MinLongDelta { get; set; } = 0.65m;
	/// <summary>Profit-to-date as fraction of initial debit; must be at least this. Default 0.50 (50%).
	/// Below this, the position hasn't earned a hedge yet — let it run.</summary>
	[JsonPropertyName("triggerProfitPct")] public decimal TriggerProfitPct { get; set; } = 0.50m;
	/// <summary>Minimum days-to-expiry on the long. Default 5. Below this, the short carries little
	/// premium and isn't worth the transaction friction.</summary>
	[JsonPropertyName("minDTE")] public int MinDTE { get; set; } = 5;
	/// <summary>Target absolute delta for the short leg. Default 0.30 (one-sigma OTM) for debit-spread
	/// mode. In credit-spread mode (<see cref="CreditSpread"/>) you want a deeper-ITM short — set this
	/// to e.g. 0.70.</summary>
	[JsonPropertyName("targetShortDelta")] public decimal TargetShortDelta { get; set; } = 0.30m;
	/// <summary>Tolerance band around <c>targetShortDelta</c> when picking the short strike. Default 0.05.</summary>
	[JsonPropertyName("shortDeltaTolerance")] public decimal ShortDeltaTolerance { get; set; } = 0.05m;
	/// <summary>Minimum per-share credit from selling the short. Default $0.30 — below this, the
	/// round-trip cost (slippage + commissions) eats the structural benefit. Credit-spread mode
	/// typically needs a higher floor (e.g. $5.00) since deeper-ITM shorts have much richer premium.</summary>
	[JsonPropertyName("minShortCreditPerShare")] public decimal MinShortCreditPerShare { get; set; } = 0.30m;
	/// <summary>When true, picks the short on the SAME side of the long strike (sell deeper-ITM call
	/// against a less-ITM long call → bear-call credit spread; mirror for puts). The resulting position
	/// is a short vertical with a guaranteed-loss-floor structure: credit collected often exceeds the
	/// strike-width loss, monetizing the long's current ITM-ness immediately. Default false (debit
	/// spread: short OTM strike, capping upside while preserving directional bet).</summary>
	[JsonPropertyName("creditSpread")] public bool CreditSpread { get; set; } = false;
	/// <summary>Skip leg-in when VIX is at or above this level. Hypothesis: high-vol regimes have
	/// fat-tail moves and the rule's cap-the-winner action gives up massive upside. Default 999
	/// (disabled). Sensible test value: 20-25.</summary>
	[JsonPropertyName("maxVix")] public decimal MaxVix { get; set; } = 999m;
	/// <summary>Skip leg-in when today's running range (high − low / open, in percent) is at or above
	/// this level by the time the rule evaluates. "Trend day" filter — early big ranges correlate with
	/// continued big moves. Default 999 (disabled). Sensible test value: 0.8 - 1.5%.</summary>
	[JsonPropertyName("maxIntradayRangePct")] public decimal MaxIntradayRangePct { get; set; } = 999m;
}

/// <summary>Config for <c>CompleteCondorRule</c> — converts a held single-sided short vertical into an
/// iron condor by selling the opposite-side vertical once the held side has cushion. Default disabled.</summary>
internal sealed class CompleteCondorConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	/// <summary>The held short strike must be at least this percent OTM (spot has moved away from it)
	/// before the opposite side is added. The "held side is winning" gate that makes this a range bet
	/// rather than a blind premium grab. Default 1.0%.</summary>
	[JsonPropertyName("minHeldSidePctOtm")] public decimal MinHeldSidePctOtm { get; set; } = 1.0m;
	/// <summary>Absolute-delta band for the opposite-side short leg. Mirror of the opener's shortVertical
	/// band; default 0.10–0.30 (far enough OTM that completing doesn't immediately re-arm a near-money loss).</summary>
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.10m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.30m;
	/// <summary>Minimum net credit per share for the completing vertical (short bid − long ask). Default
	/// $0.10 — 0DTE opposite-side credit is thin, and below this the round-trip cost (slippage +
	/// commissions) eats the structural benefit. Raise it to demand the added side actually pays.</summary>
	[JsonPropertyName("minCreditPerShare")] public decimal MinCreditPerShare { get; set; } = 0.10m;
	/// <summary>Skip when VIX is at or above this level — fat-tail regimes are exactly where capping the
	/// held winner and re-arming the opposite side turns hostile. Default 999 (disabled).</summary>
	[JsonPropertyName("maxVix")] public decimal MaxVix { get; set; } = 999m;
	/// <summary>Skip when today's running range (high − low / open, percent) is at or above this by the
	/// time the rule evaluates. Trend-day filter — completing into a trend is how you get run over.
	/// Default 999 (disabled). Sensible test value: 0.8 - 1.5%.</summary>
	[JsonPropertyName("maxIntradayRangePct")] public decimal MaxIntradayRangePct { get; set; } = 999m;
}

internal sealed class OpportunisticRollConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Minimum P&L-per-day-per-contract improvement (dollars) vs hold required to fire a proposal.</summary>
	[JsonPropertyName("minImprovementPerDayPerContract")] public decimal MinImprovementPerDayPerContract { get; set; } = 0.50m;
	/// <summary>Minimum OTM distance required for the new short leg, as a percentage of spot, at neutral technicals.</summary>
	[JsonPropertyName("baseOtmBufferPct")] public decimal BaseOtmBufferPct { get; set; } = 2.0m;
	/// <summary>Scales the OTM buffer by (1 + |compositeScore| × multiplier) when technicals are extended.</summary>
	[JsonPropertyName("technicalBufferMultiplier")] public decimal TechnicalBufferMultiplier { get; set; } = 1.5m;
	/// <summary>Maximum allowed increase in net position delta magnitude after the roll, as a percentage of current delta.</summary>
	[JsonPropertyName("maxDeltaIncreasePct")] public decimal MaxDeltaIncreasePct { get; set; } = 25.0m;
	/// <summary>Minimum required profit at current spot as a percentage of spot, at neutral technicals. Widens with technical extension using the same multiplier as baseOtmBufferPct.</summary>
	[JsonPropertyName("minBreakEvenMarginPct")] public decimal MinBreakEvenMarginPct { get; set; } = 0.5m;
	/// <summary>Composite technical-bias score above which call positions are blocked from rolling
	/// (extended bullish setup → don't reach further into the move). Reads the same bias the opener uses.</summary>
	[JsonPropertyName("bullishBlockThreshold")] public decimal BullishBlockThreshold { get; set; } = 0.25m;
	/// <summary>Composite technical-bias score below which put positions are blocked from rolling
	/// (extended bearish setup).</summary>
	[JsonPropertyName("bearishBlockThreshold")] public decimal BearishBlockThreshold { get; set; } = -0.25m;
}

internal sealed class StopLossConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Close when realized loss reaches this fraction of theoretical max loss. The scorer prices
	/// candidates against this same stop (it's copied into the realized-EV bundle at load), so the exit
	/// tracks the EV the opener ranked. 1.0 disables the stop (= let it ride to the max-loss floor). Default
	/// 0.50 ≈ the "2× credit" rule on typical 4×-width credit spreads.</summary>
	[JsonPropertyName("pctOfMaxLoss")] public decimal PctOfMaxLoss { get; set; } = 0.50m;
}

internal sealed class TakeProfitConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Take-profit as a fraction of theoretical max profit (0.50 = close at half the max credit;
	/// long premium often runs to 1.0). The scorer prices candidates against this same target (copied into
	/// the realized-EV bundle at load). 1.0 disables (= ride to max profit). Default 0.50.</summary>
	[JsonPropertyName("pctOfMaxProfit")] public decimal PctOfMaxProfit { get; set; } = 0.50m;
	/// <summary>Fixed take-profit: close on ANY day once mark-to-market profit reaches this % of the
	/// initial net debit (e.g. 25 = exit at +25% on debit). Models the discretionary "grab the win and
	/// recycle capital" policy, which fires far earlier than the % -of-max-projected-profit target.
	/// Default 0 = off (only the max-projected target applies). Fires first when both are configured.</summary>
	[JsonPropertyName("profitTargetPctOfDebit")] public decimal ProfitTargetPctOfDebit { get; set; } = 0m;
}

/// <summary>Fill-cost assumptions shared by the opener's scorer and the backtest simulator. These are
/// execution facts (what trading actually costs), not strategy knobs, so they live at the top level
/// rather than under <c>opener</c>. Copied into the realized-EV bundle at load.</summary>
internal sealed class ExecutionConfig
{
	/// <summary>Per-share price concession assumed on every leg of every order (entry and exit), modeling
	/// the gap between mid and the achievable fill. 0 = fill at mid. Default 0.</summary>
	[JsonPropertyName("slippagePerSharePerOrder")] public decimal SlippagePerSharePerOrder { get; set; } = 0m;
	/// <summary>Number of round-trip orders (open + close = 2) the slippage is charged against when the
	/// scorer prices a candidate's realized expectancy. Default 2.</summary>
	[JsonPropertyName("roundTrips")] public int RoundTrips { get; set; } = 2;
}

internal sealed class DefensiveRollConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("spotWithinPctOfShortStrike")] public decimal SpotWithinPctOfShortStrike { get; set; } = 1.0m;
	[JsonPropertyName("triggerDTE")] public int TriggerDTE { get; set; } = 3;
}

internal sealed class RollShortOnExpiryConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("triggerDTE")] public int TriggerDTE { get; set; } = 2;
	[JsonPropertyName("maxShortPremium")] public decimal MaxShortPremium { get; set; } = 0.10m;
	[JsonPropertyName("minRollCredit")] public decimal MinRollCredit { get; set; } = 0.05m;
}

/// <summary>
/// Decides whether to close a calendar/diagonal on its short-leg expiry day. Decision-only — does
/// not control execution. Scaled-out tranching lives in <c>ManagementAutoExecutor</c>.
/// </summary>
internal sealed class CloseBeforeShortExpiryConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	/// <summary>Minimum mark-to-market profit (as % of initial debit) before the rule fires on expiry day.</summary>
	[JsonPropertyName("minProfitPct")] public decimal MinProfitPct { get; set; } = 30m;
	/// <summary>How far past the calendar/diagonal break-even band (as % of BE level) before the emergency-close fires regardless of profit threshold.</summary>
	[JsonPropertyName("emergencyBreakEvenBufferPct")] public decimal EmergencyBreakEvenBufferPct { get; set; } = 1.0m;
	/// <summary>Models the broker's forced liquidation of ITM short options before cash settlement: Webull
	/// force-closes ITM SPY positions ~30 min before the bell to prevent assignment a retail account can't
	/// cover. When the rule is enabled, a PHYSICALLY-settled 0DTE position whose short leg is ITM this many
	/// minutes before the close is closed at the current mark instead of riding to 16:00 intrinsic. Cash-settled
	/// index roots (XSP/SPXW) are exempt — European cash settlement, no assignment. Default 30 (= 15:30 ET).</summary>
	[JsonPropertyName("brokerForceCloseMinutesBeforeClose")] public int BrokerForceCloseMinutesBeforeClose { get; set; } = 30;
	/// <summary>Webull also force-closes shorts merely AT RISK of finishing ITM, not only those already ITM.
	/// This widens the trigger: a short within this percent of the money (call: spot > strike×(1−buffer);
	/// put: spot < strike×(1+buffer)) is treated as at-risk and liquidated. Default 0 (ITM only); the true
	/// Webull threshold is opaque, so sweep this to bound the realistic drag.</summary>
	[JsonPropertyName("brokerForceCloseMoneynessBufferPct")] public decimal BrokerForceCloseMoneynessBufferPct { get; set; } = 0m;
}

/// <summary>Composite technical-bias indicator config (SMA5/20, RSI(14), N-day momentum, 200-day
/// trend gate). Lives at <see cref="IndicatorsConfig.TechnicalFilter"/>; consumed by the opener
/// (as macro bias for proposal scoring) and the opportunistic-roll rule (as a block gate via
/// <see cref="OpportunisticRollConfig.BullishBlockThreshold"/> / <see cref="OpportunisticRollConfig.BearishBlockThreshold"/>).</summary>
internal sealed class TechnicalFilterConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Number of daily closes to fetch. Must be ≥ 20 (required for SMA20).</summary>
	[JsonPropertyName("lookbackDays")] public int LookbackDays { get; set; } = 20;
	[JsonPropertyName("smaWeight")] public decimal SmaWeight { get; set; } = 1.0m;
	[JsonPropertyName("rsiWeight")] public decimal RsiWeight { get; set; } = 1.0m;
	[JsonPropertyName("momentumWeight")] public decimal MomentumWeight { get; set; } = 1.0m;
	[JsonPropertyName("momentumDays")] public int MomentumDays { get; set; } = 5;
	/// <summary>Weight of the 200-day trend gate (<c>price / SMA200 − 1</c>, clamped) in the composite
	/// technical bias. Default 0 disables (no extra cache pull). When > 0, the pipeline fetches
	/// ≥ 200 daily closes per ticker on first read.</summary>
	[JsonPropertyName("sma200Weight")] public decimal Sma200Weight { get; set; } = 0m;
}

internal static class AIConfigLoader
{
	internal const string ConfigPath = "data/ai-config.json";

	/// <summary>Loads and validates ai-config.json. Returns null (with stderr message) on any failure.</summary>
	internal static AIConfig? Load()
	{
		var path = Program.ResolvePath(ConfigPath);
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"Error: ai config not found at '{ConfigPath}'.");
			Console.Error.WriteLine($"  Run: cp ai-config.example.json {ConfigPath} and edit.");
			return null;
		}

		AIConfig? config;
		try { config = JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(path)); }
		catch (JsonException ex) { Console.Error.WriteLine($"Error: failed to parse ai-config.json: {ex.Message}"); return null; }

		if (config == null) { Console.Error.WriteLine("Error: ai-config.json is empty."); return null; }

		var err = Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config.json: {err}"); return null; }

		return config;
	}

	/// <summary>Copies the realized-EV inputs from their canonical JSON homes (<c>rules.stopLoss</c>,
	/// <c>rules.takeProfit</c>, top-level <c>execution</c>) into the opener's [JsonIgnore] bundle. The
	/// candidate scorer receives only <see cref="OpenerConfig"/>, so this load-time copy is how it reaches
	/// those values. Idempotent — call once per resolved config after layering. Keep
	/// <see cref="OpenerRealizedExpectancyConfig.Enabled"/> as-is (it deserializes from the opener block).</summary>
	internal static void PopulateRealizedEv(AIConfig config)
	{
		var ev = config.Opener.RealizedExpectancy;
		ev.Enabled = config.Opener.RealizedEvScoring;
		ev.StopLossPctOfMaxLoss = config.Rules.StopLoss.PctOfMaxLoss;
		ev.ProfitTargetPctOfMaxProfit = config.Rules.TakeProfit.PctOfMaxProfit;
		ev.SlippagePerSharePerOrder = config.Execution.SlippagePerSharePerOrder;
		ev.RoundTrips = config.Execution.RoundTrips;
	}

	/// <summary>Returns null when valid; otherwise a human-readable error string naming the field and bound.</summary>
	internal static string? Validate(AIConfig c)
	{
		if (c.Watch.TickIntervalSeconds < 1 || c.Watch.TickIntervalSeconds > 3600) return $"watch.tickIntervalSeconds: must be in [1, 3600], got {c.Watch.TickIntervalSeconds}";
		if (!string.IsNullOrWhiteSpace(c.Watch.StartTime) && !TimeOnly.TryParse(c.Watch.StartTime, CultureInfo.InvariantCulture, out _))
			return $"watch.startTime: must be HH:mm or HH:mm:ss (ET), got '{c.Watch.StartTime}'";
		if (c.CashReserve.Mode is not ("percent" or "absolute")) return $"cashReserve.mode: must be 'percent' or 'absolute', got '{c.CashReserve.Mode}'";
		if (c.CashReserve.Value < 0m) return $"cashReserve.value: must be non-negative, got {c.CashReserve.Value}";
		if (c.CashReserve.Mode == "percent" && c.CashReserve.Value > 100m) return $"cashReserve.value: must be ≤ 100 for mode 'percent', got {c.CashReserve.Value}";
		if (c.LogLevel is not ("error" or "information" or "debug")) return $"log-level: must be error|information|debug, got '{c.LogLevel}'";

		var sl = c.Rules.StopLoss;
		if (sl.PctOfMaxLoss <= 0m || sl.PctOfMaxLoss > 1m) return $"rules.stopLoss.pctOfMaxLoss: must be in (0, 1], got {sl.PctOfMaxLoss}";

		var tp = c.Rules.TakeProfit;
		if (tp.PctOfMaxProfit <= 0m || tp.PctOfMaxProfit > 1m) return $"rules.takeProfit.pctOfMaxProfit: must be in (0, 1], got {tp.PctOfMaxProfit}";
		if (tp.ProfitTargetPctOfDebit < 0m) return $"rules.takeProfit.profitTargetPctOfDebit: must be ≥ 0, got {tp.ProfitTargetPctOfDebit}";

		var ex = c.Execution;
		if (ex.SlippagePerSharePerOrder < 0m) return $"execution.slippagePerSharePerOrder: must be ≥ 0, got {ex.SlippagePerSharePerOrder}";
		if (ex.RoundTrips < 1) return $"execution.roundTrips: must be ≥ 1, got {ex.RoundTrips}";

		var dr = c.Rules.DefensiveRoll;
		if (dr.SpotWithinPctOfShortStrike < 0m) return $"rules.defensiveRoll.spotWithinPctOfShortStrike: must be ≥ 0, got {dr.SpotWithinPctOfShortStrike}";
		if (dr.TriggerDTE < 0) return $"rules.defensiveRoll.triggerDTE: must be ≥ 0, got {dr.TriggerDTE}";

		var rr = c.Rules.RollShortOnExpiry;
		if (rr.TriggerDTE < 0) return $"rules.rollShortOnExpiry.triggerDTE: must be ≥ 0, got {rr.TriggerDTE}";
		if (rr.MaxShortPremium < 0m) return $"rules.rollShortOnExpiry.maxShortPremium: must be ≥ 0, got {rr.MaxShortPremium}";
		if (rr.MinRollCredit < 0m) return $"rules.rollShortOnExpiry.minRollCredit: must be ≥ 0, got {rr.MinRollCredit}";

		var ce = c.Rules.CloseBeforeShortExpiry;
		if (ce.MinProfitPct < 0m) return $"rules.closeBeforeShortExpiry.minProfitPct: must be ≥ 0, got {ce.MinProfitPct}";
		if (ce.EmergencyBreakEvenBufferPct < 0m) return $"rules.closeBeforeShortExpiry.emergencyBreakEvenBufferPct: must be ≥ 0, got {ce.EmergencyBreakEvenBufferPct}";

		var li = c.Rules.LegInShort;
		if (li.MinSpotPctITM < 0m) return $"rules.legInShort.minSpotPctITM: must be ≥ 0, got {li.MinSpotPctITM}";
		if (li.MinLongDelta <= 0m || li.MinLongDelta >= 1m) return $"rules.legInShort.minLongDelta: must be in (0, 1), got {li.MinLongDelta}";
		if (li.TriggerProfitPct < 0m) return $"rules.legInShort.triggerProfitPct: must be ≥ 0, got {li.TriggerProfitPct}";
		if (li.MinDTE < 0) return $"rules.legInShort.minDTE: must be ≥ 0, got {li.MinDTE}";
		if (li.TargetShortDelta <= 0m || li.TargetShortDelta >= 1m) return $"rules.legInShort.targetShortDelta: must be in (0, 1), got {li.TargetShortDelta}";
		if (li.ShortDeltaTolerance <= 0m || li.ShortDeltaTolerance >= 1m) return $"rules.legInShort.shortDeltaTolerance: must be in (0, 1), got {li.ShortDeltaTolerance}";
		if (li.MinShortCreditPerShare < 0m) return $"rules.legInShort.minShortCreditPerShare: must be ≥ 0, got {li.MinShortCreditPerShare}";
		if (li.MaxVix <= 0m) return $"rules.legInShort.maxVix: must be > 0, got {li.MaxVix}";
		if (li.MaxIntradayRangePct <= 0m) return $"rules.legInShort.maxIntradayRangePct: must be > 0, got {li.MaxIntradayRangePct}";

		var cc = c.Rules.CompleteCondor;
		if (cc.MinHeldSidePctOtm < 0m) return $"rules.completeCondor.minHeldSidePctOtm: must be ≥ 0, got {cc.MinHeldSidePctOtm}";
		if (cc.ShortDeltaMin <= 0m || cc.ShortDeltaMin >= 1m) return $"rules.completeCondor.shortDeltaMin: must be in (0, 1), got {cc.ShortDeltaMin}";
		if (cc.ShortDeltaMax <= cc.ShortDeltaMin || cc.ShortDeltaMax >= 1m) return $"rules.completeCondor.shortDeltaMax: must be in (shortDeltaMin, 1), got {cc.ShortDeltaMax}";
		if (cc.MinCreditPerShare < 0m) return $"rules.completeCondor.minCreditPerShare: must be ≥ 0, got {cc.MinCreditPerShare}";
		if (cc.MaxVix <= 0m) return $"rules.completeCondor.maxVix: must be > 0, got {cc.MaxVix}";
		if (cc.MaxIntradayRangePct <= 0m) return $"rules.completeCondor.maxIntradayRangePct: must be > 0, got {cc.MaxIntradayRangePct}";

		foreach (var (label, value) in new[] { ("management", c.AutoExecute.Management.TimeInForce), ("opener", c.AutoExecute.Opener.TimeInForce) })
		{
			if (!string.Equals(value, "DAY", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "GTC", StringComparison.OrdinalIgnoreCase))
				return $"autoExecute.{label}.timeInForce: must be 'DAY' or 'GTC', got '{value}'";
		}
		if (c.AutoExecute.Opener.MaxOrdersPerDay < 1) return $"autoExecute.opener.maxOrdersPerDay: must be ≥ 1, got {c.AutoExecute.Opener.MaxOrdersPerDay}";

		var so = c.AutoExecute.Management.ScaleOut;
		foreach (var (label, value) in new[] {
			("tranche1Start", so.Tranche1Start), ("tranche1End", so.Tranche1End),
			("tranche2Start", so.Tranche2Start), ("tranche2End", so.Tranche2End),
			("tranche3Start", so.Tranche3Start), ("tranche3End", so.Tranche3End),
		})
		{
			if (!TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out _))
				return $"autoExecute.management.scaleOut.{label}: must be HH:MM, got '{value}'";
		}
		if (so.Tranche1Fraction <= 0m || so.Tranche1Fraction >= 1m) return $"autoExecute.management.scaleOut.tranche1Fraction: must be in (0, 1), got {so.Tranche1Fraction}";
		if (so.Tranche2Fraction <= 0m || so.Tranche2Fraction >= 1m) return $"autoExecute.management.scaleOut.tranche2Fraction: must be in (0, 1), got {so.Tranche2Fraction}";
		if (so.MinQty < 1) return $"autoExecute.management.scaleOut.minQty: must be ≥ 1, got {so.MinQty}";

		var or = c.Rules.OpportunisticRoll;
		if (or.BaseOtmBufferPct < 0m) return $"rules.opportunisticRoll.baseOtmBufferPct: must be ≥ 0, got {or.BaseOtmBufferPct}";
		if (or.TechnicalBufferMultiplier < 0m) return $"rules.opportunisticRoll.technicalBufferMultiplier: must be ≥ 0, got {or.TechnicalBufferMultiplier}";
		if (or.MaxDeltaIncreasePct < 0m) return $"rules.opportunisticRoll.maxDeltaIncreasePct: must be ≥ 0, got {or.MaxDeltaIncreasePct}";
		if (or.MinBreakEvenMarginPct < 0m) return $"rules.opportunisticRoll.minBreakEvenMarginPct: must be ≥ 0, got {or.MinBreakEvenMarginPct}";

		var ind = c.Indicators;
		if (ind.IvDefaultPct <= 0m) return $"indicators.ivDefaultPct: must be > 0, got {ind.IvDefaultPct}";
		if (ind.StrikeStep <= 0m) return $"indicators.strikeStep: must be > 0, got {ind.StrikeStep}. Set this in the per-ticker config (e.g. ai-config.SPXW.json) — it has no sensible default.";

		var tf = ind.TechnicalFilter;
		if (tf.Enabled)
		{
			if (tf.LookbackDays < 20) return $"indicators.technicalFilter.lookbackDays: must be ≥ 20, got {tf.LookbackDays}";
			if (tf.SmaWeight < 0m) return $"indicators.technicalFilter.smaWeight: must be ≥ 0, got {tf.SmaWeight}";
			if (tf.RsiWeight < 0m) return $"indicators.technicalFilter.rsiWeight: must be ≥ 0, got {tf.RsiWeight}";
			if (tf.MomentumWeight < 0m) return $"indicators.technicalFilter.momentumWeight: must be ≥ 0, got {tf.MomentumWeight}";
			if (tf.MomentumDays < 1) return $"indicators.technicalFilter.momentumDays: must be ≥ 1, got {tf.MomentumDays}";
			if (tf.Sma200Weight < 0m) return $"indicators.technicalFilter.sma200Weight: must be ≥ 0, got {tf.Sma200Weight}";
		}

		var op = c.Opener;
		if (op.TopNPerTicker < 1) return $"opener.topNPerTicker: must be ≥ 1, got {op.TopNPerTicker}";
		if (op.MaxCandidatesPerStructurePerTicker < 1) return $"opener.maxCandidatesPerStructurePerTicker: must be ≥ 1, got {op.MaxCandidatesPerStructurePerTicker}";
		if (op.MaxQtyPerProposal < 1) return $"opener.maxQtyPerProposal: must be ≥ 1, got {op.MaxQtyPerProposal}";
		if (op.MaxRiskPctPerProposal < 0m || op.MaxRiskPctPerProposal > 1m) return $"opener.maxRiskPctPerProposal: must be in [0, 1], got {op.MaxRiskPctPerProposal}";
		if (op.ProfitBandPct <= 0m || op.ProfitBandPct > 50m) return $"opener.profitBandPct: must be in (0, 50], got {op.ProfitBandPct}";
		if (op.VolatilityLookbackDays < 5) return $"opener.volatilityLookbackDays: must be ≥ 5, got {op.VolatilityLookbackDays}";

		var wgt = op.Weights;
		if (wgt.DirectionalFit < 0m) return $"opener.weights.directionalFit: must be ≥ 0, got {wgt.DirectionalFit}";
		if (wgt.BiasDrift < 0m) return $"opener.weights.biasDrift: must be ≥ 0, got {wgt.BiasDrift}";
		if (wgt.Whipsaw < 0m) return $"opener.weights.whipsaw: must be ≥ 0, got {wgt.Whipsaw}";
		if (wgt.VolatilityFit < 0m) return $"opener.weights.volatilityFit: must be ≥ 0, got {wgt.VolatilityFit}";
		if (wgt.GammaRegime < 0m) return $"opener.weights.gammaRegime: must be ≥ 0, got {wgt.GammaRegime}";
		if (wgt.GexBiasPull < 0m) return $"opener.weights.gexBiasPull: must be ≥ 0, got {wgt.GexBiasPull}";
		if (wgt.MaxPainBiasPull < 0m) return $"opener.weights.maxPainBiasPull: must be ≥ 0, got {wgt.MaxPainBiasPull}";
		if (wgt.StatArb < 0m) return $"opener.weights.statArb: must be ≥ 0, got {wgt.StatArb}";
		if (wgt.Sentiment < 0m) return $"opener.weights.sentiment: must be ≥ 0, got {wgt.Sentiment}";
		if (wgt.ExpectedMoveCredit < 0m) return $"opener.weights.expectedMoveCredit: must be ≥ 0, got {wgt.ExpectedMoveCredit}";
		if (wgt.IvRealizedPremium < 0m) return $"opener.weights.ivRealizedPremium: must be ≥ 0, got {wgt.IvRealizedPremium}";
		if (wgt.VixTermStructure < 0m || wgt.VixTermStructure > 1m) return $"opener.weights.vixTermStructure: must be in [0, 1], got {wgt.VixTermStructure}";
		if (wgt.IntradayTape < 0m || wgt.IntradayTape > 1m) return $"opener.weights.intradayTape: must be in [0, 1], got {wgt.IntradayTape}";

		var dteCurve = op.IntradayTapeDteCurve;
		if (dteCurve.WeightAt0Dte < 0m || dteCurve.WeightAt0Dte > 1m) return $"opener.intradayTapeDteCurve.weightAt0Dte: must be in [0, 1], got {dteCurve.WeightAt0Dte}";
		if (dteCurve.WeightAtFarDte < 0m || dteCurve.WeightAtFarDte > 1m) return $"opener.intradayTapeDteCurve.weightAtFarDte: must be in [0, 1], got {dteCurve.WeightAtFarDte}";
		if (dteCurve.FarDte < 0) return $"opener.intradayTapeDteCurve.farDte: must be ≥ 0, got {dteCurve.FarDte}";

		var lcg = op.LongConvictionGate;
		if (lcg.Weight < 0m || lcg.Weight > 1m) return $"opener.longConvictionGate.weight: must be in [0, 1], got {lcg.Weight}";
		if (lcg.Reference < 0m) return $"opener.longConvictionGate.reference: must be ≥ 0, got {lcg.Reference}";
		if (!string.IsNullOrWhiteSpace(op.EarliestEntryTimeEt) && !TimeSpan.TryParse(op.EarliestEntryTimeEt, CultureInfo.InvariantCulture, out _))
			return $"opener.earliestEntryTimeEt: must be HH:mm, got '{op.EarliestEntryTimeEt}'";

		var liq = op.Liquidity;
		if (liq.MinOpenInterest < 0) return $"opener.liquidity.minOpenInterest: must be ≥ 0, got {liq.MinOpenInterest}";
		if (liq.MinRelativeOpenInterest < 0m || liq.MinRelativeOpenInterest > 1m) return $"opener.liquidity.minRelativeOpenInterest: must be in [0, 1], got {liq.MinRelativeOpenInterest}";
		if (liq.Weight < 0m || liq.Weight > 1m) return $"opener.liquidity.weight: must be in [0, 1], got {liq.Weight}";

		var lc = op.Structures.LongCalendar;
		if (lc.ShortDteMin < 0) return $"opener.structures.longCalendar.shortDteMin: must be ≥ 0, got {lc.ShortDteMin}";
		if (lc.ShortDteMax < lc.ShortDteMin) return $"opener.structures.longCalendar.shortDteMax: must be ≥ shortDteMin, got {lc.ShortDteMax}";
		if (lc.LongDteMin < 0) return $"opener.structures.longCalendar.longDteMin: must be ≥ 0, got {lc.LongDteMin}";
		if (lc.LongDteMax < lc.LongDteMin) return $"opener.structures.longCalendar.longDteMax: must be ≥ longDteMin, got {lc.LongDteMax}";

		var dc = op.Structures.DoubleCalendar;
		if (dc.ShortDteMin < 0) return $"opener.structures.doubleCalendar.shortDteMin: must be ≥ 0, got {dc.ShortDteMin}";
		if (dc.ShortDteMax < dc.ShortDteMin) return $"opener.structures.doubleCalendar.shortDteMax: must be ≥ shortDteMin, got {dc.ShortDteMax}";
		if (dc.LongDteMin < 0) return $"opener.structures.doubleCalendar.longDteMin: must be ≥ 0, got {dc.LongDteMin}";
		if (dc.LongDteMax < dc.LongDteMin) return $"opener.structures.doubleCalendar.longDteMax: must be ≥ longDteMin, got {dc.LongDteMax}";
		if (dc.WidthSteps.Count == 0) return "opener.structures.doubleCalendar.widthSteps: must have at least one value";
		foreach (var w in dc.WidthSteps)
			if (w < 1) return $"opener.structures.doubleCalendar.widthSteps: each value must be ≥ 1, got {w}";

		var ld = op.Structures.LongDiagonal;
		if (ld.ShortDteMin < 0) return $"opener.structures.longDiagonal.shortDteMin: must be ≥ 0, got {ld.ShortDteMin}";
		if (ld.ShortDteMax < ld.ShortDteMin) return $"opener.structures.longDiagonal.shortDteMax: must be ≥ shortDteMin, got {ld.ShortDteMax}";
		if (ld.LongDteMin < 0) return $"opener.structures.longDiagonal.longDteMin: must be ≥ 0, got {ld.LongDteMin}";
		if (ld.LongDteMax < ld.LongDteMin) return $"opener.structures.longDiagonal.longDteMax: must be ≥ longDteMin, got {ld.LongDteMax}";

		var dd = op.Structures.DoubleDiagonal;
		if (dd.ShortDteMin < 0) return $"opener.structures.doubleDiagonal.shortDteMin: must be ≥ 0, got {dd.ShortDteMin}";
		if (dd.ShortDteMax < dd.ShortDteMin) return $"opener.structures.doubleDiagonal.shortDteMax: must be ≥ shortDteMin, got {dd.ShortDteMax}";
		if (dd.LongDteMin < 0) return $"opener.structures.doubleDiagonal.longDteMin: must be ≥ 0, got {dd.LongDteMin}";
		if (dd.LongDteMax < dd.LongDteMin) return $"opener.structures.doubleDiagonal.longDteMax: must be ≥ longDteMin, got {dd.LongDteMax}";
		if (dd.WidthSteps.Count == 0) return "opener.structures.doubleDiagonal.widthSteps: must have at least one value";
		foreach (var w in dd.WidthSteps)
			if (w < 1) return $"opener.structures.doubleDiagonal.widthSteps: each value must be ≥ 1, got {w}";
		if (dd.LongWingSteps.Count == 0) return "opener.structures.doubleDiagonal.longWingSteps: must have at least one value";
		foreach (var w in dd.LongWingSteps)
			if (w < 1) return $"opener.structures.doubleDiagonal.longWingSteps: each value must be ≥ 1, got {w}";

		var ib = op.Structures.IronButterfly;
		if (ib.DteMin < 0) return $"opener.structures.ironButterfly.dteMin: must be ≥ 0, got {ib.DteMin}";
		if (ib.DteMax < ib.DteMin) return $"opener.structures.ironButterfly.dteMax: must be ≥ dteMin, got {ib.DteMax}";
		if (ib.WingSteps.Count == 0) return "opener.structures.ironButterfly.wingSteps: must have at least one value";
		foreach (var w in ib.WingSteps)
			if (w < 1) return $"opener.structures.ironButterfly.wingSteps: each value must be ≥ 1, got {w}";

		var ic = op.Structures.IronCondor;
		if (ic.DteMin < 0) return $"opener.structures.ironCondor.dteMin: must be ≥ 0, got {ic.DteMin}";
		if (ic.DteMax < ic.DteMin) return $"opener.structures.ironCondor.dteMax: must be ≥ dteMin, got {ic.DteMax}";
		if (ic.WidthSteps.Count == 0) return "opener.structures.ironCondor.widthSteps: must have at least one value";
		foreach (var w in ic.WidthSteps)
			if (w < 1) return $"opener.structures.ironCondor.widthSteps: each value must be ≥ 1, got {w}";
		if (ic.BodyWidthSteps.Count == 0) return "opener.structures.ironCondor.bodyWidthSteps: must have at least one value";
		foreach (var w in ic.BodyWidthSteps)
			if (w < 1) return $"opener.structures.ironCondor.bodyWidthSteps: each value must be ≥ 1, got {w}";
		if (ic.ShortDeltaMin <= 0m || ic.ShortDeltaMin >= 1m) return $"opener.structures.ironCondor.shortDeltaMin: must be in (0, 1), got {ic.ShortDeltaMin}";
		if (ic.ShortDeltaMax <= ic.ShortDeltaMin || ic.ShortDeltaMax >= 1m) return $"opener.structures.ironCondor.shortDeltaMax: must be in (shortDeltaMin, 1), got {ic.ShortDeltaMax}";

		var sv = op.Structures.ShortVertical;
		if (sv.DteMin < 0) return $"opener.structures.shortVertical.dteMin: must be ≥ 0, got {sv.DteMin}";
		if (sv.DteMax < sv.DteMin) return $"opener.structures.shortVertical.dteMax: must be ≥ dteMin, got {sv.DteMax}";
		if (sv.WidthSteps.Count == 0) return "opener.structures.shortVertical.widthSteps: must have at least one value";
		foreach (var w in sv.WidthSteps)
			if (w < 1) return $"opener.structures.shortVertical.widthSteps: each value must be ≥ 1, got {w}";
		if (sv.ShortDeltaMin <= 0m || sv.ShortDeltaMin >= 1m) return $"opener.structures.shortVertical.shortDeltaMin: must be in (0, 1), got {sv.ShortDeltaMin}";
		if (sv.ShortDeltaMax <= sv.ShortDeltaMin || sv.ShortDeltaMax >= 1m) return $"opener.structures.shortVertical.shortDeltaMax: must be in (shortDeltaMin, 1), got {sv.ShortDeltaMax}";

		var lcp = op.Structures.LongCallPut;
		if (lcp.DteMin < 0) return $"opener.structures.longCallPut.dteMin: must be ≥ 0, got {lcp.DteMin}";
		if (lcp.DteMax < lcp.DteMin) return $"opener.structures.longCallPut.dteMax: must be ≥ dteMin, got {lcp.DteMax}";
		if (lcp.DeltaMin <= 0m || lcp.DeltaMin >= 1m) return $"opener.structures.longCallPut.deltaMin: must be in (0, 1), got {lcp.DeltaMin}";
		if (lcp.DeltaMax <= lcp.DeltaMin || lcp.DeltaMax >= 1m) return $"opener.structures.longCallPut.deltaMax: must be in (deltaMin, 1), got {lcp.DeltaMax}";

		return null;
	}
}

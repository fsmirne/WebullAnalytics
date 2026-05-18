using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class OpenerConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("topNPerTicker")] public int TopNPerTicker { get; set; } = 5;
	[JsonPropertyName("maxCandidatesPerStructurePerTicker")] public int MaxCandidatesPerStructurePerTicker { get; set; } = 8;
	[JsonPropertyName("maxQtyPerProposal")] public int MaxQtyPerProposal { get; set; } = 10;

	/// <summary>Hard cap on per-trade risk as a fraction of account value. Enforced alongside
	/// <see cref="MaxQtyPerProposal"/> in <c>OpenCandidateEvaluator.ApplyCashSizing</c>: the proposed
	/// qty is reduced so that <c>qty × CapitalAtRiskPerContract ≤ MaxRiskPctPerProposal × AccountValue</c>.
	/// Default 0.10 (10% of equity per trade) prevents a single position from dominating account
	/// drawdowns. Set to 1.0 to disable; 0 disables the proposal entirely.</summary>
	[JsonPropertyName("maxRiskPctPerProposal")] public decimal MaxRiskPctPerProposal { get; set; } = 0.10m;

	/// <summary>Absolute dollar cap on per-trade risk, applied alongside <see cref="MaxRiskPctPerProposal"/>.
	/// Effective risk budget is <c>min(MaxRiskPctPerProposal × accountValue, MaxDollarRiskPerProposal)</c>.
	/// Critical for compounding-aggressive strategies: a 30% pct-cap on a $10M paper-gained account
	/// would size single trades to $3M, far past what any human trader would tolerate. The dollar cap
	/// clamps absolute position size as equity grows, so the strategy plays the same size at $1M of
	/// equity as at $1B. Default 0 disables (pct-cap only). Set to a dollar amount you'd be willing
	/// to lose on one trade.</summary>
	[JsonPropertyName("maxDollarRiskPerProposal")] public decimal MaxDollarRiskPerProposal { get; set; } = 0m;

	/// <summary>Minimum FinalScore a candidate must clear to actually open. Default 0 preserves legacy
	/// behavior (any positive-EV trade opens). Raise (e.g. 0.02) to require higher conviction — the
	/// engine sits out marginal days, fewer trades but fewer whipsaw losses.</summary>
	[JsonPropertyName("minScoreToOpen")] public decimal MinScoreToOpen { get; set; } = 0m;

	/// <summary>Weight on the whipsaw-vol penalty for credit structures. When 3-day realized vol >>
	/// 30-day realized vol the regime is whipsawing — both bullish and bearish credit spreads get
	/// crushed by counter-trend reversals. Factor = <c>1 − weight × max(0, hv3/hv30 − 1.5)</c>, applied
	/// to ShortVertical / IronCondor / IronButterfly only. Default 0 disables. Set 0.5–1.0 to penalize
	/// (clamps to 0 in severe whipsaw at weight=1).</summary>
	[JsonPropertyName("whipsawWeight")] public decimal WhipsawWeight { get; set; } = 0m;

	[JsonPropertyName("directionalFitWeight")] public decimal DirectionalFitWeight { get; set; } = 0.5m;

	/// <summary>Shifts the scenario-grid center by <c>bias × biasDriftWeight × sigma</c> when computing
	/// realized EV. Bias is the technical signal in [-1, +1]; positive shifts scenarios UP, negative DOWN.
	/// This lets the scorer's raw EV reflect a directional view instead of only being adjusted post-hoc
	/// by <see cref="DirectionalFitWeight"/> — critical for long-premium structures (LongCall/LongPut)
	/// whose negative raw EV can never be flipped positive by sign-symmetric ApplyFactor. Default 0
	/// disables (legacy behavior); 0.5 shifts by half a sigma at extreme bias; 1.0 by a full sigma.
	/// Combine with a reduced DirectionalFitWeight to avoid double-counting the directional signal.</summary>
	[JsonPropertyName("biasDriftWeight")] public decimal BiasDriftWeight { get; set; } = 0m;

	/// <summary>Look-back window (trading days) for grading the bias signal's recent accuracy. If the
	/// bias direction has disagreed with the underlying's actual N-day move, the live bias gets
	/// dampened proportionally (down to floor of 0.2× of its raw value); if they've agreed it's left
	/// unchanged. The calibration affects BOTH the scenario-grid shift (BiasDriftWeight) and the
	/// BiasAdjust factor at once because it scales <c>bias</c> at the source. Default 0 disables.
	/// Recommended 5 trading days — short enough to be responsive, long enough to be meaningful.</summary>
	[JsonPropertyName("biasCalibrationLookbackDays")] public int BiasCalibrationLookbackDays { get; set; } = 0;
	[JsonPropertyName("profitBandPct")] public decimal ProfitBandPct { get; set; } = 5.0m;
	[JsonPropertyName("ivDefaultPct")] public decimal IvDefaultPct { get; set; } = 40m;
	[JsonPropertyName("strikeSteps")] public Dictionary<string, decimal> StrikeSteps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	[JsonPropertyName("volatilityLookbackDays")] public int VolatilityLookbackDays { get; set; } = 20;
	[JsonPropertyName("volatilityFitWeight")] public decimal VolatilityFitWeight { get; set; } = 0.50m;
	[JsonPropertyName("maxPainWeight")] public decimal MaxPainWeight { get; set; } = 0m;
	[JsonPropertyName("gexWeight")] public decimal GexWeight { get; set; } = 0m;
	[JsonPropertyName("statArbWeight")] public decimal StatArbWeight { get; set; } = 0.30m;

	/// <summary>Weight of the contrarian Fear &amp; Greed regime overlay on the score chain. Factor is
	/// <c>max(0.10, 1 + weight × ((50 − score) / 50) × directionalFit)</c>: extreme fear (score≈0)
	/// boosts bullish structures (fit=+1) and dampens bearish ones; extreme greed inverts. Neutral
	/// directional fits (calendars/diagonals/condors) ignore the signal entirely. Default 0.15 caps the
	/// max factor swing at ±15% — smaller than per-ticker signals because F&amp;G is a market-wide
	/// macro overlay. Set to 0 to disable.</summary>
	[JsonPropertyName("sentimentWeight")] public decimal SentimentWeight { get; set; } = 0.15m;

	/// <summary>Weight of the EM-vs-short-strike credit-trade safety factor. Factor is
	/// <c>max(0.10, 1 + weight × signal)</c> where signal ramps from −1 (≤0.5σ cushion) to +1
	/// (≥1.5σ cushion) centered on 1σ. Only fires on credit trades (skipped for debit, where
	/// the short-strike-as-loss-boundary framing doesn't apply). Default 0.20 caps the swing at
	/// ±20%, large enough to discriminate borderline-tight credit verticals from properly-spaced
	/// ones without overwhelming POP/EV signals. Set to 0 to disable.</summary>
	[JsonPropertyName("expectedMoveCreditWeight")] public decimal ExpectedMoveCreditWeight { get; set; } = 0.20m;

	/// <summary>Weight of the IV-vs-HV regime-alignment factor. Distinct from
	/// <see cref="VolatilityFitWeight"/>: that one is vega-aware and barely fires on near-zero-vega
	/// credit verticals; this one fires on trade-type sign alone (credit favored when IV &gt; HV,
	/// debit favored when IV &lt; HV). Factor: <c>max(0.10, 1 + weight × signal)</c> where signal =
	/// ±clamp(IV/HV − 1, −1, 1). Default 0.15 caps the swing at ±15% — smaller than the EM-credit
	/// factor because regime-alignment is a coarser, less-discriminating signal than strike geometry.
	/// Set to 0 to disable.</summary>
	[JsonPropertyName("ivRealizedPremiumWeight")] public decimal IvRealizedPremiumWeight { get; set; } = 0.15m;

	[JsonPropertyName("liquidity")] public OpenerLiquidityConfig Liquidity { get; set; } = new();

	[JsonPropertyName("events")] public OpenerEventsConfig Events { get; set; } = new();

	[JsonPropertyName("realizedExpectancy")] public OpenerRealizedExpectancyConfig RealizedExpectancy { get; set; } = new();

	/// <summary>Blend weight for the intraday tape signal on top of the daily-close technical bias.
	/// The final <c>bias</c> consumed by the scorer is
	/// <c>(1 − intradayTapeWeight) · macroBias + intradayTapeWeight · intradayBias</c> when an
	/// intraday signal is available; collapses to macroBias when intraday is unavailable (backtest,
	/// pre-open, fetcher outage, insufficient bars). Default 0 disables — behavior is bit-identical
	/// to a config without this field. 0DTE strategies want 0.5–0.8 (intraday dominates the
	/// time-horizon); swing strategies want 0.0–0.2 (macro dominates).</summary>
	[JsonPropertyName("intradayTapeWeight")] public decimal IntradayTapeWeight { get; set; } = 0m;

	/// <summary>Per-component configuration for the intraday tape signal. Ignored when
	/// <see cref="IntradayTapeWeight"/> is 0.</summary>
	[JsonPropertyName("intradayTape")] public OpenerIntradayTapeConfig IntradayTape { get; set; } = new();

	/// <summary>Half-width of the EV scenario grid, in standard deviations. Grid points are placed at
	/// ±sigma and ±sigma/2 around spot. Default 1.0 gives a ±1σ / ±0.5σ grid that better matches
	/// realized moves on high-IV names and doesn't overweight fat tails. Prior behavior (and stress tests)
	/// used 2.0 which pumps long-call EV at the expense of pin/theta structures.</summary>
	[JsonPropertyName("scenarioGridSigma")] public decimal ScenarioGridSigma { get; set; } = 1.0m;

	[JsonPropertyName("structures")] public OpenerStructuresConfig Structures { get; set; } = new();

	public decimal StrikeStepFor(string ticker)
	{
		if (!string.IsNullOrWhiteSpace(ticker) && StrikeSteps.TryGetValue(ticker, out var step) && step > 0m)
			return step;

		throw new KeyNotFoundException($"Missing opener strike step for ticker '{ticker}'.");
	}
}

internal sealed class OpenerStructuresConfig
{
	[JsonPropertyName("longCalendar")] public OpenerCalendarLikeConfig LongCalendar { get; set; } = new();
	[JsonPropertyName("doubleCalendar")] public OpenerDoubleCalendarConfig DoubleCalendar { get; set; } = new();
	[JsonPropertyName("longDiagonal")] public OpenerCalendarLikeConfig LongDiagonal { get; set; } = new();
	[JsonPropertyName("doubleDiagonal")] public OpenerDoubleDiagonalConfig DoubleDiagonal { get; set; } = new();
	[JsonPropertyName("ironButterfly")] public OpenerIronButterflyConfig IronButterfly { get; set; } = new();
	[JsonPropertyName("ironCondor")] public OpenerIronCondorConfig IronCondor { get; set; } = new();
	[JsonPropertyName("shortVertical")] public OpenerShortVerticalConfig ShortVertical { get; set; } = new();
	[JsonPropertyName("longCallPut")] public OpenerLongCallPutConfig LongCallPut { get; set; } = new();
	[JsonPropertyName("longVertical")] public OpenerLongVerticalConfig LongVertical { get; set; } = new();
}

internal sealed class OpenerCalendarLikeConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
}

internal sealed class OpenerDoubleCalendarConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
}

internal sealed class OpenerDoubleDiagonalConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
	[JsonPropertyName("longWingSteps")] public List<int> LongWingSteps { get; set; } = new() { 1 };
}

internal sealed class OpenerIronButterflyConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("wingSteps")] public List<int> WingSteps { get; set; } = new() { 1, 2, 3, 4 };
}

internal sealed class OpenerIronCondorConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("bodyWidthSteps")] public List<int> BodyWidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.35m;
}

internal sealed class OpenerShortVerticalConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2 };
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.30m;
}

internal sealed class OpenerLongCallPutConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 21;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 60;
	[JsonPropertyName("deltaMin")] public decimal DeltaMin { get; set; } = 0.30m;
	[JsonPropertyName("deltaMax")] public decimal DeltaMax { get; set; } = 0.60m;
}

/// <summary>Long (debit) vertical: long leg near/ATM at <c>longDelta∈[longDeltaMin,longDeltaMax]</c>,
/// short leg <c>width</c> strikes further OTM. Defined-risk directional bet with capped upside and
/// capped downside — pays less than a naked long, gives up extreme tail upside, but has explicit
/// max-loss = debit-paid bounds. WidthSteps are in strike-grid increments (so width=2 with $5 step
/// SPX = $10 wide).</summary>
internal sealed class OpenerLongVerticalConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 0;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 0;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4, 8 };
	[JsonPropertyName("longDeltaMin")] public decimal LongDeltaMin { get; set; } = 0.30m;
	[JsonPropertyName("longDeltaMax")] public decimal LongDeltaMax { get; set; } = 0.55m;
}

/// <summary>
/// Three-layer liquidity controls for the opener pipeline. The hard filter rejects candidates outright;
/// the score factor penalizes survivors on a continuous curve; the risk rules surface concerns in the
/// diagnostic regardless of score. Skipping the hard filter (set thresholds to extreme values) leaves
/// the score factor and rules in place.
/// </summary>
internal sealed class OpenerLiquidityConfig
{
	/// <summary>Reject any candidate whose worst leg has open interest strictly less than this. Default
	/// 5 contracts; below that, exits routinely walk multiple levels of the book. Set to 0 to disable
	/// the OI gate.</summary>
	[JsonPropertyName("minOpenInterest")] public long MinOpenInterest { get; set; } = 5;

	/// <summary>Reject any candidate whose worst leg's OI is below this fraction of the maximum OI
	/// among same-expiry near-spot strikes AND whose absolute OI is below
	/// <see cref="MinAbsoluteOpenInterest"/>. Catches "sub-grid" strikes — e.g., the $0.50 strikes on a
	/// chain whose far-dated expiries cluster volume on $1.00 strikes. Default 0.25 = 25%. The
	/// absolute escape hatch prevents the relative gate from over-rejecting decently-liquid strikes on
	/// meme-stock chains where one strike dwarfs all others. Set to 0 to disable the relative gate.</summary>
	[JsonPropertyName("minRelativeOpenInterest")] public decimal MinRelativeOpenInterest { get; set; } = 0.25m;

	/// <summary>Absolute open-interest floor for the relative-OI gate. A leg with OI (or volume) at or
	/// above this value passes the relative-OI check even if its share of nearby-strike liquidity is
	/// below <see cref="MinRelativeOpenInterest"/>. Default 100; 100+ contracts is plenty of liquidity
	/// in absolute terms regardless of how it compares to a max-OI neighbor — meme-stock chains
	/// regularly have one strike with 10k+ OI which makes every other active strike look "relatively
	/// thin" under the 25% relative gate. Set to a very high number to disable the escape hatch.</summary>
	[JsonPropertyName("minAbsoluteOpenInterest")] public long MinAbsoluteOpenInterest { get; set; } = 100;

	/// <summary>Strength of the multiplicative liquidity factor on the score chain. The factor maps
	/// worst-leg spread + min-OI to a value in [0.30, 1.00]. Higher weight = sharper penalty for
	/// borderline-liquidity candidates among those that survived the hard filter. Default 0.50.</summary>
	[JsonPropertyName("weight")] public decimal Weight { get; set; } = 0.50m;
}

/// <summary>Scheduled-catalyst gates: earnings and ex-dividend filtering. Defaults reject short-leg
/// structures whose short-leg expiry spans the next earnings date, and short call legs whose expiry
/// is after the next ex-dividend (early-assignment risk). Set <see cref="Enabled"/> to false to skip
/// all event-driven filtering entirely; the data is still surfaced in the risk diagnostic for
/// transparency.</summary>
internal sealed class OpenerEventsConfig
{
	/// <summary>Master switch. False bypasses both veto rules and the diagnostic rule. Default true.</summary>
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

	/// <summary>Veto any short-leg structure whose target expiry falls within
	/// <c>[asOf, earningsDate + earningsBlackoutDaysAfter]</c>. Long-only structures (long call/put)
	/// are never vetoed — they typically benefit from earnings vol. Default 0 days = veto only when
	/// earnings ≤ expiry. Set to a positive value to also reject expiries the day after earnings
	/// (rarely useful: the position is already closed).</summary>
	[JsonPropertyName("earningsBlackoutDaysAfter")] public int EarningsBlackoutDaysAfter { get; set; } = 0;

	/// <summary>Veto any structure containing a short call leg whose expiry is on or after the next
	/// ex-dividend date. Early-exercise to capture the dividend is rational on ITM short calls; even
	/// near-the-money positions can get assigned over a meaningful div. Default true.</summary>
	[JsonPropertyName("rejectShortCallsThroughExDiv")] public bool RejectShortCallsThroughExDiv { get; set; } = true;

	/// <summary>Path to a JSON override file that supplements (and overrides) Yahoo-sourced events.
	/// Format: <c>{"AAPL":{"earnings":"2026-08-01","earningsTime":"AMC","exDividend":"2026-08-09","dividendAmount":0.24}}</c>.
	/// Useful when Yahoo's calendar lags, for non-US tickers, or for known events Yahoo misses. Relative
	/// paths resolve against the project root. Null disables the override. Default null.</summary>
	[JsonPropertyName("overrideFilePath")] public string? OverrideFilePath { get; set; } = null;
}

/// <summary>Realized-expectancy scoring: replaces the theoretical "hold to expiry, collect max"
/// assumption with a managed-exit model plus round-trip slippage. When enabled, scoring uses the
/// adjusted EV; the theoretical numbers are preserved on the proposal so users can audit the gap.
///
/// Per-scenario realized P&L = clamp(theoretical_pnl, -<see cref="StopLossPctOfMaxLoss"/> × |maxLoss|,
/// +<see cref="ProfitTargetPctOfMaxProfit"/> × maxProfit) − friction. Friction = sum of per-leg
/// half-spread × 100 × <see cref="RoundTrips"/> × <see cref="SlippageMultiplier"/>.
///
/// The clamping is a path-conservative approximation: it credits managed exits only at terminal
/// scenario points, ignoring the optionality of closing intra-life when the path crosses the
/// target. The error is in the safe direction (under-estimates managed-exit value). Defaults match
/// tastytrade-style management: close shorts at 50% of max profit, stop at 50% of max loss
/// (≈ 2× credit on typical 2× wide IC widths), one round trip's worth of half-spread per leg per
/// side.</summary>
internal sealed class OpenerRealizedExpectancyConfig
{
	/// <summary>Master switch. False bypasses the realized adjustment entirely and the scorer runs
	/// on theoretical EV. Default true.</summary>
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

	/// <summary>Profit-target cap as a fraction of theoretical max profit. tastytrade convention for
	/// credit spreads is 0.50 (close at half the max credit); long premium positions often run
	/// further (0.75–1.00). Same value applies to every structure unless you split via a
	/// per-structure override (not implemented). Default 0.50.</summary>
	[JsonPropertyName("profitTargetPctOfMaxProfit")] public decimal ProfitTargetPctOfMaxProfit { get; set; } = 0.50m;

	/// <summary>Stop-loss cap as a fraction of theoretical max loss magnitude. Default 0.50 maps to
	/// "close at half the max loss," which approximates the "2× credit received" rule on typical
	/// short verticals / iron condors with 4× width-to-credit ratios. Tighter stops (0.25–0.33)
	/// reduce realized loss but at the cost of more whipsaw exits — backtest before lowering.</summary>
	[JsonPropertyName("stopLossPctOfMaxLoss")] public decimal StopLossPctOfMaxLoss { get; set; } = 0.50m;

	/// <summary>Dollars-per-share friction charged for each broker order required to enter the
	/// structure (and again to exit, scaled by <see cref="RoundTrips"/>). 0 (default) = assume mid
	/// fills, no friction charged. Set to e.g. <c>0.02</c> to model paying 2¢/share above mid on
	/// each combo fill, which is typical for Webull-style net-price execution. The opener knows
	/// which structures need more than one broker order (only double calendar and double diagonal
	/// require 2; every other supported structure fills as a single combo) so there's no per-
	/// structure knob — the math is structurally correct without one.</summary>
	[JsonPropertyName("slippagePerSharePerOrder")] public decimal SlippagePerSharePerOrder { get; set; } = 0m;

	/// <summary>Number of full bid/ask crossings the friction charge represents. 2 = open + close
	/// (the normal case). Set to 1 if you're already scoring against execution prices that bake in
	/// the entry slippage, leaving only the exit fill. Default 2.</summary>
	[JsonPropertyName("roundTrips")] public int RoundTrips { get; set; } = 2;
}

/// <summary>Per-component configuration for the intraday tape signal derived from minute bars.
/// The overall blend weight lives in <see cref="OpenerConfig.IntradayTapeWeight"/>; this block
/// shapes how the intraday signal itself is computed from the underlying bar series.</summary>
internal sealed class OpenerIntradayTapeConfig
{
	/// <summary>Maps a strategy ticker (e.g. <c>"SPXW"</c>) to the symbol used to fetch intraday bars
	/// (e.g. <c>"SPX"</c>). Required for option roots whose underlying differs from the chart symbol.
	/// When the strategy ticker isn't in the map, it's used as the chart symbol directly. Empty by
	/// default — explicit per-ticker mapping is required.</summary>
	[JsonPropertyName("dataSourceTickers")] public Dictionary<string, string> DataSourceTickers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Per-strategy-ticker fallback symbol for pre-market context. Used when the primary
	/// chart symbol has no extended-hours data (cash indexes like SPX). Empty by default.</summary>
	[JsonPropertyName("preMarketProxyTickers")] public Dictionary<string, string> PreMarketProxyTickers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Bar interval as Webull's chart-endpoint type code (<c>m1</c>, <c>m5</c>, <c>m15</c>,
	/// <c>m30</c>, <c>h1</c>, <c>d1</c>). Default <c>m1</c> for 0DTE-grade responsiveness; coarser
	/// intervals are useful for multi-day-horizon strategies.</summary>
	[JsonPropertyName("barIntervalCode")] public string BarIntervalCode { get; set; } = "m1";

	/// <summary>Lookback in minutes for the bar range request. Must span the prior trading session so
	/// the indicator can derive prev-close from its own bars (keeps the gap component on the same
	/// price scale as today's bars). 7200 = 5 calendar days, which always reaches back to the prior
	/// session even across weekends and short holiday breaks. Below ~4320 (3 days), Monday scans
	/// can't see Friday's close.</summary>
	[JsonPropertyName("lookbackMinutes")] public int LookbackMinutes { get; set; } = 7200;

	/// <summary>Minimum bars on today's session before the intraday signal is allowed to contribute.
	/// Below this threshold the indicator returns null and the bias collapses to macro-only.
	/// Default 5 maps to ~5 minutes after open on m1, where the opening range starts to stabilize.</summary>
	[JsonPropertyName("minBars")] public int MinBars { get; set; } = 5;

	[JsonPropertyName("gapWeight")] public decimal GapWeight { get; set; } = 1.0m;
	[JsonPropertyName("openToNowWeight")] public decimal OpenToNowWeight { get; set; } = 2.0m;
	[JsonPropertyName("vwapDeviationWeight")] public decimal VwapDeviationWeight { get; set; } = 1.0m;

	/// <summary>Include pre/post-market bars when fetching. Cash indexes (SPX, NDX) ignore this and
	/// return RTH only regardless; ETFs and single names honor it. Default false keeps the signal
	/// tightly scoped to the RTH session.</summary>
	[JsonPropertyName("includeExtended")] public bool IncludeExtended { get; set; } = false;
}

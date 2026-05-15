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

	[JsonPropertyName("directionalFitWeight")] public decimal DirectionalFitWeight { get; set; } = 0.5m;
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

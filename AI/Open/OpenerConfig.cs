using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class OpenerConfig
{
	/// <summary>Back-reference to <see cref="AIConfig.Indicators"/>, wired up at load time by
	/// <see cref="AIContext.ResolveConfig"/>. Lets helper methods that only receive <c>OpenerConfig</c>
	/// reach into the shared indicators block (ivDefaultPct, strikeStep, technicalFilter, intradayTape,
	/// events) without threading <see cref="IndicatorsConfig"/> through every signature. Not serialized.</summary>
	[JsonIgnore] public IndicatorsConfig Indicators { get; set; } = new();

	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("topNPerTicker")] public int TopNPerTicker { get; set; } = 5;
	[JsonPropertyName("maxCandidatesPerStructurePerTicker")] public int MaxCandidatesPerStructurePerTicker { get; set; } = 8;

	// Calendar/diagonal enumeration density (per short×long expiry pair). Defaults are effectively
	// exhaustive over the delta band — right for the one-tick live scan. For backtests (per-minute × many
	// days), a lighter setting (e.g. 10 / 5 / 3) is ~3-4× faster and validated near-equivalent; set it in
	// a backtest --config to keep the live scan exhaustive.
	[JsonPropertyName("maxLongAnchors")] public int MaxLongAnchors { get; set; } = 40;
	[JsonPropertyName("maxShortAnchors")] public int MaxShortAnchors { get; set; } = 30;
	/// <summary>Diagonal-only: in addition to the shortDelta-band shorts, emit shorts at 1..N strikes on
	/// either side of the long anchor (covered side OTM-of-long; the reverse side is dropped unless
	/// <c>allowInverted</c>). This SUPPLEMENTS — and is NOT clamped by — the shortDelta band, so it can
	/// (by design) place near-anchor shorts well outside [shortDeltaMin, shortDeltaMax]. It exists because
	/// the coarse delta spanning can never land an adjacent-strike pairing, and the balanced near-ATM
	/// covered diagonals the scorer rates highest need those tight pairings to exist. Consequence: the
	/// shortDelta band is NOT a hard moneyness bound on the short while this is > 0. Set to 0 to enforce a
	/// band-only (true-OTM) short — required for a poor-man's-covered-call, where a deep-ITM long plus a
	/// non-zero value here would otherwise leak near-ATM/near-ITM shorts.</summary>
	[JsonPropertyName("maxTightGapStrikes")] public int MaxTightGapStrikes { get; set; } = 6;
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

	/// <summary>Earliest wall-clock time (ET, "HH:mm") at which an open may fire. Null/empty = no delay
	/// (the 09:30 RTH open). Delaying entry lets the intraday tape form and blend into the bias (via
	/// <see cref="OpenerWeightsConfig.IntradayTape"/>) before the directional read is committed, instead
	/// of trading on the stale overnight macro bias at 09:30 — the dominant long-premium misfire. The
	/// backtest skips minutes before this time and decides at the first qualifying minute at/after it;
	/// the live opener should likewise withhold opens until this time.</summary>
	[JsonPropertyName("earliestEntryTimeEt")] public string? EarliestEntryTimeEt { get; set; } = null;

	/// <summary>Restrict which expiry types are eligible for the SHORT and LONG legs of calendar/diagonal
	/// structures independently. Dailies are Mon–Thu expirations (SPX/SPXW/SPY daily chains). Weeklies are
	/// non-monthly Fridays. Monthlies are the holiday-adjusted 3rd-Friday of each month. All six default to
	/// true. Typical uses: set allowShortDailies=false to restrict short legs to Fridays only (equivalent to
	/// the old weeklyMonthlyExpiriesOnly flag); set allowLongWeeklies=false for underlyings like GME where
	/// far-dated weeklies have no usable bid/ask, limiting the long leg to the liquid monthly cycle.</summary>
	[JsonPropertyName("allowShortDailies")] public bool AllowShortDailies { get; set; } = true;
	[JsonPropertyName("allowShortWeeklies")] public bool AllowShortWeeklies { get; set; } = true;
	[JsonPropertyName("allowShortMonthlies")] public bool AllowShortMonthlies { get; set; } = true;
	[JsonPropertyName("allowLongDailies")] public bool AllowLongDailies { get; set; } = true;
	[JsonPropertyName("allowLongWeeklies")] public bool AllowLongWeeklies { get; set; } = true;
	[JsonPropertyName("allowLongMonthlies")] public bool AllowLongMonthlies { get; set; } = true;

	/// <summary>How the Fear & Greed sentiment overlay reads the score: <c>"contrarian"</c> (default — extreme
	/// fear favors bullish structures, greed favors bearish; the legacy hardcoded behaviour) or <c>"momentum"</c>
	/// (fear favors bearish, greed bullish — trade WITH the crowd). Momentum suits intraday trend/0DTE plays where
	/// a fear regime confirms a down move rather than signalling a contrarian bounce. Anything other than
	/// "momentum" is treated as contrarian.</summary>
	[JsonPropertyName("sentimentMode")] public string SentimentMode { get; set; } = "contrarian";

	/// <summary>Multiplicative-factor weights applied to the candidate score chain. All twelve signals
	/// live here in one sub-block so the user can see the full set of scoring knobs at a glance.</summary>
	[JsonPropertyName("weights")] public OpenerWeightsConfig Weights { get; set; } = new();

	/// <summary>Look-back window (trading days) for grading the bias signal's recent accuracy. If the
	/// bias direction has disagreed with the underlying's actual N-day move, the live bias gets
	/// dampened proportionally (down to floor of 0.2× of its raw value); if they've agreed it's left
	/// unchanged. The calibration affects BOTH the scenario-grid shift (biasDrift) and the BiasAdjust
	/// factor at once because it scales <c>bias</c> at the source. Default 0 disables. Recommended
	/// 5 trading days — short enough to be responsive, long enough to be meaningful.</summary>
	[JsonPropertyName("biasCalibrationLookbackDays")] public int BiasCalibrationLookbackDays { get; set; } = 0;

	[JsonPropertyName("profitBandPct")] public decimal ProfitBandPct { get; set; } = 0.05m;

	[JsonPropertyName("volatilityLookbackDays")] public int VolatilityLookbackDays { get; set; } = 20;

	[JsonPropertyName("liquidity")] public OpenerLiquidityConfig Liquidity { get; set; } = new();

	/// <summary>LIVE-only quote-integrity guards (staleness + torn-NBBO). Never consulted in the backtest,
	/// which prices from a clean historical store. Defaults on. See <see cref="OpenerQuoteGuardConfig"/>.</summary>
	[JsonPropertyName("quoteGuard")] public OpenerQuoteGuardConfig QuoteGuard { get; set; } = new();

	/// <summary>Entry noise gate: the structure's |net entry per share| (at mid) must be at least this
	/// multiple of the summed per-leg half-spreads, else the candidate is rejected before scoring. When a
	/// structure's value is small relative to its legs' quote widths, the mid-priced "debit" is dominated
	/// by quote noise rather than economics — e.g. a deep-ITM put calendar quoted $0.02 at mid-of-mids whose
	/// front leg alone is $2.34 wide marks ±$0.30 overnight on nothing but spread wobble. Legs without a
	/// real two-sided quote contribute zero width (synthetic-priced legs are not penalized). 0 disables.</summary>
	[JsonPropertyName("minEntryToNoiseRatio")] public decimal MinEntryToNoiseRatio { get; set; } = 0.5m;

	/// <summary>Calendar/diagonal short-leg time-value gate: the short leg's extrinsic value (mid −
	/// intrinsic) must be at least this multiple of the leg's own half-spread. The structure's thesis is
	/// harvesting the short leg's time decay; a deep-ITM short with ~zero extrinsic has no theta to sell —
	/// any apparent edge is quote noise. A moneyness cap can't draw this line (a legitimate delta-0.7 ITM
	/// call anchor sits at the same %-distance as a junk deep-ITM put), extrinsic-vs-noise can. 0 disables.</summary>
	[JsonPropertyName("minShortExtrinsicToNoiseRatio")] public decimal MinShortExtrinsicToNoiseRatio { get; set; } = 1.0m;

	/// <summary>Master switch for realized-expectancy scoring: when true the scorer ranks candidates on
	/// managed-exit EV + round-trip friction; when false it falls back to theoretical hold-to-expiry EV.
	/// The exit thresholds and cost model it uses live in their canonical homes (<c>rules.stopLoss</c>,
	/// <c>rules.takeProfit</c>, top-level <c>execution</c>); this flag only toggles the model. Default true.</summary>
	[JsonPropertyName("realizedEvScoring")] public bool RealizedEvScoring { get; set; } = true;

	/// <summary>Runtime-only bundle of the resolved realized-EV inputs the scorer reads (it receives only
	/// <see cref="OpenerConfig"/>). Populated by AIConfigLoader.PopulateRealizedEv at load from the flag
	/// above plus rules.stopLoss/takeProfit and execution — never deserialized directly, hence [JsonIgnore].</summary>
	[JsonIgnore] public OpenerRealizedExpectancyConfig RealizedExpectancy { get; set; } = new();

	/// <summary>Half-width of the EV scenario grid, in standard deviations. Grid points are placed at
	/// ±sigma and ±sigma/2 around spot. Default 1.0 gives a ±1σ / ±0.5σ grid that better matches
	/// realized moves on high-IV names and doesn't overweight fat tails. Prior behavior (and stress tests)
	/// used 2.0 which pumps long-call EV at the expense of pin/theta structures.</summary>
	[JsonPropertyName("scenarioGridSigma")] public decimal ScenarioGridSigma { get; set; } = 1.0m;

	/// <summary>Exponent on the risk/reward component of the scorer's BalanceFactor. 0.0 (default) neutralizes
	/// the R/R term entirely (the premium-ratio cushion penalty remains); 0.5 = sqrt softening; 1.0 = linear;
	/// >1 penalizes sub-1 R/R progressively harder. The exponent's historical job — de-ranking inverted
	/// diagonals — is now done directly by <see cref="OpenerCalendarLikeConfig.AllowInverted"/> (default
	/// false), and the gated covered-only re-sweep (2025-01..2026-06, noise gates on, fills audited) peaks
	/// at 0.0: PF 2.45 / 16.6% DD vs PF 1.19 / 65% DD at 1.0 — with inverted shapes excluded, an R/R
	/// penalty mostly suppresses good covered diagonals and pushes the opener into losing calendars. The
	/// prior 1.0 default came from a pre-gate sweep contaminated by quote-noise phantom trades. Tunable per
	/// ticker/strategy; applied process-wide via CandidateScorer.RrExponent.</summary>
	[JsonPropertyName("balanceRrExponent")] public double BalanceRrExponent { get; set; } = 0.0;

	[JsonPropertyName("structures")] public OpenerStructuresConfig Structures { get; set; } = new();

	/// <summary>DTE-aware shaping of the intraday-tape blend weight. When disabled (default) the blend
	/// uses the flat <see cref="OpenerWeightsConfig.IntradayTape"/> at every DTE — exactly the legacy
	/// behavior. When enabled, the effective intraday weight ramps from <c>weightAt0Dte</c> at same-day
	/// expiry down to <c>weightAtFarDte</c> at <c>farDte</c> and beyond, so a 0DTE trade can lean fully
	/// on the tape while a swing trade stays anchored to the multi-day macro bias. This implements the
	/// "DTE-weighted mix in the consumer" documented on <see cref="IntradayBias"/>.</summary>
	[JsonPropertyName("intradayTapeDteCurve")] public OpenerIntradayTapeDteCurveConfig IntradayTapeDteCurve { get; set; } = new();

	/// <summary>Directional-conviction gate on long-premium structures (long call/put, debit verticals).
	/// Long premium only pays when the underlying actually follows through directionally; on flat/choppy
	/// days it bleeds theta — the dominant loss source in 0DTE backtests (long calls/puts are ~69% of
	/// gross losses). This de-rates long-premium scores when the directional conviction (aligned bias) is
	/// weak, so credit/neutral structures cover the marginal days, while strong-conviction longs keep
	/// full strength. Disabled by default (<c>weight = 0</c>), leaving long scoring bit-identical.</summary>
	[JsonPropertyName("longConvictionGate")] public OpenerLongConvictionGateConfig LongConvictionGate { get; set; } = new();

}

/// <summary>Multiplicative-factor weights applied to the opener candidate score chain. Each field is
/// a dimensionless weight that controls how much one signal contributes to the final score; 0 disables
/// the signal cleanly. Defaults preserve the legacy per-weight defaults from the previous flat layout.</summary>
internal sealed class OpenerWeightsConfig
{
	/// <summary>Strength of the technical-bias adjustment on the per-structure score (the post-hoc tilt
	/// for bullish vs bearish setups). Combine with <see cref="BiasDrift"/> to also shift the scenario grid.</summary>
	[JsonPropertyName("directionalFit")] public decimal DirectionalFit { get; set; } = 0.5m;

	/// <summary>Shifts the scenario-grid center by <c>bias × biasDrift × sigma</c> when computing realized
	/// EV. Critical for long-premium structures whose negative raw EV can never be flipped positive by
	/// the sign-symmetric DirectionalFit factor. 0 disables; 1.0 shifts by a full sigma at extreme bias.</summary>
	[JsonPropertyName("biasDrift")] public decimal BiasDrift { get; set; } = 0m;

	/// <summary>Whipsaw-vol penalty on credit structures (ShortVertical / IronCondor / IronButterfly).
	/// Factor = <c>1 − weight × max(0, hv3/hv30 − 1.5)</c>. 0 disables; 0.5–1.0 penalizes meaningfully.</summary>
	[JsonPropertyName("whipsaw")] public decimal Whipsaw { get; set; } = 0m;

	/// <summary>Strength of the vega-aware HV-vs-IV fit factor. Distinct from <see cref="IvRealizedPremium"/>:
	/// that one fires on trade-type sign alone; this one weighs the structure's vega exposure.</summary>
	[JsonPropertyName("volatilityFit")] public decimal VolatilityFit { get; set; } = 0.50m;

	/// <summary>Net dealer-gamma REGIME tilt (the volatility, not directional, half of the old GEX factor):
	/// <c>max(0.10, 1 + weight × clamp(NetGexFraction × volFitSign, −1, 1))</c>. Negative net gamma (put-gamma
	/// dominant, amplifying regime) favors long-vol/long-premium structures; positive net gamma (suppressive)
	/// favors short-vol. This is purely a structure-shape tilt — the DIRECTIONAL gravity pin that used to be
	/// blended in here now lives entirely in <see cref="GexBiasPull"/> (the magnet). 0 disables.</summary>
	[JsonPropertyName("gammaRegime")] public decimal GammaRegime { get; set; } = 0m;

	/// <summary>GEX gravity as a true MAGNET: shifts the scenario-grid center toward the gravity strike by
	/// <c>weight × clamp((gravity − spot) / expectedMove, −1, 1) × sigma</c>, exactly like <see cref="BiasDrift"/>
	/// does for the technical bias. This is the directional half of dealer-gamma positioning. Unlike a
	/// multiplicative factor — which can re-rank near-zero-base structures but can never flip a
	/// structurally-negative long-premium score positive (the sign-symmetric ApplyFactor caps a boost at "bring
	/// to zero", and beyond that produces a sign-flip artifact) — this moves the expected-price distribution
	/// itself, so a gravity below spot raises a long put's realized EV honestly and picks a sensible strike.
	/// 0 disables (live unchanged).</summary>
	[JsonPropertyName("gexBiasPull")] public decimal GexBiasPull { get; set; } = 0m;

	/// <summary>Max-pain as a true MAGNET — identical channel to <see cref="GexBiasPull"/> but pulling the
	/// scenario-grid center toward the max-pain strike instead of GEX gravity. Replaces the old multiplicative
	/// max-pain factor (removed: it was the same directional pin signal applied the strictly-worse way).
	/// 0 disables (live unchanged).</summary>
	[JsonPropertyName("maxPainBiasPull")] public decimal MaxPainBiasPull { get; set; } = 0m;

	[JsonPropertyName("statArb")] public decimal StatArb { get; set; } = 0.30m;

	/// <summary>Contrarian Fear & Greed regime overlay. Factor is <c>max(0.10, 1 + weight × ((50 − score) / 50) × directionalFit)</c>:
	/// extreme fear boosts bullish structures; extreme greed inverts. Neutral fits (calendars / condors)
	/// ignore the signal entirely. Default 0.15 caps the swing at ±15%.</summary>
	[JsonPropertyName("sentiment")] public decimal Sentiment { get; set; } = 0.15m;

	/// <summary>EM-vs-short-strike credit-trade safety factor. Ramps from −1 (≤0.5σ cushion) to +1
	/// (≥1.5σ cushion). Credit trades only.</summary>
	[JsonPropertyName("expectedMoveCredit")] public decimal ExpectedMoveCredit { get; set; } = 0.20m;

	/// <summary>IV-vs-HV regime-alignment factor. Credit favored when IV > HV; debit favored when
	/// IV < HV. Fires on trade-type sign alone (vega-agnostic).</summary>
	[JsonPropertyName("ivRealizedPremium")] public decimal IvRealizedPremium { get; set; } = 0.15m;

	/// <summary>Blend weight for the VIX term-structure regime signal on top of the daily-close technical
	/// bias. Layered between macroBias and intradayBias. Default 0 disables. Recommended 0.15–0.30.</summary>
	[JsonPropertyName("vixTermStructure")] public decimal VixTermStructure { get; set; } = 0m;

	/// <summary>Blend weight for the intraday tape signal. Final bias is
	/// <c>(1 − intradayTape) · macroBias + intradayTape · intradayBias</c>. 0DTE wants 0.5–0.8;
	/// swing wants 0.0–0.2.</summary>
	[JsonPropertyName("intradayTape")] public decimal IntradayTape { get; set; } = 0m;
}

/// <summary>DTE-aware curve for the intraday-tape blend weight. Linearly interpolates the effective
/// intraday weight from <see cref="WeightAt0Dte"/> at 0 DTE to <see cref="WeightAtFarDte"/> at
/// <see cref="FarDte"/> (and flat beyond). Disabled by default, in which case the consumer uses the
/// flat <see cref="OpenerWeightsConfig.IntradayTape"/> at every DTE (legacy behavior).</summary>
internal sealed class OpenerIntradayTapeDteCurveConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;

	/// <summary>Effective intraday-tape blend weight at same-day (0 DTE) expiry. A 0DTE trade's
	/// directional read should come almost entirely from the live tape, so this defaults to 1.0
	/// (full intraday lean) when the curve is enabled.</summary>
	[JsonPropertyName("weightAt0Dte")] public decimal WeightAt0Dte { get; set; } = 1.0m;

	/// <summary>Effective intraday-tape blend weight at/beyond <see cref="FarDte"/>. A multi-day swing
	/// trade should lean on the macro bias, so this defaults to 0.0 (pure macro at the far end).</summary>
	[JsonPropertyName("weightAtFarDte")] public decimal WeightAtFarDte { get; set; } = 0.0m;

	/// <summary>DTE at/above which the weight is pinned to <see cref="WeightAtFarDte"/>. Between 0 and
	/// this the weight interpolates linearly. Default 21 (≈ one expiry cycle).</summary>
	[JsonPropertyName("farDte")] public int FarDte { get; set; } = 21;

	/// <summary>Effective intraday-tape blend weight for a candidate at <paramref name="dte"/> calendar
	/// days to its target expiry. When the curve is disabled this returns <paramref name="staticWeight"/>
	/// unchanged so the blend is bit-identical to the legacy flat-weight path.</summary>
	public decimal WeightForDte(int dte, decimal staticWeight)
	{
		if (!Enabled) return Math.Clamp(staticWeight, 0m, 1m);
		if (dte <= 0) return Math.Clamp(WeightAt0Dte, 0m, 1m);
		if (FarDte <= 0 || dte >= FarDte) return Math.Clamp(WeightAtFarDte, 0m, 1m);
		var t = (decimal)dte / FarDte;
		var w = WeightAt0Dte + (WeightAtFarDte - WeightAt0Dte) * t;
		return Math.Clamp(w, 0m, 1m);
	}
}

/// <summary>Directional-conviction gate for long-premium structures. The score is multiplied by a
/// factor that is 1.0 when the trade-aligned bias is at or above <see cref="Reference"/> (strong
/// conviction — keep the trade at full strength) and falls to <c>1 − Weight</c> as aligned conviction
/// goes to zero or turns against the trade (weak/contrary signal — de-rate the coin-flip). Disabled
/// when <see cref="Weight"/> = 0 (factor always 1.0).</summary>
internal sealed class OpenerLongConvictionGateConfig
{
	/// <summary>Penalty depth for a zero-conviction long. 0 disables the gate; 0.8 means a long with no
	/// directional edge is scored at 0.2× (and so rarely clears <c>minScoreToOpen</c>). Range [0, 1].</summary>
	[JsonPropertyName("weight")] public decimal Weight { get; set; } = 0m;

	/// <summary>Aligned-bias level at which a long is considered full-conviction (factor = 1.0). Below
	/// this the factor ramps linearly down to <c>1 − Weight</c>. Default 0.35.</summary>
	[JsonPropertyName("reference")] public decimal Reference { get; set; } = 0.35m;

	/// <summary>Multiplicative score factor for a long-premium candidate whose trade-aligned bias is
	/// <paramref name="alignedBias"/> (= bias × directionalFit; positive means the bias points the trade's
	/// way). 1.0 when disabled or at/above full conviction; floored at <c>1 − Weight</c>.</summary>
	public decimal Factor(decimal alignedBias)
	{
		if (Weight <= 0m || Reference <= 0m) return 1m;
		var conviction = Math.Clamp(alignedBias / Reference, 0m, 1m);
		return 1m - Weight * (1m - conviction);
	}
}

/// <summary>Common surface for an opener structure's config so the DTE horizon/band logic has a single
/// source of truth (see <see cref="OpenerStructuresConfig.All"/>). Each structure reports the DTE window(s)
/// it prices into — one range for single-expiry structures, two (short + long) for calendar/diagonal
/// families. Adding a structure means implementing this once, not editing three separate DTE if-chains.</summary>
internal interface IOpenerStructure
{
	bool Enabled { get; }
	IEnumerable<(int Min, int Max)> DteRanges();
}

internal sealed class OpenerStructuresConfig
{
	[JsonPropertyName("longCalendar")] public OpenerCalendarLikeConfig LongCalendar { get; set; } = new();
	[JsonPropertyName("doubleCalendar")] public OpenerDoubleCalendarConfig DoubleCalendar { get; set; } = new();
	[JsonPropertyName("longDiagonal")] public OpenerCalendarLikeConfig LongDiagonal { get; set; } = new();
	[JsonPropertyName("doubleDiagonal")] public OpenerDoubleDiagonalConfig DoubleDiagonal { get; set; } = new();
	[JsonPropertyName("ironButterfly")] public OpenerIronButterflyConfig IronButterfly { get; set; } = new();
	[JsonPropertyName("ironCondor")] public OpenerIronCondorConfig IronCondor { get; set; } = new();
	[JsonPropertyName("condor")] public OpenerCondorConfig Condor { get; set; } = new();
	[JsonPropertyName("shortVertical")] public OpenerShortVerticalConfig ShortVertical { get; set; } = new();
	[JsonPropertyName("longCallPut")] public OpenerLongCallPutConfig LongCallPut { get; set; } = new();
	[JsonPropertyName("longVertical")] public OpenerLongVerticalConfig LongVertical { get; set; } = new();
	[JsonPropertyName("diagonalVertical")] public OpenerDiagonalVerticalConfig DiagonalVertical { get; set; } = new();
	[JsonPropertyName("calendarVertical")] public OpenerCalendarVerticalConfig CalendarVertical { get; set; } = new();

	/// <summary>Every structure config, in one place — the single registry the DTE-range helpers iterate so
	/// none of them carries its own per-structure list. Adding a structure = add its property here (and
	/// implement <see cref="IOpenerStructure"/> on the config).</summary>
	public IEnumerable<IOpenerStructure> All() => new IOpenerStructure[]
	{
		LongCalendar, DoubleCalendar, LongDiagonal, DoubleDiagonal, IronButterfly, IronCondor,
		Condor, ShortVertical, LongCallPut, LongVertical, DiagonalVertical, CalendarVertical
	};

	/// <summary>The DTE windows the enabled structures price into — one range per single-expiry structure,
	/// two (short + long) per calendar/diagonal. Drives both the live bootstrap probe / snapshot sweep and
	/// the backtest quote-store horizon so they can never disagree about which expiries a strategy needs.</summary>
	public IEnumerable<(int Min, int Max)> EnabledDteRanges() => All().Where(s => s.Enabled).SelectMany(s => s.DteRanges());

	/// <summary>Furthest DTE any ENABLED structure can reach — the max expiry horizon the opener will
	/// query. 0 means every enabled structure is same-day (0DTE). Used by the backtest quote store to skip
	/// parsing the longer-dated tail of each expiry file when the whole strategy is 0DTE.</summary>
	public int MaxDteAcrossEnabled() => EnabledDteRanges().Select(r => r.Max).DefaultIfEmpty(0).Max();
}

/// <summary>Diagonal-from-verticals: a near-dated SHORT vertical (credit) + a far-dated LONG vertical
/// (debit) on one side. The long-vertical's long leg sits in <see cref="LongDeltaMin"/>–<see cref="LongDeltaMax"/>
/// (the directional anchor); the short-vertical's short leg sits in <see cref="ShortDeltaMin"/>–<see cref="ShortDeltaMax"/>
/// (further OTM, theta financing). WidthSteps × strike step sizes the near short vertical; the far long
/// vertical's protective wing is pinned to the near vertical's protective wing so the far leg hedges the near
/// vertical's whole loss zone (a diagonal tent, not a zigzag with an unhedged downside cliff). Disabled by default.</summary>
internal sealed class OpenerDiagonalVerticalConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (ShortDteMin, ShortDteMax), (LongDteMin, LongDteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 45;
	[JsonPropertyName("longDeltaMin")] public decimal LongDeltaMin { get; set; } = 0.40m;
	[JsonPropertyName("longDeltaMax")] public decimal LongDeltaMax { get; set; } = 0.55m;
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.20m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.35m;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
}

/// <summary>Calendar-from-verticals: a near-dated SHORT vertical (credit) + a far-dated LONG vertical
/// (debit) on one side that SHARE one anchor strike across both expiries (the calendar leg). The anchor
/// sits in <see cref="DeltaMin"/>–<see cref="DeltaMax"/> (typically near-ATM, where calendar theta/vega is
/// richest); each vertical's wing is <see cref="WidthSteps"/> × strike step further OTM, the same on both
/// expiries. Net = long calendar at the anchor capped by a short calendar at the wing. Disabled by default.</summary>
internal sealed class OpenerCalendarVerticalConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (ShortDteMin, ShortDteMax), (LongDteMin, LongDteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 45;
	[JsonPropertyName("deltaMin")] public decimal DeltaMin { get; set; } = 0.40m;
	[JsonPropertyName("deltaMax")] public decimal DeltaMax { get; set; } = 0.55m;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
}

/// <summary>LongCalendar / LongDiagonal config. Strike placement: when <see cref="DeltaMax"/> > 0 strikes
/// are picked by delta band (the anchor / long-leg lands in <see cref="DeltaMin"/>–<see cref="DeltaMax"/>;
/// for a diagonal the near short leg is drawn from <see cref="ShortDeltaMin"/>–<see cref="ShortDeltaMax"/>,
/// further OTM — mirrors DiagonalVertical / CalendarVertical). NOTE: the short band is not the sole source —
/// <see cref="OpenerConfig.MaxTightGapStrikes"/> also emits shorts adjacent to the long anchor that bypass this
/// band (so with it > 0 the band is not a hard moneyness bound on the short; set it to 0 for a band-only short).
/// When DeltaMax is 0 (default) it falls back to the legacy ATM-centered strike grid (diagonal long leg one
/// strike off the short), preserving prior behavior.</summary>
internal sealed class OpenerCalendarLikeConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (ShortDteMin, ShortDteMax), (LongDteMin, LongDteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	// Delta-band strike placement (0 = legacy ATM grid). Anchor = calendar's shared strike / diagonal's long leg.
	[JsonPropertyName("deltaMin")] public decimal DeltaMin { get; set; } = 0m;
	[JsonPropertyName("deltaMax")] public decimal DeltaMax { get; set; } = 0m;
	// Diagonal-only: the near short leg's delta band (further OTM than the long anchor). Defaults span if unset.
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.20m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.35m;

	/// <summary>Diagonal-only: permit inverted-strike (reverse) diagonals — short ITM-of-long, i.e. the
	/// strike gap sits on the loss side. Their max loss is priced (debit + gap) and they size accordingly,
	/// but the ITM American short carries early-assignment risk the backtest does not model ("Assignment /
	/// early exercise not modeled"), so backtested reverse-diagonal P&amp;L is structurally optimistic.
	/// Default false = covered-only (the scorer rejects any diagonal with a positive strike-side loss):
	/// the covered-only re-sweep beat inverted-allowed at every R/R exponent except 1.0, so exclusion
	/// costs no edge while removing the unmodeled risk. If re-enabled, pair with an assignment-aware
	/// simulator before trusting backtest results. Couples with <see cref="OpenerConfig.BalanceRrExponent"/>:
	/// covered-only with exponent 1.0 was the worst cell of the grid (PF 1.19 / 65% DD) — keep 0.0.</summary>
	[JsonPropertyName("allowInverted")] public bool AllowInverted { get; set; } = false;
}

internal sealed class OpenerDoubleCalendarConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (ShortDteMin, ShortDteMax), (LongDteMin, LongDteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
}

internal sealed class OpenerDoubleDiagonalConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (ShortDteMin, ShortDteMax), (LongDteMin, LongDteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
	[JsonPropertyName("longWingSteps")] public List<int> LongWingSteps { get; set; } = new() { 1 };
}

internal sealed class OpenerIronButterflyConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (DteMin, DteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("wingSteps")] public List<int> WingSteps { get; set; } = new() { 1, 2, 3, 4 };
}

internal sealed class OpenerIronCondorConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (DteMin, DteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("bodyWidthSteps")] public List<int> BodyWidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.35m;
}

/// <summary>Single-sided (all-put OR all-call) LONG condor: buy the two outer-wing strikes, sell the two
/// inner body strikes — a net DEBIT with the same neutral, two-break-even payoff as an iron condor but
/// financed as a debit rather than a credit. Strike selection is identical to the iron condor (two body
/// shorts bracketing spot in the <c>shortDelta</c> band; long wings <c>widthSteps</c> listed strikes
/// further out); the difference is only that all four legs are one option type. Because a bracketing
/// same-type condor always has two ITM legs (a put condor's upper strikes, a call condor's lower), fills
/// are wider and the short ITM leg carries early-assignment risk on American-style equities — the iron
/// condor is usually the better execution for the same view. <see cref="Side"/> selects the leg type:
/// <c>put</c>, <c>call</c>, or <c>both</c> (enumerate each and let scoring pick the better-priced side).</summary>
internal sealed class OpenerCondorConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (DteMin, DteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("bodyWidthSteps")] public List<int> BodyWidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.35m;
	[JsonPropertyName("side")] public string Side { get; set; } = "both";
}

internal sealed class OpenerShortVerticalConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (DteMin, DteMax) };
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2 };
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.30m;
}

internal sealed class OpenerLongCallPutConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (DteMin, DteMax) };
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
internal sealed class OpenerLongVerticalConfig : IOpenerStructure
{
	public IEnumerable<(int Min, int Max)> DteRanges() => new[] { (DteMin, DteMax) };
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

/// <summary>LIVE-only quote-integrity guards. A logged-off/degraded vendor feed can return quotes that are
/// stale (delayed by minutes) or torn (a bid and ask stitched from different moments, e.g. bid 10.36 / ask
/// 20.36). Either corrupts the mid the opener prices and the <c>--limit</c> it proposes. These guards detect
/// both and surface a loud warning; the backtest (clean historical NBBO) never runs them.</summary>
internal sealed class OpenerQuoteGuardConfig
{
	/// <summary>Master switch. Default true. False disables both the staleness and torn-NBBO checks.</summary>
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

	/// <summary>Feed is flagged stale when even the FRESHEST two-sided quote in the fetched book is older than
	/// this many seconds vs wall-clock now — the signature of a lagging/logged-off session. Uses the freshest
	/// (not per-strike) age so an individually quiet strike doesn't false-trip. Only vendors that stamp a quote
	/// time (Schwab quoteTimeInLong) can be assessed; a vendor with no timestamp is reported as "unverifiable".
	/// Default 120s. 0 disables the staleness check.</summary>
	[JsonPropertyName("maxQuoteAgeSeconds")] public int MaxQuoteAgeSeconds { get; set; } = 120;

	/// <summary>A leg's two-sided quote is "torn" when its spread exceeds BOTH this fraction of the mid AND
	/// <see cref="MinAbsSpreadDollars"/> — the AND keeps genuinely cheap options (wide in % but pennies in
	/// absolute terms) from tripping it. A crossed quote (bid ≥ ask) is always torn regardless. Default 0.50
	/// (50% of mid). 0 disables the wide-spread check (crossed is still caught).</summary>
	[JsonPropertyName("maxSpreadPctOfMid")] public decimal MaxSpreadPctOfMid { get; set; } = 0.50m;

	/// <summary>Absolute spread floor (dollars/share) for the torn-NBBO check; a leg trips only when its
	/// spread clears this AND <see cref="MaxSpreadPctOfMid"/>. Default $1.00.</summary>
	[JsonPropertyName("minAbsSpreadDollars")] public decimal MinAbsSpreadDollars { get; set; } = 1.00m;
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

	/// <summary>Per-ticker annual dividend yield (e.g. 0.012 = 1.2%/yr) used to size a per-payment cash
	/// dividend for the Black-Scholes forward adjustment ONLY when a Yahoo/override ex-dividend date is
	/// known but its cash amount is not. Amount ≈ <c>spot × dividendYield / dividendFrequency</c>. 0
	/// disables the fallback (the leg prices with no dividend). Default 0.</summary>
	[JsonPropertyName("dividendYield")] public decimal DividendYield { get; set; } = 0m;

	/// <summary>Dividend payments per year, used with <see cref="DividendYield"/> to derive the
	/// per-payment cash amount. Default 4 (quarterly), the norm for liquid US optionable payers.</summary>
	[JsonPropertyName("dividendFrequency")] public int DividendFrequency { get; set; } = 4;
}

/// <summary>Realized-expectancy scoring: replaces the theoretical "hold to expiry, collect max"
/// assumption with a managed-exit model plus round-trip slippage. When enabled, scoring uses the
/// adjusted EV; the theoretical numbers are preserved on the proposal so users can audit the gap.
///
/// Per-scenario realized P&L = max(theoretical_pnl, -<see cref="StopLossPctOfMaxLoss"/> × |maxLoss|)
/// − friction. Friction = sum of per-leg (half-spread + <see cref="SlippagePerSharePerOrder"/>) × 100 ×
/// <see cref="RoundTrips"/>. Only the downside is clamped — winners ride to their theoretical max (the
/// profit-target cap was removed with Target B).
///
/// The flooring is a path-conservative approximation: it credits the managed stop only at terminal
/// scenario points, ignoring the optionality of stopping intra-life when the path crosses the floor. The
/// error is in the safe direction. Default stops at 50% of max loss (≈ 2× credit on typical 2× wide IC
/// widths), one round trip's worth of half-spread per leg per side.</summary>
/// <summary>The realized-EV parameters the candidate scorer prices against. The scorer receives only
/// <see cref="OpenerConfig"/>, so this is the load-time aggregation point: <see cref="Enabled"/> is the
/// only field read from JSON (the EV master switch); the exit thresholds and cost model are sourced from
/// their real homes — <c>rules.stopLoss.pctOfMaxLoss</c> and the top-level <c>execution</c> block — and
/// copied in by AIConfigLoader.PopulateRealizedEv at load. Marked
/// [JsonIgnore] here so they live in one canonical JSON home (not duplicated in the opener block).</summary>
internal sealed class OpenerRealizedExpectancyConfig
{
	/// <summary>Master switch. False bypasses the realized adjustment entirely and the scorer runs
	/// on theoretical EV. Populated from opener.realizedEvScoring at load. Default true.</summary>
	[JsonIgnore] public bool Enabled { get; set; } = true;

	/// <summary>Stop-loss as a fraction of theoretical max loss. Populated from rules.stopLoss.pctOfMaxLoss.</summary>
	[JsonIgnore] public decimal StopLossPctOfMaxLoss { get; set; } = 0.50m;

	/// <summary>Per-share friction per broker order (×RoundTrips for exit). Populated from execution.slippagePerSharePerOrder.</summary>
	[JsonIgnore] public decimal SlippagePerSharePerOrder { get; set; } = 0m;

	/// <summary>Bid/ask crossings the friction represents (2 = open+close). Populated from execution.roundTrips.</summary>
	[JsonIgnore] public int RoundTrips { get; set; } = 2;
}

/// <summary>Per-component configuration for the intraday tape signal derived from minute bars.
/// The overall blend weight lives in <see cref="OpenerConfig.IntradayTapeWeight"/>; this block
/// shapes how the intraday signal itself is computed from the underlying bar series.</summary>
internal sealed class OpenerIntradayTapeConfig
{
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

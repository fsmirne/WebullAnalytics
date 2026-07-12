namespace WebullAnalytics.AI;

/// <summary>
/// Directional-regime helpers shared by the live opener, the <c>wa analyze regime</c> command, and the
/// risk-diagnostic panel. Two responsibilities:
/// <list type="bullet">
/// <item>BlendBias — the single source of truth for how the scoring <c>bias</c> is assembled from the
/// daily technical bias, the VIX term-structure signal, and the intraday tape, then directional-agreement
/// calibrated. This was previously an inline closure inside <see cref="Open.OpenCandidateEvaluator"/>
/// (BiasForDte); extracting it keeps every consumer in lockstep and lets it be unit-tested.</item>
/// <item>Classify — maps a blended-bias scalar to a human-readable direction × strength label. This is a
/// display heuristic only: the authoritative "which structure does the regime favor" verdict comes from
/// actually scoring the candidates, not from these thresholds.</item>
/// </list>
/// </summary>
internal static class RegimeAnalyzer
{
	/// <summary>
	/// Assembles the per-candidate scoring bias from its components, exactly as the live opener does.
	/// Order of operations (must stay bit-identical to the opener so backtests don't shift):
	/// <list type="number">
	/// <item>biasedMacro = (1−w)·macroBias + w·vixTerm, with w = clamp(vixWeight, 0, 1) — skipped when no
	/// VIX score or vixWeight == 0.</item>
	/// <item>b = (1−w)·biasedMacro + w·intraday, with w = the DTE-aware curve weight — skipped when no
	/// intraday score or intradayWeight == 0.</item>
	/// <item>Directional-agreement calibration: if the bias direction disagreed with the recent N-day move,
	/// scale down to a 0.2× floor; on agreement leave at full strength. Disabled when calibLookbackDays == 0
	/// or moveSign == 0.</item>
	/// </list>
	/// </summary>
	/// <param name="macroBias">The daily-bar technical bias (SMA/RSI/momentum composite), in [−1, +1].</param>
	/// <param name="vixTermScore">VIX term-structure signal, or null when unavailable / weight 0.</param>
	/// <param name="vixWeight">opener.weights.vixTermStructure.</param>
	/// <param name="intradayScore">Intraday-tape directional score, or null when unavailable / weight 0.</param>
	/// <param name="intradayWeight">opener.weights.intradayTape (the static weight the DTE curve interpolates from).</param>
	/// <param name="dteCalendar">Calendar days to the candidate's target expiry — drives the intraday curve weight.</param>
	/// <param name="dteCurve">The intraday-tape DTE curve (collapses to a flat <paramref name="intradayWeight"/> when disabled).</param>
	/// <param name="calibLookbackDays">opener.biasCalibrationLookbackDays (0 disables calibration).</param>
	/// <param name="moveSign">Sign of the underlying's move over the calibration lookback (+1 / −1 / 0).</param>
	public static decimal BlendBias(
		decimal macroBias,
		decimal? vixTermScore,
		decimal vixWeight,
		decimal? intradayScore,
		decimal intradayWeight,
		int dteCalendar,
		OpenerIntradayTapeDteCurveConfig dteCurve,
		int calibLookbackDays,
		decimal moveSign)
	{
		var biasedMacro = macroBias;
		if (vixTermScore.HasValue && vixWeight > 0m)
		{
			var wVix = Math.Clamp(vixWeight, 0m, 1m);
			biasedMacro = (1m - wVix) * macroBias + wVix * vixTermScore.Value;
		}

		var b = biasedMacro;
		if (intradayScore.HasValue && intradayWeight > 0m)
		{
			var w = dteCurve.WeightForDte(dteCalendar, intradayWeight);
			b = (1m - w) * biasedMacro + w * intradayScore.Value;
		}

		if (calibLookbackDays > 0 && b != 0m && moveSign != 0m)
		{
			var biasSign = b > 0m ? 1m : -1m;
			var agreement = biasSign * moveSign;
			var reliability = Math.Clamp(0.5m + 0.5m * agreement, 0.2m, 1.0m);
			b *= reliability;
		}
		return b;
	}

	/// <summary>Convenience overload: blends pre-computed RegimeComponents at a given calendar DTE. Identical
	/// to the six-scalar overload — exists so callers that already hold a RegimeComponents value don't need
	/// to unpack it manually. OpenerConfig supplies the weights and curve parameters.</summary>
	public static decimal BlendBias(RegimeComponents components, OpenerConfig cfg, int dteCalendar)
		=> BlendBias(components.MacroBias, components.VixTermScore, cfg.Weights.VixTermStructure, components.Intraday?.Score, cfg.Weights.IntradayTape, dteCalendar, cfg.IntradayTapeDteCurve, cfg.BiasCalibrationLookbackDays, components.MoveSign);

	/// <summary>The biasedMacro intermediate (daily bias blended with VIX term structure), exposed so the
	/// analyze-regime breakdown can show the macro-after-VIX value separately from the intraday-blended final.</summary>
	public static decimal BlendMacro(decimal macroBias, decimal? vixTermScore, decimal vixWeight)
	{
		if (!vixTermScore.HasValue || vixWeight <= 0m) return macroBias;
		var wVix = Math.Clamp(vixWeight, 0m, 1m);
		return (1m - wVix) * macroBias + wVix * vixTermScore.Value;
	}

	/// <summary>The raw inputs the opener fed into <see cref="BlendBias"/> for a ticker on a given scan,
	/// captured so <c>wa analyze regime</c> can re-blend at any DTE and show the decomposition. MacroBias is
	/// the daily technical composite (its sub-scores live on the ticker's <see cref="TechnicalBias"/>).</summary>
	public readonly record struct RegimeComponents(
		decimal MacroBias,
		decimal? VixTermScore,
		IntradayBias? Intraday,
		decimal MoveSign);

	public enum RegimeDirection { Bearish, Neutral, Bullish }

	/// <summary>Qualitative magnitude of the blended bias. These are display buckets on |bias|; the actual
	/// debit-vs-credit flip is relative to the day's option pricing and is determined by scoring, not by
	/// these thresholds.</summary>
	public enum RegimeStrength { Flat, Mild, Moderate, Strong }

	/// <param name="Direction">Bullish / Bearish / Neutral from the sign of the blended bias.</param>
	/// <param name="Strength">Qualitative magnitude bucket.</param>
	/// <param name="Headline">One-line plain-English summary of the regime.</param>
	/// <param name="FavoredSide">"call-side" / "put-side" / "neither side" — which option side the trend favors.</param>
	public readonly record struct RegimeClassification(
		RegimeDirection Direction,
		RegimeStrength Strength,
		string Headline,
		string FavoredSide);

	// Display thresholds on |bias|. Heuristic only.
	private const decimal NeutralBand = 0.05m;
	private const decimal MildCeil = 0.20m;
	private const decimal ModerateCeil = 0.45m;

	public static RegimeClassification Classify(decimal bias)
	{
		var mag = Math.Abs(bias);
		var direction = bias > NeutralBand ? RegimeDirection.Bullish
			: bias < -NeutralBand ? RegimeDirection.Bearish
			: RegimeDirection.Neutral;

		var strength = mag < NeutralBand ? RegimeStrength.Flat
			: mag < MildCeil ? RegimeStrength.Mild
			: mag < ModerateCeil ? RegimeStrength.Moderate
			: RegimeStrength.Strong;

		var favoredSide = direction switch
		{
			RegimeDirection.Bullish => "call-side",
			RegimeDirection.Bearish => "put-side",
			_ => "neither side",
		};

		var headline = direction switch
		{
			RegimeDirection.Neutral => "Flat / rangebound — no directional trend in the bias.",
			RegimeDirection.Bullish => strength switch
			{
				RegimeStrength.Strong => "Strong uptrend — high conviction long the call side.",
				RegimeStrength.Moderate => "Uptrend — moderate bullish conviction.",
				_ => "Mild bullish drift — weak upward tilt.",
			},
			_ => strength switch
			{
				RegimeStrength.Strong => "Strong downtrend — high conviction long the put side.",
				RegimeStrength.Moderate => "Downtrend — moderate bearish conviction.",
				_ => "Mild bearish drift — weak downward tilt.",
			},
		};

		return new RegimeClassification(direction, strength, headline, favoredSide);
	}

	public static string DirectionWord(RegimeDirection d) => d switch
	{
		RegimeDirection.Bullish => "bullish",
		RegimeDirection.Bearish => "bearish",
		_ => "neutral",
	};

	public static string StrengthWord(RegimeStrength s) => s switch
	{
		RegimeStrength.Strong => "strong",
		RegimeStrength.Moderate => "moderate",
		RegimeStrength.Mild => "mild",
		_ => "flat",
	};
}

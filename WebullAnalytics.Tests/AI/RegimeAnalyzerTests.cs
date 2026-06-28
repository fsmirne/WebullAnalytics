using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class RegimeAnalyzerTests
{
	private static OpenerIntradayTapeDteCurveConfig FlatCurve() => new() { Enabled = false };
	private static OpenerIntradayTapeDteCurveConfig SlopedCurve() => new() { Enabled = true, WeightAt0Dte = 1.0m, WeightAtFarDte = 0.0m, FarDte = 21 };

	[Fact]
	public void BlendBiasNoOverlaysReturnsMacro()
	{
		// VIX weight 0 + no intraday + no calibration → bias is exactly the daily macro.
		var b = RegimeAnalyzer.BlendBias(0.30m, vixTermScore: -0.50m, vixWeight: 0m, intradayScore: null, intradayWeight: 0m, dteCalendar: 0, dteCurve: FlatCurve(), calibLookbackDays: 0, moveSign: 0m);
		Assert.Equal(0.30m, b);
	}

	[Fact]
	public void BlendBiasAppliesVixThenIntradayThenCalibration()
	{
		// Reproduce the opener's exact arithmetic and assert BlendBias matches.
		var macro = 0.40m;
		var vix = -0.20m; var vixW = 0.25m;
		var tape = 0.80m; var tapeW = 0.45m;
		var curve = FlatCurve(); // disabled → WeightForDte == staticWeight
		var calibLookback = 5; var moveSign = 1m;

		var biasedMacro = (1m - vixW) * macro + vixW * vix;            // 0.75*0.40 + 0.25*(-0.20) = 0.25
		var w = curve.WeightForDte(0, tapeW);                          // 0.45
		var blended = (1m - w) * biasedMacro + w * tape;              // 0.55*0.25 + 0.45*0.80 = 0.4975
		var biasSign = blended > 0m ? 1m : -1m;
		var reliability = System.Math.Clamp(0.5m + 0.5m * (biasSign * moveSign), 0.2m, 1.0m); // agrees → 1.0
		var expected = blended * reliability;

		var actual = RegimeAnalyzer.BlendBias(macro, vix, vixW, tape, tapeW, 0, curve, calibLookback, moveSign);
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void BlendBiasCalibrationDampensOnDisagreement()
	{
		// Bullish blended bias but the recent move was down → reliability floor 0.2 → bias × 0.2.
		var actual = RegimeAnalyzer.BlendBias(0.50m, null, 0m, null, 0m, 0, FlatCurve(), calibLookbackDays: 5, moveSign: -1m);
		Assert.Equal(0.50m * 0.2m, actual);
	}

	[Fact]
	public void BlendBiasDteCurveWeightsTapeMoreAtZeroDte()
	{
		var curve = SlopedCurve(); // 1.0 at 0DTE, 0.0 at/after 21DTE
		// At 0DTE the tape fully dominates; at farDte the tape is ignored (pure macro).
		var atZero = RegimeAnalyzer.BlendBias(0.10m, null, 0m, 0.90m, 0.45m, dteCalendar: 0, dteCurve: curve, calibLookbackDays: 0, moveSign: 0m);
		var atFar = RegimeAnalyzer.BlendBias(0.10m, null, 0m, 0.90m, 0.45m, dteCalendar: 21, dteCurve: curve, calibLookbackDays: 0, moveSign: 0m);
		Assert.Equal(0.90m, atZero);
		Assert.Equal(0.10m, atFar);
	}

	[Fact]
	public void BlendBiasSkipsCalibrationWhenBiasIsZero()
	{
		// b == 0 → calibration is a no-op (no divide/sign games), returns 0.
		var actual = RegimeAnalyzer.BlendBias(0m, null, 0m, null, 0m, 0, FlatCurve(), calibLookbackDays: 5, moveSign: -1m);
		Assert.Equal(0m, actual);
	}

	[Fact]
	public void BlendMacroMatchesTheVixBlendStep()
	{
		Assert.Equal(0.25m, RegimeAnalyzer.BlendMacro(0.40m, -0.20m, 0.25m));
		Assert.Equal(0.40m, RegimeAnalyzer.BlendMacro(0.40m, -0.20m, 0m));     // weight 0 → macro unchanged
		Assert.Equal(0.40m, RegimeAnalyzer.BlendMacro(0.40m, null, 0.25m));    // no VIX score → macro unchanged
	}

	[Theory]
	[InlineData(0.60, "Bullish", "Strong", "call-side")]
	[InlineData(0.30, "Bullish", "Moderate", "call-side")]
	[InlineData(0.10, "Bullish", "Mild", "call-side")]
	[InlineData(0.00, "Neutral", "Flat", "neither side")]
	[InlineData(-0.10, "Bearish", "Mild", "put-side")]
	[InlineData(-0.60, "Bearish", "Strong", "put-side")]
	public void ClassifyBucketsDirectionAndStrength(double bias, string dir, string strength, string side)
	{
		var c = RegimeAnalyzer.Classify((decimal)bias);
		Assert.Equal(dir, c.Direction.ToString());
		Assert.Equal(strength, c.Strength.ToString());
		Assert.Equal(side, c.FavoredSide);
	}

	[Fact]
	public void ClassifyNeutralBandIsExclusiveAtBoundary()
	{
		// |bias| == 0.05 sits on the neutral band edge: direction stays Neutral (strict > test).
		Assert.Equal(RegimeAnalyzer.RegimeDirection.Neutral, RegimeAnalyzer.Classify(0.05m).Direction);
		Assert.Equal(RegimeAnalyzer.RegimeDirection.Bullish, RegimeAnalyzer.Classify(0.06m).Direction);
	}
}

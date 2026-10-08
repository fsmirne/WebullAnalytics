using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>Locks the `analyze gex --view squeeze` scoring: factor points, the n/a-drops-out normalization, side
/// selection, and the capture-intersection rule behind the volume pace.</summary>
public class GexSqueezeScreenerTests
{
	private static readonly DateTime Expiry = new(2026, 10, 9);

	// The reference screenshot's terrain: spot $7,801.15 above a $7,715.72 flip (long gamma), call wall $7,900 (+1.27%).
	private static SqueezeTerrain LongGammaTerrain(decimal? dailyMove = 74m) => new(7801.15m, 7715.72m, ShortGamma: false, CallWall: 7900m, PutWall: 7600m, dailyMove);

	private static int Points(SqueezeReading r, string name) => r.Factors.Single(f => f.Name == name).Points!.Value;

	[Fact]
	public void LongGamma_ScoresZeroRegime_AndNeutralFlowScoresTen()
	{
		var r = GexSqueezeScreener.Evaluate(LongGammaTerrain(), new SqueezeInputs(FlowShare: 0.05m, Volume: null, DeltaOiShare: 0.2m, PriorOiDate: new DateTime(2026, 10, 7)));
		Assert.Equal(SqueezeSide.Bullish, r.Side);
		Assert.Equal(0, Points(r, "Gamma Regime"));
		Assert.Equal(10, Points(r, "Flow Alignment"));
		Assert.Equal(5, Points(r, "Delta OI Alignment"));
	}

	[Fact]
	public void WallProximity_IsLinearInDailyMoves()
	{
		// 98.85 points away at a 74-point daily move = 1.34 moves → 25 × (3 − 1.34) / 2.5 = 16.6 → 17.
		var r = GexSqueezeScreener.Evaluate(LongGammaTerrain(), new SqueezeInputs(null, null, null, null));
		Assert.Equal(17, Points(r, "Call Wall Proximity"));
	}

	[Fact]
	public void WallBehindSpot_ScoresZero()
	{
		var t = LongGammaTerrain() with { CallWall = 7800m, PutWall = 7000m };
		var r = GexSqueezeScreener.Evaluate(t, new SqueezeInputs(FlowShare: 0.5m, null, null, null));
		var wall = r.Factors.Single(f => f.Name == "Call Wall Proximity");
		Assert.Equal(0, wall.Points);
		Assert.Equal(SetupMark.Fail, wall.Mark);
	}

	[Fact]
	public void UnavailableFactors_DropOutOfTheDenominator()
	{
		// Only regime (25/25) and wall (17/25) have data → 42/50 = 84, not 42/100.
		var t = LongGammaTerrain() with { ShortGamma = true };
		var r = GexSqueezeScreener.Evaluate(t, new SqueezeInputs(null, null, null, null));
		Assert.Equal(84, r.Score);
		Assert.Equal("Imminent", r.Band);
	}

	[Fact]
	public void BearishFlowAndPutBuild_PickTheBearishSide()
	{
		var t = LongGammaTerrain() with { PutWall = 7750m };
		var r = GexSqueezeScreener.Evaluate(t, new SqueezeInputs(FlowShare: -0.4m, null, DeltaOiShare: -0.1m, PriorOiDate: new DateTime(2026, 10, 7)));
		Assert.Equal(SqueezeSide.Bearish, r.Side);
		Assert.Equal(25, Points(r, "Flow Alignment"));
		Assert.Equal(7750m, r.Wall);
	}

	[Theory]
	[InlineData(29, "Unlikely")]
	[InlineData(30, "Possible")]
	[InlineData(55, "Likely")]
	[InlineData(75, "Imminent")]
	public void Bands(int score, string band) => Assert.Equal(band, GexSqueezeScreener.Band(score));

	[Fact]
	public void VolumePace_ComparesRecentRateToSessionRate_OverSharedContracts()
	{
		var open = new TimeSpan(9, 30, 0);
		var captures = new SortedDictionary<TimeSpan, Dictionary<string, long>>
		{
			[new TimeSpan(10, 30, 0)] = new() { ["A"] = 600, ["B"] = 400 },
			[new TimeSpan(10, 50, 0)] = new() { ["A"] = 700 },
			// Anchor adds contract C (a wider window) which must not count as recent flow.
			[new TimeSpan(11, 0, 0)] = new() { ["A"] = 900, ["B"] = 500, ["C"] = 5000 },
		};
		var pace = GexSqueezeScreener.VolumePaceFrom(captures, open);
		Assert.NotNull(pace);
		// Reference = 10:30 (latest ≥15 min before 11:00; 10:50 is too close). Shared {A,B}: 1000 → 1400 over 30 min
		// = 13.33/min recent vs 1400 / 90 min = 15.56/min session → 0.857x.
		Assert.Equal(new TimeSpan(10, 30, 0), pace!.ReferenceTs);
		Assert.Equal(0.857m, Math.Round(pace.Ratio, 3));
	}

	[Fact]
	public void VolumePace_NullWithoutAQualifyingReference()
	{
		var captures = new SortedDictionary<TimeSpan, Dictionary<string, long>>
		{
			[new TimeSpan(10, 50, 0)] = new() { ["A"] = 700 },
			[new TimeSpan(11, 0, 0)] = new() { ["A"] = 900 },
		};
		Assert.Null(GexSqueezeScreener.VolumePaceFrom(captures, new TimeSpan(9, 30, 0)));
	}

	[Fact]
	public void FlowShare_IsDeltaWeighted()
	{
		// Equal raw volume, but the call is ATM (|Δ|≈0.5) and the put far OTM (|Δ| small) → strongly call-leaning.
		var contributors = new[]
		{
			new GexContributor(Expiry, 7800m, 1.0 / 365, 0.15m, 1000, IsCall: true, Volume: 1000),
			new GexContributor(Expiry, 7500m, 1.0 / 365, 0.15m, 1000, IsCall: false, Volume: 1000),
		};
		var share = GexSqueezeScreener.FlowShare(contributors, 7800m);
		Assert.True(share > 0.9m, $"expected a call-dominated share, got {share}");
	}
}

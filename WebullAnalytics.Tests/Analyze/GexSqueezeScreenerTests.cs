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
	public void UnavailableFactors_DropOutOfTheDenominator_AndCapTheBand()
	{
		// Flip 1.34 moves above spot → regime 25/25; wall 17/25. Only those two have data → 42/50 = 84, not 42/100,
		// and with three factors missing the 84 cannot read "Imminent".
		var t = LongGammaTerrain() with { Trigger = 7900m, ShortGamma = true };
		var r = GexSqueezeScreener.Evaluate(t, new SqueezeInputs(null, null, null, null));
		Assert.Equal(84, r.Score);
		Assert.Equal((42, 50, 2), (r.Points, r.Possible, r.FactorsScored));
		Assert.False(r.Complete);
		Assert.Equal("Likely", r.Band);
	}

	[Theory]
	[InlineData(0, 13)]        // at the flip: half credit
	[InlineData(37, 25)]       // half a daily move (74) below: full
	[InlineData(-18.5, 6)]     // a quarter move above: 25 × 0.25 = 6.25 → 6
	[InlineData(-37, 0)]       // half a move above: none
	[InlineData(2.5, 13)]      // the 2026-10-08 09:31 shape: $2.50 below the flip (0.03 moves) no longer earns the full 25
	public void GammaRegime_RampsAcrossTheFlip(double flipMinusSpot, int expected)
	{
		var t = LongGammaTerrain() with { Trigger = 7801.15m + (decimal)flipMinusSpot };
		var r = GexSqueezeScreener.Evaluate(t, new SqueezeInputs(null, null, null, null));
		Assert.Equal(expected, Points(r, "Gamma Regime"));
	}

	[Fact]
	public void GammaRegime_FallsBackToSign_WithoutADailyMove()
	{
		var t = LongGammaTerrain(dailyMove: null) with { ShortGamma = true, Trigger = 7802m };
		var r = GexSqueezeScreener.Evaluate(t, new SqueezeInputs(null, null, null, null));
		Assert.Equal(25, Points(r, "Gamma Regime"));
	}

	[Theory]
	[InlineData(90, true, "Imminent")]
	[InlineData(90, false, "Likely")]
	[InlineData(40, false, "Possible")]
	public void Band_IsCappedOnlyWhenIncomplete(int score, bool complete, string band) => Assert.Equal(band, GexSqueezeScreener.Band(score, complete));

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

	private static readonly TimeSpan Open = new(9, 30, 0);

	private static SortedDictionary<TimeSpan, Dictionary<string, long>> Captures() => new()
	{
		[new TimeSpan(10, 30, 0)] = new() { ["A"] = 600, ["B"] = 400 },
		[new TimeSpan(10, 50, 0)] = new() { ["A"] = 700 },
		// Anchor adds contract C (a wider window) which must not count as recent flow.
		[new TimeSpan(11, 0, 0)] = new() { ["A"] = 900, ["B"] = 500, ["C"] = 5000 },
	};

	[Fact]
	public void RecentWindow_SumsSharedContracts_FromTheLatestCaptureAtLeast15MinBack()
	{
		var w = GexSqueezeScreener.RecentVolumeWindow(Captures(), Open);
		// Reference = 10:30 (latest ≥15 min before 11:00; 10:50 is too close). Shared {A,B}: 1000 → 1400.
		Assert.Equal(new VolumeWindow(new TimeSpan(10, 30, 0), new TimeSpan(11, 0, 0), 400, 1400), w);
	}

	[Fact]
	public void RecentWindow_NullWithoutAQualifyingReference()
	{
		var captures = new SortedDictionary<TimeSpan, Dictionary<string, long>>
		{
			[new TimeSpan(10, 50, 0)] = new() { ["A"] = 700 },
			[new TimeSpan(11, 0, 0)] = new() { ["A"] = 900 },
		};
		Assert.Null(GexSqueezeScreener.RecentVolumeWindow(captures, Open));
	}

	[Fact]
	public void VolumePace_RatesAgainstTheSameWindowMedian()
	{
		var w = new VolumeWindow(new TimeSpan(11, 7, 0), new TimeSpan(11, 22, 0), 30_000, 400_000);
		var pace = GexSqueezeScreener.VolumePaceFrom(w, Open, [10_000, 20_000, 15_000, 12_000, 18_000]);
		Assert.Equal(VolumeBasis.SameWindowHistory, pace.Basis);
		Assert.Equal(5, pace.Sessions);
		Assert.Equal(2m, pace.Ratio);   // 30k ÷ median 15k
	}

	[Fact]
	public void VolumePace_EvenCountMedian_AveragesTheMiddlePair()
	{
		var w = new VolumeWindow(new TimeSpan(11, 7, 0), new TimeSpan(11, 22, 0), 30_000, 400_000);
		var pace = GexSqueezeScreener.VolumePaceFrom(w, Open, [10_000, 20_000, 15_000, 12_000, 18_000, 30_000]);
		Assert.Equal(30_000m / 16_500m, pace.Ratio);
	}

	[Fact]
	public void VolumePace_FallsBackToSessionAverage_WithTooLittleHistory()
	{
		// 400 over 30 min = 13.33/min recent vs 1400 over 90 min = 15.56/min session → 0.857x.
		var w = new VolumeWindow(new TimeSpan(10, 30, 0), new TimeSpan(11, 0, 0), 400, 1400);
		var pace = GexSqueezeScreener.VolumePaceFrom(w, Open, [500, 600, 700, 800]);
		Assert.Equal(VolumeBasis.SessionAverage, pace.Basis);
		Assert.Equal(0.857m, Math.Round(pace.Ratio, 3));
	}

	[Fact]
	public void DeltaOiShare_WeightsTheChangeByThePriorSnapshotsDelta()
	{
		// Calls +1000 contracts at prior |Δ| 0.1 (= 100); puts +200 at prior |Δ| 0.5 (= 100) → balanced, despite 5× the raw call change.
		// Today's contributor IV/T are deliberately extreme: the result must not depend on them (or on today's spot).
		var contributors = new[]
		{
			new GexContributor(Expiry, 7900m, 1.0 / 365, 0.90m, 3000, IsCall: true, Volume: 0),
			new GexContributor(Expiry, 7800m, 1.0 / 365, 0.90m, 1200, IsCall: false, Volume: 0),
			new GexContributor(Expiry, 7700m, 1.0 / 365, 0.90m, 500, IsCall: false, Volume: 0),   // not in the prior snapshot: skipped
		};
		var prior = new Dictionary<(DateTime, decimal, bool), PriorContract>
		{
			[(Expiry, 7900m, true)] = new PriorContract(2000, 0.1m),
			[(Expiry, 7800m, false)] = new PriorContract(1000, 0.5m),
		};
		Assert.Equal(0m, GexSqueezeScreener.DeltaOiShare(contributors, prior));
		prior[(Expiry, 7800m, false)] = new PriorContract(1100, 0.5m);   // puts +100 → 50 vs calls 100 → (100 − 50) / 150
		Assert.Equal(50m / 150m, GexSqueezeScreener.DeltaOiShare(contributors, prior));
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

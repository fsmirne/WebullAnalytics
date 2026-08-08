using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

// Campaign gex_layers: the four terrain layers are independent, default-off config gates. These tests lock
// (1) the Layer-1 placement veto's terrain and placebo modes plus its fail-open behavior on missing data,
// (2) the Layer-2 regime classification boundaries, (3) the Layer-3 sizing scalar clamps, and (4) the
// Layer-4 gravity-fly enumeration centering the body on the max-gross-gamma strike rather than spot.
public class GexLayersTests
{
	private static readonly DateTime AsOf = new(2026, 8, 3);      // Monday
	private static readonly DateTime Expiry = new(2026, 8, 10);   // next Monday, 7 DTE

	/// <summary>Chain around spot 100: puts carry the OI below <paramref name="putHeavyBelow"/>, calls above —
	/// so strikes under it read put-dominated (negative net) and strikes over it call-dominated.</summary>
	private static Dictionary<string, OptionContractQuote> Chain(decimal putHeavyBelow, long oi = 1000)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		for (var strike = 90m; strike <= 110m; strike += 1m)
		{
			var putOi = strike <= putHeavyBelow ? oi : 10;
			var callOi = strike <= putHeavyBelow ? 10 : oi;
			quotes[MatchKeys.OccSymbol("SPY", Expiry, strike, "P")] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m, openInterest: putOi);
			quotes[MatchKeys.OccSymbol("SPY", Expiry, strike, "C")] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m, openInterest: callOi);
		}
		return quotes;
	}

	// ---- Layer 1: placement veto ----

	[Fact]
	public void TerrainVeto_RejectsShortInPutDominatedBand_PassesCallDominated()
	{
		var cfg = new OpenerPlacementVetoConfig { Enabled = true };
		var quotes = Chain(putHeavyBelow: 100m);

		Assert.True(CandidateScorer.ShortStrikePlacementVetoed(cfg, "SPY", Expiry, 97m, 100m, AsOf, quotes, out var reason));
		Assert.Contains("put-dominated", reason);
		Assert.False(CandidateScorer.ShortStrikePlacementVetoed(cfg, "SPY", Expiry, 105m, 100m, AsOf, quotes, out _));
	}

	[Fact]
	public void TerrainVeto_FailsOpen_WhenNoOiData()
	{
		var cfg = new OpenerPlacementVetoConfig { Enabled = true };
		var bare = new Dictionary<string, OptionContractQuote>
		{
			[MatchKeys.OccSymbol("SPY", Expiry, 97m, "P")] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m)   // no OI → no terrain
		};
		Assert.False(CandidateScorer.ShortStrikePlacementVetoed(cfg, "SPY", Expiry, 97m, 100m, AsOf, bare, out _));
	}

	[Fact]
	public void PlaceboMode_VetoesByDistanceAlone()
	{
		var cfg = new OpenerPlacementVetoConfig { Enabled = true, Mode = "nearSpot", NearSpotMaxDistancePct = 0.01m };
		var quotes = Chain(putHeavyBelow: 100m);

		Assert.True(CandidateScorer.ShortStrikePlacementVetoed(cfg, "SPY", Expiry, 100m, 100m, AsOf, quotes, out var reason));
		Assert.Contains("placebo", reason);
		// 105 is call-dominated AND far: passes; 97 is put-dominated but far: placebo is terrain-blind → passes.
		Assert.False(CandidateScorer.ShortStrikePlacementVetoed(cfg, "SPY", Expiry, 97m, 100m, AsOf, quotes, out _));
	}

	[Fact]
	public void Veto_Disabled_NeverFires()
	{
		var cfg = new OpenerPlacementVetoConfig { Enabled = false };
		Assert.False(CandidateScorer.ShortStrikePlacementVetoed(cfg, "SPY", Expiry, 97m, 100m, AsOf, Chain(100m), out _));
	}

	// ---- Layer 2: regime classification ----

	[Fact]
	public void Regime_Classification_Boundaries()
	{
		var cfg = new ExpiryDayRegimeConfig { Enabled = true };
		// Put-dominated book → amplification regardless of gravity distance.
		Assert.Equal(ExpiryDayRegime.Amplification, ExpiryRegimeProvider.Classify(new CandidateScorer.GexResult(100m, -0.4m), 100m, cfg));
		// Call-dominated + gravity on spot → pin.
		Assert.Equal(ExpiryDayRegime.Pin, ExpiryRegimeProvider.Classify(new CandidateScorer.GexResult(100.2m, 0.5m), 100m, cfg));
		// Call-dominated but gravity 2% away → no clear read.
		Assert.Equal(ExpiryDayRegime.None, ExpiryRegimeProvider.Classify(new CandidateScorer.GexResult(102m, 0.5m), 100m, cfg));
		// No gravity (no data) → none; disabled → none.
		Assert.Equal(ExpiryDayRegime.None, ExpiryRegimeProvider.Classify(new CandidateScorer.GexResult(null, 0.5m), 100m, cfg));
		Assert.Equal(ExpiryDayRegime.None, ExpiryRegimeProvider.Classify(new CandidateScorer.GexResult(100m, -0.4m), 100m, new ExpiryDayRegimeConfig { Enabled = false }));
	}

	// ---- Layer 3: sizing scalar ----

	[Fact]
	public void VexScalar_ClampsAndSigns()
	{
		var cfg = new OpenerVexSizingConfig { Weight = 0.5m };
		Assert.Equal(1m, new OpenerVexSizingConfig().Scalar(1m, 1));                  // weight 0 → off
		Assert.Equal(1.25m, cfg.Scalar(1m, 1));                                       // clamped at max
		Assert.Equal(0.75m, cfg.Scalar(-1m, 1));                                      // clamped at min
		Assert.Equal(1.1m, cfg.Scalar(0.2m, 1));                                      // linear inside band
		Assert.Equal(0.9m, cfg.Scalar(0.2m, -1));                                     // short-vega flips the sign
	}

	// ---- Layer 4: gravity fly ----

	/// <summary>Default OpenerConfig ships with several structures enabled — turn everything off so the
	/// enumeration under test is the gravity fly alone.</summary>
	private static OpenerConfig OnlyGravityFly()
	{
		var cfg = new OpenerConfig();
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.DoubleCalendar.Enabled = false;
		cfg.Structures.LongDiagonal.Enabled = false;
		cfg.Structures.DoubleDiagonal.Enabled = false;
		cfg.Structures.IronButterfly.Enabled = false;
		cfg.Structures.IronCondor.Enabled = false;
		cfg.Structures.Condor.Enabled = false;
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.LongCallPut.Enabled = false;
		cfg.Structures.LongVertical.Enabled = false;
		cfg.Structures.DiagonalVertical.Enabled = false;
		cfg.Structures.CalendarVertical.Enabled = false;
		cfg.Structures.GravityFly.Enabled = true;
		return cfg;
	}

	[Fact]
	public void GravityFly_CentersBodyOnGravityStrike_NotSpot()
	{
		var cfg = OnlyGravityFly();
		cfg.Structures.GravityFly.DteMin = 0;
		cfg.Structures.GravityFly.DteMax = 10;
		cfg.Structures.GravityFly.WingSteps = new List<int> { 2 };
		// Heaviest gross OI pile at 100.25% of spot: chain put-heavy below 100 but the GROSS max sits at
		// the strike where both sides stack — make one strike carry outsized OI on both sides.
		var quotes = Chain(putHeavyBelow: 95m);
		var heavy = MatchKeys.OccSymbol("SPY", Expiry, 100m, "C");
		quotes[heavy] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m, openInterest: 50_000);
		quotes[MatchKeys.OccSymbol("SPY", Expiry, 100m, "P")] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m, openInterest: 50_000);

		var skels = CandidateEnumerator.Enumerate("SPY", spot: 100.20m, AsOf, cfg,
			availableExpirations: new HashSet<DateTime> { Expiry }, quotes: quotes).ToList();

		Assert.NotEmpty(skels);
		var fly = skels[0];
		Assert.Equal(OpenStructureKind.IronButterfly, fly.StructureKind);
		// Body (the two sells) on the gravity strike 100, wings 2 listed steps out.
		var sells = fly.Legs.Where(l => l.Action == "sell").ToList();
		Assert.All(sells, l => Assert.Equal(100m, ParsingHelpers.ParseOptionSymbol(l.Symbol)!.Strike));
		var wings = fly.Legs.Where(l => l.Action == "buy").Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!.Strike).OrderBy(k => k).ToList();
		Assert.Equal(new List<decimal> { 98m, 102m }, wings);
	}

	[Fact]
	public void GravityFly_DistanceGate_SuppressesWhenGravityFar()
	{
		var cfg = OnlyGravityFly();
		cfg.Structures.GravityFly.DteMax = 10;
		// 500k OI overwhelms the gamma falloff at 8% OTM, forcing gravity to 108 — far past the gate.
		var quotes = Chain(putHeavyBelow: 95m);
		quotes[MatchKeys.OccSymbol("SPY", Expiry, 108m, "C")] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m, openInterest: 500_000);
		quotes[MatchKeys.OccSymbol("SPY", Expiry, 108m, "P")] = TestQuote.Q(1.00m, 1.10m, iv: 0.20m, openInterest: 500_000);

		var skels = CandidateEnumerator.Enumerate("SPY", spot: 100.20m, AsOf, cfg,
			availableExpirations: new HashSet<DateTime> { Expiry }, quotes: quotes).ToList();
		Assert.Empty(skels);   // gravity at 108 is ~7.8% from spot — far past the 0.3% gate
	}
}

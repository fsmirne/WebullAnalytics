using WebullAnalytics.AI;
using WebullAnalytics.AI.Open.ZeroDte;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open.ZeroDte;

// The filter is where a verdict becomes a trading decision: it drops candidates whose structure, side,
// short-strike placement, distance or credit the session read does not endorse. It fails CLOSED — nothing
// passes on an unconfirmed session.
public class ZeroDteCandidateFilterTests
{
	private const string Ticker = "SPXW";
	private static readonly DateTime AsOf = new(2026, 9, 23, 10, 30, 0);
	private static readonly DateTime Expiry = new(2026, 9, 23);   // same-day = 0DTE
	private const decimal Spot = 7700m;

	/// <summary>Chain around spot where the biggest CALL gamma sits at <paramref name="callWall"/> and the
	/// biggest PUT gamma at <paramref name="putWall"/>, so ComputeGex reports those as the side-restricted
	/// walls. Uniform IV keeps gamma comparable across strikes, leaving OI as the discriminator.</summary>
	private static Dictionary<string, OptionContractQuote> Chain(decimal callWall, decimal putWall)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		for (var strike = Spot - 100m; strike <= Spot + 100m; strike += 5m)
		{
			quotes[MatchKeys.OccSymbol(Ticker, Expiry, strike, "C")] = TestQuote.Q(1.00m, 1.10m, iv: 0.15m, openInterest: strike == callWall ? 50_000 : 500);
			quotes[MatchKeys.OccSymbol(Ticker, Expiry, strike, "P")] = TestQuote.Q(1.00m, 1.10m, iv: 0.15m, openInterest: strike == putWall ? 50_000 : 500);
		}
		return quotes;
	}

	private static CandidateSkeleton ShortCallVertical(decimal shortStrike, decimal width = 5m, DateTime? expiry = null)
		=> new(Ticker, OpenStructureKind.ShortCallVertical, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol(Ticker, expiry ?? Expiry, shortStrike, "C"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol(Ticker, expiry ?? Expiry, shortStrike + width, "C"), 1)
		}, expiry ?? Expiry);

	private static CandidateSkeleton ShortPutVertical(decimal shortStrike, decimal width = 5m)
		=> new(Ticker, OpenStructureKind.ShortPutVertical, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol(Ticker, Expiry, shortStrike, "P"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol(Ticker, Expiry, shortStrike - width, "P"), 1)
		}, Expiry);

	private static ZeroDteGateVerdict BearVerdict(decimal? rangeHigh = null) => new(
		ZeroDteGateState.BearConfirmed,
		new HashSet<OpenStructureKind> { OpenStructureKind.ShortCallVertical },
		ProtectiveCallLevel: rangeHigh, ProtectivePutLevel: null, Tape: null, Summary: "bearish confirmed");

	private static ZeroDteGateVerdict BullVerdict() => new(
		ZeroDteGateState.BullConfirmed,
		new HashSet<OpenStructureKind> { OpenStructureKind.ShortPutVertical },
		ProtectiveCallLevel: null, ProtectivePutLevel: null, Tape: null, Summary: "bullish confirmed");

	private static ZeroDteGateConfig Cfg(Action<ZeroDteGateConfig>? tweak = null)
	{
		var cfg = new ZeroDteGateConfig { Enabled = true };
		tweak?.Invoke(cfg);
		return cfg;
	}

	// ---- side-restricted walls (the Class #05 protective levels) ----

	/// <summary>The walls must be on the CORRECT side of spot. analyze gex's unrestricted argmax can report a
	/// "call wall" below spot; as a barrier for strike placement that is meaningless, so ComputeGex's
	/// side-restricted pair ignores same-side-of-spot concentration on the wrong side.</summary>
	[Fact]
	public void ComputeGex_ReportsSideRestrictedWalls()
	{
		var gex = CandidateScorer.ComputeGex(Ticker, Expiry, Spot, AsOf, Chain(callWall: 7730m, putWall: 7660m));

		Assert.Equal(7730m, gex.CallWallAbove);
		Assert.Equal(7660m, gex.PutWallBelow);
	}

	/// <summary>Heavy call OI BELOW spot is not resistance. The call wall must fall back to the largest
	/// concentration that is actually above price.</summary>
	[Fact]
	public void ComputeGex_IgnoresWrongSideConcentration()
	{
		var gex = CandidateScorer.ComputeGex(Ticker, Expiry, Spot, AsOf, Chain(callWall: 7620m, putWall: 7660m));

		Assert.NotNull(gex.CallWallAbove);
		Assert.True(gex.CallWallAbove >= Spot, $"call wall {gex.CallWallAbove} must be at or above spot {Spot}");
	}

	// ---- structure / state matching ----

	[Fact]
	public void UnconfirmedSession_AdmitsNothing()
	{
		var waiting = ZeroDteGateVerdict.Blocked(ZeroDteGateState.WaitingForConfirmation, null, "waiting — gap unconfirmed");

		Assert.False(ZeroDteCandidateFilter.Admits(Cfg(), waiting, ShortCallVertical(7730m), Spot, AsOf, Chain(7730m, 7660m), out var reason));
		Assert.Contains("waiting", reason);
	}

	[Fact]
	public void WrongSideForTheState_IsRejected()
	{
		// Bearish session, bull put candidate.
		Assert.False(ZeroDteCandidateFilter.Admits(Cfg(), BearVerdict(), ShortPutVertical(7660m), Spot, AsOf, Chain(7730m, 7660m), out var reason));
		Assert.Contains("ShortPutVertical", reason);
	}

	/// <summary>Non-0DTE candidates pass straight through under the default zeroDteOnly, so this gate can be
	/// layered onto a mixed-DTE config without touching its longer-dated structures.</summary>
	[Fact]
	public void LongerDatedCandidate_PassesThroughUngated()
	{
		var waiting = ZeroDteGateVerdict.Blocked(ZeroDteGateState.WaitingForConfirmation, null, "waiting");
		var nextWeek = ShortCallVertical(7730m, expiry: Expiry.AddDays(7));

		Assert.True(ZeroDteCandidateFilter.Admits(Cfg(), waiting, nextWeek, Spot, AsOf, Chain(7730m, 7660m), out _));
	}

	[Fact]
	public void DisabledGate_AdmitsEverything()
	{
		var waiting = ZeroDteGateVerdict.Blocked(ZeroDteGateState.WaitingForConfirmation, null, "waiting");

		Assert.True(ZeroDteCandidateFilter.Admits(new ZeroDteGateConfig { Enabled = false }, waiting, ShortCallVertical(7730m), Spot, AsOf, Chain(7730m, 7660m), out _));
	}

	// ---- placement (Class #05) ----

	/// <summary>The literal Class #05 rule: a short call at or beyond the call wall is protected; one inside
	/// it is not, and is rejected however attractive its delta.</summary>
	[Fact]
	public void ShortBeyondCallWall_IsAdmitted_InsideIsRejected()
	{
		var quotes = Chain(callWall: 7730m, putWall: 7660m);

		Assert.True(ZeroDteCandidateFilter.Admits(Cfg(), BearVerdict(), ShortCallVertical(7735m), Spot, AsOf, quotes, out _));
		Assert.False(ZeroDteCandidateFilter.Admits(Cfg(), BearVerdict(), ShortCallVertical(7715m), Spot, AsOf, quotes, out var reason));
		Assert.Contains("inside call wall", reason);
	}

	[Fact]
	public void ShortBelowPutWall_IsAdmitted_AboveIsRejected()
	{
		var quotes = Chain(callWall: 7730m, putWall: 7660m);

		Assert.True(ZeroDteCandidateFilter.Admits(Cfg(), BullVerdict(), ShortPutVertical(7655m), Spot, AsOf, quotes, out _));
		Assert.False(ZeroDteCandidateFilter.Admits(Cfg(), BullVerdict(), ShortPutVertical(7685m), Spot, AsOf, quotes, out var reason));
		Assert.Contains("inside put wall", reason);
	}

	/// <summary>"If there is no good short strike, there is no good trade": a protective level further out
	/// than maxLevelDistancePct leaves nowhere logical to place the short, so the side is passed on rather
	/// than placed unprotected.</summary>
	[Fact]
	public void LevelTooFarFromSpot_VetoesTheSide()
	{
		// Wall 100 pts (1.30%) above a 7700 spot, level cap at 0.5%. The short-distance bound is disabled so
		// the LEVEL rule is unambiguously the one that fires (at 1.36% out the short would otherwise trip the
		// default 1.20% maxShortDistancePct first).
		var quotes = Chain(callWall: 7800m, putWall: 7660m);
		var cfg = Cfg(c => { c.Placement.MaxLevelDistancePct = 0.5m; c.Placement.MaxShortDistancePct = 0m; });

		Assert.False(ZeroDteCandidateFilter.Admits(cfg, BearVerdict(), ShortCallVertical(7805m), Spot, AsOf, quotes, out var reason));
		Assert.Contains("nowhere logical", reason);
	}

	[Theory]
	[InlineData(7702.0, false)]  // 0.03% out — too close
	[InlineData(7790.0, false)]  // 1.17% out — too far
	[InlineData(7730.0, true)]   // 0.39% out — inside the band
	public void ShortDistanceBounds_AreEnforced(double shortStrike, bool admitted)
	{
		var quotes = Chain(callWall: 7700m, putWall: 7660m);  // wall AT spot so placement never blocks
		var cfg = Cfg(c =>
		{
			c.Placement.MinShortDistancePct = 0.10m;
			c.Placement.MaxShortDistancePct = 1.00m;
			c.Placement.MaxLevelDistancePct = 0m;
		});

		Assert.Equal(admitted, ZeroDteCandidateFilter.Admits(cfg, BearVerdict(), ShortCallVertical((decimal)shortStrike), Spot, AsOf, quotes, out _));
	}

	/// <summary>With levelSource "both" the short must clear the FARTHER of the wall and the range edge — the
	/// strictest reading, not a disjunction.</summary>
	[Fact]
	public void LevelSourceBoth_RequiresClearingTheFartherLevel()
	{
		var quotes = Chain(callWall: 7720m, putWall: 7660m);
		var cfg = Cfg(c => { c.Placement.LevelSource = "both"; c.Placement.MaxLevelDistancePct = 0m; });
		var verdict = BearVerdict(rangeHigh: 7740m);   // range edge is FARTHER than the 7720 wall

		Assert.False(ZeroDteCandidateFilter.Admits(cfg, verdict, ShortCallVertical(7725m), Spot, AsOf, quotes, out _));
		Assert.True(ZeroDteCandidateFilter.Admits(cfg, verdict, ShortCallVertical(7745m), Spot, AsOf, quotes, out _));
	}

	/// <summary>An iron condor must satisfy the rule on BOTH wings — the framework structures both sides with
	/// the same logic, so one unprotected wing rejects the whole structure.</summary>
	[Fact]
	public void IronCondor_MustProtectBothWings()
	{
		var quotes = Chain(callWall: 7730m, putWall: 7660m);
		var rangeVerdict = new ZeroDteGateVerdict(ZeroDteGateState.RangeConfirmed,
			new HashSet<OpenStructureKind> { OpenStructureKind.IronCondor }, null, null, null, "balanced confirmed");
		var cfg = Cfg(c => c.Placement.MaxLevelDistancePct = 0m);

		CandidateSkeleton Condor(decimal shortPut, decimal shortCall) => new(Ticker, OpenStructureKind.IronCondor, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol(Ticker, Expiry, shortPut, "P"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol(Ticker, Expiry, shortPut - 5m, "P"), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol(Ticker, Expiry, shortCall, "C"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol(Ticker, Expiry, shortCall + 5m, "C"), 1)
		}, Expiry);

		Assert.True(ZeroDteCandidateFilter.Admits(cfg, rangeVerdict, Condor(7655m, 7735m), Spot, AsOf, quotes, out _));
		// Put wing inside its wall → whole condor rejected.
		Assert.False(ZeroDteCandidateFilter.Admits(cfg, rangeVerdict, Condor(7685m, 7735m), Spot, AsOf, quotes, out var reason));
		Assert.Contains("short put", reason);
	}

	// ---- credit vs width (Class #03 §7 / #04 §8) ----

	private static OpenProposal Priced(decimal creditPerContract, decimal widthDollars) => new(
		Ticker, OpenStructureKind.ShortCallVertical,
		new[] { new ProposalLeg("sell", MatchKeys.OccSymbol(Ticker, Expiry, 7730m, "C"), 1), new ProposalLeg("buy", MatchKeys.OccSymbol(Ticker, Expiry, 7735m, "C"), 1) },
		Qty: 1, DebitOrCreditPerContract: creditPerContract, MaxProfitPerContract: creditPerContract,
		MaxLossPerContract: -(widthDollars - creditPerContract), CapitalAtRiskPerContract: widthDollars - creditPerContract,
		Breakevens: Array.Empty<decimal>(), ProbabilityOfProfit: 0.7m, ExpectedValuePerContract: 1m,
		DaysToTarget: 0, RawScore: 0m, BiasAdjustedScore: 0m, DirectionalFit: -1, Rationale: "", Fingerprint: "");

	[Theory]
	[InlineData(110.0, true)]   // $1.10 on a 5-wide = 22% — the observed median
	[InlineData(55.0, false)]   // $0.55 = 11%, under the 12% floor: not worth the risk
	[InlineData(240.0, false)]  // $2.40 = 48%, over the 45% cap: short is too close to price
	public void CreditAsPctOfWidth_IsBounded(double creditPerContract, bool admitted)
	{
		Assert.Equal(admitted, ZeroDteCandidateFilter.AdmitsCredit(Cfg(), Priced((decimal)creditPerContract, 500m), AsOf, out _));
	}

	/// <summary>The framework trades credit only, so a net-debit structure is rejected outright rather than
	/// having its "credit ratio" computed from a negative premium.</summary>
	[Fact]
	public void DebitStructure_IsRejected()
	{
		Assert.False(ZeroDteCandidateFilter.AdmitsCredit(Cfg(), Priced(-150m, 500m), AsOf, out var reason));
		Assert.Contains("credit-only", reason);
	}

	/// <summary>The credit floor scales with width, so the same percentage passes on a 10-wide at twice the
	/// dollar credit — matching the trader's own move to 10-wide spreads at ~15% of width.</summary>
	[Fact]
	public void CreditFloor_ScalesWithWidth()
	{
		Assert.True(ZeroDteCandidateFilter.AdmitsCredit(Cfg(), Priced(150m, 1000m), AsOf, out _));    // 15% of a 10-wide
		Assert.False(ZeroDteCandidateFilter.AdmitsCredit(Cfg(), Priced(110m, 1000m), AsOf, out _));   // 11% of a 10-wide
	}
}

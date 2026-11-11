using WebullAnalytics.AI.Open.ZeroDte;
using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>Locks the `analyze cascade` classifier: each regime confirms on its own textbook session, a quiet session
/// stays neutral, missing inputs never confirm, the trigger score is honoured, and the Bear Trap measures its dip and
/// bounce from the SESSION low rather than the trailing range window's.</summary>
public class CascadeScreenerTests
{
	private const decimal Open = 7000m;
	private const decimal Move = 70m;   // one-day expected move: 1% of spot, above the 0.6% floor

	private static CascadeTerrain Terrain(decimal spot, bool shortGamma, decimal netGexMulti, decimal? gravity, decimal? pull, decimal? callWall = null, decimal? putWall = null) => new(
		Front: new SqueezeTerrain(spot, Trigger: shortGamma ? spot + 30m : spot - 30m, shortGamma, callWall, putWall, Move),
		FrontExpiry: new DateTime(2026, 11, 9),
		Gravity0Dte: gravity,
		Centroid0Dte: gravity,
		NetPull0Dte: pull,
		FlipMulti: null,
		NetGexFractionMulti: netGexMulti,
		NetVexMulti: 0m,
		NetVexFractionMulti: 0m,
		ExpiryCount: 20);

	private static SessionTape Tape(decimal spot, VwapSide side, int held, decimal high, decimal low, decimal? windowLow = null, decimal gapPct = 0m, int flips = 0) => new(
		SessionOpen: Open,
		PrevClose: Open / (1m + gapPct / 100m),
		Spot: spot,
		Vwap: side == VwapSide.Above ? spot - 10m : spot + 10m,
		GapPct: gapPct,
		Gap: gapPct > 0.15m ? GapDirection.Bull : gapPct < -0.15m ? GapDirection.Bear : GapDirection.Flat,
		Side: side,
		MinutesHeldOnSide: held,
		VwapFlips: flips,
		SessionHigh: high,
		SessionLow: low,
		WindowHigh: high,
		WindowLow: windowLow ?? low,
		WindowHighTouches: 0,
		WindowLowTouches: 0,
		WindowVwapCrosses: 0,
		WindowBars: 20,
		MinutesSinceOpen: 20,
		BarCount: 20);

	private static CascadeInputs Inputs(SessionTape? tape, decimal? flow, decimal? vixOpen = 20m, decimal? vixNow = 20m) => new(
		FlowShare: flow,
		Volume: null,
		DeltaOiShare: null,
		PriorOiDate: null,
		Tape: tape,
		VixPriorClose: 20m,
		VixOpen: vixOpen,
		VixNow: vixNow,
		MaxPriorVix9dOverVix: null,
		Hv3OverHv30: null,
		SmileIndex: null,
		SmileChange1d: null);

	// Up 0.8 EM from the open, 20 minutes above VWAP, VIX −5% from its open, heavy call flow, short gamma, call wall 0.5 EM overhead.
	private static (CascadeTerrain T, CascadeInputs I) BullCascadeSession()
	{
		const decimal spot = 7056m;
		return (Terrain(spot, shortGamma: true, netGexMulti: -0.40m, gravity: 7090m, pull: 0.60m, callWall: 7091m, putWall: 6950m),
			Inputs(Tape(spot, VwapSide.Above, held: 20, high: 7058m, low: 6998m), flow: 0.50m, vixOpen: 20m, vixNow: 19m));
	}

	// The mirror image: down 0.8 EM, below VWAP, VIX +5%, heavy put flow, short gamma, put wall 0.5 EM below.
	private static (CascadeTerrain T, CascadeInputs I) BearCascadeSession()
	{
		const decimal spot = 6944m;
		return (Terrain(spot, shortGamma: true, netGexMulti: -0.40m, gravity: 6910m, pull: -0.60m, callWall: 7050m, putWall: 6909m),
			Inputs(Tape(spot, VwapSide.Below, held: 20, high: 7002m, low: 6942m), flow: -0.50m, vixOpen: 20m, vixNow: 21m));
	}

	// Dipped 0.8 EM to 6944, bounced 0.6 EM to 6986 and reclaimed VWAP, VIX −2% from its open, call buyers, gravity overhead.
	private static (CascadeTerrain T, CascadeInputs I) BearTrapSession(decimal? windowLow = null)
	{
		const decimal spot = 6986m;
		return (Terrain(spot, shortGamma: true, netGexMulti: -0.30m, gravity: 7030m, pull: 0.45m, callWall: 7050m, putWall: 6900m),
			Inputs(Tape(spot, VwapSide.Above, held: 8, high: 7001m, low: 6944m, windowLow: windowLow), flow: 0.35m, vixOpen: 20m, vixNow: 19.6m));
	}

	// Rallied 0.8 EM to 7056, faded 0.6 EM to 7014 and lost VWAP, VIX +2% from its open, put buyers, gravity below.
	private static (CascadeTerrain T, CascadeInputs I) BullTrapSession()
	{
		const decimal spot = 7014m;
		return (Terrain(spot, shortGamma: false, netGexMulti: -0.30m, gravity: 6970m, pull: -0.45m, callWall: 7060m, putWall: 6950m),
			Inputs(Tape(spot, VwapSide.Below, held: 8, high: 7056m, low: 6999m), flow: -0.35m, vixOpen: 20m, vixNow: 20.4m));
	}

	[Fact]
	public void BullCascade_ConfirmsOnTrendUpWithVixCrushAndCallFlow()
	{
		var (t, i) = BullCascadeSession();
		var r = CascadeScreener.Evaluate(t, i);
		Assert.Equal(CascadeRegime.BullCascade, r.PrimaryRegime);
		Assert.True(r.Active);
		Assert.Equal(1, r.DirectionalSign);
		Assert.False(r.Setups[CascadeRegime.BearCascade].Complete);
	}

	[Fact]
	public void BearCascade_ConfirmsOnTrendDownWithVixExpansionAndPutFlow()
	{
		var (t, i) = BearCascadeSession();
		var r = CascadeScreener.Evaluate(t, i);
		Assert.Equal(CascadeRegime.BearCascade, r.PrimaryRegime);
		Assert.Equal(-1, r.DirectionalSign);
		Assert.False(r.Setups[CascadeRegime.BullCascade].Complete);
	}

	[Fact]
	public void BearTrap_ConfirmsOnDipReclaimedWithStallingVix()
	{
		var (t, i) = BearTrapSession();
		var r = CascadeScreener.Evaluate(t, i);
		Assert.Equal(CascadeRegime.BearTrap, r.PrimaryRegime);
		Assert.Equal(1, r.DirectionalSign);
	}

	[Fact]
	public void BullTrap_ConfirmsOnRallyLostWithVixNotCrushing()
	{
		var (t, i) = BullTrapSession();
		var r = CascadeScreener.Evaluate(t, i);
		Assert.Equal(CascadeRegime.BullTrap, r.PrimaryRegime);
		Assert.Equal(-1, r.DirectionalSign);
	}

	[Fact]
	public void BearTrap_MeasuresDipAndBounceFromSessionLow_NotTrailingWindowLow()
	{
		// Later in the day the morning low has left the trailing range window (window low 6980, 6 points under spot).
		// The dip and the bounce are still the session's: the trap must confirm exactly as it does with the low in the window.
		var (t, i) = BearTrapSession(windowLow: 6980m);
		var late = CascadeScreener.Evaluate(t, i);
		var (t0, i0) = BearTrapSession();
		var early = CascadeScreener.Evaluate(t0, i0);
		Assert.Equal(CascadeRegime.BearTrap, late.PrimaryRegime);
		Assert.Equal(early.Setups[CascadeRegime.BearTrap].Score, late.Setups[CascadeRegime.BearTrap].Score);
	}

	// The same session with the front expiry above its flip and the multi-expiry book at the given net GEX.
	private static CascadeTerrain FrontLongGamma(CascadeTerrain t, decimal netGexMulti) => t with { Front = t.Front with { ShortGamma = false, Trigger = t.Front.Spot - 30m }, NetGexFractionMulti = netGexMulti };

	[Fact]
	public void BothCascades_RequireShortGammaTerrain_AndScoreItIdentically()
	{
		var (bullT, bullI) = BullCascadeSession();
		var (bearT, bearI) = BearCascadeSession();

		// Same book either way: the terrain factor is direction-blind, so mirrored sessions on equal terrain score it the same.
		var bullF1 = CascadeScreener.Evaluate(bullT, bullI).Setups[CascadeRegime.BullCascade].Factors.Single(f => f.Name == "Short-Gamma Terrain").Points;
		var bearF1 = CascadeScreener.Evaluate(bearT, bearI).Setups[CascadeRegime.BearCascade].Factors.Single(f => f.Name == "Short-Gamma Terrain").Points;
		Assert.Equal(bearF1, bullF1);

		// Long gamma on both the front expiry and the multi-expiry book: neither cascade confirms, however clean the tape.
		Assert.False(CascadeScreener.Evaluate(FrontLongGamma(bullT, netGexMulti: 0.30m), bullI).Setups[CascadeRegime.BullCascade].Complete);
		Assert.False(CascadeScreener.Evaluate(FrontLongGamma(bearT, netGexMulti: 0.30m), bearI).Setups[CascadeRegime.BearCascade].Complete);

		// Either leg of the requirement is enough, for both: front expiry long gamma but the multi-expiry book net short.
		Assert.True(CascadeScreener.Evaluate(FrontLongGamma(bullT, netGexMulti: -0.20m), bullI).Setups[CascadeRegime.BullCascade].Complete);
		Assert.True(CascadeScreener.Evaluate(FrontLongGamma(bearT, netGexMulti: -0.20m), bearI).Setups[CascadeRegime.BearCascade].Complete);
	}

	// Reflects a session through its open: prices mirror around 7000, VIX's change from its open flips sign, flow and
	// net pull flip sign, call and put walls swap. The gamma terrain (direction-blind) is left as it is.
	private static (CascadeTerrain T, CascadeInputs I) Mirror((CascadeTerrain T, CascadeInputs I) s)
	{
		var (t, i) = s;
		var tape = i.Tape!;
		var mirroredTape = tape with
		{
			Spot = 2m * Open - tape.Spot,
			Vwap = 2m * Open - tape.Vwap,
			Side = (VwapSide)(-(int)tape.Side),
			SessionHigh = 2m * Open - tape.SessionLow,
			SessionLow = 2m * Open - tape.SessionHigh,
			WindowHigh = 2m * Open - tape.WindowLow,
			WindowLow = 2m * Open - tape.WindowHigh,
			GapPct = -tape.GapPct,
		};
		var front = t.Front with { Spot = 2m * Open - t.Front.Spot, CallWall = 2m * Open - t.Front.PutWall, PutWall = 2m * Open - t.Front.CallWall, Trigger = 2m * Open - t.Front.Spot + (t.Front.Trigger - t.Front.Spot) };
		var terrain = t with { Front = front, Gravity0Dte = 2m * Open - t.Gravity0Dte, Centroid0Dte = 2m * Open - t.Centroid0Dte, NetPull0Dte = -t.NetPull0Dte };
		return (terrain, i with { Tape = mirroredTape, FlowShare = -i.FlowShare, VixNow = 2m * i.VixOpen - i.VixNow, VixPriorClose = i.VixOpen });
	}

	public static TheoryData<decimal, decimal, decimal, decimal, int, decimal> MirrorCases => new()
	{
		// spot move, VIX now (open 20), flow, gravity offset, minutes held, net GEX
		{ 56m, 19.0m, 0.50m, 34m, 20, -0.40m },    // textbook cascade
		{ 20m, 19.7m, 0.25m, 10m, 6, -0.20m },     // marginal on every gate
		{ 56m, 19.9m, 0.50m, 34m, 20, -0.40m },    // VIX barely moving: fails the VIX gate
		{ 56m, 19.9m, 0.50m, -60m, 20, -0.40m },   // gravity far behind + tame VIX: the gravity veto
		{ 56m, 19.0m, 0.10m, 34m, 20, -0.40m },    // flow too weak
		{ 10m, 19.0m, 0.50m, 34m, 20, -0.40m },    // not far enough from the open
		{ 56m, 19.0m, 0.50m, 34m, 3, -0.40m },     // not held long enough
		{ 56m, 19.0m, 0.50m, 34m, 20, 0.30m },     // long-gamma terrain
	};

	[Theory]
	[MemberData(nameof(MirrorCases))]
	public void BearCascade_IsTheExactMirrorOfBullCascade(decimal up, decimal vixNow, decimal flow, decimal gravityOffset, int held, decimal netGex)
	{
		var spot = Open + up;
		var bull = (
			Terrain(spot, shortGamma: netGex < 0m, netGexMulti: netGex, gravity: spot + gravityOffset, pull: gravityOffset > 0m ? 0.60m : -0.60m, callWall: spot + 35m, putWall: Open - 50m),
			Inputs(Tape(spot, VwapSide.Above, held, high: spot + 2m, low: Open - 2m), flow, vixOpen: 20m, vixNow: vixNow));
		var bear = Mirror(bull);

		var bullSetup = CascadeScreener.Evaluate(bull.Item1, bull.Item2).Setups[CascadeRegime.BullCascade];
		var bearSetup = CascadeScreener.Evaluate(bear.T, bear.I).Setups[CascadeRegime.BearCascade];

		Assert.Equal(bullSetup.Complete, bearSetup.Complete);
		Assert.Equal(bullSetup.Score, bearSetup.Score);
		Assert.Equal(bullSetup.Factors.Select(f => f.Points), bearSetup.Factors.Select(f => f.Points));
	}

	[Fact]
	public void AfterTheClose_TheReadingIsARecap_NothingConfirms_AndSettledFrontLevelsAreUnscored()
	{
		// Every textbook session, re-read after the close with its front expiry settled.
		foreach (var (t, i) in new[] { BullCascadeSession(), BearCascadeSession(), BearTrapSession(), BullTrapSession() })
		{
			var live = CascadeScreener.Evaluate(t, i);
			Assert.True(live.Active);

			var recap = CascadeScreener.Evaluate(t with { SessionClosed = true, FrontSettled = true }, i);
			Assert.Equal(CascadeRegime.Neutral, recap.PrimaryRegime);
			Assert.All(recap.Setups.Values, s => Assert.False(s.Complete));
			// The recap still names the same session shape.
			Assert.Equal(live.LeadingSetup.Regime, recap.LeadingSetup.Regime);
		}

		var (ct, ci) = BullCascadeSession();
		var cascade = CascadeScreener.Evaluate(ct with { SessionClosed = true, FrontSettled = true }, ci).Setups[CascadeRegime.BullCascade];
		Assert.Null(cascade.Factors.Single(f => f.Name == "Short-Gamma Terrain").Points);
		Assert.Null(cascade.Factors.Single(f => f.Name == "Gravity Pull & Target Wall").Points);
		var (tt, ti) = BearTrapSession();
		Assert.Null(CascadeScreener.Evaluate(tt with { SessionClosed = true, FrontSettled = true }, ti).Setups[CascadeRegime.BearTrap].Factors.Single(f => f.Name == "Overhead Gravity Magnet").Points);
	}

	[Fact]
	public void AfterTheClose_WithALaterFrontExpiry_FactorsStayScored_ButNothingConfirms()
	{
		var (t, i) = BearCascadeSession();
		var recap = CascadeScreener.Evaluate(t with { SessionClosed = true }, i);
		Assert.Equal(CascadeRegime.Neutral, recap.PrimaryRegime);
		Assert.Equal(CascadeRegime.BearCascade, recap.LeadingSetup.Regime);
		Assert.All(recap.LeadingSetup.Factors, f => Assert.NotNull(f.Points));
	}

	[Fact]
	public void QuietSession_IsNeutral_AndNoSetupCompletes()
	{
		var t = Terrain(7001m, shortGamma: false, netGexMulti: 0.10m, gravity: 7000m, pull: 0m, callWall: 7050m, putWall: 6950m);
		var r = CascadeScreener.Evaluate(t, Inputs(Tape(7001m, VwapSide.Above, held: 2, high: 7004m, low: 6997m, flips: 5), flow: 0.02m));
		Assert.Equal(CascadeRegime.Neutral, r.PrimaryRegime);
		Assert.False(r.Active);
		Assert.All(r.Setups.Values, s => Assert.False(s.Complete));
	}

	[Fact]
	public void MissingIntradayVix_NeverConfirms()
	{
		var (t, i) = BullCascadeSession();
		var r = CascadeScreener.Evaluate(t, i with { VixOpen = null, VixNow = null });
		Assert.Equal(CascadeRegime.Neutral, r.PrimaryRegime);
		Assert.All(r.Setups.Values, s => Assert.False(s.Complete));
	}

	[Fact]
	public void MissingFlow_LeavesTheFactorUnscored_AndNeverConfirms()
	{
		var (t, i) = BearCascadeSession();
		var r = CascadeScreener.Evaluate(t, i with { FlowShare = null });
		Assert.Equal(CascadeRegime.Neutral, r.PrimaryRegime);
		Assert.Null(r.Setups[CascadeRegime.BearCascade].Factors.Single(f => f.Name == "Put Flow & Pace").Points);
		// The score is scaled over the four scored factors and may sit above the trigger; it still does not confirm.
		Assert.False(r.Setups[CascadeRegime.BearCascade].Complete);
	}

	[Fact]
	public void MissingTape_NeverConfirms()
	{
		var (t, i) = BullCascadeSession();
		var r = CascadeScreener.Evaluate(t, i with { Tape = null });
		Assert.Equal(CascadeRegime.Neutral, r.PrimaryRegime);
	}

	[Fact]
	public void TriggerScore_AboveTheSetupScore_ReportsNeutralWithTheSetupLeading()
	{
		var (t, i) = BullCascadeSession();
		var score = CascadeScreener.Evaluate(t, i).LeadingSetup.Score;
		Assert.True(score < 100);
		var r = CascadeScreener.Evaluate(t, i, triggerScore: score + 1);
		Assert.Equal(CascadeRegime.Neutral, r.PrimaryRegime);
		Assert.Equal(CascadeRegime.BullCascade, r.LeadingSetup.Regime);
		Assert.True(r.LeadingSetup.Complete);
	}

	[Fact]
	public void Scores_StayWithinZeroToOneHundred_AndFactorsWithinTheirMax()
	{
		foreach (var (t, i) in new[] { BullCascadeSession(), BearCascadeSession(), BearTrapSession(), BullTrapSession() })
		{
			var r = CascadeScreener.Evaluate(t, i);
			foreach (var s in r.Setups.Values)
			{
				Assert.InRange(s.Score, 0, 100);
				Assert.Equal(5, s.Factors.Count);
				Assert.All(s.Factors, f => Assert.InRange(f.Points ?? 0, 0, f.Max));
			}
		}
	}

	private static KeyValuePair<string, OptionContractQuote> Contract(string symbol, long? oi) =>
		new(symbol, new OptionContractQuote(symbol, LastPrice: null, Bid: 1m, Ask: 1.2m, Change: null, PercentChange: null, Volume: 100, OpenInterest: oi, ImpliedVolatility: 0.2m, HistoricalVolatility: null));

	[Fact]
	public void OiBlackout_WhenOnlyTheExpiredSessionKeepsOpenInterest()
	{
		var asOf = new DateTime(2026, 11, 10);
		var overnight = new Dictionary<string, OptionContractQuote>(new[] { Contract("SPXW261110C07500000", 62), Contract("SPXW261111C07500000", 0), Contract("SPXW261113P07400000", null) });
		Assert.True(AnalyzeGexCommand.IsOiBlackout(overnight, "SPXW", asOf));

		var session = new Dictionary<string, OptionContractQuote>(new[] { Contract("SPXW261110C07500000", 62), Contract("SPXW261111C07500000", 15) });
		Assert.False(AnalyzeGexCommand.IsOiBlackout(session, "SPXW", asOf));

		// A same-day-only chain (--dte 0) has no later expiry to judge by: usable as long as it carries OI at all.
		var zeroDteOnly = new Dictionary<string, OptionContractQuote>(new[] { Contract("SPXW261110C07500000", 62) });
		Assert.False(AnalyzeGexCommand.IsOiBlackout(zeroDteOnly, "SPXW", asOf));

		var empty = new Dictionary<string, OptionContractQuote>(new[] { Contract("SPXW261110C07500000", 0) });
		Assert.True(AnalyzeGexCommand.IsOiBlackout(empty, "SPXW", asOf));
	}
}

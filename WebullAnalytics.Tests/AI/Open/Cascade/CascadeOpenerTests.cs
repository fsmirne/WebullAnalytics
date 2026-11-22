using WebullAnalytics.AI;
using WebullAnalytics.AI.Open.Cascade;
using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open.Cascade;

// opener.cascade: each confirmed setup opens the one 0DTE trade its config block names (structure, anchor strike, width), once a day,
// at its first confirmation. The fixtures use the forward-test pair: Bull Trap sells a call vertical at the call wall the session
// high ran into, Bear Trap buys the call at the first strike at or above spot.
public class CascadeOpenerTests
{
	private static readonly DateTime Today = new(2026, 11, 10);
	private const decimal Spot = 7500.50m;

	private static string Occ(decimal strike, string root = "SPXW", DateTime? expiry = null, string right = "C") => MatchKeys.OccSymbol(root, expiry ?? Today, strike, right);

	private static OptionContractQuote Quote(string symbol, decimal bid, decimal ask) => new(symbol, null, bid, ask, null, null, 500, 2000, 0.20m);

	/// <summary>0DTE SPXW on the $5 grid. Calls 7500–7570: the 7505 call 15.00/15.40, the 7550 call 1.40/1.50, the 7560 call 0.50/0.60.
	/// Puts 7440–7500: the 7500 put 17.30/17.70, the 7450 put 1.55/1.65, the 7440 put 0.90/1.00.</summary>
	private static Dictionary<string, OptionContractQuote> Chain()
	{
		var calls = new Dictionary<decimal, decimal> { [7500m] = 18.00m, [7505m] = 15.20m, [7510m] = 12.80m, [7515m] = 10.60m, [7550m] = 1.45m, [7555m] = 0.90m, [7560m] = 0.55m, [7565m] = 0.35m, [7570m] = 0.20m };
		var puts = new Dictionary<decimal, decimal> { [7500m] = 17.50m, [7495m] = 15.00m, [7490m] = 12.70m, [7450m] = 1.60m, [7445m] = 1.25m, [7440m] = 0.95m, [7435m] = 0.70m };
		var chain = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (right, mids) in new[] { ("C", calls), ("P", puts) })
			foreach (var (strike, mid) in mids)
			{
				var half = mid >= 3m ? 0.20m : 0.05m;
				chain[Occ(strike, right: right)] = Quote(Occ(strike, right: right), mid - half, mid + half);
			}
		return chain;
	}

	private static readonly CascadeTerrain Terrain = new(new SqueezeTerrain(Spot, Trigger: Spot + 30m, ShortGamma: true, CallWall: 7550m, PutWall: 7450m, DailyMove: 60m), Today, null, null, null, null, 0m, 0m, 0m, 20, CallWallAtHigh0Dte: 7550m);

	private static CascadeSetupConfig Setup(string structure, string anchor = "atm", int offsetSteps = 0, int widthSteps = 2, decimal minCredit = 0m) =>
		new() { Enabled = true, Structure = structure, Anchor = anchor, OffsetSteps = offsetSteps, WidthSteps = widthSteps, MinCredit = minCredit };

	private static (CandidateSkeleton? Skeleton, string? Skip) Build(CascadeSetupConfig setup, CascadeTerrain? terrain = null, Dictionary<string, OptionContractQuote>? chain = null, decimal spot = Spot) =>
		CascadeOpener.BuildTrade("SPXW", Today, spot, terrain ?? Terrain, chain ?? Chain(), 5m, setup);

	private static (string, string)[] Legs(CandidateSkeleton? skeleton) => skeleton!.Legs.Select(l => (l.Action, l.Symbol)).ToArray();

	private static CascadeLiveRead Read(CascadeRegime regime, decimal? callWallAtHigh = 7550m, Dictionary<string, OptionContractQuote>? chain = null)
	{
		var terrain = Terrain with { CallWallAtHigh0Dte = callWallAtHigh };
		var inputs = new CascadeInputs(null, null, null, null, null, 20m, 20m, 20m, null, null, null, null);
		var lead = regime == CascadeRegime.Neutral ? CascadeRegime.BullTrap : regime;
		var setup = new CascadeSetupEvaluation(lead, regime == CascadeRegime.Neutral ? 40 : 76, regime != CascadeRegime.Neutral, Array.Empty<FactorScore>(), Array.Empty<SetupNote>());
		var reading = new CascadeReading(regime, setup, new Dictionary<CascadeRegime, CascadeSetupEvaluation> { [lead] = setup }, terrain, inputs, CascadeScreener.DefaultTriggerScore);
		return new CascadeLiveRead(reading, 20, Spot, chain ?? Chain());
	}

	private static AIConfig Config()
	{
		var cascade = new OpenerCascadeConfig { Enabled = true, ReadSeconds = 60, BullTrap = Setup("ShortCallVertical", "callWallAtHigh", minCredit: 0.10m), BearTrap = Setup("LongCall") };
		var opener = new OpenerConfig { MaxQtyPerProposal = 500, MaxRiskPctPerProposal = 0.10m, Cascade = cascade };
		var ai = new AIConfig { Ticker = "SPXW", Indicators = new IndicatorsConfig { IvDefaultPct = 0.18m, StrikeStep = 5m }, Opener = opener, CashReserve = new CashReserveConfig { Mode = "absolute", Value = 0m } };
		opener.Indicators = ai.Indicators;
		return ai;
	}

	private static EvaluationContext Ctx(int minute = 0) => new(
		Now: MarketTime.EtToUtc(Today.AddHours(10).AddMinutes(45 + minute)).ToLocalTime(),
		OpenPositions: new Dictionary<string, OpenPosition>(),
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPXW"] = Spot },
		Quotes: new Dictionary<string, OptionContractQuote>(),
		AccountCash: 100_000m, AccountValue: 100_000m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>());

	private sealed class Harness : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), $"cascade-signals-{Guid.NewGuid():N}.jsonl");
		public int Reads;
		public CascadeLiveRead? Next;
		public CascadeSignalLedger Ledger => new(_path);

		/// <summary>One read at <paramref name="minute"/>; a proposal is reported acted on, as the auto-executor does once it places (or dry-runs) it.</summary>
		public async Task<IReadOnlyList<OpenProposal>> ActedRead(CascadeOpener opener, int minute)
		{
			var proposals = await opener.EvaluateAsync(Ctx(minute), TestContext.Current.CancellationToken);
			foreach (var p in proposals) opener.Acted(p);
			return proposals;
		}

		public CascadeOpener Opener(bool live = false, AIConfig? config = null) =>
			new(config ?? Config(), live, new CascadeSignalLedger(_path), (_, _) => { Reads++; return Task.FromResult<(CascadeLiveRead? Read, string? Error)>((Next, Next == null ? "no chain" : null)); });

		public void Dispose() => File.Delete(_path);
	}

	// ── Trade construction ────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void CreditCallVertical_SellsTheAnchor_BuysWidthStepsAbove()
	{
		var (skeleton, skip) = Build(Setup("ShortCallVertical", "callWallAtHigh", minCredit: 0.10m));
		Assert.Null(skip);
		Assert.Equal(OpenStructureKind.ShortCallVertical, skeleton!.StructureKind);
		Assert.Equal([("sell", Occ(7550m)), ("buy", Occ(7560m))], Legs(skeleton));
		Assert.Equal(Today, skeleton.TargetExpiry);
	}

	[Fact]
	public void CreditPutVertical_SellsTheAnchor_BuysWidthStepsBelow()
	{
		var (skeleton, skip) = Build(Setup("ShortPutVertical", "putWall"));
		Assert.Null(skip);
		Assert.Equal(OpenStructureKind.ShortPutVertical, skeleton!.StructureKind);
		Assert.Equal([("sell", Occ(7450m, right: "P")), ("buy", Occ(7440m, right: "P"))], Legs(skeleton));
	}

	[Fact]
	public void DebitVerticals_BuyTheAnchor_SellFurtherOutOfTheMoney()
	{
		Assert.Equal([("buy", Occ(7505m)), ("sell", Occ(7515m))], Legs(Build(Setup("LongCallVertical")).Skeleton));
		Assert.Equal([("buy", Occ(7500m, right: "P")), ("sell", Occ(7490m, right: "P"))], Legs(Build(Setup("LongPutVertical")).Skeleton));
	}

	[Fact]
	public void SingleOptions_AtTheMoney_AreTheFirstStrikeAtOrBeyondSpot()
	{
		// Spot 7500.50: the call at or above it is 7505, the put at or below it 7500.
		Assert.Equal([("buy", Occ(7505m))], Legs(Build(Setup("LongCall")).Skeleton));
		Assert.Equal([("buy", Occ(7500m, right: "P"))], Legs(Build(Setup("LongPut")).Skeleton));
		// Spot exactly on a strike takes that strike.
		Assert.Equal([("buy", Occ(7500m))], Legs(Build(Setup("LongCall"), spot: 7500m).Skeleton));
	}

	[Fact]
	public void OffsetSteps_MoveTheAnchorFurtherOutOfTheMoney_NegativeTowardIt()
	{
		Assert.Equal([("buy", Occ(7510m))], Legs(Build(Setup("LongCall", offsetSteps: 1)).Skeleton));
		Assert.Equal([("buy", Occ(7495m, right: "P"))], Legs(Build(Setup("LongPut", offsetSteps: 1)).Skeleton));
		Assert.Equal([("sell", Occ(7555m)), ("buy", Occ(7565m))], Legs(Build(Setup("ShortCallVertical", "callWall", offsetSteps: 1)).Skeleton));
		Assert.Equal([("buy", Occ(7500m))], Legs(Build(Setup("LongCall", offsetSteps: -1)).Skeleton));
	}

	[Fact]
	public void TheLocalWallsAndTheWallsAtSessionExtremesAreSeparateAnchors()
	{
		var terrain = Terrain with { Front = Terrain.Front with { CallWall = 7555m, PutWall = 7445m }, CallWallAtHigh0Dte = 7550m, PutWallAtLow0Dte = 7450m };
		Assert.Equal(Occ(7555m), Legs(Build(Setup("ShortCallVertical", "callWall"), terrain).Skeleton)[0].Item2);
		Assert.Equal(Occ(7550m), Legs(Build(Setup("ShortCallVertical", "callWallAtHigh"), terrain).Skeleton)[0].Item2);
		Assert.Equal(Occ(7445m, right: "P"), Legs(Build(Setup("ShortPutVertical", "putWall"), terrain).Skeleton)[0].Item2);
		Assert.Equal(Occ(7450m, right: "P"), Legs(Build(Setup("ShortPutVertical", "putWallAtLow"), terrain).Skeleton)[0].Item2);
	}

	[Fact]
	public void NoTradeWithoutAWallOutOfTheMoney()
	{
		Assert.Contains("no call wall at the session high", Build(Setup("ShortCallVertical", "callWallAtHigh"), Terrain with { CallWallAtHigh0Dte = null }).Skip, StringComparison.Ordinal);
		Assert.Contains("not above spot", Build(Setup("ShortCallVertical", "callWallAtHigh"), Terrain with { CallWallAtHigh0Dte = 7500m }).Skip, StringComparison.Ordinal);
		Assert.Contains("no put wall at the session low", Build(Setup("ShortPutVertical", "putWallAtLow"), Terrain with { PutWallAtLow0Dte = null }).Skip, StringComparison.Ordinal);
		Assert.Contains("not below spot", Build(Setup("ShortPutVertical", "putWallAtLow"), Terrain with { PutWallAtLow0Dte = 7505m }).Skip, StringComparison.Ordinal);
		Assert.Contains("not below spot", Build(Setup("ShortPutVertical", "putWall"), Terrain with { Front = Terrain.Front with { PutWall = 7505m } }).Skip, StringComparison.Ordinal);
		Assert.Contains("no put wall", Build(Setup("ShortPutVertical", "putWall"), Terrain with { Front = Terrain.Front with { PutWall = null } }).Skip, StringComparison.Ordinal);
	}

	[Fact]
	public void CreditVertical_NoTradeUnderTheCreditFloor()
	{
		// 7560/7570 mid credit = 0.55 − 0.20 = 0.35, under a 0.50 floor.
		var (skeleton, skip) = Build(Setup("ShortCallVertical", "callWallAtHigh", minCredit: 0.50m), Terrain with { CallWallAtHigh0Dte = 7560m });
		Assert.Null(skeleton);
		Assert.Contains("under minCredit", skip, StringComparison.Ordinal);
		// The floor is a credit floor: a debit vertical ignores it.
		Assert.NotNull(Build(Setup("LongCallVertical", minCredit: 50m)).Skeleton);
	}

	[Fact]
	public void NoTradeWhenALegIsNotListedOrNotQuoted()
	{
		var (skeleton, skip) = Build(Setup("ShortCallVertical", "callWallAtHigh"), Terrain with { CallWallAtHigh0Dte = 7565m });
		Assert.Null(skeleton);
		Assert.Contains("no call expiring today at $7,575", skip, StringComparison.Ordinal);

		var chain = Chain();
		chain[Occ(7505m)] = Quote(Occ(7505m), 0m, 15.40m);
		Assert.Contains("no two-sided quote on the $7,505 call", Build(Setup("LongCall"), chain: chain).Skip, StringComparison.Ordinal);
	}

	[Fact]
	public void ABoughtWingNeedsOnlyAnOffer_ASoldOneNeedsABid()
	{
		var chain = Chain();
		chain[Occ(7560m)] = Quote(Occ(7560m), 0m, 0.10m);     // unbid wing
		Assert.NotNull(Build(Setup("ShortCallVertical", "callWallAtHigh"), chain: chain).Skeleton);
		chain[Occ(7515m)] = Quote(Occ(7515m), 0m, 10.80m);    // the leg a debit vertical sells
		Assert.Contains("no two-sided quote on the $7,505/$7,515 call spread", Build(Setup("LongCallVertical"), chain: chain).Skip, StringComparison.Ordinal);
	}

	[Fact]
	public void LegsComeOnlyFromTheTradedRootExpiringToday()
	{
		// On a monthly expiry the AM-settled SPX series shares the date; a later SPXW expiry shares the strike. Neither is the 0DTE SPXW call.
		var chain = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[Occ(7505m, "SPX")] = Quote(Occ(7505m, "SPX"), 15m, 15.4m),
			[Occ(7505m, expiry: Today.AddDays(1))] = Quote(Occ(7505m, expiry: Today.AddDays(1)), 30m, 30.4m),
		};
		Assert.Null(Build(Setup("LongCall"), chain: chain).Skeleton);
	}

	[Fact]
	public void AStructureOutsideTheSupportedSetBuildsNothing()
	{
		Assert.Contains("not a structure", Build(Setup("IronCondor")).Skip, StringComparison.Ordinal);
	}

	// ── Signal handling ───────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task ABullTrapConfirmationProposesTheSizedSpread()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var p = Assert.Single(await h.Opener().EvaluateAsync(Ctx(), TestContext.Current.CancellationToken));
		Assert.Equal(OpenStructureKind.ShortCallVertical, p.StructureKind);
		Assert.False(p.Informational);
		Assert.True(p.Qty >= 1);
		Assert.All(p.Legs, l => Assert.Equal(p.Qty, l.Qty));
		// 10-wide, 0.90 mid credit → $910 at risk per contract; 10% of a $100k account sizes 10 contracts.
		Assert.Equal(10, p.Qty);
		Assert.StartsWith("cascade BULL TRAP 76/100", p.Rationale, StringComparison.Ordinal);
		Assert.Equal(Spot, p.Spot);
	}

	[Fact]
	public async Task ABearTrapConfirmationProposesTheLongCall()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BearTrap) };
		var p = Assert.Single(await h.Opener().EvaluateAsync(Ctx(), TestContext.Current.CancellationToken));
		Assert.Equal(OpenStructureKind.LongCall, p.StructureKind);
		Assert.Equal(Occ(7505m), p.Legs.Single().Symbol);
		Assert.Equal(6, p.Qty);   // $15.20 call = $1,520 a contract against a $10,000 risk budget
	}

	[Fact]
	public async Task ASetupTradesOnceADay()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var opener = h.Opener();
		Assert.Single(await h.ActedRead(opener, 0));
		Assert.Empty(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));    // still confirmed a minute later
		Assert.Empty(await h.Opener().EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken));   // and after a restart
		h.Next = Read(CascadeRegime.BearTrap);
		Assert.Single(await opener.EvaluateAsync(Ctx(3), TestContext.Current.CancellationToken));   // the other setup still has its own entry
	}

	[Fact]
	public async Task OutsideTheEntryWindowTheRegimeIsReadButNothingIsRecordedOrTraded()
	{
		// Before opener.earliestEntryTimeEt the watch loop still shows the panel; the first confirmation inside the window is the one that trades.
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var opener = h.Opener();
		Assert.Empty(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken, entryWindowOpen: false));
		Assert.Equal(1, h.Reads);
		Assert.Empty(h.Ledger.ConfirmedRegimes(DateOnly.FromDateTime(Today), "SPXW"));
		Assert.Single(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ATradeTheExecutorHeldBackIsProposedAgainUntilTheRetryWindowEnds()
	{
		// retrySeconds 180: the quote guard or a broker refresh failure held the order back, so nothing was opened and nothing is final.
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var opener = h.Opener();
		Assert.Single(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		Assert.Single(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
		Assert.Single(await h.Opener().EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken));   // a restarted loop picks the retry up from the ledger
		Assert.Single(await opener.EvaluateAsync(Ctx(3), TestContext.Current.CancellationToken));       // 180s after the first confirmation: still inside
		Assert.Empty(await opener.EvaluateAsync(Ctx(4), TestContext.Current.CancellationToken));        // 240s: the signal is stale, the day is done
		Assert.Equal([CascadeSignalLedger.Signaled], h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false).Select(e => e.Outcome));
	}

	[Fact]
	public async Task ARetryThatIsActedOnEndsTheDay()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var opener = h.Opener();
		Assert.Single(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));   // held back
		Assert.Single(await h.ActedRead(opener, 1));                  // placed on the retry
		Assert.Empty(await opener.EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken));
		var entries = h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false);
		Assert.Equal([CascadeSignalLedger.Signaled, CascadeSignalLedger.Placed], entries.Select(e => e.Outcome));
		Assert.Equal(["10:45:00", "10:46:00"], entries.Select(e => e.TimeEt));
	}

	[Fact]
	public async Task ARetryNeedsTheSetupStillConfirmedAndTheTradeStillBuildable()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var opener = h.Opener();
		Assert.Single(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		h.Next = Read(CascadeRegime.Neutral);
		Assert.Empty(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));    // no longer confirmed
		h.Next = Read(CascadeRegime.BullTrap, callWallAtHigh: null);
		Assert.Empty(await opener.EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken));    // confirmed, but no wall to sell: waits, the retry stays open
		h.Next = Read(CascadeRegime.BullTrap);
		Assert.Single(await opener.EvaluateAsync(Ctx(3), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task RetrySecondsZeroIsTheFirstConfirmationOnly()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var config = Config();
		config.Opener.Cascade.RetrySeconds = 0;
		var opener = h.Opener(config: config);
		Assert.Single(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		Assert.Empty(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ActedIgnoresAProposalThisOpenerDidNotJustHandOut()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var opener = h.Opener();
		var p = Assert.Single(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		opener.Acted(p with { Fingerprint = "someone-else" });
		Assert.Single(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));   // still pending
	}

	[Fact]
	public async Task AConfirmationWithNoTradeDoesNotUseUpTheDay_TheNextReadThatYieldsATradePlacesIt()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap, callWallAtHigh: null) };
		var opener = h.Opener();
		Assert.Empty(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		var waiting = Assert.Single(h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false));
		Assert.Equal(CascadeSignalLedger.Waiting, waiting.Outcome);

		h.Next = Read(CascadeRegime.BullTrap, callWallAtHigh: 7550m);
		Assert.Single(await h.ActedRead(opener, 1));                         // the conditions cleared: it trades
		Assert.Empty(await opener.EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken));          // and only a placed trade uses the day up
		Assert.Equal(CascadeSignalLedger.Placed, h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false).Last().Outcome);
	}

	[Fact]
	public async Task AWaitingSetupIsRecordedOncePerReason_NotOncePerRead()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap, callWallAtHigh: null) };
		var opener = h.Opener();
		await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken);
		await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken);
		await opener.EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken);
		var entries = h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false);
		Assert.Single(entries);
		Assert.Equal(3, h.Reads);

		// A different reason is a new line, so the panel shows the latest.
		h.Next = Read(CascadeRegime.BullTrap, callWallAtHigh: 7552m);
		await opener.EvaluateAsync(Ctx(3), TestContext.Current.CancellationToken);
		Assert.Equal(2, h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false).Count);
	}

	[Fact]
	public async Task WithRetrySkippedOff_TheFirstConfirmationIsTheDaysOnlyEntry()
	{
		var config = Config();
		config.Opener.Cascade.RetrySkipped = false;
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap, callWallAtHigh: null) };
		var opener = h.Opener(config: config);
		Assert.Empty(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		Assert.Equal(CascadeSignalLedger.Skipped, Assert.Single(h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false)).Outcome);
		h.Next = Read(CascadeRegime.BullTrap, callWallAtHigh: 7550m);
		Assert.Empty(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
	}

	/// <summary>A read where Bull Trap (76) leads and Bear Trap (70) is also confirmed.</summary>
	private static CascadeLiveRead TwoConfirmed()
	{
		var read = Read(CascadeRegime.BullTrap);
		var second = new CascadeSetupEvaluation(CascadeRegime.BearTrap, 70, true, Array.Empty<FactorScore>(), Array.Empty<SetupNote>());
		var setups = new Dictionary<CascadeRegime, CascadeSetupEvaluation>(read.Reading.Setups) { [CascadeRegime.BearTrap] = second };
		return read with { Reading = read.Reading with { Setups = setups } };
	}

	[Fact]
	public async Task ByDefault_OnlyThePrimaryConfirmedSetupTrades()
	{
		using var h = new Harness { Next = TwoConfirmed() };
		var p = Assert.Single(await h.Opener().EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		Assert.Equal(OpenStructureKind.ShortCallVertical, p.StructureKind);
	}

	[Fact]
	public async Task WithTradeAllConfirmed_EveryConfirmedEnabledSetupTradesWithItsOwnScore()
	{
		var config = Config();
		config.Opener.Cascade.TradeAllConfirmed = true;
		using var h = new Harness { Next = TwoConfirmed() };
		var opener = h.Opener(config: config);
		var proposals = await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken);
		Assert.Equal(2, proposals.Count);
		Assert.Contains(proposals, p => p.StructureKind == OpenStructureKind.ShortCallVertical && p.Rationale.Contains("76/100"));
		Assert.Contains(proposals, p => p.StructureKind == OpenStructureKind.LongCall && p.Rationale.Contains("70/100"));
		Assert.Equal(CascadeRegime.BearTrap, opener.SetupOf(proposals.Single(p => p.StructureKind == OpenStructureKind.LongCall))!.Value.Setup);
	}

	[Fact]
	public async Task ADryRunDoesNotUseUpTheLiveSignal()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		Assert.Single(await h.ActedRead(h.Opener(live: false), 0));
		Assert.Single(await h.ActedRead(h.Opener(live: true), 1));
		Assert.Empty(await h.Opener(live: true).EvaluateAsync(Ctx(2), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ReadsOnItsOwnClock()
	{
		using var h = new Harness { Next = Read(CascadeRegime.Neutral) };
		var opener = h.Opener();
		var t0 = Ctx(0);
		await opener.EvaluateAsync(t0, TestContext.Current.CancellationToken);
		await opener.EvaluateAsync(t0 with { Now = t0.Now.AddSeconds(20) }, TestContext.Current.CancellationToken);   // a watch tick inside readSeconds
		Assert.Equal(1, h.Reads);
		await opener.EvaluateAsync(t0 with { Now = t0.Now.AddSeconds(60) }, TestContext.Current.CancellationToken);
		Assert.Equal(2, h.Reads);
	}

	[Fact]
	public async Task SaysWhenTheNextReadIsDue()
	{
		using var h = new Harness { Next = Read(CascadeRegime.Neutral) };
		var opener = h.Opener();
		var t0 = Ctx(0);
		Assert.Null(opener.LastReadAt);
		Assert.Null(opener.SecondsUntilNextRead(t0.Now));
		await opener.EvaluateAsync(t0, TestContext.Current.CancellationToken);
		Assert.Equal(t0.Now, opener.LastReadAt);
		Assert.Equal(40d, opener.SecondsUntilNextRead(t0.Now.AddSeconds(20))!.Value, 3);
		await opener.EvaluateAsync(t0 with { Now = t0.Now.AddSeconds(20) }, TestContext.Current.CancellationToken);   // no read due: the last one stays the last
		Assert.Equal(t0.Now, opener.LastReadAt);
		Assert.Equal(0d, opener.SecondsUntilNextRead(t0.Now.AddSeconds(90))!.Value);
	}

	[Fact]
	public async Task EachOfTheFourSetupsOpensItsOwnConfiguredTrade()
	{
		using var h = new Harness();
		var config = Config();
		config.Opener.Cascade.BullCascade = Setup("ShortPutVertical", "putWall");
		config.Opener.Cascade.BearCascade = Setup("LongPut");
		var expected = new Dictionary<CascadeRegime, OpenStructureKind>
		{
			[CascadeRegime.BullTrap] = OpenStructureKind.ShortCallVertical,
			[CascadeRegime.BearTrap] = OpenStructureKind.LongCall,
			[CascadeRegime.BullCascade] = OpenStructureKind.ShortPutVertical,
			[CascadeRegime.BearCascade] = OpenStructureKind.LongPut,
		};
		var minute = 0;
		foreach (var (regime, kind) in expected)
		{
			h.Next = Read(regime);
			Assert.Equal(kind, Assert.Single(await h.Opener(config: config).EvaluateAsync(Ctx(minute++), TestContext.Current.CancellationToken)).StructureKind);
		}
	}

	[Fact]
	public async Task NeutralAndDisabledSetupsDoNotTrade()
	{
		using var h = new Harness();
		foreach (var regime in new[] { CascadeRegime.Neutral, CascadeRegime.BullCascade, CascadeRegime.BearCascade })   // the cascades are off in this config
		{
			h.Next = Read(regime);
			Assert.Empty(await h.Opener().EvaluateAsync(Ctx(), TestContext.Current.CancellationToken));
		}
		var config = Config();
		config.Opener.Cascade.BullTrap.Enabled = false;
		h.Next = Read(CascadeRegime.BullTrap);
		Assert.Empty(await h.Opener(config: config).EvaluateAsync(Ctx(), TestContext.Current.CancellationToken));
		// A disabled setup's confirmation is not recorded, so enabling it later the same day still trades.
		Assert.Single(await h.Opener().EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task AFailedReadProposesNothingAndIsRetried()
	{
		using var h = new Harness { Next = null };
		var opener = h.Opener();
		Assert.Empty(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		h.Next = Read(CascadeRegime.BullTrap);
		Assert.Single(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task OffByDefault()
	{
		using var h = new Harness { Next = Read(CascadeRegime.BullTrap) };
		var config = Config();
		config.Opener.Cascade = new OpenerCascadeConfig();
		Assert.Empty(await h.Opener(config: config).EvaluateAsync(Ctx(), TestContext.Current.CancellationToken));
		Assert.Equal(0, h.Reads);
	}

	// ── Backtest use ──────────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task ABacktestRunsTheSameOpenerOnAnEtClockWithAnInMemoryLedger()
	{
		// The backtest's contexts are already ET wall time and its ledger lives in memory: the identity clock and a path-less ledger.
		var ledger = new CascadeSignalLedger();
		var read = Read(CascadeRegime.BullTrap);
		var opener = new CascadeOpener(Config(), live: false, ledger, (_, _) => Task.FromResult<(CascadeLiveRead? Read, string? Error)>((read, null)), quiet: true, toEt: et => et);
		var ctx = Ctx() with { Now = Today.AddHours(10).AddMinutes(40) };

		var p = Assert.Single(await opener.EvaluateAsync(ctx, TestContext.Current.CancellationToken));
		opener.Acted(p);
		Assert.Empty(await opener.EvaluateAsync(ctx with { Now = ctx.Now.AddMinutes(30) }, TestContext.Current.CancellationToken));   // once a day, as live

		// The regime's confirmation is recorded first (the neutral setup reads it), then the trade's signal and its placement.
		Assert.Equal([(CascadeSignalLedger.Confirmed, "10:40:00"), (CascadeSignalLedger.Signaled, "10:40:00"), (CascadeSignalLedger.Placed, "10:40:00")], ledger.Entries.Select(e => (e.Outcome, e.TimeEt)));
		Assert.All(ledger.Entries, e => Assert.Equal("2026-11-10", e.Date));

		// The next session starts clean.
		var nextDay = ctx with { Now = ctx.Now.AddDays(1) };
		Assert.Empty(ledger.Find(DateOnly.FromDateTime(nextDay.Now), "SPXW", CascadeRegime.BullTrap, live: false));
	}

	// ── Watch-loop panel ─────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task ALongSingleEntryRecordsItsOrderLineWithTheBracketStopWhenTheBrokerStopIsOn()
	{
		var config = Config();
		config.Rules.TrailingStop.Enabled = true;
		config.AutoExecute.Management.Enabled = true;
		config.AutoExecute.Management.Rules.Add("TrailingStopRule");
		var ledger = new CascadeSignalLedger();
		var read = Read(CascadeRegime.BearTrap);
		var opener = new CascadeOpener(config, live: false, ledger, (_, _) => Task.FromResult<(CascadeLiveRead? Read, string? Error)>((read, null)), quiet: true, toEt: et => et);
		var p = Assert.Single(await opener.EvaluateAsync(Ctx() with { Now = Today.AddHours(10).AddMinutes(40) }, TestContext.Current.CancellationToken));
		opener.Acted(p);

		var entry = ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BearTrap, live: false).Last();
		var line = Assert.Single(entry.Commands!);
		var m = System.Text.RegularExpressions.Regex.Match(line, @"^wa trade place ""buy:(\S+):(\d+)"" --side buy --limit (\d+\.\d\d) --protect (\d+\.\d\d) --submit$");
		Assert.True(m.Success, line);
		Assert.Equal(0.8m * decimal.Parse(m.Groups[3].Value), decimal.Parse(m.Groups[4].Value), 1);   // trailPct under the entry, to the tick

		// Without the broker stop the same entry has no --protect.
		var plain = new CascadeSignalLedger();
		var plainOpener = new CascadeOpener(Config(), live: false, plain, (_, _) => Task.FromResult<(CascadeLiveRead? Read, string? Error)>((read, null)), quiet: true, toEt: et => et);
		var q = Assert.Single(await plainOpener.EvaluateAsync(Ctx() with { Now = Today.AddHours(10).AddMinutes(40) }, TestContext.Current.CancellationToken));
		plainOpener.Acted(q);
		Assert.DoesNotContain("--protect", Assert.Single(plain.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BearTrap, live: false).Last().Commands!));
	}

	[Fact]
	public async Task TheTradeSectionShowsTheTradeItsCommandsAndTheDay()
	{
		// The cascade proposal has no panel of its own: the day's outcomes, the notices, the trade and its three commands are a
		// full-width subpanel at the bottom of the cascade panel.
		var ledger = new CascadeSignalLedger();
		var read = Read(CascadeRegime.BullTrap);
		var opener = new CascadeOpener(Config(), live: false, ledger, (_, _) => Task.FromResult<(CascadeLiveRead? Read, string? Error)>((read, null)), quiet: true, toEt: et => et);
		var p = Assert.Single(await opener.EvaluateAsync(Ctx() with { Now = Today.AddHours(10).AddMinutes(40) }, TestContext.Current.CancellationToken));
		opener.Acted(p);
		var placed = ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BullTrap, live: false).Last();
		var skipped = placed with { Setup = CascadeRegime.BearTrap, TimeEt = "11:02:00", Outcome = CascadeSignalLedger.Skipped, Detail = "credit 0.20 under the 0.50 floor", Structure = null, Qty = null, NetPerShare = null, Commands = null };

		var text = Render(CascadeTradeSection.Build([new CascadeTradeSection.Trade(p, "BULL TRAP 76/100")], ["BEAR TRAP: no trade today — credit 0.20 under the 0.50 floor."], [new("BULL TRAP", placed), new("BEAR TRAP", skipped), new("BULL CASCADE", null)], live: false));

		Assert.Contains("TRADE · TODAY (dry run)", text);
		// One line: the structure, the reading's spot and score, then the order it went out as (or would have, in a dry run).
		Assert.Matches(@"BULL TRAP\s+10:40 logged \(dry run\) · ShortCallVertical · spot \$[\d,.]+ · score \d+ · wa trade place ""sell:" + Occ(7550m) + @":10,buy:" + Occ(7560m) + @":10"" --side sell --limit 0\.90 --submit", text);
		Assert.Matches(@"BEAR TRAP\s+11:02 no trade · credit 0.20 under the 0.50 floor", text);
		Assert.Matches(@"BULL CASCADE\s+—", text);
		Assert.Contains("BEAR TRAP: no trade today — credit 0.20 under the 0.50 floor.", text);
		Assert.Contains($"▶ BULL TRAP 76/100 · ShortCallVertical SPXW x{p.Qty} · credit $", text);
		Assert.Contains("max loss $", text);
		Assert.Contains($"SELL {Occ(7550m)} x{p.Qty}, BUY {Occ(7560m)} x{p.Qty}", text);
		Assert.Contains($"wa trade place \"sell:{Occ(7550m)}:{p.Qty},buy:{Occ(7560m)}:{p.Qty}\" --limit", text);
		Assert.Contains($"wa analyze trade \"sell:{Occ(7550m)}:{p.Qty}@MID,buy:{Occ(7560m)}:{p.Qty}@MID\"", text);
		Assert.Contains($"wa analyze risk \"sell:{Occ(7550m)}:{p.Qty},buy:{Occ(7560m)}:{p.Qty}\"", text);

		// Nothing opening: the day's outcomes alone, under a TODAY title.
		var quiet = Render(CascadeTradeSection.Build([], [], [new("BULL TRAP", placed)], live: true));
		Assert.Contains("TODAY (live)", quiet);
		Assert.DoesNotContain("TRADE", quiet);
		Assert.Matches(@"BULL TRAP\s+10:40 placed · ShortCallVertical · spot", quiet);
		Assert.Contains("wa trade place", quiet);

		// A setup still waiting on its trade says so, with the reason, and is not final.
		var waiting = placed with { Setup = CascadeRegime.BearTrap, TimeEt = "12:26:00", Outcome = CascadeSignalLedger.Waiting, Detail = "put spread mid credit $0.48 is under minCredit $0.50", Structure = null, Qty = null, NetPerShare = null, Commands = null };
		Assert.Matches(@"BEAR TRAP\s+12:26 no trade yet, trying each read · put spread mid credit \$0\.48 is under minCredit \$0\.50 · spot", Render(CascadeTradeSection.Build([], [], [new("BEAR TRAP", waiting)], live: false)));

		// Too narrow for one line: the command moves to a line of its own, whole, rather than wrapping.
		var narrow = Render(CascadeTradeSection.Build([], [], [new("BULL TRAP", placed)], live: false), width: 150);
		Assert.Matches(@"BULL TRAP\s+10:40 logged \(dry run\) · ShortCallVertical · spot \$[\d,.]+ · score \d+\s*│", narrow);
		Assert.Matches($@"│\s+wa trade place ""sell:{Occ(7550m)}:10,buy:{Occ(7560m)}:10"" --side sell --limit 0\.90 --submit\s*│", narrow);
	}

	[Fact]
	public async Task ATradeSizedToZeroIsShownAsNotPlacedAndTheDayRecordsWhy()
	{
		// 2026-11-16 10:02: a LongPut at $1,810 a contract against a $1,802 risk budget. The executor skips a zero-sized proposal, so the
		// panel must not say it is awaiting one, show the unscaled one-lot legs as the size, or offer a one-lot wa trade place.
		var ledger = new CascadeSignalLedger();
		var read = Read(CascadeRegime.BearTrap);
		var opener = new CascadeOpener(Config(), live: false, ledger, (_, _) => Task.FromResult<(CascadeLiveRead? Read, string? Error)>((read, null)), quiet: true, toEt: et => et);
		var p = Assert.Single(await opener.EvaluateAsync(Ctx() with { Now = Today.AddHours(10).AddMinutes(2), AccountValue = 10_000m }, TestContext.Current.CancellationToken));
		Assert.Equal(0, p.Qty);
		var signaled = ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BearTrap, live: false).Single();
		Assert.Equal(CascadeSignalLedger.Signaled, signaled.Outcome);
		Assert.Equal($"BUY {Occ(7505m)} x0", signaled.Detail);
		Assert.Equal(("LongCall", 0), (signaled.Structure, signaled.Qty));
		Assert.Equal(p.DebitOrCreditPerContract / 100m, signaled.NetPerShare);
		Assert.Equal("risk budget $1000 (10% of $10000) below $1520 per contract", signaled.Note);

		var retrying = Render(CascadeTradeSection.Build([new CascadeTradeSection.Trade(p, "BEAR TRAP 76/100")], [], [new("BEAR TRAP", signaled, new TimeSpan(10, 5, 0))], live: false));
		Assert.Contains("TODAY (dry run)", retrying);
		Assert.DoesNotContain("TRADE", retrying);
		Assert.DoesNotContain("awaiting the executor", retrying);
		// One line: status, trade at its size, price, why it was sized to 0, the reading.
		Assert.Matches(@"│ BEAR TRAP\s+10:02 confirmed, retrying until 10:05 · LongCall BUY " + Occ(7505m) + @" x0 · debit \$15\.20 · risk budget \$1000 \(10% of \$10000\) below \$1520 per contract · spot \$7,500\.50 · score 76\s*│", retrying);
		Assert.Contains("▷ BEAR TRAP 76/100 · LongCall SPXW · debit $", retrying);
		Assert.Contains($"per contract: BUY {Occ(7505m)}", retrying);
		Assert.Contains("not placed: sized to 0 contracts — risk budget $1000", retrying);
		Assert.DoesNotContain("wa trade place", retrying);

		var expired = Render(CascadeTradeSection.Build([], [], [new("BEAR TRAP", signaled)], live: false));
		Assert.Matches(@"BEAR TRAP\s+10:02 confirmed, never placed · LongCall BUY " + Occ(7505m) + " x0", expired);

		// An entry written before the trade fields were recorded: its legs, then the reading.
		var legacy = Render(CascadeTradeSection.Build([], [], [new("BEAR TRAP", signaled with { Detail = $"BUY {Occ(7505m)} x1", Structure = null, Qty = null, NetPerShare = null, Note = null, Commands = null })], live: false));
		Assert.Matches(@"BEAR TRAP\s+10:02 confirmed, never placed · BUY " + Occ(7505m) + @" x1 · spot \$7,500\.50 · score 76", legacy);
	}

	private static string Render(Spectre.Console.Rendering.IRenderable panel, int width = 240)
	{
		var output = new StringWriter();
		var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings { Out = new Spectre.Console.AnsiConsoleOutput(output), Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors });
		console.Profile.Width = width;
		console.Write(panel);
		return output.ToString();
	}

	[Fact]
	public void SessionAnchors_SellTheFirstStrikeBeyondTheDaysExtreme()
	{
		// High 7551.20 → first call strike at or above it, 7555; low 7448.90 → first put strike at or below it, 7445.
		var tape = new WebullAnalytics.AI.Open.ZeroDte.SessionTape(7520m, 7510m, Spot, 7505m, 0.1m, WebullAnalytics.AI.Open.ZeroDte.GapDirection.Flat, WebullAnalytics.AI.Open.ZeroDte.VwapSide.Below, 5, 2, 7551.20m, 7448.90m, 7551.20m, 7448.90m, 1, 1, 0, 20, 60, 60);
		Assert.Equal((7555m, 7575m, null), CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("ShortCallVertical", "sessionHigh", widthSteps: 4), tape));
		Assert.Equal((7440m, 7420m, null), CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("ShortPutVertical", "sessionLow", offsetSteps: 1, widthSteps: 4), tape));
		// Exactly on a strike takes that strike; no tape, no trade.
		Assert.Equal(7550m, CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("ShortCallVertical", "sessionHigh"), tape with { SessionHigh = 7550m }).First);
		Assert.Contains("no strike at the session high", CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("ShortCallVertical", "sessionHigh")).Skip);
		// A session anchor takes a structure on its own side.
		Assert.Contains("opener.cascade.bullTrap.anchor", Validate("BullTrap", Setup("ShortPutVertical", "sessionHigh")) ?? "");
		Assert.Null(Validate("BearTrap", Setup("ShortPutVertical", "sessionLow")));
	}

	[Fact]
	public void TradeStrikes_AreTheLegsBuildTradeLooksUp()
	{
		Assert.Equal((7550m, 7560m, null), CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("ShortCallVertical", "callWallAtHigh")));
		Assert.Equal((7505m, null, null), CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("LongCall")));
		Assert.Equal((7450m, 7440m, null), CascadeOpener.TradeStrikes(Spot, Terrain, 5m, Setup("ShortPutVertical", "putWall")));
		Assert.Contains("no call wall at the session high", CascadeOpener.TradeStrikes(Spot, Terrain with { CallWallAtHigh0Dte = null }, 5m, Setup("ShortCallVertical", "callWallAtHigh")).Skip);
	}

	// ── Entry conditions ──────────────────────────────────────────────────────────────────────────────────

	private static CascadeSetupConfig With(CascadeSetupConfig setup, Action<CascadeSetupConfig> set)
	{
		set(setup);
		return setup;
	}

	private static CascadeLiveRead ReadWithVix(CascadeRegime regime, decimal? vixNow, decimal vixPriorClose = 20m, int score = 76)
	{
		var r = Read(regime);
		var lead = r.Reading.LeadingSetup with { Score = score };
		var inputs = r.Reading.Inputs with { VixPriorClose = vixPriorClose, VixNow = vixNow };
		return r with { Reading = r.Reading with { LeadingSetup = lead, Inputs = inputs } };
	}

	[Fact]
	public async Task AVixGateHoldsTheSetupUntilAReadingPassesIt()
	{
		// Bear Cascade only once VIX is up at least 8% on the day: 20 → 21 (+5%) is held back, 20 → 22 (+10%) trades.
		using var h = new Harness();
		var config = Config();
		config.Opener.Cascade.BearCascade = With(Setup("LongPut"), s => { s.MinVixChange = 0.08m; });
		var opener = h.Opener(config: config);

		h.Next = ReadWithVix(CascadeRegime.BearCascade, vixNow: 21m);
		Assert.Empty(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		Assert.Contains("VIX change +5.0% < minVixChange +8.0%", opener.GatedReadings.Single().Reason, StringComparison.Ordinal);
		Assert.Empty(h.Ledger.Find(DateOnly.FromDateTime(Today), "SPXW", CascadeRegime.BearCascade, live: false));   // not an entry: nothing used up

		h.Next = ReadWithVix(CascadeRegime.BearCascade, vixNow: 22m);
		Assert.Equal(OpenStructureKind.LongPut, Assert.Single(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken)).StructureKind);
	}

	[Fact]
	public void EntryConditions_EachBoundIsCheckedAndOffByDefault()
	{
		var reading = ReadWithVix(CascadeRegime.BullCascade, vixNow: 18m, vixPriorClose: 20m, score: 70).Reading;   // VIX 18, −10%, score 70
		Assert.Null(new CascadeSetupConfig().EntryConditionFailure(reading));
		Assert.Null(new CascadeSetupConfig { MaxVixChange = -0.05m, MinVix = 15m, MaxVix = 25m, MinScore = 70 }.EntryConditionFailure(reading));
		Assert.Contains("minScore", new CascadeSetupConfig { MinScore = 75 }.EntryConditionFailure(reading));
		Assert.Contains("minVix", new CascadeSetupConfig { MinVix = 19m }.EntryConditionFailure(reading));
		Assert.Contains("maxVix", new CascadeSetupConfig { MaxVix = 17m }.EntryConditionFailure(reading));
		Assert.Contains("maxVixChange", new CascadeSetupConfig { MaxVixChange = -0.12m }.EntryConditionFailure(reading));
		Assert.Contains("minVixChange", new CascadeSetupConfig { MinVixChange = 0m }.EntryConditionFailure(reading));

		var noVix = ReadWithVix(CascadeRegime.BullCascade, vixNow: null).Reading;
		Assert.Contains("no intraday VIX", new CascadeSetupConfig { MinVix = 10m }.EntryConditionFailure(noVix));
		Assert.Null(new CascadeSetupConfig { MinScore = 50 }.EntryConditionFailure(noVix));   // VIX is only needed by a VIX bound
	}

	[Fact]
	public void EntryConditionBandsMustBeOrdered()
	{
		Assert.Contains("minVix", Validate("BullCascade", With(Setup("LongCall"), s => { s.MinVix = 30m; s.MaxVix = 20m; })) ?? "");
		Assert.Contains("minVixChange", Validate("BearCascade", With(Setup("LongPut"), s => { s.MinVixChange = 0.1m; s.MaxVixChange = 0.05m; })) ?? "");
		Assert.Contains("minScore", Validate("BearTrap", With(Setup("LongCall"), s => { s.MinScore = 150; })) ?? "");
		Assert.Contains("fraction", Validate("BearCascade", With(Setup("LongPut"), s => { s.MinVixChange = 8m; })) ?? "");   // 8 = +800%: a percent, not a fraction
	}

	// ── Neutral setup ─────────────────────────────────────────────────────────────────────────────────────

	/// <summary>The trap setups as in <see cref="Config"/>, plus the neutral setup from 11:00 (Ctx minute 15): a call spread at the call wall at
	/// the high (7550/7560) and a put spread at the put wall at the low (7450/7440).</summary>
	private static AIConfig NeutralConfig()
	{
		var config = Config();
		config.Opener.Cascade.Neutral = new CascadeNeutralConfig
		{
			Enabled = true,
			AfterTimeEt = "11:00",
			Call = Setup("ShortCallVertical", "callWallAtHigh", minCredit: 0.10m),
			Put = Setup("ShortPutVertical", "putWallAtLow", minCredit: 0.10m),
		};
		return config;
	}

	private static CascadeLiveRead NeutralRead() => Read(CascadeRegime.Neutral) with { Reading = Read(CascadeRegime.Neutral).Reading with { Terrain = Terrain with { PutWallAtLow0Dte = 7450m } } };

	[Fact]
	public async Task OnADayNothingConfirms_TheNeutralSetupSellsBothSidesFromItsTime()
	{
		using var h = new Harness { Next = NeutralRead() };
		var opener = h.Opener(config: NeutralConfig());
		Assert.Empty(await opener.EvaluateAsync(Ctx(14), TestContext.Current.CancellationToken));   // 10:59: before its time
		var proposals = await opener.EvaluateAsync(Ctx(15), TestContext.Current.CancellationToken);
		Assert.Equal([OpenStructureKind.ShortCallVertical, OpenStructureKind.ShortPutVertical], proposals.Select(p => p.StructureKind));
		Assert.Equal([("sell", Occ(7550m)), ("buy", Occ(7560m))], proposals[0].Legs.Select(l => (l.Action, l.Symbol)));
		Assert.Equal([("sell", Occ(7450m, right: "P")), ("buy", Occ(7440m, right: "P"))], proposals[1].Legs.Select(l => (l.Action, l.Symbol)));
		Assert.StartsWith("cascade NEUTRAL call", proposals[0].Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public async Task EachNeutralSideTradesOnceADay_AndAHeldBackSideIsRetried()
	{
		using var h = new Harness { Next = NeutralRead() };
		var opener = h.Opener(config: NeutralConfig());
		var first = await opener.EvaluateAsync(Ctx(15), TestContext.Current.CancellationToken);
		opener.Acted(first[0]);                                       // the call side placed, the put side held back
		var retry = Assert.Single(await opener.EvaluateAsync(Ctx(16), TestContext.Current.CancellationToken));
		Assert.Equal(OpenStructureKind.ShortPutVertical, retry.StructureKind);
		opener.Acted(retry);
		Assert.Empty(await opener.EvaluateAsync(Ctx(17), TestContext.Current.CancellationToken));
		Assert.Empty(await h.Opener(config: NeutralConfig()).EvaluateAsync(Ctx(18), TestContext.Current.CancellationToken));   // and after a restart
	}

	[Fact]
	public async Task AnyConfirmationEarlierInTheDayRulesOutTheNeutralSetup()
	{
		// Bull Cascade is off in this config, but its confirmation at 10:45 still means the day is not neutral — also after a restart.
		using var h = new Harness { Next = Read(CascadeRegime.BullCascade) };
		Assert.Empty(await h.Opener(config: NeutralConfig()).EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		h.Next = NeutralRead();
		Assert.Empty(await h.Opener(config: NeutralConfig()).EvaluateAsync(Ctx(20), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task TheNeutralSetupIsOffByDefaultAndItsSidesCanBeSwitchedOffApart()
	{
		using var h = new Harness { Next = NeutralRead() };
		Assert.Empty(await h.Opener().EvaluateAsync(Ctx(20), TestContext.Current.CancellationToken));
		var config = NeutralConfig();
		config.Opener.Cascade.Neutral.Call.Enabled = false;
		Assert.Equal(OpenStructureKind.ShortPutVertical, Assert.Single(await h.Opener(config: config).EvaluateAsync(Ctx(21), TestContext.Current.CancellationToken)).StructureKind);
	}

	[Fact]
	public void NeutralConfigIsValidatedWhenEnabled()
	{
		var config = NeutralConfig();
		config.Opener.Cascade.Neutral.AfterTimeEt = "noon";
		Assert.Contains("opener.cascade.neutral.afterTimeEt", AIConfigLoader.Validate(config) ?? "");
		config = NeutralConfig();
		config.Opener.Cascade.Neutral.Put = Setup("ShortPutVertical", "callWallAtHigh");
		Assert.Contains("opener.cascade.neutral.put.anchor", AIConfigLoader.Validate(config) ?? "");
		config.Opener.Cascade.Neutral.Enabled = false;
		Assert.Null(AIConfigLoader.Validate(config));   // a disabled neutral setup is not validated
	}

	// ── Lean entries ──────────────────────────────────────────────────────────────────────────────────────

	/// <summary>A reading that did not confirm and leans to <paramref name="lead"/> with <paramref name="score"/>.</summary>
	private static CascadeLiveRead Lean(CascadeRegime lead, int score)
	{
		var r = Read(lead);
		var setup = r.Reading.LeadingSetup with { Score = score, Complete = false };
		return r with { Reading = r.Reading with { PrimaryRegime = CascadeRegime.Neutral, LeadingSetup = setup, Setups = new Dictionary<CascadeRegime, CascadeSetupEvaluation> { [lead] = setup } } };
	}

	private static AIConfig LeanConfig(int minLean)
	{
		var config = Config();
		config.Opener.Cascade.BullTrap.MinLeanScore = minLean;
		return config;
	}

	[Fact]
	public async Task ALeanStrongEnoughTradesTheSetup_AWeakerOneDoesNot()
	{
		using var h = new Harness { Next = Lean(CascadeRegime.BullTrap, 54) };
		var opener = h.Opener(config: LeanConfig(55));
		Assert.Empty(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));
		h.Next = Lean(CascadeRegime.BullTrap, 58);
		var p = Assert.Single(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
		Assert.Equal(OpenStructureKind.ShortCallVertical, p.StructureKind);
		Assert.StartsWith("cascade BULL TRAP (lean 58)", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LeansDoNotTradeUnlessTheBlockAllowsThem()
	{
		using var h = new Harness { Next = Lean(CascadeRegime.BullTrap, 64) };
		Assert.Empty(await h.Opener().EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken));                 // minLeanScore 0
		h.Next = Lean(CascadeRegime.BullCascade, 64);
		Assert.Empty(await h.Opener(config: LeanConfig(50)).EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));   // leans to a setup that is off
	}

	[Fact]
	public async Task ALeanEntryIsTheSetupsOneEntryOfTheDay()
	{
		using var h = new Harness { Next = Lean(CascadeRegime.BullTrap, 60) };
		var opener = h.Opener(config: LeanConfig(55));
		opener.Acted(Assert.Single(await opener.EvaluateAsync(Ctx(0), TestContext.Current.CancellationToken)));
		h.Next = Read(CascadeRegime.BullTrap);                                       // confirms later the same day
		Assert.Empty(await opener.EvaluateAsync(Ctx(1), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ALeanIsNotAConfirmation_TheNeutralSetupStillTrades()
	{
		var config = NeutralConfig();
		config.Opener.Cascade.BullTrap.MinLeanScore = 55;
		using var h = new Harness { Next = Lean(CascadeRegime.BullTrap, 60) with { Reading = Lean(CascadeRegime.BullTrap, 60).Reading with { Terrain = Terrain with { PutWallAtLow0Dte = 7450m } } } };
		var proposals = await h.Opener(config: config).EvaluateAsync(Ctx(15), TestContext.Current.CancellationToken);
		Assert.Equal(3, proposals.Count);   // the lean's call spread, and both neutral sides
		Assert.DoesNotContain(h.Ledger.Entries, e => e.Outcome == CascadeSignalLedger.Confirmed);
	}

	// ── Config validation ─────────────────────────────────────────────────────────────────────────────────

	private static string? Validate(string setupName, CascadeSetupConfig setup)
	{
		var config = Config();
		config.Opener.Cascade = new OpenerCascadeConfig { Enabled = true };
		typeof(OpenerCascadeConfig).GetProperty(setupName)!.SetValue(config.Opener.Cascade, setup);
		return AIConfigLoader.Validate(config);
	}

	[Fact]
	public void AnEnabledSetupMustNameASupportedStructureAndAnchor()
	{
		Assert.Contains("opener.cascade.bearCascade.structure", Validate("BearCascade", new CascadeSetupConfig { Enabled = true }) ?? "");
		Assert.Contains("opener.cascade.bullCascade.structure", Validate("BullCascade", Setup("IronCondor")) ?? "");
		Assert.Contains("opener.cascade.bearTrap.anchor", Validate("BearTrap", Setup("LongCall", anchor: "gravity")) ?? "");
		Assert.Contains("opener.cascade.bullTrap.widthSteps", Validate("BullTrap", Setup("ShortCallVertical", widthSteps: 0)) ?? "");
		Assert.Null(Validate("BullTrap", Setup("shortcallvertical", "CALLWALLATHIGH")));   // names are case-insensitive
		Assert.Null(Validate("BearTrap", Setup("LongCall", widthSteps: 0)));                // width is a vertical's
	}

	[Fact]
	public void AWallAnchorTakesAStructureOnItsOwnSide()
	{
		Assert.Contains("opener.cascade.bullCascade.anchor", Validate("BullCascade", Setup("ShortPutVertical", "callWall")) ?? "");
		Assert.Contains("opener.cascade.bearCascade.anchor", Validate("BearCascade", Setup("LongCall", "putWall")) ?? "");
		Assert.Contains("opener.cascade.bearCascade.anchor", Validate("BearCascade", Setup("LongCall", "putWallAtLow")) ?? "");
		Assert.Contains("opener.cascade.bearCascade.anchor", Validate("BearCascade", Setup("LongPutVertical", "callWallAtHigh")) ?? "");
		Assert.Null(Validate("BullCascade", Setup("ShortPutVertical", "putWall")));
		Assert.Null(Validate("BullCascade", Setup("ShortPutVertical", "putWallAtLow")));
	}

	[Fact]
	public void ADisabledSetupIsNotValidated()
	{
		Assert.Null(Validate("BullTrap", new CascadeSetupConfig { Enabled = false, Structure = "nonsense", Anchor = "nowhere" }));
	}
}

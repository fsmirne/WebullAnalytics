using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// rules.trailingStop: a position is closed once it gives back trailPct of what it had at risk at its best level since entry. It is
// valued at what closing it fetches now (long legs at the bid, short legs at the ask); the peak starts at the first such value seen.
public class TrailingStopRuleTests
{
	private const string Call = "SPXW261112C07600000";
	private const string Wing = "SPXW261112C07610000";
	private static readonly DateTime Expiry = new(2026, 11, 12);
	private static readonly DateTime T0 = new(2026, 11, 12, 10, 0, 0);

	private static OpenPosition LongCall(decimal entry = 15.00m) => new(
		Key: "SPXW_LongCall_7600.00_20261112",
		Ticker: "SPXW",
		StrategyKind: "LongCall",
		Legs: new[] { new PositionLeg(Call, Side.Buy, 7600m, Expiry, "C", 3) },
		InitialNetDebit: entry,
		AdjustedNetDebit: entry,
		Quantity: 3);

	/// <summary>Long the 7600 call, short the 7610 call, bought for <paramref name="debit"/>.</summary>
	private static OpenPosition DebitVertical(decimal debit = 4.00m) => new(
		Key: "SPXW_LongCallVertical_7610.00_20261112",
		Ticker: "SPXW",
		StrategyKind: "LongCallVertical",
		Legs: new[] { new PositionLeg(Call, Side.Buy, 7600m, Expiry, "C", 2), new PositionLeg(Wing, Side.Sell, 7610m, Expiry, "C", 2) },
		InitialNetDebit: debit,
		AdjustedNetDebit: debit,
		Quantity: 2);

	/// <summary>Short the 7600 call, long the 7610 call ($10 wide), sold for <paramref name="credit"/>.</summary>
	private static OpenPosition CreditVertical(decimal credit = 0.90m) => new(
		Key: "SPXW_ShortCallVertical_7600.00_20261112",
		Ticker: "SPXW",
		StrategyKind: "ShortCallVertical",
		Legs: new[] { new PositionLeg(Call, Side.Sell, 7600m, Expiry, "C", 1), new PositionLeg(Wing, Side.Buy, 7610m, Expiry, "C", 1) },
		InitialNetDebit: -credit,
		AdjustedNetDebit: -credit,
		Quantity: 1);

	private static OptionContractQuote Q(string symbol, decimal bid, decimal ask) => new(symbol, null, bid, ask, null, null, 100, 1000, 0.20m);

	private static EvaluationContext Ctx(OpenPosition p, int minute, params OptionContractQuote[] quotes) => new(
		Now: T0.AddMinutes(minute),
		OpenPositions: new Dictionary<string, OpenPosition> { [p.Key] = p },
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPXW"] = 7600m },
		Quotes: quotes.ToDictionary(q => q.ContractSymbol),
		AccountCash: 0m, AccountValue: 0m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>());

	/// <summary>A long-call tick: the 7600 call quoted <paramref name="bid"/> / bid + 0.20.</summary>
	private static EvaluationContext Tick(OpenPosition p, decimal bid, int minute = 0) => Ctx(p, minute, Q(Call, bid, bid + 0.20m));

	private static TrailingStopRule Rule(decimal trailPct = 0.20m, TrailingStopStore? store = null, params string[] exclude) =>
		new(new TrailingStopConfig { Enabled = true, TrailPct = trailPct, Exclude = exclude.ToList() }, store ?? new TrailingStopStore());

	private static TrailingStopRule ArmedAt(decimal activateAtProfitPct, decimal trailPct = 0.20m, TrailingStopStore? store = null) =>
		new(new TrailingStopConfig { Enabled = true, TrailPct = trailPct, ActivateAtProfitPct = activateAtProfitPct }, store ?? new TrailingStopStore());

	// ── Activation threshold ──────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void BelowTheThreshold_NothingGuardsThePosition_HoweverFarItFalls()
	{
		// "If it opens, it goes all the way" until it has made +50%: a $10 call that only ever falls is never stopped.
		var rule = ArmedAt(0.50m);
		var p = LongCall(entry: 10.00m);
		Assert.Null(rule.Evaluate(p, Tick(p, 14.90m, 0)));   // +49%: not armed
		Assert.Null(rule.Evaluate(p, Tick(p, 2.00m, 1)));    // −80% from its peak, still not armed
	}

	[Fact]
	public void OnceArmed_ItTrailsFromThePeak_AndStaysArmed()
	{
		var rule = ArmedAt(0.50m);
		var p = LongCall(entry: 10.00m);
		Assert.Null(rule.Evaluate(p, Tick(p, 15.00m, 0)));   // +50% at the bid: armed, stop $12.00
		Assert.Null(rule.Evaluate(p, Tick(p, 20.00m, 1)));   // peak $20, stop $16.00
		Assert.Null(rule.Evaluate(p, Tick(p, 16.10m, 2)));
		var close = rule.Evaluate(p, Tick(p, 16.00m, 3));     // the trail, from the peak that armed it and rose since
		Assert.NotNull(close);
		Assert.Contains("peak $20.00", close!.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void TheThresholdIsReadAtTheBid_NotTheMid()
	{
		// A +50% the market will not pay does not arm it: a 14.80 / 15.40 market is +51% at the mid but +48% at the bid.
		var rule = ArmedAt(0.50m);
		var p = LongCall(entry: 10.00m);
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 14.80m, 15.40m))));
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(Call, 1.00m, 1.20m))));   // so this collapse is not stopped
	}

	[Theory]
	[InlineData(10.00, 15.00, true)]     // debit: +50%
	[InlineData(10.00, 14.99, false)]
	[InlineData(-2.00, -1.00, true)]     // credit $2.00: closing for $1.00 keeps half of it
	[InlineData(-2.00, -1.01, false)]
	public void IsArmed_OneFormulaForDebitsAndCredits(double entry, double peak, bool armed) =>
		Assert.Equal(armed, TrailingStopRule.IsArmed(new TrailingStopConfig { ActivateAtProfitPct = 0.50m }, (decimal)entry, (decimal)peak));

	[Fact]
	public void WithoutAThreshold_ItIsArmedFromEntry() =>
		Assert.True(TrailingStopRule.IsArmed(new TrailingStopConfig(), 10.00m, 1.00m));

	// ── Long option ───────────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void HoldsWhileTheBidIsWithinTheTrailOfItsPeak()
	{
		var rule = Rule();
		var p = LongCall();
		Assert.Null(rule.Evaluate(p, Tick(p, 20.00m, 0)));   // peak 20.00
		Assert.Null(rule.Evaluate(p, Tick(p, 16.10m, 1)));   // 16.10 > 16.00 = 20 × 0.8
	}

	[Fact]
	public void ClosesOnceTheBidFallsTheTrailFromThePeak()
	{
		var rule = Rule();
		var p = LongCall();
		Assert.Null(rule.Evaluate(p, Tick(p, 20.00m, 0)));
		var close = rule.Evaluate(p, Tick(p, 16.00m, 1));     // 20% off the peak
		Assert.NotNull(close);
		Assert.Equal(ProposalKind.Close, close!.Kind);
		Assert.Equal("TrailingStopRule", close.Rule);
		Assert.Contains("peak $20.00", close.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void TheCloseIsAMarketableFullCloseAtTheBid()
	{
		var rule = Rule();
		var p = LongCall();
		rule.Evaluate(p, Tick(p, 20.00m, 0));
		var close = rule.Evaluate(p, Tick(p, 15.00m, 1))!;
		Assert.True(close.Marketable);
		Assert.StartsWith("[emergency]", close.Rationale, StringComparison.Ordinal);   // the executor closes the whole position now, outside the scale-out schedule
		var leg = Assert.Single(close.Legs);
		Assert.Equal(("sell", Call, 3, 15.00m), (leg.Action, leg.Symbol, leg.Qty, leg.ExecutionPricePerShare!.Value));
	}

	[Fact]
	public void ThePeakStartsAtTheFirstClosingValueSeen_NotTheFillPrice()
	{
		// Bought at a $1.00 mid on a 0.80 / 1.20 market. Measured against the fill, the $0.80 bid is already 20% down and the stop
		// would fire on the spread alone. Measured against the first bid seen, nothing has happened until the bid itself falls 20%.
		var rule = Rule();
		var p = LongCall(entry: 1.00m);
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 0.80m, 1.20m))));
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(Call, 0.65m, 1.05m))));   // 18.75% under the 0.80 peak
		Assert.NotNull(rule.Evaluate(p, Ctx(p, 2, Q(Call, 0.64m, 1.04m))));   // 20%
	}

	[Fact]
	public void APositionThatNeverRalliesIsStoppedTheTrailBelowWhereItStarted()
	{
		var rule = Rule();
		var p = LongCall(entry: 15.00m);
		Assert.Null(rule.Evaluate(p, Tick(p, 14.80m, 0)));     // first closing value: the peak
		Assert.Null(rule.Evaluate(p, Tick(p, 11.90m, 1)));     // 19.6% under it
		Assert.NotNull(rule.Evaluate(p, Tick(p, 11.84m, 2)));  // 20%
	}

	[Fact]
	public void TrailPctIsConfigurable()
	{
		var p = LongCall();
		var tight = Rule(trailPct: 0.10m);
		tight.Evaluate(p, Tick(p, 20.00m, 0));
		Assert.NotNull(tight.Evaluate(p, Tick(p, 18.00m, 1)));   // 10% off 20.00
		var loose = Rule(trailPct: 0.40m);
		loose.Evaluate(p, Tick(p, 20.00m, 0));
		Assert.Null(loose.Evaluate(p, Tick(p, 14.00m, 1)));      // 30% off: inside a 40% trail
	}

	// ── Bad quotes ────────────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void AnAskSpikeDoesNotRaiseThePeak()
	{
		// 10.00 / 22.00 has a $16.00 mid. Marked at the mid, that tick would set a $16.00 peak and the next normal quote would read
		// as a 38% collapse. Marked at the bid, nothing happened.
		var rule = Rule();
		var p = LongCall(entry: 10.00m);
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 10.00m, 10.40m))));
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(Call, 10.00m, 22.00m))));
		Assert.Null(rule.Evaluate(p, Ctx(p, 2, Q(Call, 9.90m, 10.30m))));
	}

	[Fact]
	public void ACrossedQuoteIsNotEvaluated()
	{
		var rule = Rule();
		var p = LongCall(entry: 10.00m);
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 30.00m, 20.00m))));   // bid above ask: torn, and its $30 bid must not become the peak
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(Call, 9.90m, 10.30m))));
	}

	[Fact]
	public void NoQuoteDoesNotFire()
	{
		var rule = Rule();
		var p = LongCall();
		Assert.Null(rule.Evaluate(p, Ctx(p, 0)));
	}

	// ── Spreads ───────────────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void ADebitVerticalTrailsItsClosingValue()
	{
		var rule = Rule();
		var p = DebitVertical(debit: 4.00m);
		// Closing value = long bid − short ask: 7.00 − 2.00 = 5.00 (peak), then 5.50 − 1.50 = 4.00, 20% below it.
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 7.00m, 7.20m), Q(Wing, 1.80m, 2.00m))));
		var close = rule.Evaluate(p, Ctx(p, 1, Q(Call, 5.50m, 5.70m), Q(Wing, 1.30m, 1.50m)));
		Assert.NotNull(close);
		Assert.True(close!.Marketable);
		Assert.Equal([("sell", Call, 2, 5.50m), ("buy", Wing, 2, 1.50m)], close.Legs.Select(l => (l.Action, l.Symbol, l.Qty, l.ExecutionPricePerShare!.Value)));
	}

	[Fact]
	public void ACreditSpreadIsMeasuredAgainstTheWidthAtRisk_NotTheCredit()
	{
		var rule = Rule();
		var p = CreditVertical(credit: 0.90m);   // $10 wide: $9.10 at risk at entry
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 0.85m, 0.95m), Q(Wing, 0.00m, 0.10m))));
		// $0.23 against it is 26% of the credit but 2.5% of what is at risk: quote noise, not a stop.
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(Call, 1.03m, 1.13m), Q(Wing, 0.00m, 0.10m))));
		// Decays to $0.30 to close: the peak is −0.30 and $9.70 of width is still at risk, so the stop sits $1.94 away at −2.24.
		Assert.Null(rule.Evaluate(p, Ctx(p, 2, Q(Call, 0.25m, 0.30m), Q(Wing, 0.00m, 0.05m))));
		Assert.Null(rule.Evaluate(p, Ctx(p, 3, Q(Call, 2.10m, 2.20m), Q(Wing, 0.05m, 0.15m))));   // −2.15
		var close = rule.Evaluate(p, Ctx(p, 4, Q(Call, 2.20m, 2.30m), Q(Wing, 0.05m, 0.15m)));     // −2.25
		Assert.NotNull(close);
		Assert.Contains("of the $9.70 at risk from its peak $-0.30", close!.Rationale, StringComparison.Ordinal);
		Assert.Equal([("buy", Call, 2.30m), ("sell", Wing, 0.05m)], close.Legs.Select(l => (l.Action, l.Symbol, l.ExecutionPricePerShare!.Value)));
	}

	[Fact]
	public void AStructureWithNoDefinedMaxLossIsNotTrailed()
	{
		var rule = Rule();
		var nakedShort = LongCall() with { StrategyKind = "ShortCall", Legs = new[] { new PositionLeg(Call, Side.Sell, 7600m, Expiry, "C", 1) }, InitialNetDebit = -2.00m, AdjustedNetDebit = -2.00m };
		Assert.Null(rule.Evaluate(nakedShort, Tick(nakedShort, 50.00m)));
	}

	[Fact]
	public void ACalendarTrailsItsClosingValue()
	{
		// Short the expiring 7600 call, long next week's: a multi-expiry debit structure, floor $0 like any debit.
		const string far = "SPXW261119C07600000";
		var p = new OpenPosition("SPXW_LongCalendar_7600.00_20261112", "SPXW", "LongCalendar",
			new[] { new PositionLeg(Call, Side.Sell, 7600m, Expiry, "C", 5), new PositionLeg(far, Side.Buy, 7600m, Expiry.AddDays(7), "C", 5) },
			InitialNetDebit: 20.00m, AdjustedNetDebit: 20.00m, Quantity: 5);
		var rule = Rule();
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(Call, 9.80m, 10.00m), Q(far, 35.00m, 35.40m))));   // 35.00 − 10.00 = 25.00 (peak)
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(Call, 9.80m, 10.00m), Q(far, 30.10m, 30.50m))));   // 20.10: 19.6% off
		var close = rule.Evaluate(p, Ctx(p, 2, Q(Call, 9.80m, 10.00m), Q(far, 30.00m, 30.40m)));     // 20.00: 20% off
		Assert.NotNull(close);
		Assert.Equal([("buy", Call, 10.00m), ("sell", far, 30.00m)], close!.Legs.Select(l => (l.Action, l.Symbol, l.ExecutionPricePerShare!.Value)));
	}

	[Fact]
	public void AnIronCondorIsMeasuredAgainstItsWiderWing()
	{
		// Short 7550 put / 7650 call, long 7540 put / 7660 call, $10 wings, $2.00 credit: the floor is −$10.
		const string sp = "SPXW261112P07550000", lp = "SPXW261112P07540000", sc = "SPXW261112C07650000", lc = "SPXW261112C07660000";
		var p = new OpenPosition("SPXW_IronCondor_7550.00_20261112", "SPXW", "IronCondor",
			new[] { new PositionLeg(sp, Side.Sell, 7550m, Expiry, "P", 1), new PositionLeg(lp, Side.Buy, 7540m, Expiry, "P", 1), new PositionLeg(sc, Side.Sell, 7650m, Expiry, "C", 1), new PositionLeg(lc, Side.Buy, 7660m, Expiry, "C", 1) },
			InitialNetDebit: -2.00m, AdjustedNetDebit: -2.00m, Quantity: 1);
		var rule = Rule();
		// Closing value = long bids − short asks. −2.10 when first seen (the peak): $7.90 at risk, stop at −2.10 − 20% × 7.90 = −3.68.
		Assert.Null(rule.Evaluate(p, Ctx(p, 0, Q(sp, 1.00m, 1.10m), Q(lp, 0.50m, 0.60m), Q(sc, 1.90m, 2.00m), Q(lc, 0.50m, 0.60m))));
		Assert.Null(rule.Evaluate(p, Ctx(p, 1, Q(sp, 0.40m, 0.50m), Q(lp, 0.10m, 0.20m), Q(sc, 3.80m, 3.90m), Q(lc, 0.80m, 0.90m))));   // −3.50
		var close = rule.Evaluate(p, Ctx(p, 2, Q(sp, 0.40m, 0.50m), Q(lp, 0.10m, 0.20m), Q(sc, 3.98m, 4.08m), Q(lc, 0.80m, 0.90m)));     // −3.68
		Assert.NotNull(close);
		Assert.Equal(4, close!.Legs.Count);
		Assert.Contains("of the $7.90 at risk", close.Rationale, StringComparison.Ordinal);
	}

	// ── Scope ─────────────────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void EveryPositionIsTrailedUnlessItsStructureIsExcluded()
	{
		var spread = CreditVertical(credit: 0.90m);
		var calm = Ctx(spread, 0, Q(Call, 0.85m, 0.95m), Q(Wing, 0.00m, 0.10m));
		var blownOut = Ctx(spread, 1, Q(Call, 5.00m, 5.20m), Q(Wing, 0.50m, 0.60m));

		var all = Rule();
		Assert.Null(all.Evaluate(spread, calm));
		Assert.NotNull(all.Evaluate(spread, blownOut));

		var excluding = Rule(exclude: "shortcallvertical");   // names are case-insensitive
		Assert.Null(excluding.Evaluate(spread, calm));
		Assert.Null(excluding.Evaluate(spread, blownOut));

		// Excluding the spread leaves every other structure trailed.
		var call = LongCall(entry: 15.00m);
		Assert.Null(excluding.Evaluate(call, Tick(call, 15.00m, 0)));
		Assert.NotNull(excluding.Evaluate(call, Tick(call, 11.00m, 1)));
	}

	[Fact]
	public void DisabledNeverFires()
	{
		var rule = new TrailingStopRule(new TrailingStopConfig { Enabled = false, TrailPct = 0.20m }, new TrailingStopStore());
		var p = LongCall();
		Assert.Null(rule.Evaluate(p, Tick(p, 0.05m)));
	}

	// ── Peak bookkeeping ──────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void ARestartedLoopTrailsFromThePersistedPeak()
	{
		var path = Path.Combine(Path.GetTempPath(), $"trail-{Guid.NewGuid():N}.json");
		try
		{
			var p = LongCall();
			Rule(store: new TrailingStopStore(path)).Evaluate(p, Tick(p, 25.00m, 0));              // peak 25.00, then the process ends
			var close = Rule(store: new TrailingStopStore(path)).Evaluate(p, Tick(p, 20.00m, 5));   // 20% off 25.00; a fresh peak would be 20.00
			Assert.NotNull(close);
			Assert.Contains("peak $25.00", close!.Rationale, StringComparison.Ordinal);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void APositionReopenedAtAnotherPriceStartsAFreshPeak()
	{
		var rule = Rule();
		var first = LongCall(entry: 15.00m);
		rule.Evaluate(first, Tick(first, 30.00m, 0));                   // peak 30.00 on the first position
		var second = LongCall(entry: 10.00m);
		Assert.Null(rule.Evaluate(second, Tick(second, 9.90m, 30)));    // against its own $10.00 peak, not the old $30.00
	}

	// ── Config ────────────────────────────────────────────────────────────────────────────────────────────

	private static string? Validate(params string[] exclude)
	{
		var c = new AIConfig { Ticker = "SPXW", Indicators = new IndicatorsConfig { IvDefaultPct = 0.18m, StrikeStep = 5m } };
		c.Opener.Indicators = c.Indicators;
		c.Rules.TrailingStop = new TrailingStopConfig { Enabled = true, Exclude = exclude.ToList() };
		return AIConfigLoader.Validate(c);
	}

	[Fact]
	public void ExcludeTakesAnyStructureAPositionCanCarry()
	{
		// The opener's own structures — calendars, diagonals, condors, verticals, singles — and the short shapes only a live account holds.
		Assert.Null(Validate());
		Assert.Null(Validate("LongCalendar", "LongDiagonal", "DoubleCalendar", "DoubleDiagonal", "IronCondor", "IronButterfly", "DiagonalVertical"));
		Assert.Null(Validate("ShortCallVertical", "ShortPutVertical", "LongCallVertical", "LongPutVertical", "LongCall", "LongPut"));
		Assert.Null(Validate("ShortCall", "ShortPut", "ShortCalendar", "ShortDiagonal"));
		Assert.Null(Validate("longcalendar"));
	}

	[Fact]
	public void AnExcludeNameThatIsNoStructureIsAConfigError()
	{
		// A typo must not silently leave the structure trailed.
		Assert.Contains("rules.trailingStop.exclude: 'ShortCallVerticle'", Validate("ShortCallVerticle") ?? "");
		Assert.Contains("rules.trailingStop.exclude: 'bullTrap'", Validate("LongCall", "bullTrap") ?? "");
	}

	[Fact]
	public void AnActivationThresholdMustBePositive()
	{
		var c = new AIConfig { Ticker = "SPXW", Indicators = new IndicatorsConfig { IvDefaultPct = 0.18m, StrikeStep = 5m } };
		c.Opener.Indicators = c.Indicators;
		c.Rules.TrailingStop = new TrailingStopConfig { Enabled = true, ActivateAtProfitPct = 0m };
		Assert.Contains("rules.trailingStop.activateAtProfitPct", AIConfigLoader.Validate(c) ?? "");
		c.Rules.TrailingStop.ActivateAtProfitPct = 0.50m;
		Assert.Null(AIConfigLoader.Validate(c));
	}
}

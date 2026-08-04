using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers the theta-exhaustion trigger (rules.stopLoss.thetaExhaustShortMid): on an underwater cross-expiry
// structure whose early short legs have decayed to pennies, the theta engine is gone and the rule closes.
// The knob is deliberately independent of stopLoss.enabled (which gates the realized-loss branch and feeds
// the opener's scorer EV), so arming it must not change opener rankings — tests run with Enabled = false.
public class StopLossRuleThetaExhaustTests
{
	// The 2026-07-31 live position that motivated the knob: 746P Aug-31 long / 745P Aug-6 short, 6.65 debit.
	private static OpenPosition PutDiagonal(decimal initialDebit = 6.65m) => new(
		Key: "SPY_DIAG_746_745",
		Ticker: "SPY",
		StrategyKind: "DIAGONAL",
		Legs: new[]
		{
			new PositionLeg("SPY260806P00745000", Side.Sell, 745.00m, new DateTime(2026, 8, 6), "P", 100),
			new PositionLeg("SPY260831P00746000", Side.Buy, 746.00m, new DateTime(2026, 8, 31), "P", 100),
		},
		InitialNetDebit: initialDebit,
		AdjustedNetDebit: initialDebit,
		Quantity: 100);

	private static EvaluationContext Ctx(OpenPosition position, DateTime now, params (string Symbol, decimal Mid)[] mids)
	{
		const decimal h = 0.02m;
		var quotes = new Dictionary<string, OptionContractQuote>();
		foreach (var (symbol, mid) in mids)
			quotes[symbol] = new(symbol, null, mid - h, mid + h, null, null, 100, 1000, 0.20m);
		return new EvaluationContext(
			Now: now,
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = 766.75m },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	private static StopLossRule Rule(decimal floor, bool enabled = false) =>
		new(new StopLossConfig { Enabled = enabled, ThetaExhaustShortMid = floor },
			new OpenerRealizedExpectancyConfig { Enabled = true, StopLossPctOfMaxLoss = 0.50m });

	// 2026-08-04 11:05 marks: short at 0.12, long at 4.04 → mark 3.92, underwater 2.73/share.
	private static readonly DateTime TwoDaysBeforeShortExpiry = new(2026, 8, 4, 11, 5, 0);

	[Fact]
	public void Fires_WhenUnderwaterAndShortAtPennies_EvenWithRealizedLossStopDisabled()
	{
		var p = Rule(floor: 0.15m).Evaluate(PutDiagonal(),
			Ctx(PutDiagonal(), TwoDaysBeforeShortExpiry, ("SPY260806P00745000", 0.12m), ("SPY260831P00746000", 4.04m)));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Contains("theta exhausted", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void DoesNotFire_WhenProfitable()
	{
		// Same penny short but the long is marked above the debit → not underwater; TakeProfit territory.
		Assert.Null(Rule(floor: 0.15m).Evaluate(PutDiagonal(),
			Ctx(PutDiagonal(), TwoDaysBeforeShortExpiry, ("SPY260806P00745000", 0.12m), ("SPY260831P00746000", 7.50m))));
	}

	[Fact]
	public void DoesNotFire_WhenShortStillCarriesPremium()
	{
		Assert.Null(Rule(floor: 0.15m).Evaluate(PutDiagonal(),
			Ctx(PutDiagonal(), TwoDaysBeforeShortExpiry, ("SPY260806P00745000", 1.20m), ("SPY260831P00746000", 5.00m))));
	}

	[Fact]
	public void DoesNotFire_OnShortExpiryDay()
	{
		// DTE 0 belongs to CloseBeforeShortExpiryRule (defer-to-late-session behavior wins there).
		var expiryDay = new DateTime(2026, 8, 6, 11, 0, 0);
		Assert.Null(Rule(floor: 0.15m).Evaluate(PutDiagonal(),
			Ctx(PutDiagonal(), expiryDay, ("SPY260806P00745000", 0.05m), ("SPY260831P00746000", 4.00m))));
	}

	[Fact]
	public void DoesNotFire_WhenKnobOff()
	{
		Assert.Null(Rule(floor: 0m).Evaluate(PutDiagonal(),
			Ctx(PutDiagonal(), TwoDaysBeforeShortExpiry, ("SPY260806P00745000", 0.12m), ("SPY260831P00746000", 4.04m))));
	}

	private static OpenPosition DoubleCalendar() => new(
		Key: "SPY_DC_740_760",
		Ticker: "SPY",
		StrategyKind: "DOUBLECALENDAR",
		Legs: new[]
		{
			new PositionLeg("SPY260806P00740000", Side.Sell, 740.00m, new DateTime(2026, 8, 6), "P", 100),
			new PositionLeg("SPY260831P00740000", Side.Buy, 740.00m, new DateTime(2026, 8, 31), "P", 100),
			new PositionLeg("SPY260806C00760000", Side.Sell, 760.00m, new DateTime(2026, 8, 6), "C", 100),
			new PositionLeg("SPY260831C00760000", Side.Buy, 760.00m, new DateTime(2026, 8, 31), "C", 100),
		},
		InitialNetDebit: 3.00m,
		AdjustedNetDebit: 3.00m,
		Quantity: 100);

	[Fact]
	public void DoesNotFire_WhenOnlyOneOfTwoShortsIsExhausted()
	{
		// Put side dead but the call short still carries 1.80 — half the theta engine is alive.
		Assert.Null(Rule(floor: 0.15m).Evaluate(DoubleCalendar(),
			Ctx(DoubleCalendar(), TwoDaysBeforeShortExpiry,
				("SPY260806P00740000", 0.05m), ("SPY260831P00740000", 0.40m),
				("SPY260806C00760000", 1.80m), ("SPY260831C00760000", 2.30m))));
	}

	[Fact]
	public void Fires_WhenBothShortsAreExhausted()
	{
		// Runaway rally: both shorts at pennies, marks sum to 2.60 < 3.00 debit → underwater, fire.
		var p = Rule(floor: 0.15m).Evaluate(DoubleCalendar(),
			Ctx(DoubleCalendar(), TwoDaysBeforeShortExpiry,
				("SPY260806P00740000", 0.03m), ("SPY260831P00740000", 0.30m),
				("SPY260806C00760000", 0.10m), ("SPY260831C00760000", 2.45m)));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
	}

	[Fact]
	public void DoesNotFire_OnSameExpiryVertical()
	{
		// No short expiring before the longest long → not a theta structure; the knob never applies.
		var vertical = new OpenPosition(
			Key: "SPY_VERT", Ticker: "SPY", StrategyKind: "LongPutVertical",
			Legs: new[]
			{
				new PositionLeg("SPY260806P00746000", Side.Buy, 746.00m, new DateTime(2026, 8, 6), "P", 100),
				new PositionLeg("SPY260806P00740000", Side.Sell, 740.00m, new DateTime(2026, 8, 6), "P", 100),
			},
			InitialNetDebit: 1.50m, AdjustedNetDebit: 1.50m, Quantity: 100);
		Assert.Null(Rule(floor: 0.15m).Evaluate(vertical,
			Ctx(vertical, TwoDaysBeforeShortExpiry, ("SPY260806P00746000", 0.30m), ("SPY260806P00740000", 0.10m))));
	}
}

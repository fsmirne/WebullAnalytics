using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers BreakEvenGuardRule (rules.breakEvenGuard): any-day close once spot is within bufferPct of either
// short-expiry break-even. Quotes carry no IV, so the estimator uses its shortStrike ± debit heuristic:
// short 766C, debit $9 → BE band [757, 775].
public class BreakEvenGuardRuleTests
{
	private static readonly DateTime ShortExpiry = new(2026, 10, 21);
	private static readonly DateTime LongExpiry = new(2026, 11, 13);

	private static OpenPosition Diagonal() => new(
		Key: "SPY_DIAG",
		Ticker: "SPY",
		StrategyKind: "LongDiagonal",
		Legs: new[]
		{
			new PositionLeg("SPY261021C00766000", Side.Sell, 766m, ShortExpiry, "C", 12),
			new PositionLeg("SPY261113C00765000", Side.Buy, 765m, LongExpiry, "C", 12),
		},
		InitialNetDebit: 9m,
		AdjustedNetDebit: 9m,
		Quantity: 12);

	private static EvaluationContext Ctx(decimal spot, DateTime now) => new(
		Now: now,
		OpenPositions: new Dictionary<string, OpenPosition> { [Diagonal().Key] = Diagonal() },
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = spot },
		Quotes: new Dictionary<string, OptionContractQuote>
		{
			["SPY261021C00766000"] = new("SPY261021C00766000", null, 5.10m, 5.20m, null, null, 100, 1000, null),
			["SPY261113C00765000"] = new("SPY261113C00765000", null, 14.10m, 14.20m, null, null, 100, 1000, null),
		},
		AccountCash: 0m, AccountValue: 0m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>());

	private static BreakEvenGuardRule Rule(bool enabled = true, decimal buffer = 0.0026m) => new(new BreakEvenGuardConfig { Enabled = enabled, BufferPct = buffer });

	private static readonly DateTime Day0 = new(2026, 10, 15, 11, 0, 0);

	[Theory]
	[InlineData(773.5)] // inside the band, within 0.26% of the 775 upper BE
	[InlineData(776.0)] // already past the upper BE
	[InlineData(758.5)] // within 0.26% of the 757 lower BE
	public void Fires_NearOrPastEitherBreakEven(double spot)
	{
		var p = Rule().Evaluate(Diagonal(), Ctx((decimal)spot, Day0));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Equal("BreakEvenGuardRule", p.Rule);
		Assert.All(p.Legs, l => Assert.Equal(12, l.Qty));
	}

	[Fact]
	public void Silent_MidBand() => Assert.Null(Rule().Evaluate(Diagonal(), Ctx(770m, Day0)));

	[Fact]
	public void ZeroBuffer_FiresOnlyAtTheBreakEven()
	{
		Assert.Null(Rule(buffer: 0m).Evaluate(Diagonal(), Ctx(774.9m, Day0)));
		Assert.NotNull(Rule(buffer: 0m).Evaluate(Diagonal(), Ctx(775m, Day0)));
	}

	[Fact]
	public void Silent_WhenDisabled() => Assert.Null(Rule(enabled: false).Evaluate(Diagonal(), Ctx(776m, Day0)));

	[Fact]
	public void Silent_AfterShortExpiry() => Assert.Null(Rule().Evaluate(Diagonal(), Ctx(776m, ShortExpiry.AddDays(1))));
}

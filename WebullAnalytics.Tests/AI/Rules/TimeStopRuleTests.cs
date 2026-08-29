using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers TimeStopRule (rules.timeStop): force-close once elapsed calendar days since open reach a
// configured fraction of the position's original DTE, regardless of P&L. 50-DTE vertical opened
// 2026-07-01, expiring 2026-08-20 (50 calendar days out) — half-life trigger at 25 elapsed days.
public class TimeStopRuleTests
{
	private static readonly DateTime OpenedAt = new(2026, 7, 1);
	private static readonly DateTime Expiry = new(2026, 8, 20); // 50 calendar days after OpenedAt

	private static OpenPosition Vertical(DateTime? openedAt) => new(
		Key: "SPY_VERT_50DTE",
		Ticker: "SPY",
		StrategyKind: "ShortPutVertical",
		Legs: new[]
		{
			new PositionLeg("SPY260820P00600000", Side.Sell, 600.00m, Expiry, "P", 100),
			new PositionLeg("SPY260820P00595000", Side.Buy, 595.00m, Expiry, "P", 100),
		},
		InitialNetDebit: -1.00m,
		AdjustedNetDebit: -1.00m,
		Quantity: 100,
		OpenedAt: openedAt);

	private static EvaluationContext Ctx(OpenPosition position, DateTime now) => new(
		Now: now,
		OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = 610m },
		Quotes: new Dictionary<string, OptionContractQuote>
		{
			["SPY260820P00600000"] = new("SPY260820P00600000", null, 0.48m, 0.52m, null, null, 100, 1000, 0.20m),
			["SPY260820P00595000"] = new("SPY260820P00595000", null, 0.18m, 0.22m, null, null, 100, 1000, 0.20m),
		},
		AccountCash: 0m, AccountValue: 0m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>());

	private static TimeStopRule Rule(decimal lifeFraction = 0.5m, bool enabled = true) =>
		new(new TimeStopConfig { Enabled = enabled, LifeFractionElapsed = lifeFraction });

	[Fact]
	public void Fires_AtExactlyHalfOriginalDte()
	{
		var now = OpenedAt.AddDays(25); // 50 * 0.5
		var p = Rule().Evaluate(Vertical(OpenedAt), Ctx(Vertical(OpenedAt), now));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Equal("TimeStopRule", p.Rule);
		Assert.Contains("regardless of P&L", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void Fires_PastHalfOriginalDte()
	{
		var now = OpenedAt.AddDays(40);
		Assert.NotNull(Rule().Evaluate(Vertical(OpenedAt), Ctx(Vertical(OpenedAt), now)));
	}

	[Fact]
	public void DoesNotFire_BeforeHalfOriginalDte()
	{
		var now = OpenedAt.AddDays(24);
		Assert.Null(Rule().Evaluate(Vertical(OpenedAt), Ctx(Vertical(OpenedAt), now)));
	}

	[Fact]
	public void DoesNotFire_WhenOpenedAtMissing()
	{
		var now = OpenedAt.AddDays(40);
		Assert.Null(Rule().Evaluate(Vertical(openedAt: null), Ctx(Vertical(openedAt: null), now)));
	}

	[Fact]
	public void DoesNotFire_WhenDisabled()
	{
		var now = OpenedAt.AddDays(40);
		Assert.Null(Rule(enabled: false).Evaluate(Vertical(OpenedAt), Ctx(Vertical(OpenedAt), now)));
	}

	[Fact]
	public void RespectsCustomLifeFraction()
	{
		// 0.2 * 50 = 10 days.
		var rule = Rule(lifeFraction: 0.2m);
		Assert.Null(rule.Evaluate(Vertical(OpenedAt), Ctx(Vertical(OpenedAt), OpenedAt.AddDays(9))));
		Assert.NotNull(rule.Evaluate(Vertical(OpenedAt), Ctx(Vertical(OpenedAt), OpenedAt.AddDays(10))));
	}
}

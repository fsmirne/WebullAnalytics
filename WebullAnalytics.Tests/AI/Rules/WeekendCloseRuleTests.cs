using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers WeekendCloseRule (rules.weekendClose): flatten a carry-over position in the last minutesBeforeClose of the
// session before a market closure, optionally only when VIX is up minVixChangePct from the prior close.
public class WeekendCloseRuleTests
{
	private static readonly DateTime ShortExpiry = new(2026, 10, 21);
	private static readonly DateTime LongExpiry = new(2026, 11, 13);
	private static readonly DateTime Friday1530 = new(2026, 10, 16, 15, 30, 0);

	private static OpenPosition Diagonal(DateTime shortExpiry, DateTime longExpiry) => new(
		Key: "SPY_DIAG",
		Ticker: "SPY",
		StrategyKind: "LongDiagonal",
		Legs: new[]
		{
			new PositionLeg("SPY_SHORT", Side.Sell, 767m, shortExpiry, "P", 16),
			new PositionLeg("SPY_LONG", Side.Buy, 768m, longExpiry, "P", 16),
		},
		InitialNetDebit: 6.88m,
		AdjustedNetDebit: 6.88m,
		Quantity: 16);

	private static OpenPosition Diagonal() => Diagonal(ShortExpiry, LongExpiry);

	private static EvaluationContext Ctx(DateTime now, decimal? vix = null, decimal? vixPriorClose = null) => new(
		Now: now,
		OpenPositions: new Dictionary<string, OpenPosition> { [Diagonal().Key] = Diagonal() },
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = 767.5m },
		Quotes: new Dictionary<string, OptionContractQuote>(),
		AccountCash: 0m, AccountValue: 0m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>(),
		Vix: vix,
		VixPriorClose: vixPriorClose);

	private static WeekendCloseRule Rule(bool enabled = true, int minutes = 30, decimal minVixChange = 0m) => new(new WeekendCloseConfig { Enabled = enabled, MinutesBeforeClose = minutes, MinVixChangePct = minVixChange });

	[Fact]
	public void Fires_FridayInTheLastMinutes()
	{
		var p = Rule().Evaluate(Diagonal(), Ctx(Friday1530));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Equal("WeekendCloseRule", p.Rule);
		Assert.All(p.Legs, l => Assert.Equal(16, l.Qty));
		Assert.Equal(new[] { "buy", "sell" }, p.Legs.Select(l => l.Action));
	}

	[Fact]
	public void Silent_FridayBeforeTheWindow() => Assert.Null(Rule().Evaluate(Diagonal(), Ctx(new DateTime(2026, 10, 16, 15, 29, 0))));

	[Fact]
	public void Silent_MidWeek() => Assert.Null(Rule().Evaluate(Diagonal(), Ctx(new DateTime(2026, 10, 15, 15, 45, 0))));

	[Fact]
	public void Fires_BeforeAMidweekHoliday() => Assert.NotNull(Rule().Evaluate(Diagonal(new DateTime(2026, 12, 4), new DateTime(2026, 12, 18)), Ctx(new DateTime(2026, 11, 25, 15, 30, 0))));   // Wed before Thanksgiving

	[Fact]
	public void HalfDay_WindowEndsAtTheEarlyClose()
	{
		var blackFriday = new DateTime(2026, 11, 27);
		var position = Diagonal(new DateTime(2026, 12, 4), new DateTime(2026, 12, 18));
		Assert.Null(Rule().Evaluate(position, Ctx(blackFriday.AddHours(12).AddMinutes(29))));
		Assert.NotNull(Rule().Evaluate(position, Ctx(blackFriday.AddHours(12).AddMinutes(30))));
	}

	[Fact]
	public void Silent_WhenEveryLegExpiresToday() => Assert.Null(Rule().Evaluate(Diagonal(Friday1530.Date, Friday1530.Date), Ctx(Friday1530)));

	[Fact]
	public void Silent_WhenDisabled() => Assert.Null(Rule(enabled: false).Evaluate(Diagonal(), Ctx(Friday1530)));

	[Theory]
	[InlineData(22.40, 20.00, true)]    // +12.0%
	[InlineData(22.39, 20.00, false)]   // +11.95%
	[InlineData(18.00, 20.00, false)]   // VIX down
	public void VixGate_FiresOnlyOnTheThreshold(double vix, double prior, bool fires)
	{
		var p = Rule(minVixChange: 0.12m).Evaluate(Diagonal(), Ctx(Friday1530, (decimal)vix, (decimal)prior));
		Assert.Equal(fires, p != null);
	}

	[Fact]
	public void VixGate_SilentWithoutVix()
	{
		Assert.Null(Rule(minVixChange: 0.12m).Evaluate(Diagonal(), Ctx(Friday1530)));
		Assert.Null(Rule(minVixChange: 0.12m).Evaluate(Diagonal(), Ctx(Friday1530, vix: 25m)));
	}
}

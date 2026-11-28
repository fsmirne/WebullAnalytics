using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// `analyze position` sweeps every short from today to the long's expiry for a single long's conversions, and ranks them by total EV — so on
// 2026-11-27 a GME 2027-12-17 25C's best conversion sold the 2027-06-17 30C (202 DTE), not a weekly. And the scorer's realized-vol paths carry no
// earnings jump, so a 12-11 27C through the 12-08 report (IV 64% vs 45% for 12-04) scored +$485 against +$23 for the 12-04 short. The sweep is
// now held to the strategy's short-DTE band and the earnings-spanning shorts rank after the rest.
public class PositionSweptScreenTests
{
	private static readonly DateTime AsOf = new(2026, 11, 27);
	private static readonly DateTime LongExp = new(2027, 12, 17);
	private static readonly DateTime Earnings = new(2026, 12, 8);
	private static readonly TickerEvents Events = new("GME", NextEarningsDate: Earnings, null, null, null);

	private static AnalyzePositionCommand.PositionSnapshot Leg(DateTime exp, decimal strike, LegAction action) =>
		new(MatchKeys.OccSymbol("GME", exp, strike, "C"), action, 12, 1m, new OptionParsed("GME", exp, "C", strike));

	private static readonly AnalyzePositionCommand.PositionSnapshot Long = Leg(LongExp, 25m, LegAction.Buy);

	private static AnalyzePositionCommand.Scenario Conversion(DateTime shortExp, decimal strike, decimal ev) =>
		new($"Convert to covered diagonal (sell {shortExp:MM-dd} ${strike:F2})", "—", 0m, 0m, 0m, 0m, Qty: 12, DaysToTarget: (shortExp - AsOf).Days, Rationale: "r",
			ExpectedPnLPerContract: ev, Book: [new("existing", [Long, Leg(shortExp, strike, LegAction.Sell)], 12)], Swept: true);

	private static readonly AnalyzePositionCommand.Scenario Hold = new("Hold to expiry", "—", 0m, 0m, 0m, 0m, Qty: 12, DaysToTarget: 385, Rationale: "r", ExpectedPnLPerContract: -141.83m, Book: [new("existing", [Long], 12)]);
	private static readonly AnalyzePositionCommand.Scenario Weekly = Conversion(new DateTime(2026, 12, 4), 26m, 23m / 12m);
	private static readonly AnalyzePositionCommand.Scenario ThroughEarnings = Conversion(new DateTime(2026, 12, 11), 27m, 485m / 12m);
	private static readonly AnalyzePositionCommand.Scenario SixMonth = Conversion(new DateTime(2027, 6, 17), 30m, 565m / 12m);

	[Theory]
	[InlineData("5-15", 5, 15)]
	[InlineData(" 0 - 30 ", 0, 30)]
	[InlineData("7-7", 7, 7)]
	public void TryParseDteBand_ReadsMinMax(string spec, int min, int max)
	{
		Assert.True(AnalyzePositionSettings.TryParseDteBand(spec, out var band));
		Assert.Equal((min, max), band);
	}

	[Theory]
	[InlineData("15-5")]
	[InlineData("5")]
	[InlineData("all")]
	[InlineData("-5-15")]
	[InlineData(null)]
	public void TryParseDteBand_RejectsMalformed(string? spec) => Assert.False(AnalyzePositionSettings.TryParseDteBand(spec, out _));

	private static AIConfig Strategy(bool calendar)
	{
		var ai = new AIConfig();
		var s = ai.Opener.Structures;
		s.LongDiagonal.Enabled = true; s.LongDiagonal.ShortDteMin = 5; s.LongDiagonal.ShortDteMax = 15;
		s.LongCalendar.Enabled = calendar; s.LongCalendar.ShortDteMin = 3; s.LongCalendar.ShortDteMax = 20;
		return ai;
	}

	[Fact]
	public void ResolveShortDteBand_IsTheUnionOfTheEnabledCalendarLikeStructures()
	{
		Assert.Equal((5, 15), AnalyzePositionCommand.ResolveShortDteBand(new AnalyzePositionSettings(), Strategy(calendar: false)));
		Assert.Equal((3, 20), AnalyzePositionCommand.ResolveShortDteBand(new AnalyzePositionSettings(), Strategy(calendar: true)));
	}

	[Fact]
	public void ResolveShortDteBand_DteFlagOverridesAndAllOrSellDisableIt()
	{
		Assert.Equal((0, 30), AnalyzePositionCommand.ResolveShortDteBand(new AnalyzePositionSettings { Dte = "0-30" }, Strategy(calendar: false)));
		Assert.Null(AnalyzePositionCommand.ResolveShortDteBand(new AnalyzePositionSettings { Dte = "all" }, Strategy(calendar: false)));
		Assert.Null(AnalyzePositionCommand.ResolveShortDteBand(new AnalyzePositionSettings { Sell = "GME261211C00027000" }, Strategy(calendar: false)));
		Assert.Null(AnalyzePositionCommand.ResolveShortDteBand(new AnalyzePositionSettings(), ai: null));
	}

	[Fact]
	public void ScreenSwept_DropsShortsOutsideTheBandAndMarksThoseThroughEarnings()
	{
		var (kept, dropped) = AnalyzePositionCommand.ScreenSwept([Hold, Weekly, ThroughEarnings, SixMonth], [Long], (5, 15), Events, earningsBlackoutDaysAfter: 0, AsOf);

		Assert.Equal(1, dropped);
		Assert.DoesNotContain(kept, s => s.Name.Contains("06-17"));
		Assert.Same(Hold, kept[0]); // not swept: passes through untouched
		Assert.Null(kept.Single(s => s.Name.Contains("12-04")).EarningsInside);
		var marked = kept.Single(s => s.Name.Contains("12-11"));
		Assert.Equal(Earnings, marked.EarningsInside);
		Assert.EndsWith("⚠ earnings 12-08", marked.Name);
	}

	[Fact]
	public void ScreenSwept_WithoutABandKeepsEveryExpiry()
	{
		var (kept, dropped) = AnalyzePositionCommand.ScreenSwept([Weekly, ThroughEarnings, SixMonth], [Long], band: null, Events, earningsBlackoutDaysAfter: 0, AsOf);
		Assert.Equal(0, dropped);
		Assert.Equal(3, kept.Count);
		Assert.Equal(Earnings, kept.Single(s => s.Name.Contains("06-17")).EarningsInside); // 06-17 also expires after the 12-08 report
	}

	[Fact]
	public void ScreenSwept_AHeldShortIsNotANewShort()
	{
		// A roll's Book keeps the long and sells a new short; a scenario that leaves the existing short held (an add) is judged by its new legs only.
		var heldShort = Leg(new DateTime(2026, 12, 11), 27m, LegAction.Sell);
		var add = ThroughEarnings with { Book = [new("existing", [Long, heldShort], 12)] };
		var (kept, dropped) = AnalyzePositionCommand.ScreenSwept([add], [Long, heldShort], (5, 6), Events, earningsBlackoutDaysAfter: 0, AsOf);
		Assert.Equal(0, dropped);
		Assert.Null(kept[0].EarningsInside);
	}

	[Fact]
	public void OrderScenariosForDisplay_RanksAShortThroughEarningsAfterTheRest()
	{
		var (kept, _) = AnalyzePositionCommand.ScreenSwept([ThroughEarnings, Weekly, Hold], [Long], (5, 15), Events, earningsBlackoutDaysAfter: 0, AsOf);
		var ordered = AnalyzePositionCommand.OrderScenariosForDisplay(kept, availableCash: null, riskAversion: 0m);
		Assert.Equal(["Convert to covered diagonal (sell 12-04 $26.00)", "Hold to expiry", "Convert to covered diagonal (sell 12-11 $27.00) ⚠ earnings 12-08"], ordered.Select(s => s.Name));
	}
}

using WebullAnalytics.Analyze;
using WebullAnalytics.IO;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public class AnalyzePositionDoubleAddTests
{
	// Spot sits between the existing call calendar and the added put side; chain lists $1 strikes.
	private const decimal Spot = 756.30m;
	private static readonly DateTime ShortExp = new(2026, 6, 10);
	private static readonly DateTime LongExp = new(2026, 6, 19);
	private static readonly DateTime AsOf = new(2026, 6, 3);

	private static List<AnalyzePositionCommand.PositionSnapshot> CallCalendar() => new()
	{
		new(MatchKeys.OccSymbol("SPY", ShortExp, 757m, "C"), LegAction.Sell, 1, 5.00m, new OptionParsed("SPY", ShortExp, "C", 757m)),
		new(MatchKeys.OccSymbol("SPY", LongExp, 757m, "C"), LegAction.Buy, 1, 7.00m, new OptionParsed("SPY", LongExp, "C", 757m)),
	};

	// Front expiry fully listed both sides; far expiry holds only the existing long call (mirrors how the
	// chain endpoint returns the front expiry in full and far expiries only for explicitly-requested legs).
	private static Dictionary<string, OptionContractQuote> Chain()
	{
		var q = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		void Add(DateTime exp, decimal k, string cp, decimal bid, decimal ask)
		{
			var sym = MatchKeys.OccSymbol("SPY", exp, k, cp);
			q[sym] = new OptionContractQuote(sym, null, bid, ask, null, null, 1000, 1000, 0.20m);
		}
		foreach (var k in new[] { 754m, 755m, 756m, 757m, 758m })
		{
			Add(ShortExp, k, "C", 5m, 5.2m);
			Add(ShortExp, k, "P", 5m, 5.2m);
		}
		Add(LongExp, 757m, "C", 9m, 9.4m); // existing long only; far puts intentionally absent → BS-estimated
		return q;
	}

	[Fact]
	public void GenerateScenarios_CalendarOffersDoubleCalendarAndDoubleDiagonalAdds()
	{
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };

		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			CallCalendar(), AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());

		// Opposite (put) side, snapped to the listed $1 grid: short at 756 (nearest ≤ spot), diagonal long at 755.
		var dcal = Assert.Single(scenarios, s => s.Name.Contains("double calendar", StringComparison.Ordinal));
		var ddiag = Assert.Single(scenarios, s => s.Name.Contains("double diagonal", StringComparison.Ordinal));

		Assert.Contains("$756.00", dcal.Name, StringComparison.Ordinal);
		Assert.Contains("$756.00/$755.00", ddiag.Name, StringComparison.Ordinal);
		Assert.Contains("SELL " + MatchKeys.OccSymbol("SPY", ShortExp, 756m, "P"), dcal.ActionSummary, StringComparison.Ordinal);
		Assert.Contains("BUY " + MatchKeys.OccSymbol("SPY", LongExp, 756m, "P"), dcal.ActionSummary, StringComparison.Ordinal);
		Assert.Contains("BUY " + MatchKeys.OccSymbol("SPY", LongExp, 755m, "P"), ddiag.ActionSummary, StringComparison.Ordinal);
	}

	[Fact]
	public void GenerateScenarios_FarLegNotLiveQuoted_FlagsEstimated()
	{
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };
		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			CallCalendar(), AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());

		// The far-expiry put long isn't in the snapshot, so both adds are Black-Scholes-estimated.
		Assert.Contains(scenarios, s => s.Name.Contains("double calendar", StringComparison.Ordinal) && s.Name.Contains("[estimated]", StringComparison.Ordinal));
		Assert.Contains(scenarios, s => s.Name.Contains("double diagonal", StringComparison.Ordinal) && s.Name.Contains("[estimated]", StringComparison.Ordinal));
	}

	[Fact]
	public void GenerateScenarios_ReportsExpectedProfitAndFlagsAtMostOneBestDoubleAdd()
	{
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };
		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			CallCalendar(), AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());

		var doubleAdds = scenarios.Where(s => s.Name.Contains("double ", StringComparison.Ordinal)).ToList();
		Assert.Equal(2, doubleAdds.Count);
		Assert.All(doubleAdds, a => Assert.Contains("expected profit", a.Rationale, StringComparison.Ordinal));
		// The best add is starred only when it is expected to make money, so there is at most one.
		var best = doubleAdds.Where(a => a.Rationale.Contains("best add", StringComparison.Ordinal)).ToList();
		Assert.True(best.Count <= 1);
		Assert.All(best, a => Assert.DoesNotContain("expected profit -", a.Rationale, StringComparison.Ordinal));
	}

	[Fact]
	public void GenerateScenarios_PartialAddKeepsTheAddsExpectedProfit()
	{
		// Cash for one added contract only: the partial must still say what the add is expected to make, not just how it was sized.
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };
		var legs = CallCalendar().Select(l => l with { Qty = 5 }).ToList();
		var full = AnalyzePositionCommand.GenerateScenarios(legs, AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());
		var add = full.First(s => s.Name.Contains("double ", StringComparison.Ordinal));
		settings.Cash = add.MarginDeltaPerContract * 1.5m;
		var scenarios = AnalyzePositionCommand.GenerateScenarios(legs, AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());
		var partials = scenarios.Where(s => s.Name.Contains("double ", StringComparison.Ordinal) && s.Name.Contains("· partial", StringComparison.Ordinal)).ToList();
		Assert.NotEmpty(partials);
		Assert.All(partials, p => Assert.Contains("expected profit", p.Rationale, StringComparison.Ordinal));
		Assert.All(partials, p => Assert.Contains("full size would need", p.Rationale, StringComparison.Ordinal));
	}

	[Theory]
	// The 2026-11-20 SPY 731/732 diagonal scale-up: expected value $5.24 for a $5.015 debit is +4.5%, not the 104% value/cost once shown.
	[InlineData(5.24, 5.015, 501.5, 0.0449)]
	[InlineData(5.00, 5.00, 500, 0)]
	[InlineData(4.00, 5.00, 500, -0.2)]
	[InlineData(4.00, 5.00, 0, 0)]
	public void ExpectedProfitReturn_IsProfitPerAddedDollar_ZeroAtBreakEven(decimal ev, decimal debit, decimal margin, decimal expected)
		=> Assert.Equal(expected, Math.Round(AnalyzePositionCommand.ExpectedProfitReturn(ev, debit, margin), 4));

	[Fact]
	public void GenerateScenarios_PopulatesEvOnHoldAndAdds()
	{
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };
		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			CallCalendar(), AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());

		// EV is the probability-weighted P&L; it must differ from the spot-pinned TotalPnL (the whole point).
		var hold = Assert.Single(scenarios, s => s.Name == "Hold to short expiry");
		Assert.NotNull(hold.ExpectedPnLPerContract);
		Assert.NotEqual(hold.ExpectedPnLPerContract!.Value, hold.TotalPnLPerContract);
		Assert.All(scenarios.Where(s => s.Name.Contains("double ", StringComparison.Ordinal)), a => Assert.NotNull(a.ExpectedPnLPerContract));
	}

	[Fact]
	public void GenerateScenarios_OffersScaleUpComparatorWithEv()
	{
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };
		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			CallCalendar(), AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, Chain());

		var scaleUp = Assert.Single(scenarios, s => s.Name.Contains("scale up", StringComparison.Ordinal));
		Assert.NotNull(scaleUp.ExpectedPnLPerContract);
		Assert.Contains("baseline the doubles must beat", scaleUp.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void GenerateScenarios_WithoutChain_OmitsDoubleAdds()
	{
		// No chain → no listed-strike grid → no phantom OPPOSITE-side double proposals. (The scale-up
		// comparator doesn't need the chain — it re-prices the existing legs — so it may still appear.)
		var settings = new AnalyzePositionSettings { IvDefault = 40m, StrikeStep = 0.50m };
		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			CallCalendar(), AnalyzePositionCommand.StructureKind.Calendar, settings, Spot, AsOf, quotes: null);

		Assert.DoesNotContain(scenarios, s => s.Name.Contains("double ", StringComparison.Ordinal));
	}
}

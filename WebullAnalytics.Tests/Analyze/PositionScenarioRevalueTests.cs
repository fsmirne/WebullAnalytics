using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// `analyze position` ranks every calendar/diagonal scenario on the opener scorer: the P&L of closing at mid now plus the scorer's forward EV of
// each structure the scenario leaves held. On 2026-11-20 the adds kept the lognormal EV while Hold and Rolls had the scorer's, and the two models'
// value of the SAME 22 existing lots differed by $2.8k — enough to rank "add 22 more" above Hold and Close.
public class PositionScenarioRevalueTests
{
	private static readonly DateTime ShortExp = new(2026, 11, 23);
	private static readonly DateTime LongExp = new(2026, 12, 11);

	private static AnalyzePositionCommand.PositionSnapshot Leg(string cp, DateTime exp, decimal strike, LegAction action) =>
		new(MatchKeys.OccSymbol("SPY", exp, strike, cp), action, 1, 1m, new OptionParsed("SPY", exp, cp, strike));

	private static readonly IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> Existing = [Leg("C", ShortExp, 732m, LegAction.Sell), Leg("C", LongExp, 731m, LegAction.Buy)];
	private static readonly IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> PutAdd = [Leg("P", ShortExp, 699m, LegAction.Sell), Leg("P", LongExp, 698m, LegAction.Buy)];

	private static AnalyzePositionCommand.BookStructure B(string label, IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> legs, int qty) => new(label, legs, qty);

	private static AnalyzePositionCommand.Scenario Scenario(decimal marginPerContract, params AnalyzePositionCommand.BookStructure[] book) =>
		new("s", "—", 0m, 0m, 0m, marginPerContract, Qty: 22, DaysToTarget: 3, Rationale: "r", ExpectedPnLPerContract: 999m, Book: book, EvNote: "lognormal EV");

	private static RiskDiagnosticOpenerScore Score(decimal ev, decimal? pop = null) => new("LongDiagonal", 1, null, null, null, null, pop, ev, 3, null, null, null);

	/// <summary>Scores a structure by its first leg: existing diagonal −$100.60/contract, put add +$40.</summary>
	private static RiskDiagnosticOpenerScore? Scorer(AnalyzePositionCommand.BookStructure s) =>
		s.Legs[0].Parsed.CallPut == "C" ? Score(-100.60m, 0.413m) : s.Legs[0].Parsed.CallPut == "P" ? Score(40m) : null;

	[Fact]
	public void HoldIsClosePnLPlusTheExistingLotsForwardEv()
	{
		// The 11-20 Hold row: −$358.50 to close at mid, −$100.60 a contract forward → −$459.10 a contract, −$10,100 on 22.
		var (hold, added) = PositionAiResetScanner.Revalue(Scenario(0m, B("existing", Existing, 22)), 22, -358.50m, 0m, Scorer);
		Assert.Equal(-459.10m, hold.ExpectedPnLPerContract);
		Assert.Equal(0.413m, hold.ProbabilityOfProfit);
		Assert.Null(added);
		Assert.Contains("opener scorer", hold.EvNote);
	}

	[Fact]
	public void ScalingUpALosingStructureLosesMoreThanHolding()
	{
		// The same 22 existing lots plus 22 more of the same: −$100.60 each on 44, not the lognormal +$25.50 that made it rank first.
		var (scaleUp, added) = PositionAiResetScanner.Revalue(Scenario(497m, B("existing", Existing, 22), B("added", Existing, 22)), 22, -358.50m, 0m, Scorer);
		Assert.Equal(-358.50m - 2 * 100.60m, scaleUp.ExpectedPnLPerContract);
		Assert.Equal(-100.60m / 497m, added!.Value, 6);
		Assert.Null(scaleUp.ProbabilityOfProfit);   // two structures: no single POP describes the scenario
	}

	[Fact]
	public void APartialAddCountsEachStructureAtItsOwnSize()
	{
		var (partial, added) = PositionAiResetScanner.Revalue(Scenario(1000m * 7 / 22, B("existing", Existing, 22), B("added", PutAdd, 7)), 22, -358.50m, 0m, Scorer);
		Assert.Equal(-358.50m + (-100.60m * 22 + 40m * 7) / 22, partial.ExpectedPnLPerContract);
		Assert.Equal(40m / 1000m, added!.Value, 6);   // $280 expected on $7,000 added ($1,000 a contract × 7)
	}

	[Fact]
	public void CloseAllIsItsClosePnL()
	{
		var (close, _) = PositionAiResetScanner.Revalue(Scenario(0m), 22, -358.50m, 0m, Scorer);
		Assert.Equal(-358.50m, close.ExpectedPnLPerContract);
		Assert.Null(close.EvNote);
	}

	[Fact]
	public void AStructureTheScorerCannotValueLeavesNoEv()
	{
		// Never the other model's figure: that is the mismatch this replaces.
		var (sc, _) = PositionAiResetScanner.Revalue(Scenario(500m, B("existing", Existing, 22), B("added", PutAdd, 22)), 22, -358.50m, 0m, s => s.Legs[0].Parsed.CallPut == "C" ? Score(-100.60m) : null);
		Assert.Null(sc.ExpectedPnLPerContract);
		Assert.Contains("could not value the added structure", sc.EvNote);
	}

	[Fact]
	public void AnUnvaluedScenarioRanksAfterEveryValuedOne()
	{
		// Its pinned P&L is a best case; ranking it on that would put it on top.
		var unvalued = Scenario(0m, B("existing", Existing, 22)) with { ExpectedPnLPerContract = null, TotalPnLPerContract = 5000m };
		var valued = Scenario(0m, B("existing", Existing, 22)) with { ExpectedPnLPerContract = -400m };
		var ordered = AnalyzePositionCommand.OrderScenariosForDisplay([unvalued, valued], availableCash: null, riskAversion: 0m);
		Assert.Same(valued, ordered[0]);
	}

	[Fact]
	public void TheBestAddIsFlaggedOnlyWhenItIsExpectedToMakeMoney()
	{
		var list = new List<AnalyzePositionCommand.Scenario> { Scenario(1m) with { EvNote = "a" }, Scenario(1m) with { EvNote = "b" } };
		PositionAiResetScanner.FlagBestAdd(list, [(0, -0.05m), (1, -0.01m)]);
		Assert.DoesNotContain(list, s => s.EvNote!.Contains("best add"));
		PositionAiResetScanner.FlagBestAdd(list, [(0, 0.03m), (1, 0.08m)]);
		Assert.DoesNotContain("best add", list[0].EvNote);
		Assert.Contains("best add", list[1].EvNote);
	}

	// Friction: $5 per order per contract ($0.05/share). The scorer charges each structure it values its opening order; a scenario pays the orders it places now.
	[Fact]
	public void HoldIsRefundedTheOpeningOrderItNeverPlaces()
	{
		var (hold, _) = PositionAiResetScanner.Revalue(Scenario(0m, B("existing", Existing, 22)), 22, -358.50m, 5m, Scorer);
		Assert.Equal(-459.10m + 5m, hold.ExpectedPnLPerContract);
	}

	[Fact]
	public void CloseAllPaysItsClosingOrder()
	{
		var (close, _) = PositionAiResetScanner.Revalue(Scenario(0m) with { OrderContractsNow = 22 }, 22, -358.50m, 5m, Scorer);
		Assert.Equal(-363.50m, close.ExpectedPnLPerContract);
		Assert.Contains("trading costs -$110.00", close.EvNote);
	}

	[Fact]
	public void AComboRollPaysExactlyTheOrderTheScorerCharged()
	{
		// One roll order now, and the rolled structure's opening order the scorer charged: the same order, so no adjustment.
		var (roll, _) = PositionAiResetScanner.Revalue(Scenario(0m, B("rolled", Existing, 22)) with { OrderContractsNow = 22 }, 22, -358.50m, 5m, Scorer);
		Assert.Equal(-459.10m, roll.ExpectedPnLPerContract);
		Assert.DoesNotContain("trading costs", roll.EvNote);
		// A roll that has to go as two orders pays the second.
		var (split, _) = PositionAiResetScanner.Revalue(Scenario(0m, B("rolled", Existing, 22)) with { OrderContractsNow = 44 }, 22, -358.50m, 5m, Scorer);
		Assert.Equal(-459.10m - 5m, split.ExpectedPnLPerContract);
	}

	[Fact]
	public void AnAddPaysOnlyItsOwnOpeningOrder()
	{
		// Existing refunded (already open), added charged by the scorer and placed now: net +$5 a contract for the existing lots.
		var (add, _) = PositionAiResetScanner.Revalue(Scenario(1000m, B("existing", Existing, 22), B("added", PutAdd, 22)) with { OrderContractsNow = 22 }, 22, -358.50m, 5m, Scorer);
		Assert.Equal(-358.50m - 100.60m + 40m + 5m, add.ExpectedPnLPerContract);
	}

	[Fact]
	public void TheRollShortlistIsChosenOnTheEvItIsGiven()
	{
		// Six candidates, ranked by the EV they carry after re-valuing: the top 3, the best pure-EV one left (roll 3) and the best needing no margin (roll 5).
		var rolls = Enumerable.Range(0, 6).Select(i => Scenario(i == 5 ? -1m : 100m) with { Name = $"roll {i}", SweptRoll = true, ExpectedPnLPerContract = -100m * i }).ToList();
		var other = Scenario(0m) with { Name = "hold" };
		var kept = AnalyzePositionCommand.ShortlistSweptRolls([other, .. rolls], availableCash: null, riskAversion: 0m);
		Assert.Equal(["hold", "roll 0", "roll 1", "roll 2", "roll 3", "roll 5"], kept.Select(s => s.Name));
	}

	[Fact]
	public void GeneratedScenariosSayWhatTheyLeaveHeld()
	{
		var legs = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new(Existing[0].Symbol, LegAction.Sell, 22, 3.92m, Existing[0].Parsed),
			new(Existing[1].Symbol, LegAction.Buy, 22, 12.47m, Existing[1].Parsed),
		};
		var scenarios = AnalyzePositionCommand.GenerateScenarios(legs, AnalyzePositionCommand.StructureKind.Diagonal, new AnalyzePositionSettings { IvDefault = 24m, StrikeStep = 1m }, 699.74m, new DateTime(2026, 11, 20), quotes: null);

		var hold = Assert.Single(scenarios, s => s.Name == "Hold to short expiry");
		var heldHold = Assert.Single(hold.Book!);
		Assert.Equal(("existing", 22), (heldHold.Label, heldHold.Qty));
		Assert.Empty(Assert.Single(scenarios, s => s.Name == "Close all").Book!);
		var longOnly = Assert.Single(Assert.Single(scenarios, s => s.Name == "Close short only").Book!);
		Assert.Equal(Existing[1].Symbol, Assert.Single(longOnly.Legs).Symbol);
		var scaleUp = Assert.Single(scenarios, s => s.Name.Contains("scale up", StringComparison.Ordinal));
		Assert.Equal(["existing", "added"], scaleUp.Book!.Select(b => b.Label));
		Assert.All(scenarios.Where(s => s.IsRoll && s.Name.StartsWith("Roll", StringComparison.Ordinal)), r => Assert.Contains(r.Book!, b => b.Label == "rolled"));
		Assert.All(scenarios.Where(s => s.Book != null).SelectMany(s => s.Book!).SelectMany(b => b.Legs), l => Assert.Equal(1, l.Qty));
	}

	[Fact]
	public void SingleLongScenariosAreValuedNotPinned()
	{
		// The 11-20 lone 731C x22: Hold pinned at today's intrinsic ($0, the whole premium lost) ranked last, so "Close now" always won.
		var longLeg = new AnalyzePositionCommand.PositionSnapshot(Existing[1].Symbol, LegAction.Buy, 22, 8.64m, Existing[1].Parsed);
		var scenarios = AnalyzePositionCommand.GenerateScenarios([longLeg], AnalyzePositionCommand.StructureKind.SingleLong, new AnalyzePositionSettings { IvDefault = 24m, StrikeStep = 1m }, 691m, new DateTime(2026, 11, 20), quotes: null);

		var hold = Assert.Single(scenarios, s => s.Name == "Hold to expiry");
		Assert.True(hold.ExpectedPnLPerContract > hold.TotalPnLPerContract);   // an OTM call is worth more than its intrinsic
		Assert.Equal(("existing", 22), (Assert.Single(hold.Book!).Label, hold.Book![0].Qty));
		var close = Assert.Single(scenarios, s => s.Name == "Close now");
		Assert.Empty(close.Book!);
		Assert.Equal(22m, close.OrderContractsNow);
		var calendar = Assert.Single(scenarios, s => s.Name.StartsWith("Convert to calendar", StringComparison.Ordinal));
		var held = Assert.Single(calendar.Book!);
		Assert.Equal([LegAction.Buy, LegAction.Sell], held.Legs.Select(l => l.Action));
		Assert.All(held.Legs, l => Assert.Equal(1, l.Qty));
		Assert.NotNull(calendar.ExpectedPnLPerContract);
	}
}

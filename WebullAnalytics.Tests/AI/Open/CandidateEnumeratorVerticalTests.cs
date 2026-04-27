using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorVerticalTests
{
	private static OpenerConfig Cfg() => new()
	{
		StrikeStep = 1.0m,
		IvDefaultPct = 40m,
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = false },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			ShortVertical = new OpenerShortVerticalConfig
			{
				Enabled = true,
				DteMin = 3,
				DteMax = 10,
				WidthSteps = new() { 1, 2 },
				ShortDeltaMin = 0.15m,
				ShortDeltaMax = 0.30m
			},
			LongCallPut = new OpenerLongCallPutConfig { Enabled = false }
		}
	};

	[Fact]
	public void ProducesBothCallAndPutSides()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg()).ToList();
		Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.ShortPutVertical);
		Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.ShortCallVertical);
	}

	[Fact]
	public void PutCreditSpreadShortBelowSpot()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg())
			.Where(s => s.StructureKind == OpenStructureKind.ShortPutVertical).ToList();
		foreach (var s in skeletons)
		{
			var shortLeg = s.Legs.First(l => l.Action == "sell");
			var parsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
			Assert.True(parsed.Strike < 50m, $"short strike {parsed.Strike} should be below spot 50 for put credit");
		}
	}

	[Fact]
	public void CallCreditSpreadShortAboveSpot()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg())
			.Where(s => s.StructureKind == OpenStructureKind.ShortCallVertical).ToList();
		foreach (var s in skeletons)
		{
			var shortLeg = s.Legs.First(l => l.Action == "sell");
			var parsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
			Assert.True(parsed.Strike > 50m, $"short strike {parsed.Strike} should be above spot 50 for call credit");
		}
	}

	[Fact]
	public void WidthMatchesConfiguredSteps()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg()).ToList();
		foreach (var s in skeletons.Where(x => x.StructureKind is OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical))
		{
			var shortLeg = s.Legs.First(l => l.Action == "sell");
			var longLeg = s.Legs.First(l => l.Action == "buy");
			var ps = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
			var pl = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol)!;
			var width = Math.Abs(ps.Strike - pl.Strike);
			Assert.Contains(width, new[] { 1.0m, 2.0m });
		}
	}
}

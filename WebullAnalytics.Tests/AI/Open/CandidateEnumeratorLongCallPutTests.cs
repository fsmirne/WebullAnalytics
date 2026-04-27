using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorLongCallPutTests
{
	private static OpenerConfig Cfg() => new()
	{
		StrikeStep = 1.0m,
		IvDefaultPct = 40m,
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = false },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
			LongCallPut = new OpenerLongCallPutConfig
			{
				Enabled = true,
				DteMin = 21,
				DteMax = 60,
				DeltaMin = 0.30m,
				DeltaMax = 0.60m
			}
		}
	};

	[Fact]
	public void ProducesBothLongCallAndLongPut()
	{
		var asOf = new DateTime(2026, 4, 1);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg()).ToList();
		Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongCall);
		Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongPut);
	}

	[Fact]
	public void LongCallIsSingleBuyLeg()
	{
		var asOf = new DateTime(2026, 4, 1);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg())
			.Where(s => s.StructureKind == OpenStructureKind.LongCall).ToList();
		foreach (var s in skeletons)
		{
			Assert.Single(s.Legs);
			Assert.Equal("buy", s.Legs[0].Action);
			var parsed = ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!;
			Assert.Equal("C", parsed.CallPut);
		}
	}

	[Fact]
	public void LongPutIsSingleBuyLeg()
	{
		var asOf = new DateTime(2026, 4, 1);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg())
			.Where(s => s.StructureKind == OpenStructureKind.LongPut).ToList();
		foreach (var s in skeletons)
		{
			Assert.Single(s.Legs);
			Assert.Equal("buy", s.Legs[0].Action);
			var parsed = ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!;
			Assert.Equal("P", parsed.CallPut);
		}
	}
}

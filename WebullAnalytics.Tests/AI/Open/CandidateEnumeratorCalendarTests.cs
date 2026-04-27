using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorCalendarTests
{
	private static OpenerConfig DefaultCfg() => new()
	{
		StrikeStep = 1.0m,
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = true, ShortDteMin = 3, ShortDteMax = 10, LongDteMin = 21, LongDteMax = 60 },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
			LongCallPut = new OpenerLongCallPutConfig { Enabled = false }
		}
	};

	[Fact]
	public void CalendarProducesBothCallAndPutVariants()
	{
		var asOf = new DateTime(2026, 4, 20); // Monday
		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, DefaultCfg()).ToList();
		Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongCalendar && s.Legs.Any(l => l.Symbol.Contains("C00015")));
		Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongCalendar && s.Legs.Any(l => l.Symbol.Contains("P00015")));
	}

	[Fact]
	public void CalendarLegsUseMatchingStrikes()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, DefaultCfg())
			.Where(s => s.StructureKind == OpenStructureKind.LongCalendar).ToList();
		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			Assert.Equal(2, s.Legs.Count);
			var p0 = ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!;
			var p1 = ParsingHelpers.ParseOptionSymbol(s.Legs[1].Symbol)!;
			Assert.Equal(p0.Strike, p1.Strike);
			Assert.Equal(p0.CallPut, p1.CallPut);
			Assert.NotEqual(p0.ExpiryDate, p1.ExpiryDate);
		}
	}

	[Fact]
	public void DiagonalUsesOffsetStrikes()
	{
		var cfg = DefaultCfg();
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.LongDiagonal.Enabled = true;

		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, cfg)
			.Where(s => s.StructureKind == OpenStructureKind.LongDiagonal).ToList();
		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			var shortLeg = s.Legs.First(l => l.Action == "sell");
			var longLeg = s.Legs.First(l => l.Action == "buy");
			var ps = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
			var pl = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol)!;
			Assert.Equal(1.0m, Math.Abs(ps.Strike - pl.Strike));
		}
	}

	[Fact]
	public void DisabledStructureProducesNothing()
	{
		var cfg = DefaultCfg();
		cfg.Structures.LongCalendar.Enabled = false;
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, cfg).ToList();
		Assert.Empty(skeletons);
	}
}

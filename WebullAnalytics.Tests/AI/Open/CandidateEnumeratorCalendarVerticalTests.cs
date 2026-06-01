using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorCalendarVerticalTests
{
	private static OpenerConfig Cfg()
	{
		var cfg = new OpenerConfig { Indicators = new() { IvDefaultPct = 40m, StrikeStep = 1.0m } };
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.DoubleCalendar.Enabled = false;
		cfg.Structures.LongDiagonal.Enabled = false;
		cfg.Structures.DoubleDiagonal.Enabled = false;
		cfg.Structures.IronButterfly.Enabled = false;
		cfg.Structures.IronCondor.Enabled = false;
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.LongCallPut.Enabled = false;
		cfg.Structures.LongVertical.Enabled = false;
		cfg.Structures.DiagonalVertical.Enabled = false;
		cfg.Structures.CalendarVertical = new OpenerCalendarVerticalConfig
		{
			Enabled = true,
			ShortDteMin = 3,
			ShortDteMax = 10,
			LongDteMin = 21,
			LongDteMax = 45,
			DeltaMin = 0.40m,
			DeltaMax = 0.55m,
			WidthSteps = new() { 2, 4 }
		};
		return cfg;
	}

	private static List<CandidateSkeleton> Enumerate() =>
		CandidateEnumerator.Enumerate("SPY", spot: 50m, new DateTime(2026, 4, 20), Cfg())
			.Where(s => s.StructureKind == OpenStructureKind.CalendarVertical)
			.ToList();

	[Fact]
	public void ProducesBothCallAndPutSides()
	{
		var cv = Enumerate();
		Assert.NotEmpty(cv);
		Assert.Contains(cv, s => ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!.CallPut == "C");
		Assert.Contains(cv, s => ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!.CallPut == "P");
	}

	[Fact]
	public void SharesOneAnchorStrikeAcrossTwoExpiries()
	{
		var cv = Enumerate();
		Assert.NotEmpty(cv);
		foreach (var s in cv)
		{
			Assert.Equal(4, s.Legs.Count);
			var parsed = s.Legs.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!).ToList();
			// Single-sided, two expiries, and — the calendar property — exactly two distinct strikes
			// (anchor + wing), each appearing on BOTH expiries. A diagonal-vertical would have 3-4.
			Assert.Single(parsed.Select(p => p.CallPut).Distinct());
			Assert.Equal(2, parsed.Select(p => p.ExpiryDate).Distinct().Count());
			Assert.Equal(2, parsed.Select(p => p.Strike).Distinct().Count());
			Assert.All(parsed.GroupBy(p => p.Strike), g => Assert.Equal(2, g.Select(p => p.ExpiryDate).Distinct().Count()));
		}
	}

	[Fact]
	public void SplitsIntoNearAndFarVertical()
	{
		var cv = Enumerate();
		Assert.NotEmpty(cv);
		foreach (var s in cv)
		{
			var split = StructureOrderSplit.Split(OpenStructureKind.CalendarVertical, s.Legs);
			Assert.Equal(2, split.Count);
			Assert.All(split, g => Assert.Equal(2, g.Legs.Count));
			Assert.Contains(split, g => g.Label == "near vertical");
			Assert.Contains(split, g => g.Label == "far vertical");
		}
	}
}

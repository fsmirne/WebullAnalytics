using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorVerticalTests
{
	private static OpenerConfig Cfg() => new()
	{
		StrikeSteps = new() { ["SPY"] = 1.0m },
		IvDefaultPct = 40m,
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = false },
			DoubleCalendar = new OpenerDoubleCalendarConfig { Enabled = false },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			DoubleDiagonal = new OpenerDoubleDiagonalConfig { Enabled = false },
			IronButterfly = new OpenerIronButterflyConfig { Enabled = false },
			IronCondor = new OpenerIronCondorConfig { Enabled = false },
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

	[Fact]
	public void IronButterflyUsesSharedBodyAndSymmetricWings()
	{
		var cfg = Cfg();
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.IronButterfly.Enabled = true;
		cfg.Structures.IronButterfly.WingSteps = new() { 2 };

		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, cfg)
			.Where(s => s.StructureKind == OpenStructureKind.IronButterfly)
			.ToList();

		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			Assert.Equal(4, s.Legs.Count);
			var parsed = s.Legs.Select(l => (Leg: l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)!)).ToList();
			var shortPut = parsed.Single(x => x.Leg.Action == "sell" && x.Parsed.CallPut == "P").Parsed;
			var shortCall = parsed.Single(x => x.Leg.Action == "sell" && x.Parsed.CallPut == "C").Parsed;
			var longPut = parsed.Single(x => x.Leg.Action == "buy" && x.Parsed.CallPut == "P").Parsed;
			var longCall = parsed.Single(x => x.Leg.Action == "buy" && x.Parsed.CallPut == "C").Parsed;
			Assert.Equal(shortPut.Strike, shortCall.Strike);
			Assert.Equal(2m, shortPut.Strike - longPut.Strike);
			Assert.Equal(2m, longCall.Strike - shortCall.Strike);
		}
	}

	[Fact]
	public void IronCondorUsesSeparatedShortBodyAndSymmetricWings()
	{
		var cfg = Cfg();
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.IronCondor.Enabled = true;
		cfg.Structures.IronCondor.WidthSteps = new() { 2 };
		cfg.Structures.IronCondor.BodyWidthSteps = new() { 2 };

		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, cfg)
			.Where(s => s.StructureKind == OpenStructureKind.IronCondor)
			.ToList();

		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			Assert.Equal(4, s.Legs.Count);
			var parsed = s.Legs.Select(l => (Leg: l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)!)).ToList();
			var shortPut = parsed.Single(x => x.Leg.Action == "sell" && x.Parsed.CallPut == "P").Parsed;
			var shortCall = parsed.Single(x => x.Leg.Action == "sell" && x.Parsed.CallPut == "C").Parsed;
			var longPut = parsed.Single(x => x.Leg.Action == "buy" && x.Parsed.CallPut == "P").Parsed;
			var longCall = parsed.Single(x => x.Leg.Action == "buy" && x.Parsed.CallPut == "C").Parsed;
			Assert.Equal(2m, shortPut.Strike - longPut.Strike);
			Assert.Equal(2m, longCall.Strike - shortCall.Strike);
			Assert.Equal(2m, shortCall.Strike - shortPut.Strike);
		}
	}
}

using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorCalendarTests
{
	private static OpenerConfig DefaultCfg() => new()
	{
		Indicators = new() { IvDefaultPct = 40m, StrikeStep = 1.0m },
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = true, ShortDteMin = 3, ShortDteMax = 10, LongDteMin = 21, LongDteMax = 60 },
			DoubleCalendar = new OpenerDoubleCalendarConfig { Enabled = false },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			DoubleDiagonal = new OpenerDoubleDiagonalConfig { Enabled = false },
			IronButterfly = new OpenerIronButterflyConfig { Enabled = false },
			IronCondor = new OpenerIronCondorConfig { Enabled = false },
			ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
			LongVertical = new OpenerLongVerticalConfig { Enabled = false },
			LongCallPut = new OpenerLongCallPutConfig { Enabled = false },
			DiagonalVertical = new OpenerDiagonalVerticalConfig { Enabled = false },
			CalendarVertical = new OpenerCalendarVerticalConfig { Enabled = false }
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
			// No chain supplied → opener uses the spot-magnitude FallbackStep ($0.50 at spot $15), not the
			// config strikeStep. The diagonal long leg is one step from the short leg.
			Assert.Equal(0.5m, Math.Abs(ps.Strike - pl.Strike));
		}
	}

	[Fact]
	public void DiagonalDeltaBandSpansOtmToItmLongAnchors()
	{
		// Wide long-leg band 0.30–0.75 should enumerate call diagonals whose LONG (buy) leg spans from
		// OTM (delta < 0.5 → strike above spot) to ITM (delta > 0.5 → strike below spot) — not just the
		// 2 strikes nearest the band midpoint. This is what lets the scorer pick ITM call diagonals in a
		// bull regime while OTM stays available in neutral tape.
		var cfg = DefaultCfg();
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.LongDiagonal = new OpenerCalendarLikeConfig
		{
			Enabled = true, ShortDteMin = 3, ShortDteMax = 10, LongDteMin = 21, LongDteMax = 60,
			DeltaMin = 0.30m, DeltaMax = 0.75m, ShortDeltaMin = 0.15m, ShortDeltaMax = 0.30m
		};

		var asOf = new DateTime(2026, 4, 20);
		const decimal spot = 100m;
		var longCallStrikes = CandidateEnumerator.Enumerate("SPY", spot, asOf, cfg)
			.Where(s => s.StructureKind == OpenStructureKind.LongDiagonal)
			.Select(s => s.Legs.First(l => l.Action == "buy"))
			.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!)
			.Where(p => p.CallPut == "C")
			.Select(p => p.Strike)
			.Distinct().ToList();

		Assert.NotEmpty(longCallStrikes);
		Assert.Contains(longCallStrikes, k => k < spot);   // an ITM long call (delta > 0.5)
		Assert.Contains(longCallStrikes, k => k > spot);   // an OTM long call (delta < 0.5)
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

	[Fact]
	public void DoubleCalendarProducesPutAndCallCalendarsAtDifferentStrikes()
	{
		var cfg = DefaultCfg();
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.DoubleCalendar.Enabled = true;
		cfg.Structures.DoubleCalendar.WidthSteps = new() { 2 };

		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, cfg)
			.Where(s => s.StructureKind == OpenStructureKind.DoubleCalendar)
			.ToList();

		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			Assert.Equal(4, s.Legs.Count);
			var puts = s.Legs.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!).Where(p => p.CallPut == "P").ToList();
			var calls = s.Legs.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!).Where(p => p.CallPut == "C").ToList();
			Assert.Equal(2, puts.Count);
			Assert.Equal(2, calls.Count);
			Assert.True(puts[0].Strike < calls[0].Strike);
		}
	}

	[Fact]
	public void DoubleDiagonalUsesOffsetLongStrikesOnBothSides()
	{
		var cfg = DefaultCfg();
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.DoubleDiagonal.Enabled = true;
		cfg.Structures.DoubleDiagonal.WidthSteps = new() { 2 };
		cfg.Structures.DoubleDiagonal.LongWingSteps = new() { 1 };

		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, cfg)
			.Where(s => s.StructureKind == OpenStructureKind.DoubleDiagonal)
			.ToList();

		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			Assert.Equal(4, s.Legs.Count);
			var parsed = s.Legs.Select(l => (Leg: l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)!)).ToList();
			var shortPut = parsed.Single(x => x.Leg.Action == "sell" && x.Parsed.CallPut == "P").Parsed;
			var longPut = parsed.Single(x => x.Leg.Action == "buy" && x.Parsed.CallPut == "P").Parsed;
			var shortCall = parsed.Single(x => x.Leg.Action == "sell" && x.Parsed.CallPut == "C").Parsed;
			var longCall = parsed.Single(x => x.Leg.Action == "buy" && x.Parsed.CallPut == "C").Parsed;
			// No chain → FallbackStep $0.50 at spot $15: longWingSteps=1 → $0.50 wings, widthSteps=2 → $1.00 body.
			Assert.Equal(0.5m, shortPut.Strike - longPut.Strike);
			Assert.Equal(0.5m, longCall.Strike - shortCall.Strike);
			Assert.Equal(1.0m, shortCall.Strike - shortPut.Strike);
		}
	}
}

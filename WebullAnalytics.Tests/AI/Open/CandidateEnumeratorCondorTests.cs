using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorCondorTests
{
	private static OpenerConfig Cfg(string side = "both") => new()
	{
		Indicators = new() { IvDefaultPct = 40m, StrikeStep = 1.0m },
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = false },
			DoubleCalendar = new OpenerDoubleCalendarConfig { Enabled = false },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			DoubleDiagonal = new OpenerDoubleDiagonalConfig { Enabled = false },
			IronButterfly = new OpenerIronButterflyConfig { Enabled = false },
			IronCondor = new OpenerIronCondorConfig { Enabled = false },
			ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
			LongCallPut = new OpenerLongCallPutConfig { Enabled = false },
			Condor = new OpenerCondorConfig
			{
				Enabled = true,
				DteMin = 3,
				DteMax = 10,
				WidthSteps = new() { 2 },
				BodyWidthSteps = new() { 2 },
				ShortDeltaMin = 0.15m,
				ShortDeltaMax = 0.45m,
				Side = side
			}
		}
	};

	private static (decimal outerLow, decimal innerLow, decimal innerHigh, decimal outerHigh, string type) Shape(CandidateSkeleton s)
	{
		var parsed = s.Legs.Select(l => (l.Action, P: ParsingHelpers.ParseOptionSymbol(l.Symbol)!)).OrderBy(x => x.P.Strike).ToList();
		Assert.Equal(4, parsed.Count);
		// Long condor = buy the outer wings, sell the two inner body strikes.
		Assert.Equal("buy", parsed[0].Action);
		Assert.Equal("sell", parsed[1].Action);
		Assert.Equal("sell", parsed[2].Action);
		Assert.Equal("buy", parsed[3].Action);
		Assert.Single(parsed.Select(x => x.P.CallPut).Distinct());   // all one option type
		return (parsed[0].P.Strike, parsed[1].P.Strike, parsed[2].P.Strike, parsed[3].P.Strike, parsed[0].P.CallPut);
	}

	[Fact]
	public void ProducesBothPutAndCallCondors()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg()).ToList();
		Assert.NotEmpty(skeletons);
		Assert.All(skeletons, s => Assert.Equal(OpenStructureKind.Condor, s.StructureKind));
		Assert.Contains(skeletons, s => Shape(s).type == "P");
		Assert.Contains(skeletons, s => Shape(s).type == "C");
	}

	[Fact]
	public void PutCondorSitsBelowSpotWithSymmetricWings()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg("put")).ToList();
		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			var (outerLow, innerLow, innerHigh, outerHigh, type) = Shape(s);
			Assert.Equal("P", type);
			Assert.True(innerHigh < 50m, $"body must sit below spot for a bearish put condor; innerHigh={innerHigh}");
			Assert.Equal(2m, innerHigh - innerLow);          // body width
			Assert.Equal(2m, innerLow - outerLow);           // lower wing
			Assert.Equal(2m, outerHigh - innerHigh);         // upper wing (symmetric)
		}
	}

	[Fact]
	public void CallCondorSitsAboveSpot()
	{
		var asOf = new DateTime(2026, 4, 20);
		var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg("call")).ToList();
		Assert.NotEmpty(skeletons);
		foreach (var s in skeletons)
		{
			var (_, innerLow, _, _, type) = Shape(s);
			Assert.Equal("C", type);
			Assert.True(innerLow > 50m, $"body must sit above spot for a bullish call condor; innerLow={innerLow}");
		}
	}

	[Fact]
	public void SideFilterRestrictsToRequestedType()
	{
		var asOf = new DateTime(2026, 4, 20);
		var putOnly = CandidateEnumerator.Enumerate("SPY", spot: 50m, asOf, Cfg("put")).ToList();
		Assert.NotEmpty(putOnly);
		Assert.All(putOnly, s => Assert.Equal("P", Shape(s).type));
	}
}

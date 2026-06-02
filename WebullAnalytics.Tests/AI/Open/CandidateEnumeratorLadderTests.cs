using WebullAnalytics.AI;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

/// <summary>Proves the variable-grid fix: against a chain that lists $1 strikes near the money and $5 in the
/// wings, the enumerator must only emit legs whose strikes are actually listed — even with strikeStep=1.
/// Before the StrikeLadder change, strikeStep=1 generated $1 wing strikes the chain never listed, every leg
/// came back unpriced, and the candidate was dropped (the live XSP/SPY failure).</summary>
public class CandidateEnumeratorLadderTests
{
	private static readonly DateTime AsOf = new(2026, 4, 20);
	private static readonly DateTime ShortExp = new(2026, 4, 24);
	private static readonly DateTime LongExp = new(2026, 5, 15);

	private static readonly HashSet<decimal> ListedStrikes = BuildListedStrikes();

	private static HashSet<decimal> BuildListedStrikes()
	{
		var set = new HashSet<decimal>();
		for (decimal k = 520m; k <= 590m; k += 5m) set.Add(k);   // lower wing, $5
		for (decimal k = 595m; k <= 605m; k += 1m) set.Add(k);   // ATM, $1
		for (decimal k = 610m; k <= 700m; k += 5m) set.Add(k);   // upper wing, $5
		return set;
	}

	private static Dictionary<string, OptionContractQuote> Chain()
	{
		var d = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var exp in new[] { ShortExp, LongExp })
			foreach (var cp in new[] { "C", "P" })
				foreach (var k in ListedStrikes)
				{
					var sym = MatchKeys.OccSymbol("SPY", exp, k, cp);
					// Bid/ask present; IV ~0.2 so the delta-band filters resolve anchors near ATM.
					d[sym] = new OptionContractQuote(sym, 2m, 1.8m, 2.2m, null, null, 500, 500, 0.20m);
				}
		return d;
	}

	private static OpenerConfig DiagVertCfg()
	{
		var cfg = new OpenerConfig { Indicators = new() { IvDefaultPct = 20m, StrikeStep = 1.0m } };
		foreach (var s in new[]
		{
			cfg.Structures.LongCalendar, cfg.Structures.LongDiagonal,
		}) s.Enabled = false;
		cfg.Structures.DoubleCalendar.Enabled = false;
		cfg.Structures.DoubleDiagonal.Enabled = false;
		cfg.Structures.IronButterfly.Enabled = false;
		cfg.Structures.IronCondor.Enabled = false;
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.LongCallPut.Enabled = false;
		cfg.Structures.LongVertical.Enabled = false;
		cfg.Structures.CalendarVertical.Enabled = false;
		cfg.Structures.DiagonalVertical = new OpenerDiagonalVerticalConfig
		{
			Enabled = true,
			ShortDteMin = 3, ShortDteMax = 10, LongDteMin = 21, LongDteMax = 45,
			LongDeltaMin = 0.40m, LongDeltaMax = 0.55m,
			ShortDeltaMin = 0.20m, ShortDeltaMax = 0.35m,
			WidthSteps = new() { 2, 4 }
		};
		return cfg;
	}

	[Fact]
	public void DiagonalVerticalEmitsOnlyListedStrikesWithStrikeStepOne()
	{
		var avail = new HashSet<DateTime> { ShortExp, LongExp };
		var skels = CandidateEnumerator.Enumerate("SPY", spot: 600m, AsOf, DiagVertCfg(), avail, Chain()).ToList();

		Assert.NotEmpty(skels); // the whole point: strikeStep=1 no longer produces zero candidates
		var legStrikes = skels.SelectMany(s => s.Legs)
			.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!.Strike)
			.Distinct()
			.ToList();
		// Every leg must be a strike the chain actually lists — no fabricated $1 wing strikes in the $5 zone.
		Assert.All(legStrikes, k => Assert.Contains(k, ListedStrikes));
		// And it must actually have reached into the $5 wing region (otherwise we proved nothing).
		Assert.Contains(legStrikes, k => k >= 610m && k % 5m == 0m);
	}

	[Fact]
	public void DiagonalVerticalFarWingCoversNearVerticalLossZone()
	{
		// The fix: the far long vertical's protective (sold) wing must sit at or beyond (OTM of) the near short
		// vertical's protective (bought) wing, so the far long leg keeps gaining through the near vertical's whole
		// loss zone. Otherwise the far vertical caps out above the near short strikes, leaving that max-loss zone
		// unhedged → the sharp downside cliff / max-loss valley (third breakeven) that made the payoff a zigzag.
		var avail = new HashSet<DateTime> { ShortExp, LongExp };
		var skels = CandidateEnumerator.Enumerate("SPY", spot: 600m, AsOf, DiagVertCfg(), avail, Chain())
			.Where(s => s.StructureKind == OpenStructureKind.DiagonalVertical)
			.ToList();
		Assert.NotEmpty(skels);

		foreach (var s in skels)
		{
			var legs = s.Legs.Select(l => (l.Action, P: ParsingHelpers.ParseOptionSymbol(l.Symbol)!)).ToList();
			var side = legs[0].P.CallPut;
			Assert.All(legs, l => Assert.Equal(side, l.P.CallPut)); // single-sided
			var dir = side == "C" ? 1 : -1;

			var farExp = legs.Max(l => l.P.ExpiryDate);
			var nearExp = legs.Min(l => l.P.ExpiryDate);
			Assert.NotEqual(farExp, nearExp);

			var farSell = legs.Single(l => l.P.ExpiryDate == farExp && l.Action == "sell").P.Strike;
			var nearBuy = legs.Single(l => l.P.ExpiryDate == nearExp && l.Action == "buy").P.Strike;
			Assert.True(dir * farSell >= dir * nearBuy, $"far protective wing {farSell} fails to cover near protective wing {nearBuy} (side {side})");
		}
	}

	[Fact]
	public void EmptyChainFallsBackToUniformGrid()
	{
		// No quotes → ladder empty → uniform strikeStep grid; still enumerates (preserves test/--theoretical path).
		var avail = new HashSet<DateTime> { ShortExp, LongExp };
		var skels = CandidateEnumerator.Enumerate("SPY", spot: 600m, AsOf, DiagVertCfg(), avail, quotes: null).ToList();
		Assert.NotEmpty(skels);
	}

	private static OpenerConfig DeltaCalDiagCfg()
	{
		var cfg = new OpenerConfig { Indicators = new() { IvDefaultPct = 20m, StrikeStep = 1.0m } };
		cfg.Structures.DoubleCalendar.Enabled = false;
		cfg.Structures.DoubleDiagonal.Enabled = false;
		cfg.Structures.IronButterfly.Enabled = false;
		cfg.Structures.IronCondor.Enabled = false;
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.LongCallPut.Enabled = false;
		cfg.Structures.LongVertical.Enabled = false;
		cfg.Structures.DiagonalVertical.Enabled = false;
		cfg.Structures.CalendarVertical.Enabled = false;
		cfg.Structures.LongCalendar = new OpenerCalendarLikeConfig { Enabled = true, ShortDteMin = 1, ShortDteMax = 10, LongDteMin = 20, LongDteMax = 40, DeltaMin = 0.40m, DeltaMax = 0.55m };
		cfg.Structures.LongDiagonal = new OpenerCalendarLikeConfig { Enabled = true, ShortDteMin = 1, ShortDteMax = 10, LongDteMin = 20, LongDteMax = 40, DeltaMin = 0.40m, DeltaMax = 0.55m, ShortDeltaMin = 0.20m, ShortDeltaMax = 0.35m };
		return cfg;
	}

	[Fact]
	public void CalendarLike_DeltaBands_PlaceStrikesByDelta()
	{
		// Delta-band placement (deltaMax>0): calendar shares one strike across expiries; diagonal puts the long
		// leg on the far expiry and the short leg further OTM on the near expiry. All legs are listed strikes.
		var avail = new HashSet<DateTime> { ShortExp, LongExp };
		var skels = CandidateEnumerator.Enumerate("SPY", spot: 600m, AsOf, DeltaCalDiagCfg(), avail, Chain()).ToList();

		var calendars = skels.Where(s => s.StructureKind == OpenStructureKind.LongCalendar).ToList();
		var diagonals = skels.Where(s => s.StructureKind == OpenStructureKind.LongDiagonal).ToList();
		Assert.NotEmpty(calendars);
		Assert.NotEmpty(diagonals);

		foreach (var c in calendars)
		{
			var legs = c.Legs.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!).ToList();
			Assert.Equal(2, legs.Count);
			Assert.Single(legs.Select(l => l.Strike).Distinct());               // shared strike
			Assert.Equal(2, legs.Select(l => l.ExpiryDate).Distinct().Count()); // two expiries
		}

		foreach (var d in diagonals)
		{
			var buy = ParsingHelpers.ParseOptionSymbol(d.Legs.Single(l => l.Action == "buy").Symbol)!;
			var sell = ParsingHelpers.ParseOptionSymbol(d.Legs.Single(l => l.Action == "sell").Symbol)!;
			Assert.True(buy.ExpiryDate > sell.ExpiryDate, "long leg is the far expiry");
			var dir = buy.CallPut == "C" ? 1 : -1;
			Assert.True(dir * sell.Strike > dir * buy.Strike, "short leg further OTM than long anchor");
			Assert.Contains(buy.Strike, ListedStrikes);
			Assert.Contains(sell.Strike, ListedStrikes);
		}
	}

	[Fact]
	public void Calendar_DeltaBand_SkipsAnchorNotListedAtShortExpiry()
	{
		// Short expiry lists a $10 grid, long lists $5 (as on real SPXW where weekly/back-month ladders differ).
		// A delta anchor picked off the long ladder that isn't a short-expiry strike must be skipped — else the
		// calendar's near leg is unpriceable. This is the bug that produced zero calendar opens in the backtest.
		var chain = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		void Add(DateTime exp, decimal k) { var s = MatchKeys.OccSymbol("SPY", exp, k, "C"); chain[s] = new OptionContractQuote(s, 2m, 1.8m, 2.2m, null, null, 500, 500, 0.20m); }
		for (decimal k = 560m; k <= 640m; k += 10m) Add(ShortExp, k); // short: $10 grid
		for (decimal k = 560m; k <= 640m; k += 5m) Add(LongExp, k);   // long:  $5 grid
		var shortStrikes = new HashSet<decimal>();
		for (decimal k = 560m; k <= 640m; k += 10m) shortStrikes.Add(k);

		var cfg = DeltaCalDiagCfg();
		cfg.Structures.LongDiagonal.Enabled = false; // calendars only

		var calendars = CandidateEnumerator.Enumerate("SPY", 600m, AsOf, cfg, new HashSet<DateTime> { ShortExp, LongExp }, chain)
			.Where(s => s.StructureKind == OpenStructureKind.LongCalendar).ToList();

		Assert.NotEmpty(calendars);
		foreach (var c in calendars)
			foreach (var leg in c.Legs)
				Assert.Contains(ParsingHelpers.ParseOptionSymbol(leg.Symbol)!.Strike, shortStrikes); // anchor listed at the short expiry
	}
}

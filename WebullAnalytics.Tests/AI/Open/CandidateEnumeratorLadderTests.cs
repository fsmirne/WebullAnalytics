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
	public void EmptyChainFallsBackToUniformGrid()
	{
		// No quotes → ladder empty → uniform strikeStep grid; still enumerates (preserves test/--theoretical path).
		var avail = new HashSet<DateTime> { ShortExp, LongExp };
		var skels = CandidateEnumerator.Enumerate("SPY", spot: 600m, AsOf, DiagVertCfg(), avail, quotes: null).ToList();
		Assert.NotEmpty(skels);
	}
}

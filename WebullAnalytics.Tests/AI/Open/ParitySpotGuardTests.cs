using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class ParitySpotGuardTests
{
	private static readonly DateTime AsOf = new(2026, 8, 3, 9, 30, 0);

	/// <summary>Builds a same-day SPXW chain whose NBBO mids are parity-consistent with <paramref name="bookSpot"/> (C − P = S − K at every strike, DTE 0 so the discount is 1).</summary>
	private static Dictionary<string, OptionContractQuote> ChainPricedAt(decimal bookSpot, params decimal[] strikes)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var k in strikes)
		{
			// Symmetric 10-point extrinsic on both rights keeps mids two-sided and positive without moving the C−P difference off parity.
			var call = Math.Max(0m, bookSpot - k) + 10m;
			var put = Math.Max(0m, k - bookSpot) + 10m;
			quotes[MatchKeys.OccSymbol("SPXW", AsOf.Date, k, "C")] = new OptionContractQuote(MatchKeys.OccSymbol("SPXW", AsOf.Date, k, "C"), null, call - 0.5m, call + 0.5m, null, null, null, null, null);
			quotes[MatchKeys.OccSymbol("SPXW", AsOf.Date, k, "P")] = new OptionContractQuote(MatchKeys.OccSymbol("SPXW", AsOf.Date, k, "P"), null, put - 0.5m, put + 0.5m, null, null, null, null, null);
		}
		return quotes;
	}

	[Fact]
	public void FiresOnComposedOpenLag()
	{
		// The 2026-08-03 09:30 shape: bar-midpoint spot 7511.72, option book pricing ~7520.7 (12 bp apart).
		var quotes = ChainPricedAt(7520.7m, 7505m, 7510m, 7515m, 7520m, 7525m);
		var corrected = ParitySpotGuard.Correct("SPXW", 7511.72m, quotes, AsOf);
		Assert.NotNull(corrected);
		Assert.Equal(7520.7m, corrected.Value, 1);
	}

	[Fact]
	public void StandsWithinTolerance()
	{
		// 7520.7 vs 7520.4 is under 1 bp — normal parity/mid noise, the reported spot stands.
		var quotes = ChainPricedAt(7520.7m, 7505m, 7510m, 7515m, 7520m, 7525m);
		Assert.Null(ParitySpotGuard.Correct("SPXW", 7520.4m, quotes, AsOf));
		// ~7 bp is within the fast-minute convention-skew band observed 2026-08-03/04 (alternating-sign
		// jitter up to 8.2 bp on the management walk) — still no correction.
		Assert.Null(ParitySpotGuard.Correct("SPXW", 7515.5m, quotes, AsOf));
	}

	[Fact]
	public void IgnoresEquityRoots()
	{
		// SPY's spot is a real traded print and a pending dividend legitimately shifts parity below spot — the guard must never touch non-index roots.
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[MatchKeys.OccSymbol("SPY", AsOf.Date, 750m, "C")] = new(MatchKeys.OccSymbol("SPY", AsOf.Date, 750m, "C"), null, 2.9m, 3.1m, null, null, null, null, null),
			[MatchKeys.OccSymbol("SPY", AsOf.Date, 750m, "P")] = new(MatchKeys.OccSymbol("SPY", AsOf.Date, 750m, "P"), null, 0.9m, 1.1m, null, null, null, null, null),
		};
		Assert.Null(ParitySpotGuard.Correct("SPY", 749.66m, quotes, AsOf));
	}

	[Fact]
	public void StandsWithoutTwoSidedStraddle()
	{
		// Calls only — no strike has both rights quoted, so parity is unsolvable and the reported spot stands.
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "C")] = new(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "C"), null, 18.5m, 19.5m, null, null, null, null, null),
		};
		Assert.Null(ParitySpotGuard.Correct("SPXW", 7511.72m, quotes, AsOf));
	}

	[Fact]
	public void StandsOutsideRth()
	{
		// Premarket books can be frozen at the prior session's two-sided NBBO (passes the strict-mids test while echoing yesterday's spot) — that window belongs to PremarketSpotOverride's bar cross-check, so the guard must stay out of it.
		var quotes = ChainPricedAt(7520.7m, 7505m, 7510m, 7515m, 7520m, 7525m);
		Assert.Null(ParitySpotGuard.Correct("SPXW", 7511.72m, quotes, new DateTime(2026, 8, 3, 9, 0, 0)));
		Assert.Null(ParitySpotGuard.Correct("SPXW", 7511.72m, quotes, new DateTime(2026, 8, 3, 16, 0, 0)));
	}

	[Fact]
	public void ProbeSymbolsBracketAtm()
	{
		// Nearest strike to 7511.72 on the $5 grid is 7510; the probes bracket it: 7505/7510/7515, both rights each.
		var symbols = ParitySpotGuard.ProbeSymbols("SPXW", AsOf.Date, 7511.72m, 5m).ToList();
		Assert.Equal(6, symbols.Count);
		Assert.Contains(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7505m, "C"), symbols);
		Assert.Contains(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7510m, "C"), symbols);
		Assert.Contains(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7510m, "P"), symbols);
		Assert.Contains(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "P"), symbols);
	}

	[Fact]
	public void ProbeSymbolsEmptyForEquityRoots()
	{
		Assert.Empty(ParitySpotGuard.ProbeSymbols("SPY", AsOf.Date, 750m, 1m));
	}

	[Fact]
	public void RejectsLastPriceEchoes()
	{
		// Quote-less chain that only carries last prices (the frozen-book premarket shape) must not solve — a last-price echo would just reproduce the stale spot as "fresh".
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "C")] = new(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "C"), 19m, null, null, null, null, null, null, null),
			[MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "P")] = new(MatchKeys.OccSymbol("SPXW", AsOf.Date, 7515m, "P"), 13m, null, null, null, null, null, null, null),
		};
		Assert.Null(ParitySpotGuard.Correct("SPXW", 7511.72m, quotes, AsOf));
	}
}

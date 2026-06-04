using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

/// <summary>The scorer's market-vs-theoretical edge (the stat-arb factor that feeds candidate selection,
/// and therefore backtest fills) must price on the dividend-adjusted forward. These pin that the dividend
/// actually moves the theoretical — so the opener fix that feeds the TRUE next ex-date into the scorer
/// (vs the event-cache's stale projection) changes scoring as intended.</summary>
public class CandidateScorerDividendTests
{
	private const string Occ = "SPY260213C00410000"; // SPY $410 call, expiry 2026-02-13
	private static readonly DateTime AsOf = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Unspecified);

	private static (IReadOnlyList<(string, OptionParsed, bool)> Legs, Dictionary<string, OptionContractQuote> Quotes) Setup()
	{
		var parsed = ParsingHelpers.ParseOptionSymbol(Occ)!;
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[Occ] = TestQuote.Q(bid: 9.90m, ask: 10.10m, iv: 0.20m),
		};
		return (new[] { (Occ, parsed, true) }, quotes);
	}

	[Fact]
	public void DividendInWindow_LowersCallTheoretical()
	{
		var (legs, quotes) = Setup();

		var noDiv = CandidateScorer.ComputeMarketTheoreticalAggregate(legs, 405m, AsOf, quotes, defaultIvPct: 20m, events: null);
		var withDiv = CandidateScorer.ComputeMarketTheoreticalAggregate(legs, 405m, AsOf, quotes, defaultIvPct: 20m,
			events: new TickerEvents("SPY", null, null, new DateTime(2026, 1, 30), 1.50m)); // ex inside the leg's life

		Assert.NotNull(noDiv);
		Assert.NotNull(withDiv);
		Assert.Equal(noDiv!.Value.MarketNet, withDiv!.Value.MarketNet); // market mid unchanged
		Assert.True(withDiv.Value.TheoreticalNet < noDiv.Value.TheoreticalNet,
			$"dividend in window must lower the call theoretical: {withDiv.Value.TheoreticalNet} vs {noDiv.Value.TheoreticalNet}");
	}

	[Fact]
	public void DividendAfterExpiry_NoChange()
	{
		var (legs, quotes) = Setup();

		var noDiv = CandidateScorer.ComputeMarketTheoreticalAggregate(legs, 405m, AsOf, quotes, 20m, events: null);
		var afterExpiry = CandidateScorer.ComputeMarketTheoreticalAggregate(legs, 405m, AsOf, quotes, 20m,
			events: new TickerEvents("SPY", null, null, new DateTime(2026, 3, 20), 1.50m)); // ex after expiry → out of window

		Assert.Equal(noDiv!.Value.TheoreticalNet, afterExpiry!.Value.TheoreticalNet);
	}
}

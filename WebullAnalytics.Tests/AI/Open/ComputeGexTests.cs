using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class ComputeGexTests
{
	private static OptionContractQuote Q(long oi, decimal iv) => new(
		ContractSymbol: "ignored",
		LastPrice: null,
		Bid: null,
		Ask: null,
		Change: null,
		PercentChange: null,
		Volume: null,
		OpenInterest: oi,
		ImpliedVolatility: iv);

	/// <summary>Replicates the GME 2026-05-08 case: $25 has heavy calls AND heavy puts, $26 has heavier calls
	/// but almost no puts. Under the old net (call−put) rule the pin was $26; under the new gross (call+put)
	/// rule the gravity strike is $25 — matching Barchart's reported gravity.</summary>
	[Fact]
	public void GexGravityPicksStrikeWithHighestGrossCallPlusPut()
	{
		var expiry = new DateTime(2026, 5, 8);
		var asOf = new DateTime(2026, 5, 6);
		var spot = 25.17m;
		var iv = 0.55m;

		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[OccSymbol("GME", expiry, 25.00m, "C")] = Q(15346, iv),
			[OccSymbol("GME", expiry, 25.00m, "P")] = Q(4394, iv),
			[OccSymbol("GME", expiry, 26.00m, "C")] = Q(24043, iv),
			[OccSymbol("GME", expiry, 26.00m, "P")] = Q(1084, iv),
		};

		var result = CandidateScorer.ComputeGex("GME", expiry, spot, asOf, quotes);

		Assert.Equal(25.00m, result.GexGravity);
		Assert.True(result.NetGexFraction > 0m, "call gamma dominates the chain");
	}

	[Fact]
	public void GexGravityIsNullWhenNoStrikesHaveOiOrIv()
	{
		var expiry = new DateTime(2026, 5, 8);
		var asOf = new DateTime(2026, 5, 6);
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[OccSymbol("GME", expiry, 25.00m, "C")] = Q(0, 0.5m),
			[OccSymbol("GME", expiry, 26.00m, "P")] = Q(100, 0m),
		};

		var result = CandidateScorer.ComputeGex("GME", expiry, 25m, asOf, quotes);

		Assert.Null(result.GexGravity);
		Assert.Equal(0m, result.NetGexFraction);
	}

	[Fact]
	public void NetGexFractionIsPositiveWhenCallsDominateAndNegativeWhenPutsDominate()
	{
		var expiry = new DateTime(2026, 5, 8);
		var asOf = new DateTime(2026, 5, 6);
		var spot = 25m;
		var iv = 0.5m;

		var callHeavy = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[OccSymbol("GME", expiry, 25m, "C")] = Q(10000, iv),
			[OccSymbol("GME", expiry, 25m, "P")] = Q(1000, iv),
		};
		var putHeavy = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[OccSymbol("GME", expiry, 25m, "C")] = Q(1000, iv),
			[OccSymbol("GME", expiry, 25m, "P")] = Q(10000, iv),
		};

		Assert.True(CandidateScorer.ComputeGex("GME", expiry, spot, asOf, callHeavy).NetGexFraction > 0m);
		Assert.True(CandidateScorer.ComputeGex("GME", expiry, spot, asOf, putHeavy).NetGexFraction < 0m);
	}

	/// <summary>Standard-monthly (3rd Friday) SPX expiries fragment OI across two roots: legacy SPX
	/// (AM-settled) and SPXW (PM-settled). A gravity request for either root on that date must pick up
	/// both, or it reads a fraction of the real book — reproduces the live 2026-08-25 finding where
	/// SPXW-only gravity at 2026-10-16 landed on the opposite side of spot from SPY and from SPX-only.</summary>
	[Fact]
	public void GexMergesSpxAndSpxwRootsOnStandardMonthlyExpiry()
	{
		var monthlyExpiry = new DateTime(2026, 10, 16); // 3rd Friday of October 2026
		var asOf = new DateTime(2026, 8, 25);
		var spot = 7673.73m;
		var iv = 0.15m;

		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			// SPXW-only book: light OI, gravity lands above spot.
			[OccSymbol("SPXW", monthlyExpiry, 7700m, "C")] = Q(500, iv),
			[OccSymbol("SPXW", monthlyExpiry, 7700m, "P")] = Q(500, iv),
			// Legacy SPX-rooted OI at the same expiry dwarfs it, and sits below spot.
			[OccSymbol("SPX", monthlyExpiry, 7600m, "C")] = Q(20000, iv),
			[OccSymbol("SPX", monthlyExpiry, 7600m, "P")] = Q(20000, iv),
		};

		var resultForSpxw = CandidateScorer.ComputeGex("SPXW", monthlyExpiry, spot, asOf, quotes);
		var resultForSpx = CandidateScorer.ComputeGex("SPX", monthlyExpiry, spot, asOf, quotes);

		Assert.Equal(7600m, resultForSpxw.GexGravity);
		Assert.Equal(7600m, resultForSpx.GexGravity);

		var maxPain = CandidateScorer.ComputeMaxPainPrice("SPXW", monthlyExpiry, quotes, spot);
		Assert.Equal(7600m, maxPain);
	}

	/// <summary>Every non-monthly SPXW date (dailies, non-3rd-Friday weeklies) only ever lists under the
	/// SPXW root — there's no legacy SPX contract to merge, and the fix must not reach across roots there.</summary>
	[Fact]
	public void GexDoesNotMergeRootsOnNonMonthlyExpiry()
	{
		var weeklyExpiry = new DateTime(2026, 10, 9); // Friday, but 2nd Friday - not the monthly
		var asOf = new DateTime(2026, 8, 25);
		var spot = 7673.73m;
		var iv = 0.15m;

		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[OccSymbol("SPXW", weeklyExpiry, 7700m, "C")] = Q(500, iv),
			[OccSymbol("SPXW", weeklyExpiry, 7700m, "P")] = Q(500, iv),
			// A stray SPX-rooted symbol at this date shouldn't exist in real data, and must not be picked up.
			[OccSymbol("SPX", weeklyExpiry, 7600m, "C")] = Q(20000, iv),
			[OccSymbol("SPX", weeklyExpiry, 7600m, "P")] = Q(20000, iv),
		};

		var result = CandidateScorer.ComputeGex("SPXW", weeklyExpiry, spot, asOf, quotes);

		Assert.Equal(7700m, result.GexGravity);
	}

	private static string OccSymbol(string root, DateTime expiry, decimal strike, string callPut)
	{
		var strikeMillis = (long)(strike * 1000m);
		return $"{root}{expiry:yyMMdd}{callPut}{strikeMillis:D8}";
	}
}

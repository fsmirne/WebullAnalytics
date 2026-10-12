using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerPnlRangeTests
{
	// SPXW 2026-10-12 iron fly 7780/7800/7820 for a $14.025 credit at spot 7812.81. The spot×0.40–1.60 grid steps ~$26 and lands on spot, never
	// on the 7800 body, so the scan used to report the P&L at spot ($121.50) as max profit instead of the full $1,402.50 credit.
	private static decimal IronFlyPnl(decimal s) =>
		(Math.Max(0m, 7780m - s) - Math.Max(0m, 7800m - s) - Math.Max(0m, s - 7800m) + Math.Max(0m, s - 7820m) + 14.025m) * 100m;

	[Fact]
	public void IronFlyMaxProfitIsTheCreditAtTheBodyStrike()
	{
		const decimal spot = 7812.81m;
		var (maxProfit, maxLoss) = CandidateScorer.ScanPnlRange(IronFlyPnl, spot * 0.40m, spot * 1.60m, new[] { 7780m, 7800m, 7800m, 7820m });

		Assert.Equal(1402.5m, maxProfit);
		Assert.Equal(-597.5m, maxLoss);
	}

	[Fact]
	public void StrikesOutsideTheRangeAreIgnored()
	{
		var (maxProfit, _) = CandidateScorer.ScanPnlRange(IronFlyPnl, 7805m, 7900m, new[] { 7780m, 7800m, 7820m }, steps: 19);

		Assert.Equal(1402.5m - 500m, maxProfit); // best in-range point is the 7805 endpoint, not the excluded 7800 body
	}
}

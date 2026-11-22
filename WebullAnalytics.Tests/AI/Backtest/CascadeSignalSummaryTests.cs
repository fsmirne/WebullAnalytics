using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// The cascade signal summary groups skip reasons by kind (numbers blanked) but must show real reasons: it printed the grouping key itself,
// "1× #/# call spread mid credit # is under minCredit #".
public class CascadeSignalSummaryTests
{
	[Fact]
	public void ASingleReason_ReadsWithItsNumbers()
	{
		Assert.Equal("1× $7,650/$7,660 call spread mid credit $0.07 is under minCredit $0.30",
			BacktestRunner.SummarizeReasons(["$7,650/$7,660 call spread mid credit $0.07 is under minCredit $0.30"]));
	}

	[Fact]
	public void RepeatedReasons_CountByKind_AndShowTheLatest()
	{
		var summary = BacktestRunner.SummarizeReasons([
			"$7,650/$7,660 call spread mid credit $0.07 is under minCredit $0.30",
			"risk budget $1420 (10% of $14204) below $3295 per contract",
			"$7,700/$7,720 call spread mid credit $0.12 is under minCredit $0.30",
		]);

		Assert.Equal("2×, latest: $7,700/$7,720 call spread mid credit $0.12 is under minCredit $0.30, 1× risk budget $1420 (10% of $14204) below $3295 per contract", summary);
		Assert.DoesNotContain("#", summary);
	}
}

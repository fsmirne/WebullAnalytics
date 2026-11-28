using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Open positions are marked at the session's closing book, not at the 09:30 step instant. Marking at the step valued a
// position opened at 09:30 at its own fill: a 702C/701C diagonal opened 2026-11-25 at SPY 702.63 (close 716.70)
// carried a window-end unrealized of only −$18.90 (fees), and every day's equity point missed that day's move.
// The mark reads the last RTH minute's row — the book struck at the close — not the close instant as a label, which is
// the book one minute after the close.
public class BacktestSessionCloseMarkTests
{
	[Fact]
	public void RegularSessionMarksTheBookStruckAt1600()
	{
		var label = BacktestRunner.SessionCloseMarkLabelEt(new DateTime(2026, 11, 25, 9, 30, 0));
		Assert.Equal(new DateTime(2026, 11, 25, 15, 59, 0), label);
		Assert.Equal(new DateTime(2026, 11, 25, 16, 0, 0), QuoteStoreCache.BookInstant(label));
	}

	[Fact]
	public void HalfDayMarksTheBookStruckAt1300()
	{
		var label = BacktestRunner.SessionCloseMarkLabelEt(new DateTime(2026, 11, 27, 9, 30, 0));
		Assert.Equal(new DateTime(2026, 11, 27, 12, 59, 0), label);
		Assert.Equal(new DateTime(2026, 11, 27, 13, 0, 0), QuoteStoreCache.BookInstant(label));
	}
}

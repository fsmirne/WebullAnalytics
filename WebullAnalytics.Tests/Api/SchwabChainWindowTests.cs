using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

// Schwab rejects a chains response over its body cap, and only after building it (a 30-day $SPX request at range=ALL took 10.5 s to be refused). Once a window is known too wide the
// next fetch starts from windows that fit, so the windows must tile the requested range exactly: a gap would drop expiries from the chain, an overlap would fetch some twice.
public class SchwabChainWindowTests
{
	private static readonly DateOnly Start = new(2026, 11, 19);

	[Fact]
	public void AnUnboundedSpanIsOneWindow()
	{
		var windows = SchwabOptionsClient.PlanWindows(Start, Start.AddDays(30), int.MaxValue);
		Assert.Equal([(Start, Start.AddDays(30))], windows);
	}

	[Fact]
	public void ASpanWiderThanTheRangeIsOneWindow()
	{
		Assert.Equal([(Start, Start.AddDays(7))], SchwabOptionsClient.PlanWindows(Start, Start.AddDays(7), 15));
	}

	[Fact]
	public void TheLearnedSpanSplitsTheRangeIntoWindowsThatTileIt()
	{
		// The 30-day request is 31 days inclusive; its failed half is 15 days.
		var windows = SchwabOptionsClient.PlanWindows(Start, Start.AddDays(30), 15);
		Assert.Equal([(Start, Start.AddDays(14)), (Start.AddDays(15), Start.AddDays(29)), (Start.AddDays(30), Start.AddDays(30))], windows);
	}

	[Theory]
	[InlineData(0, 45, 7)]
	[InlineData(0, 45, 1)]
	[InlineData(0, 6, 3)]
	[InlineData(3, 3, 5)]
	public void WindowsCoverEveryDayOnceAndNeverExceedTheSpan(int fromOffset, int toOffset, int span)
	{
		var from = Start.AddDays(fromOffset);
		var to = Start.AddDays(toOffset);
		var windows = SchwabOptionsClient.PlanWindows(from, to, span);
		Assert.Equal(from, windows[0].From);
		Assert.Equal(to, windows[^1].To);
		Assert.All(windows, w => Assert.InRange(w.To.DayNumber - w.From.DayNumber + 1, 1, span));
		for (var i = 1; i < windows.Count; i++) Assert.Equal(windows[i - 1].To.AddDays(1), windows[i].From);
	}

	[Fact]
	public void ANonPositiveSpanIsTreatedAsOneDay()
	{
		var windows = SchwabOptionsClient.PlanWindows(Start, Start.AddDays(2), 0);
		Assert.Equal(3, windows.Count);
	}

	private static OptionContractQuote Quote(string symbol) => new(symbol, null, 1m, 1.1m, null, null, null, 100, 0.2m);

	[Fact]
	public void WindowsMergeIntoOneChain()
	{
		var merged = SchwabOptionsClient.Merge([(7000m, [Quote("SPXW261120C07000000")]), (null, [Quote("SPXW261127C07000000")])]);
		Assert.NotNull(merged);
		Assert.Equal(7000m, merged.Value.Spot);
		Assert.Equal(2, merged.Value.Quotes.Count);
	}

	[Fact]
	public void AFailedWindowFailsTheWholeChain()
	{
		// One window drew a 429: the rest are not a chain missing a span of expiries, they are no chain.
		Assert.Null(SchwabOptionsClient.Merge([(7000m, [Quote("SPXW261120C07000000")]), null]));
	}
}

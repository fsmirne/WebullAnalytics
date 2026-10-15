using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Covers FillsReplayLoader (--opens): a --fills-jsonl ledger's Open rows become replay opens at their booked leg
// prices with slippage re-applied; Close rows and out-of-window opens are ignored.
public class FillsReplayLoaderTests
{
	private const string OpenRow = "{\"ts\":\"2022-01-05T14:34:00\",\"ticker\":\"SPY\",\"key\":\"K1\",\"kind\":\"Open\",\"strategy\":\"LongDiagonal\",\"spot\":472.75,\"qty\":1,\"net\":-425.000,\"fees\":0.10,\"rawScore\":0.0094,\"finalScore\":0.0022,\"iv\":0.1343,\"rule\":null,\"lineage\":1,\"legs\":[{\"sym\":\"SPY220110P00472000\",\"side\":\"Sell\",\"qty\":1,\"price\":2.575},{\"sym\":\"SPY220126P00474000\",\"side\":\"Buy\",\"qty\":1,\"price\":6.805}]}";
	private const string CloseRow = "{\"ts\":\"2022-01-07T10:00:00\",\"ticker\":\"SPY\",\"key\":\"K1\",\"kind\":\"Close\",\"strategy\":\"LongDiagonal\",\"spot\":470,\"qty\":1,\"net\":500,\"fees\":0.10,\"rawScore\":null,\"finalScore\":null,\"iv\":null,\"rule\":\"TakeProfitRule\",\"lineage\":1,\"legs\":[{\"sym\":\"SPY220110P00472000\",\"side\":\"Buy\",\"qty\":1,\"price\":2.0},{\"sym\":\"SPY220126P00474000\",\"side\":\"Sell\",\"qty\":1,\"price\":7.0}]}";

	private static IReadOnlyList<ProposalReplayOpen> Load(DateTime since, DateTime until)
	{
		var path = Path.GetTempFileName();
		try
		{
			File.WriteAllLines(path, new[] { OpenRow, CloseRow });
			var (opens, warnings) = FillsReplayLoader.Load(path, since, until);
			Assert.Empty(warnings);
			return opens;
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void OpenRows_BecomeReplayOpens_AtBookedPrices()
	{
		var o = Assert.Single(Load(new DateTime(2022, 1, 1), new DateTime(2022, 1, 31)));
		Assert.Equal(new DateTime(2022, 1, 5, 14, 34, 0), o.OpenEt);
		Assert.Equal(OpenStructureKind.LongDiagonal, o.StructureKind);
		Assert.Equal(1, o.Qty);
		Assert.Equal(472.75m, o.Spot);
		Assert.Equal(0.1343m, o.RepIv);
		Assert.True(o.ApplySlippage);
		Assert.Collection(o.Legs,
			l => { Assert.Equal("sell", l.Action); Assert.Equal("SPY220110P00472000", l.Symbol); Assert.Equal(2.575m, l.PricePerShare); Assert.Null(l.ExecutionPricePerShare); },
			l => { Assert.Equal("buy", l.Action); Assert.Equal(6.805m, l.PricePerShare); });
	}

	[Fact]
	public void OutOfWindow_Skipped() => Assert.Empty(Load(new DateTime(2022, 1, 6), new DateTime(2022, 1, 31)));
}

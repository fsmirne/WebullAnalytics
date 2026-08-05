using WebullAnalytics;
using WebullAnalytics.IO;
using Xunit;

namespace WebullAnalytics.Tests.IO;

// Guards the equity-leg fix: Webull covered-stock tickets (buy shares + sell calls, one combo order,
// same transactTime) carry the share fill as symbol "GME" / subSymbol "EQUITY", which the two option
// regexes used to reject — the shares silently vanished from the report (live 2026-08-04: 2500 GME
// shares + 25 covered calls reported as calls only). Equity fills now become standalone Asset.Stock
// trades; the option legs keep their normal combo treatment; the per-ticker combined break-even panel
// merges the units downstream.
public class JsonlParserEquityLegTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"wa-jsonl-equity-test-{Guid.NewGuid():N}.jsonl");

	public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

	private void WriteOrders(params (string Symbol, string Sub, string Action, string Qty, string Price, long TransactTime)[] rows)
	{
		var list = rows.Select(r => $"{{\"orderId\":\"{Guid.NewGuid():N}\",\"symbol\":\"{r.Symbol}\",\"subSymbol\":\"{r.Sub}\",\"filledTime\":\"08/04/2026 11:25:29 EDT\",\"transactTime\":{r.TransactTime},\"currency\":\"USD\",\"action\":\"{r.Action}\",\"quantity\":\"{r.Qty}\",\"filledPrice\":\"{r.Price}\",\"fee\":\"0.01\",\"commission\":\"0.00\",\"tickerType\":\"{(r.Sub == "EQUITY" ? "EQUITY" : "OPTION")}\",\"ticker\":{{}}}}");
		File.WriteAllText(_path, $"{{\"orderList\":[{string.Join(",", list)}]}}\n");
	}

	[Fact]
	public void CoveredCallTicket_EmitsStockTradeAndStandaloneCall()
	{
		// The live 2026-08-04 covered-stock fill verbatim: 2500 shares + 25 short calls, one second.
		WriteOrders(
			("GME", "EQUITY", "BUY", "2500.00000", "18.99", 1785857129000),
			("GME $19.50", "07 Aug 26 Call 100", "SELL", "25.00000", "0.1904", 1785857129000));

		var (trades, fees) = JsonlParser.ParseOrdersJsonl(_path);

		var stock = Assert.Single(trades, t => t.Asset == Asset.Stock);
		Assert.Equal("GME", stock.Instrument);
		Assert.Equal(MatchKeys.Stock("GME"), stock.MatchKey);
		Assert.Equal(Side.Buy, stock.Side);
		Assert.Equal(2500, stock.Qty);
		Assert.Equal(18.99m, stock.Price);
		Assert.Equal(Trade.StockMultiplier, stock.Multiplier);
		Assert.Null(stock.Expiry);
		Assert.Null(stock.ParentStrategySeq);
		Assert.Equal(0.01m, stock.Fee);
		Assert.Equal(0.01m, fees[(stock.Timestamp, Side.Buy, 2500)]);

		// The call must NOT be absorbed into a phantom strategy parent with the shares.
		var option = Assert.Single(trades, t => t.Asset == Asset.Option);
		Assert.Equal(Side.Sell, option.Side);
		Assert.Equal(25, option.Qty);
		Assert.Null(option.ParentStrategySeq);
		Assert.DoesNotContain(trades, t => t.Asset == Asset.OptionStrategy);
	}

	[Fact]
	public void EquityFill_SameSecondAsOptionCombo_LeavesComboIntact()
	{
		// Shares plus a 2-leg option roll in the same second: the roll must still build its strategy parent.
		WriteOrders(
			("GME", "EQUITY", "BUY", "2500.00000", "18.99", 1785857129000),
			("GME $19.50", "07 Aug 26 Call 100", "BUY", "25.00000", "0.18", 1785857129000),
			("GME $19.00", "07 Aug 26 Call 100", "SELL", "25.00000", "0.37", 1785857129000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);

		Assert.Single(trades, t => t.Asset == Asset.Stock);
		var parent = Assert.Single(trades, t => t.Asset == Asset.OptionStrategy);
		Assert.Equal(2, trades.Count(t => t.Asset == Asset.Option && t.ParentStrategySeq == parent.Seq));
	}

	[Fact]
	public void EquityOnlyGroup_EmitsStockTradeWithoutCrashing()
	{
		WriteOrders(("GME", "EQUITY", "SELL", "2500.00000", "19.00", 1785857129000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);

		var stock = Assert.Single(trades);
		Assert.Equal(Asset.Stock, stock.Asset);
		Assert.Equal(Side.Sell, stock.Side);
	}
}

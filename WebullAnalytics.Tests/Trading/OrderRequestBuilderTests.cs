using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Trading;

public class OrderRequestBuilderTests
{
	private static readonly IReadOnlyList<ParsedLeg> Legs = TradeLegParser.Parse("buy:SPY261117P00760000:1,sell:SPY261127P00759000:1");

	[Fact]
	public void Build_DefaultsToCoreSession()
	{
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams("acct", Legs, "diagonal", "SELL", "LIMIT", 10m, "GTC"));
		Assert.Equal("CORE", body.NewOrders[0].SupportTradingSession);
	}

	[Fact]
	public void Build_SendsRequestedSession()
	{
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams("acct", Legs, "diagonal", "SELL", "LIMIT", 10m, "GTC", TradingSession: "ALL"));
		Assert.Equal("ALL", body.NewOrders[0].SupportTradingSession);
	}

	private static readonly IReadOnlyList<ParsedLeg> SingleCall = TradeLegParser.Parse("buy:SPY261218C00700000:2");

	[Fact]
	public void Build_StopLimitOrder_CarriesStopAndLimit()
	{
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams("acct", Legs, "diagonal", "SELL", "STOP_LOSS_LIMIT", 2.28m, "GTC", StopPrice: 2.40m));
		var order = Assert.Single(body.NewOrders);
		Assert.Equal("STOP_LOSS_LIMIT", order.OrderType);
		Assert.Equal("2.28", order.LimitPrice);
		Assert.Equal("2.4", order.StopPrice);
	}

	[Fact]
	public void Build_StopMarket_HasNoLimit()
	{
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams("acct", SingleCall, "Single", "SELL", "STOP_LOSS", null, "DAY", StopPrice: 1.5m));
		Assert.Null(body.NewOrders[0].LimitPrice);
		Assert.Equal("1.5", body.NewOrders[0].StopPrice);
	}

	[Fact]
	public void Build_ProtectiveStop_AddsAStopLossChildThatClosesTheSameContract()
	{
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams("acct", SingleCall, "Single", "BUY", "LIMIT", 5.00m, "DAY", PositionIntent: "BUY_TO_OPEN", ProtectiveStop: 4.00m));

		Assert.False(string.IsNullOrEmpty(body.ClientComboOrderId));
		Assert.Equal(2, body.NewOrders.Count);
		var (master, child) = (body.NewOrders[0], body.NewOrders[1]);
		Assert.Equal("MASTER", master.ComboType);
		Assert.Equal("LIMIT", master.OrderType);
		Assert.Equal("STOP_LOSS", child.ComboType);
		Assert.Equal("STOP_LOSS", child.OrderType);
		Assert.Equal("4", child.StopPrice);
		Assert.Null(child.LimitPrice);
		Assert.Equal("GTC", child.TimeInForce);
		Assert.Equal("SELL", child.Side);
		Assert.Equal("SELL_TO_CLOSE", child.PositionIntent);
		Assert.Equal("2", child.Quantity);
		Assert.NotEqual(master.ClientOrderId, child.ClientOrderId);
		var leg = Assert.Single(child.Legs!);
		Assert.Equal("SELL", leg.Side);
		Assert.Equal("700", leg.StrikePrice);
		Assert.Equal("2026-12-18", leg.OptionExpireDate);
	}

	[Fact]
	public void Build_NoProtectiveStop_IsAPlainOrder()
	{
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams("acct", SingleCall, "Single", "BUY", "LIMIT", 5.00m, "DAY"));
		Assert.Single(body.NewOrders);
		Assert.Null(body.ClientComboOrderId);
		Assert.Equal("NORMAL", body.NewOrders[0].ComboType);
	}
}

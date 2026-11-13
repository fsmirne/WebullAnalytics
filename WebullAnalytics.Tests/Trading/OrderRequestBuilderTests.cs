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
}

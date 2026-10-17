using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class WebullOptionRankClientTests
{
	[Fact]
	public void ParseResponse_ContractRow_MapsDerivativeAndValues()
	{
		const string json = """
		{"rankType":"volume","direction":-1,"hasMore":true,"latestUpdateTime":1792209575003,"hasOpraPermission":true,"data":[
		  {"derivative":{"symbol":"SPY261030P00750000","unSymbol":"SPY","direction":"put","expireDate":"2026-10-30","strikePrice":"750"},
		   "values":{"volume":"95399","position":"28359","price":"4.66","changeRatio":"-0.0476","middlePrice":"4.70","turnover":"44455934.00","positionChange":"-120","implVol":"0.1715"}},
		  {"derivative":{"symbol":"BAD"},"values":{}}
		]}
		""";

		var page = WebullOptionRankClient.ParseResponse(json);

		Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792209575003), page.UpdatedAt);
		Assert.True(page.HasOpraPermission);
		var row = Assert.Single(page.Rows);
		Assert.Equal("SPY261030P00750000", row.Symbol);
		Assert.Equal("SPY", row.Underlying);
		Assert.Equal(new DateOnly(2026, 10, 30), row.Expiry);
		Assert.Equal(750m, row.Strike);
		Assert.Equal("P", row.Right);
		Assert.Equal(4.66m, row.Last);
		Assert.Equal(-0.0476m, row.ChangeRatio);
		Assert.Equal(4.70m, row.Mid);
		Assert.Equal(95399, row.Volume);
		Assert.Equal(28359, row.OpenInterest);
		Assert.Equal(-120, row.OiChange);
		Assert.Equal(0.1715m, row.ImpliedVol);
		Assert.Equal(44455934.00m, row.Turnover);
	}

	[Fact]
	public void ParseResponse_NoData_ReturnsEmpty()
	{
		var page = WebullOptionRankClient.ParseResponse("""{"hasMore":false,"hasOpraPermission":false}""");

		Assert.Null(page.UpdatedAt);
		Assert.False(page.HasOpraPermission);
		Assert.Empty(page.Rows);
	}
}

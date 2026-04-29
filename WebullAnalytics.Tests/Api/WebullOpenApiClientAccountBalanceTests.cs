using System.Text.Json;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class WebullOpenApiClientAccountBalanceTests
{
	[Fact]
	public void TryGetAvailableFunds_PrefersBuyingPowerWhenPresent()
	{
		const string json = """
		{
			"total_cash_balance": "11190.95",
			"balances": {
			"option_buying_power": "12500.50"
			}
		}
		""";

		var balance = JsonSerializer.Deserialize<WebullOpenApiClient.AccountBalance>(json)!;

		Assert.Equal(12500.50m, balance.TryGetAvailableFunds());
	}

	[Fact]
	public void TryGetAvailableFunds_FallsBackToTotalCashBalance()
	{
		const string json = """
		{
			"total_cash_balance": "11190.95",
			"total_unrealized_profit_loss": "15.00",
			"total_asset_currency": "USD"
		}
		""";

		var balance = JsonSerializer.Deserialize<WebullOpenApiClient.AccountBalance>(json)!;

		Assert.Equal(11190.95m, balance.TryGetAvailableFunds());
	}
}

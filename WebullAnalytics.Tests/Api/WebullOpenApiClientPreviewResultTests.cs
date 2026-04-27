using System.Text.Json;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class WebullOpenApiClientPreviewResultTests
{
	[Fact]
	public void TryGetMarginSummary_ReadsTopLevelMarginFields()
	{
		const string json = """
        {
          "estimated_cost": "-75.32",
          "estimated_transaction_fee": "1.25",
          "margin_requirement": "26900"
        }
        """;

		var preview = JsonSerializer.Deserialize<WebullOpenApiClient.PreviewResult>(json)!;

		var summary = preview.TryGetMarginSummary();

		Assert.Equal("margin $26,900.00", summary);
	}

	[Fact]
	public void TryGetMarginSummary_ReadsNestedBuyingPowerFields()
	{
		const string json = """
        {
          "estimated_cost": "-75.32",
          "estimated_transaction_fee": "1.25",
          "risk": {
            "buying_power_effect": "-125.5"
          }
        }
        """;

		var preview = JsonSerializer.Deserialize<WebullOpenApiClient.PreviewResult>(json)!;

		var summary = preview.TryGetMarginSummary();

		Assert.Equal("BP effect -$125.50", summary);
	}

	[Fact]
	public void TryGetMarginSummary_ReturnsNullWhenMarginFieldsAbsent()
	{
		const string json = """
        {
          "estimated_cost": "-75.32",
          "estimated_transaction_fee": "1.25"
        }
        """;

		var preview = JsonSerializer.Deserialize<WebullOpenApiClient.PreviewResult>(json)!;

		Assert.Null(preview.TryGetMarginSummary());
	}
}

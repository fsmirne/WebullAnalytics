using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class OptionPriceRoundingTests
{
	// Source: Cboe Titanium U.S. Options Complex Book Process v1.2.69 (Jan 13, 2026) +
	// Cboe Options 2022 release notes on net-price increments. Webull's
	// OAUTH_OPENAPI_OPTION_PRICE_STEP_GTE rejection confirms the single-leg side.

	[Theory]
	[InlineData(10.82, "SPXW", 10.80)]   // the actual rejection case: $0.10 tick above $3
	[InlineData(2.97, "SPXW", 2.95)]     // $0.05 tick below $3
	[InlineData(2.99, "SPXW", 3.00)]     // boundary: rounds across the $3 threshold
	[InlineData(3.05, "SPXW", 3.10)]     // just above boundary, $0.10 tick
	[InlineData(0.07, "SPXW", 0.05)]     // small premium, $0.05 tick
	[InlineData(1.234, "SPY", 1.25)]     // single-leg non-SPX (penny-pilot in reality, but helper conservatively rounds to $0.05)
	[InlineData(5.07, "GME", 5.10)]      // single-leg non-SPX at/above $3
	public void SingleLeg_UsesNonPennyPilotRule(decimal input, string ticker, decimal expected)
	{
		Assert.Equal(expected, OptionPriceRounding.RoundToTick(input, legCount: 1, ticker));
	}

	[Theory]
	[InlineData(1.234, "SPXW", 1.25)]    // SPX-class complex: $0.05 net
	[InlineData(0.07, "SPXW", 0.05)]
	[InlineData(2.97, "SPX", 2.95)]
	[InlineData(10.82, "XSP", 10.80)]    // XSP grouped with SPX-class
	public void MultiLeg_SpxClass_UsesFiveCentNet(decimal input, string ticker, decimal expected)
	{
		Assert.Equal(expected, OptionPriceRounding.RoundToTick(input, legCount: 4, ticker));
	}

	[Theory]
	[InlineData(1.234, "SPY", 1.23)]     // non-SPX complex: penny net
	[InlineData(0.07, "QQQ", 0.07)]
	[InlineData(10.826, "GME", 10.83)]
	public void MultiLeg_NonSpxClass_UsesPennyNet(decimal input, string ticker, decimal expected)
	{
		Assert.Equal(expected, OptionPriceRounding.RoundToTick(input, legCount: 2, ticker));
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(-1.5)]
	public void NonPositiveInput_ReturnsZero(decimal input)
	{
		Assert.Equal(0m, OptionPriceRounding.RoundToTick(input, legCount: 1, "SPXW"));
		Assert.Equal(0m, OptionPriceRounding.RoundToTick(input, legCount: 4, "SPXW"));
	}

	[Fact]
	public void TickerComparison_IsCaseInsensitive()
	{
		// SPX-class detection must not care about config casing.
		Assert.Equal(1.25m, OptionPriceRounding.RoundToTick(1.234m, legCount: 4, "spxw"));
		Assert.Equal(1.25m, OptionPriceRounding.RoundToTick(1.234m, legCount: 4, "SpXw"));
	}
}

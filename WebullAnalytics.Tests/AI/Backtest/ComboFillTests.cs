using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Guards the backtest's multi-leg fill model. Summing independent per-leg mids priced a $10-wide SPY iron fly at a $10.50 credit — a
// riskless profit no counterparty pays — which made tight credit structures look loss-proof (PF 4.55 at mid vs 0.15 at bid/ask).
public class ComboFillTests
{
	private static decimal Net(IReadOnlyList<BacktestLegFill> fills) => fills.Sum(f => (f.Side == Side.Buy ? 1m : -1m) * f.PricePerShare);

	[Theory]
	[InlineData(0.0, 2.00)]
	[InlineData(1.0, 2.10)]
	[InlineData(0.25, 2.025)]
	public void LegPrice_MovesFractionOfHalfSpreadTowardCrossingSide(double cross, double expected)
	{
		Assert.Equal((decimal)expected, ComboFill.LegPrice(2.00m, 2.10m, (decimal)cross));
		Assert.Equal(2.00m - ((decimal)expected - 2.00m), ComboFill.LegPrice(2.00m, 1.90m, (decimal)cross));
	}

	[Fact]
	public void IronFly_CreditAboveWingWidth_ClampedBelowWidth()
	{
		var fills = new[]
		{
			new BacktestLegFill("SPY260116P00624000", Side.Buy, 1, 1.00m),
			new BacktestLegFill("SPY260116P00634000", Side.Sell, 1, 6.25m),
			new BacktestLegFill("SPY260116C00634000", Side.Sell, 1, 6.25m),
			new BacktestLegFill("SPY260116C00644000", Side.Buy, 1, 1.00m),
		};
		Assert.Equal(-10.50m, Net(fills));

		var clamped = ComboFill.ClampToNoArb(fills);

		Assert.Equal(-(10m - ComboFill.NoArbMargin), Net(clamped));
		Assert.All(clamped, f => Assert.True(f.PricePerShare >= 0m));
	}

	[Fact]
	public void IronCondor_CreditInsideRange_Unchanged()
	{
		var fills = new[]
		{
			new BacktestLegFill("SPXW260116P06700000", Side.Buy, 2, 0.80m),
			new BacktestLegFill("SPXW260116P06705000", Side.Sell, 2, 1.20m),
			new BacktestLegFill("SPXW260116C06750000", Side.Sell, 2, 1.10m),
			new BacktestLegFill("SPXW260116C06755000", Side.Buy, 2, 0.70m),
		};
		Assert.Same(fills, ComboFill.ClampToNoArb(fills));
	}

	[Fact]
	public void DebitVertical_DebitAboveWidth_ClampedBelowWidth()
	{
		var fills = new[]
		{
			new BacktestLegFill("SPY260116C00600000", Side.Buy, 1, 40.30m),
			new BacktestLegFill("SPY260116C00605000", Side.Sell, 1, 35.10m),
		};
		Assert.Equal(5.20m, Net(fills));
		Assert.Equal(5m - ComboFill.NoArbMargin, Net(ComboFill.ClampToNoArb(fills)));
	}

	[Fact]
	public void ClosingWorthlessCondor_PaysAtLeastMargin()
	{
		// Buying back a condor whose legs all mid at 0: the closing order's payoff is [0, width], so a $0 net is the free-option bound.
		var fills = new[]
		{
			new BacktestLegFill("SPXW260116P06700000", Side.Sell, 1, 0m),
			new BacktestLegFill("SPXW260116P06705000", Side.Buy, 1, 0m),
			new BacktestLegFill("SPXW260116C06750000", Side.Buy, 1, 0m),
			new BacktestLegFill("SPXW260116C06755000", Side.Sell, 1, 0m),
		};
		Assert.Equal(ComboFill.NoArbMargin, Net(ComboFill.ClampToNoArb(fills)));
	}

	[Fact]
	public void CrossExpiryAndSingleLeg_Unchanged()
	{
		var diagonal = new[]
		{
			new BacktestLegFill("SPY260116P00634000", Side.Sell, 1, 9.00m),
			new BacktestLegFill("SPY260220P00636000", Side.Buy, 1, 1.00m),
		};
		Assert.Same(diagonal, ComboFill.ClampToNoArb(diagonal));
		var single = new[] { new BacktestLegFill("SPY260116C00634000", Side.Sell, 1, 0m) };
		Assert.Same(single, ComboFill.ClampToNoArb(single));
	}

	[Fact]
	public void PayoffRange_NakedShortCall_UnboundedBelow()
	{
		var (min, max) = ComboFill.PayoffRange(new[] { (634m, "C", -1m), (630m, "P", 1m) });
		Assert.Null(min);
		Assert.Equal(630m, max);
	}
}

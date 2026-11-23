using WebullAnalytics.AI.Output;
using Xunit;

namespace WebullAnalytics.Tests.AI.Output;

public class FormatPlaceTests
{
	[Fact]
	public void CreditDiagonalSells_DebitDiagonalBuys()
	{
		// Leg structure alone reads every diagonal as a debit (SideInferrer: buy the back month → BUY); a credit inverted diagonal is a SELL.
		const string diagonal = "sell:SPY261127C00700000:1,buy:SPY261211C00731000:1";
		Assert.Equal($"wa trade place \"{diagonal}\" --side sell --limit 5.00", ReproductionCommands.FormatPlace(diagonal, 2, 5.00m));
		Assert.Equal($"wa trade place \"{diagonal}\" --side buy --limit 1.25", ReproductionCommands.FormatPlace(diagonal, 2, -1.25m));
	}

	[Fact]
	public void SingleLegCarriesNoSide() =>
		Assert.Equal("wa trade place \"sell:SPY261211C00700000:5\" --limit 17.91", ReproductionCommands.FormatPlace("sell:SPY261211C00700000:5", 1, 17.91m));
}

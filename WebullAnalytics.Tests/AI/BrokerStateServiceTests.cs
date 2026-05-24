using System.Reflection;
using WebullAnalytics.AI;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// BrokerStateService normally calls Webull's ListOpenOrdersAsync, which we can't drive from
// a unit test. These tests poke the matching logic via reflection — the two fingerprint methods
// are the entire behavior we care about. The proposal-leg uses OCC option symbols; the broker-leg
// uses underlying root + separate strike/expiry/type fields. The bug we're guarding against is
// the two fingerprints diverging so the dedup never matched.
public class BrokerStateServiceTests
{
	private static string FingerprintProposal(IEnumerable<(string Symbol, string Action)> legs)
	{
		var m = typeof(BrokerStateService).GetMethod("FingerprintProposal", BindingFlags.NonPublic | BindingFlags.Static)!;
		return (string)m.Invoke(null, new object[] { legs })!;
	}

	private static string FingerprintLegs(IEnumerable<WebullOpenApiClient.OrderDetailLeg>? legs)
	{
		var m = typeof(BrokerStateService).GetMethod("FingerprintLegs", BindingFlags.NonPublic | BindingFlags.Static)!;
		return (string)m.Invoke(null, new object?[] { legs })!;
	}

	[Fact]
	public void SingleLegCall_ProposalAndBrokerLegProduceSameFingerprint()
	{
		// User's reported case: buying SPXW 7375 Call expiring 2026-05-26.
		var proposal = new[] { ("SPXW260526C07375000", "buy") };
		var broker = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(
				Symbol: "SPXW", Side: "BUY", Quantity: "23",
				OptionType: "CALL", StrikePrice: "7375", OptionExpireDate: "2026-05-26"),
		};
		Assert.Equal(FingerprintLegs(broker), FingerprintProposal(proposal));
	}

	[Fact]
	public void SingleLegPut_Matches()
	{
		var proposal = new[] { ("SPY260530P00500000", "sell") };
		var broker = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(
				Symbol: "SPY", Side: "SELL", Quantity: "1",
				OptionType: "PUT", StrikePrice: "500.00", OptionExpireDate: "2026-05-30"),
		};
		Assert.Equal(FingerprintLegs(broker), FingerprintProposal(proposal));
	}

	[Fact]
	public void MultiLeg_OrderInvariant()
	{
		// Bull put spread: long lower put + short higher put. Proposal in one order, broker may return
		// legs in any order — the canonical sort makes the fingerprint match regardless.
		var proposal = new[]
		{
			("SPXW260612P07300000", "buy"),
			("SPXW260612P07400000", "sell"),
		};
		var brokerReversed = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(Symbol: "SPXW", Side: "SELL", Quantity: "1", OptionType: "PUT", StrikePrice: "7400", OptionExpireDate: "2026-06-12"),
			new WebullOpenApiClient.OrderDetailLeg(Symbol: "SPXW", Side: "BUY", Quantity: "1", OptionType: "PUT", StrikePrice: "7300", OptionExpireDate: "2026-06-12"),
		};
		Assert.Equal(FingerprintLegs(brokerReversed), FingerprintProposal(proposal));
	}

	[Fact]
	public void DifferentStrikes_NotMatching()
	{
		var proposal = new[] { ("SPXW260526C07375000", "buy") };
		var broker = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(
				Symbol: "SPXW", Side: "BUY", Quantity: "23",
				OptionType: "CALL", StrikePrice: "7400", OptionExpireDate: "2026-05-26"),
		};
		Assert.NotEqual(FingerprintLegs(broker), FingerprintProposal(proposal));
	}

	[Fact]
	public void DifferentSides_NotMatching()
	{
		var proposal = new[] { ("SPXW260526C07375000", "buy") };
		var broker = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(
				Symbol: "SPXW", Side: "SELL", Quantity: "23",
				OptionType: "CALL", StrikePrice: "7375", OptionExpireDate: "2026-05-26"),
		};
		Assert.NotEqual(FingerprintLegs(broker), FingerprintProposal(proposal));
	}

	[Fact]
	public void StrikeFormatVariations_StillMatch()
	{
		// Webull might return strike as "7375" or "7375.00" depending on whether it's integer-valued.
		// We parse to decimal and format consistently, so both should match.
		var proposal = new[] { ("SPXW260526C07375000", "buy") };
		var brokerInt = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(Symbol: "SPXW", Side: "BUY", Quantity: "1", OptionType: "CALL", StrikePrice: "7375", OptionExpireDate: "2026-05-26"),
		};
		var brokerDec = new[]
		{
			new WebullOpenApiClient.OrderDetailLeg(Symbol: "SPXW", Side: "BUY", Quantity: "1", OptionType: "CALL", StrikePrice: "7375.00", OptionExpireDate: "2026-05-26"),
		};
		Assert.Equal(FingerprintLegs(brokerInt), FingerprintProposal(proposal));
		Assert.Equal(FingerprintLegs(brokerDec), FingerprintProposal(proposal));
	}
}

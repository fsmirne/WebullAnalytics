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

	private static bool IsCloseIntent(string? positionIntent)
	{
		var m = typeof(BrokerStateService).GetMethod("IsCloseIntent", BindingFlags.NonPublic | BindingFlags.Static)!;
		return (bool)m.Invoke(null, new object?[] { positionIntent })!;
	}

	[Fact]
	public void CloseIntents_DoNotCountTowardDailyCap()
	{
		// User's reported case: closing the SPY 740P calendar (SELL_TO_CLOSE combo) consumed the day's
		// single opening slot and blocked `wa ai watch SPY --submit` from opening anything.
		Assert.True(IsCloseIntent("SELL_TO_CLOSE"));
		Assert.True(IsCloseIntent("BUY_TO_CLOSE"));
		Assert.True(IsCloseIntent("sell_to_close")); // case-insensitive
	}

	[Fact]
	public void OpenAndUnknownIntents_CountTowardDailyCap()
	{
		Assert.False(IsCloseIntent("BUY_TO_OPEN"));
		Assert.False(IsCloseIntent("SELL_TO_OPEN"));
		Assert.False(IsCloseIntent(null));          // missing intent — fail-closed, counts
		Assert.False(IsCloseIntent("SOMETHING_ELSE"));
	}

	private static BrokerStateService SeededService(Dictionary<string, int> fingerprintCounts)
	{
		var svc = (BrokerStateService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BrokerStateService));
		typeof(BrokerStateService).GetField("_activeFingerprintCounts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(svc, fingerprintCounts);
		return svc;
	}

	private static readonly (string Symbol, string Action)[] OpenLegs = { ("SPY260616C00730000", "sell"), ("SPY260630C00734000", "buy") };
	private static readonly (string Symbol, string Action)[] CloseLegs = { ("SPY260616C00730000", "buy"), ("SPY260630C00734000", "sell") };

	[Fact]
	public void NetOpenMatching_OpenedAndClosedToday_AllowsReopen()
	{
		// Live 2026-06-11: the morning's DD was opened and closed before noon; the afternoon's
		// deliberate `scan --submit-override` re-entry must not be blocked by the filled open order.
		var counts = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			[FingerprintProposal(OpenLegs)] = 1,
			[FingerprintProposal(CloseLegs)] = 1,
		};
		var svc = SeededService(counts);
		Assert.False(svc.HasNetOpenMatching(OpenLegs));
		// The close-side dedup must stay presence-based: a resting close still blocks a second close.
		Assert.True(svc.HasPendingMatching(CloseLegs));
	}

	[Fact]
	public void NetOpenMatching_OpenNotYetClosed_StillBlocks()
	{
		var svc = SeededService(new Dictionary<string, int>(StringComparer.Ordinal) { [FingerprintProposal(OpenLegs)] = 1 });
		Assert.True(svc.HasNetOpenMatching(OpenLegs));
	}

	[Fact]
	public void NetOpenMatching_ReopenedAfterClose_BlocksThirdOpen()
	{
		// open, close, re-open (override) → 2 opens vs 1 close: a further identical open must block.
		var counts = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			[FingerprintProposal(OpenLegs)] = 2,
			[FingerprintProposal(CloseLegs)] = 1,
		};
		Assert.True(SeededService(counts).HasNetOpenMatching(OpenLegs));
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

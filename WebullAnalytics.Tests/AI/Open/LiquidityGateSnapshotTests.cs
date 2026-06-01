using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

/// <summary>The snapshot-aware liquidity gate: a leg the daily chain snapshot confirmed tradeable (real
/// bid/ask) bypasses the open-interest checks, since thin index roots like XSP quote far-dated strikes
/// with little/no open interest and the snapshot is the authoritative liquidity signal.</summary>
public class LiquidityGateSnapshotTests
{
	private static readonly DateTime Exp = new(2026, 6, 22);

	private static (List<ProposalLeg> legs, Dictionary<string, OptionContractQuote> quotes) ZeroOiSpread()
	{
		// A diagonal-vertical leg set where the long legs quote but carry zero open interest (the live XSP shape).
		var legs = new List<ProposalLeg>();
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (strike, action) in new[] { (766m, "buy"), (768m, "sell") })
		{
			var sym = MatchKeys.OccSymbol("XSP", Exp, strike, "C");
			legs.Add(new ProposalLeg(action, sym, 1));
			quotes[sym] = new OptionContractQuote(sym, 6m, 6.90m, 7.07m, null, null, Volume: 1, OpenInterest: 0, ImpliedVolatility: 0.2m);
		}
		return (legs, quotes);
	}

	private static OpenerLiquidityConfig DefaultLiquidity() => new() { MinOpenInterest = 5, MinRelativeOpenInterest = 0.25m, MinAbsoluteOpenInterest = 100 };

	[Fact]
	public void ZeroOpenInterestLegsFailWithoutSnapshot()
	{
		var (legs, quotes) = ZeroOiSpread();
		var failures = CandidateScorer.GetLiquidityFailures(legs, quotes, DefaultLiquidity(), spot: 761m, snapshotTradeable: null);
		Assert.NotEmpty(failures); // oi 0<5 etc.
		Assert.False(CandidateScorer.PassesLiquidityGate(legs, quotes, DefaultLiquidity(), 761m));
	}

	[Fact]
	public void SnapshotTradeableLegsBypassTheOpenInterestGate()
	{
		var (legs, quotes) = ZeroOiSpread();
		var tradeable = new HashSet<string>(legs.Select(l => l.Symbol), StringComparer.OrdinalIgnoreCase);
		Assert.Empty(CandidateScorer.GetLiquidityFailures(legs, quotes, DefaultLiquidity(), spot: 761m, snapshotTradeable: tradeable));
		Assert.True(CandidateScorer.PassesLiquidityGate(legs, quotes, DefaultLiquidity(), 761m, tradeable));
	}

	[Fact]
	public void OnlyLegsInSnapshotAreExempt()
	{
		var (legs, quotes) = ZeroOiSpread();
		// Exempt only the first leg; the second still fails on OI.
		var partial = new HashSet<string>(new[] { legs[0].Symbol }, StringComparer.OrdinalIgnoreCase);
		var failures = CandidateScorer.GetLiquidityFailures(legs, quotes, DefaultLiquidity(), spot: 761m, snapshotTradeable: partial);
		Assert.NotEmpty(failures);
		Assert.All(failures, f => Assert.StartsWith(legs[1].Symbol, f));
	}
}

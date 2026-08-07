using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>Locks the vendor-IV trust guard inside <see cref="GexMatrix.Build"/>, the twin of the one
/// <see cref="OptionMath.GetLegIv"/> already carries. The regression this prevents: run `analyze gex` on the
/// evening of an expiry day and the chain is full of dead books. Schwab quoted the just-expired SPCX 110P at
/// no-bid / 0.01-ask with IV 887%; taken at face value that collapsed the strike's dollar gamma from $13.4M
/// (at its real ~127% IV) to $1.0M and moved the heatmap's gravity strike onto a different row.</summary>
public class GexVendorIvGuardTests
{
	private static readonly DateTime AsOf = new(2026, 8, 6);
	private static readonly DateTime Expiry = new(2026, 8, 7);
	private const decimal Spot = 114.97m;
	private const decimal Strike = 110m;

	private static Dictionary<string, OptionContractQuote> Chain(decimal? bid, decimal? ask, decimal? iv, decimal? hv = null)
	{
		var symbol = MatchKeys.OccSymbol("SPCX", Expiry, Strike, "P");
		return new Dictionary<string, OptionContractQuote>
		{
			[symbol] = new(ContractSymbol: symbol, LastPrice: null, Bid: bid, Ask: ask, Change: null,
				PercentChange: null, Volume: null, OpenInterest: 41882, ImpliedVolatility: iv, HistoricalVolatility: hv),
		};
	}

	private static decimal GrossAtStrike(Dictionary<string, OptionContractQuote> quotes)
	{
		var m = GexMatrix.Build(quotes, "SPCX", Spot, AsOf, Expiry, strikeRangeFraction: 0.20m, maxDteDays: 7, maxStrikes: 50);
		return m.Cells.TryGetValue((Expiry.Date, Strike), out var cell) ? cell.Gross : 0m;
	}

	[Fact]
	public void TwoSidedBook_TrustsVendorIv_AndCarriesRealGamma()
	{
		// The same contract as it quoted while alive: a tight two-sided book at a believable 126.9% IV.
		var gross = GrossAtStrike(Chain(bid: 1.12m, ask: 1.16m, iv: 1.269m));
		Assert.True(gross > 10_000_000m, $"a live two-sided book should carry its real dollar gamma, got {gross:N0}");
	}

	[Fact]
	public void DeadOneSidedBook_DoesNotPriceGammaOffThePhantomIv()
	{
		// No bid, 0.01 ask, vendor IV 887% — the expired-contract residue. Trusting it yields ~$1M of phantom
		// gamma at this strike; the guard must not let that number through.
		var gross = GrossAtStrike(Chain(bid: null, ask: 0.01m, iv: 8.87m));
		Assert.Equal(0m, gross);
	}

	[Fact]
	public void DeadOneSidedBook_FallsBackToHvWhenAvailable()
	{
		// With an HV to stand in for the unusable vendor IV the strike survives — priced off a real surface,
		// and nowhere near the 887%-IV answer.
		var gross = GrossAtStrike(Chain(bid: null, ask: 0.01m, iv: 8.87m, hv: 1.269m));
		Assert.True(gross > 10_000_000m, $"HV fallback should reprice the strike sanely, got {gross:N0}");
	}

	[Fact]
	public void ZeroBid_IsTreatedAsOneSided()
	{
		Assert.Equal(0m, GrossAtStrike(Chain(bid: 0m, ask: 0.01m, iv: 8.87m)));
	}
}

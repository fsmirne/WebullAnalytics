using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class ParityMedianTests
{
	private static readonly DateTime AsOf = new(2026, 11, 22, 21, 0, 0);
	private static readonly DateTime Monday = new(2026, 11, 23);
	private static readonly DateTime Friday = new(2026, 11, 27);

	/// <summary>Two-sided call/put pairs at <paramref name="strikes"/> whose parity reads <paramref name="spot"/> plus a per-strike error (C − P + K·disc = spot + error).</summary>
	private static void AddExpiry(Dictionary<string, OptionContractQuote> quotes, DateTime expiry, decimal spot, params (decimal Strike, decimal Error)[] strikes)
	{
		var discount = (decimal)Math.Exp(-0.04 * (expiry.Date - AsOf.Date).Days / 365.0);
		foreach (var (k, error) in strikes)
		{
			var put = 5m;
			var call = put + spot + error - k * discount;
			var c = MatchKeys.OccSymbol("NVDA", expiry, k, "C");
			var p = MatchKeys.OccSymbol("NVDA", expiry, k, "P");
			quotes[c] = new OptionContractQuote(c, null, call - 0.01m, call + 0.01m, null, null, null, null, null);
			quotes[p] = new OptionContractQuote(p, null, put - 0.01m, put + 0.01m, null, null, null, null, null);
		}
	}

	[Fact]
	public void MedianOfTheStrikesNearestTheMoney_IgnoresOneNoisyPair()
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		AddExpiry(quotes, Monday, 126.20m, (124m, 0.05m), (125m, -0.02m), (126m, 0.60m), (127m, 0.01m), (128m, -0.04m));

		var parity = PremarketSpotOverride.DeriveSpotFromParity("NVDA", quotes, 0.04, AsOf, allowLastPrice: false);

		Assert.NotNull(parity);
		Assert.Equal(126.21m, parity.Value.Spot, 2); // the 126 strike's +0.60 reading — nearest the money — doesn't move the median
	}

	[Fact]
	public void ExpiriesSpanningTheExDividendDateAreSkipped()
	{
		// Monday's expiry spans a Monday ex-date (struck Friday, cum-dividend): its parity reads low by the dividend. The next expiry is skipped too.
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		AddExpiry(quotes, Monday, 126.20m - 0.50m, (125m, 0m), (126m, 0m), (127m, 0m));
		AddExpiry(quotes, Friday, 126.20m - 0.50m, (125m, 0m), (126m, 0m), (127m, 0m));

		Assert.Equal(125.70m, PremarketSpotOverride.DeriveSpotFromParity("NVDA", quotes, 0.04, AsOf, allowLastPrice: false)!.Value.Spot, 2);
		Assert.Null(PremarketSpotOverride.DeriveSpotFromParity("NVDA", quotes, 0.04, AsOf, allowLastPrice: false, excludeExpiriesFrom: Monday));

		// An ex-date after Monday leaves Monday's expiry — which ends before it — as the one to read.
		AddExpiry(quotes, Monday, 126.20m, (125m, 0m), (126m, 0m), (127m, 0m));
		Assert.Equal(126.20m, PremarketSpotOverride.DeriveSpotFromParity("NVDA", quotes, 0.04, AsOf, allowLastPrice: false, excludeExpiriesFrom: Friday)!.Value.Spot, 2);
	}
}

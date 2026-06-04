using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

/// <summary>Verifies that <see cref="BacktestQuoteSource"/> prices on the dividend-adjusted forward when a
/// historical schedule is supplied — the backtest analog of the live dividend-aware Black-Scholes path. A
/// SPY call whose life straddles an ex-date prices BELOW its q=0 value by exactly the present-value drop in
/// the forward; a dividend outside the window (or no schedule) leaves pricing unchanged.</summary>
public class BacktestQuoteSourceDividendTests : IDisposable
{
	private const double R = 0.036;
	private readonly string _tmpDir;

	public BacktestQuoteSourceDividendTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-bqs-div-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	// SPY $410 call, ~60 DTE, synthetic (no captured bar) so the BS forward is what's exercised.
	private const string Occ = "SPY260213C00410000";
	private static readonly DateTime AsOf = new(2025, 12, 15, 9, 30, 0, DateTimeKind.Unspecified);
	private static readonly DateTime Expiry = new(2026, 2, 13);

	private static IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>> Sched(DateTime exDate, decimal amt) =>
		new Dictionary<string, IReadOnlyList<DividendEvent>>(StringComparer.OrdinalIgnoreCase)
		{
			["SPY"] = new[] { new DividendEvent(exDate, amt) },
		};

	[Fact]
	public async Task SyntheticCall_DividendInWindow_PricesOnReducedForward()
	{
		var divEx = new DateTime(2026, 1, 15); // ~31 days out, inside the leg's life
		const decimal amt = 1.50m;

		var noDiv = await PriceAsync(dividendsByRoot: null);
		var withDiv = await PriceAsync(dividendsByRoot: Sched(divEx, amt));

		// Same IV in both paths (smile is keyed on raw spot, identical), so only the forward differs.
		Assert.NotNull(noDiv.ImpliedVolatility);
		Assert.Equal(noDiv.ImpliedVolatility, withDiv.ImpliedVolatility);

		// A call on a dividend-reduced forward is strictly cheaper.
		Assert.True(withDiv.LastPrice < noDiv.LastPrice,
			$"expected dividend-adjusted call < q=0 call, got {withDiv.LastPrice} vs {noDiv.LastPrice}");

		// And cheaper by exactly the model: reprice the no-div leg's own IV on the adjusted spot.
		var dte = (Expiry.Date - AsOf.Date).Days;
		var t = dte / 365.0;
		var adjSpot = OptionMath.DividendAdjustedSpot(405m, new[] { new DividendEvent(divEx, amt) }, AsOf, Expiry.Date + OptionMath.MarketClose, R);
		var expected = OptionMath.BlackScholes(adjSpot, 410m, t, R, withDiv.ImpliedVolatility!.Value, "C");
		Assert.Equal(expected, withDiv.LastPrice!.Value, 2);
	}

	[Fact]
	public async Task SyntheticCall_DividendAfterExpiry_NoAdjustment()
	{
		// Ex-date falls AFTER the leg expires → not in window → identical to the q=0 price.
		var noDiv = await PriceAsync(dividendsByRoot: null);
		var afterExpiry = await PriceAsync(dividendsByRoot: Sched(new DateTime(2026, 3, 20), 1.50m));
		Assert.Equal(noDiv.LastPrice, afterExpiry.LastPrice);
	}

	private async Task<OptionContractQuote> PriceAsync(IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividendsByRoot)
	{
		var (bars, iv) = BuildSpyCaches(spyOpen: 405m);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: R, dividendsByRoot: dividendsByRoot);
		var snap = await quotes.GetQuotesAsync(AsOf,
			new HashSet<string>(new[] { Occ }),
			new HashSet<string>(new[] { "SPY" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);
		Assert.True(snap.Options.TryGetValue(Occ, out var q));
		return q!;
	}

	private (HistoricalBarCache Bars, BacktestIVProvider Iv) BuildSpyCaches(decimal spyOpen)
	{
		var prior = AsOf.Date.AddDays(-1);
		var data = new Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>(StringComparer.OrdinalIgnoreCase)
		{
			["SPY"] = new() { [AsOf.Date] = MakeBar(AsOf.Date, spyOpen) },
			["VIX"] = new() { [prior] = MakeBar(prior, 18m) },
			["VIX1D"] = new() { [prior] = MakeBar(prior, 18m) },
			["VIX9D"] = new() { [prior] = MakeBar(prior, 18m) },
		};
		var cacheDir = Path.Combine(_tmpDir, "bars-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(cacheDir);
		var bars = new HistoricalBarCache(
			cacheDir,
			(ticker, from, to, ct) => Task.FromResult(data.TryGetValue(ticker, out var map) ? map : new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>()),
			utcNow: () => new DateTimeOffset(AsOf.Date.AddDays(1).AddHours(18), TimeSpan.Zero).UtcDateTime);
		var iv = new BacktestIVProvider(bars, smileEnabled: false);
		return (bars, iv);
	}

	private static YahooOptionsClient.HistoricalBar MakeBar(DateTime date, decimal value) =>
		new(date, value, value, value, value, value, null);
}

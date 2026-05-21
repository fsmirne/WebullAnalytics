using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

public class BacktestIVProviderTests
{
	[Fact]
	public async Task Spxw_0Dte_AnchorsOnVix1D()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: 25.5m);
		var provider = new BacktestIVProvider(bars, smileEnabled: false);

		var iv = await provider.GetAtmIVAsync("SPXW", new DateTime(2026, 5, 21), dteDays: 0, CancellationToken.None);

		Assert.Equal(0.255m, iv);
	}

	[Fact]
	public async Task Spxw_5Dte_AnchorsOnVix9D()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: 25.5m);
		var provider = new BacktestIVProvider(bars, smileEnabled: false);

		var iv = await provider.GetAtmIVAsync("SPXW", new DateTime(2026, 5, 21), dteDays: 5, CancellationToken.None);

		Assert.Equal(0.185m, iv);
	}

	[Fact]
	public async Task Spxw_30Dte_AnchorsOnVix()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: 25.5m);
		var provider = new BacktestIVProvider(bars, smileEnabled: false);

		var iv = await provider.GetAtmIVAsync("SPXW", new DateTime(2026, 5, 21), dteDays: 30, CancellationToken.None);

		Assert.Equal(0.170m, iv);
	}

	[Fact]
	public async Task Spxw_0Dte_FallsBackToVix9D_WhenVix1DMissing()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: null);
		var provider = new BacktestIVProvider(bars, smileEnabled: false);

		var iv = await provider.GetAtmIVAsync("SPXW", new DateTime(2026, 5, 21), dteDays: 0, CancellationToken.None);

		Assert.Equal(0.185m, iv);
	}

	[Fact]
	public async Task Spxw_0Dte_FallsBackToVix_WhenVix1DAndVix9DMissing()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: null, vix1d: null);
		var provider = new BacktestIVProvider(bars, smileEnabled: false);

		var iv = await provider.GetAtmIVAsync("SPXW", new DateTime(2026, 5, 21), dteDays: 0, CancellationToken.None);

		Assert.Equal(0.170m, iv);
	}

	[Fact]
	public void IndexCallWing_IsFlatRegardlessOfStrike()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: 25.5m);
		var provider = new BacktestIVProvider(bars, smileEnabled: true);

		// Spot 7400, strikes 7430-7470 (OTM calls). Live observation: IVs are flat ~25.8% across this range.
		// Model should produce IV ≈ ATM for all OTM call strikes.
		var atm = 0.258m;
		var iv7430 = provider.ApplySmile(atm, "SPXW", strike: 7430m, spot: 7400m);
		var iv7440 = provider.ApplySmile(atm, "SPXW", strike: 7440m, spot: 7400m);
		var iv7470 = provider.ApplySmile(atm, "SPXW", strike: 7470m, spot: 7400m);

		Assert.Equal(atm, iv7430);
		Assert.Equal(atm, iv7440);
		Assert.Equal(atm, iv7470);
	}

	[Fact]
	public void IndexPutWing_IsLiftedByVShape()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: 25.5m);
		var provider = new BacktestIVProvider(bars, smileEnabled: true);

		// Spot 7400, strike 7326 (1% OTM put, m = -0.01). With put-side linear=-8, curvature=20:
		// smile = -8 * -0.01 + 20 * 0.01 = 0.08 + 0.20 = 0.28 → IV = ATM * 1.28
		var atm = 0.25m;
		var iv = provider.ApplySmile(atm, "SPXW", strike: 7326m, spot: 7400m);

		Assert.NotNull(iv);
		Assert.True(iv.Value > atm * 1.25m, $"OTM put IV should be lifted ≥25% above ATM, got {iv.Value / atm:P}");
	}

	[Fact]
	public void EquityTicker_RetainsSymmetricVShape()
	{
		var bars = BuildBars(vix: 17.0m, vix9d: 18.5m, vix1d: 25.5m);
		var provider = new BacktestIVProvider(bars, smileEnabled: true);

		// Single stock: both wings should be lifted symmetrically (V-shape preserved for equities).
		var atm = 0.40m;
		var ivOtmCall = provider.ApplySmile(atm, "GME", strike: 27m, spot: 25m);
		var ivOtmPut = provider.ApplySmile(atm, "GME", strike: 23m, spot: 25m);

		Assert.NotNull(ivOtmCall);
		Assert.NotNull(ivOtmPut);
		Assert.True(ivOtmCall.Value > atm, "Equity OTM call should be lifted above ATM (V-shape)");
		Assert.True(ivOtmPut.Value > atm, "Equity OTM put should be lifted above ATM (V-shape)");
	}

	private static HistoricalBarCache BuildBars(decimal? vix, decimal? vix9d, decimal? vix1d)
	{
		// The asOf in tests is 2026-05-21; the lookahead guard reads the PRIOR settled session, so seed
		// the bars on 2026-05-20 (yesterday). Equivalent to: live model at 09:30 sees yesterday's close.
		var data = new Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>(StringComparer.OrdinalIgnoreCase);
		var d = new DateTime(2026, 5, 20);
		if (vix.HasValue) data["VIX"] = new() { [d] = Bar(d, vix.Value) };
		if (vix9d.HasValue) data["VIX9D"] = new() { [d] = Bar(d, vix9d.Value) };
		if (vix1d.HasValue) data["VIX1D"] = new() { [d] = Bar(d, vix1d.Value) };

		var cacheDir = Path.Combine(Path.GetTempPath(), $"BacktestIVProviderTests_{Guid.NewGuid():N}");
		Directory.CreateDirectory(cacheDir);
		return new HistoricalBarCache(
			cacheDir,
			(ticker, from, to, ct) => Task.FromResult(data.TryGetValue(ticker, out var map) ? map : new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>()),
			utcNow: () => new DateTime(2026, 5, 22, 18, 0, 0, DateTimeKind.Utc));
	}

	private static YahooOptionsClient.HistoricalBar Bar(DateTime date, decimal value) =>
		new(date, value, value, value, value, value, null);
}

using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

/// <summary>End-to-end test that <see cref="BacktestQuoteSource"/> prefers a captured per-minute bar
/// over its synthetic Black-Scholes pricing when a <see cref="HistoricalOptionBarCache"/> is wired
/// in and a bar exists for the leg's minute. Verifies the IV-percentage → decimal conversion and
/// the fall-through to synthetic when the cache misses.</summary>
public class BacktestQuoteSourceCapturedBarsTests : IDisposable
{
	private readonly string _tmpDir;

	public BacktestQuoteSourceCapturedBarsTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-bqs-cap-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	[Fact]
	public async Task GetQuotesAsync_BarPresent_UsesTimeMidpoint_BackSolvesIv()
	{
		// SPXW 0DTE ATM, spot $7400. Captured bar: open $10 → close $20 (price climbed through
		// the minute, range 8–20). Leg should be priced at the *time-midpoint* (Open+Close)/2 = $15,
		// not bar.Open's $10 — the live tick that decides at :30 sees roughly the time-midpoint
		// price, not the start-of-bar price. IV is back-solved fresh from this midpoint; the
		// captured iv column (15.5%) is ignored because it was Webull-side back-solved from
		// bar.Open and carries the wrong moment-in-minute anchor.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 30, 0, DateTimeKind.Unspecified);
		var asOfUtc = ToUtcOpen(asOf);
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(asOfUtc, 10m, 20m, 8m, 20m, 100, 15.5m),
		});

		var (bars, iv) = BuildDailyCaches(spxwOpen: 7400m, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var optionBars = new HistoricalOptionBarCache(_tmpDir);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: 0.036, optionBars: optionBars);

		var snap = await quotes.GetQuotesAsync(asOf,
			new HashSet<string>(new[] { occ }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(occ, out var q));
		Assert.Equal(15m, q!.LastPrice);
		// Captured iv (0.155) is intentionally NOT used directly — back-solving from $15 mid gives
		// a different IV than 0.155 (which would correspond to $10 bar.Open). Verify the back-solve
		// produced a sane IV in the expected band and not the captured value.
		Assert.NotNull(q.ImpliedVolatility);
		Assert.NotEqual(0.155m, q.ImpliedVolatility!.Value);
		Assert.InRange(q.ImpliedVolatility!.Value, 0.05m, 1.5m);
		// Bid/ask synthetic half-spread around the midpoint. SPXW is index-class → tight.
		Assert.NotNull(q.Bid);
		Assert.NotNull(q.Ask);
		Assert.True(q.Bid!.Value < 15m && q.Ask!.Value > 15m);
		// Volume comes through from the bar.
		Assert.Equal(100, q.Volume);
	}

	[Fact]
	public async Task GetQuotesAsync_BarMissing_FallsThroughToSynthetic()
	{
		// No CSV seeded → cache returns null → synthetic path runs.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 30, 0, DateTimeKind.Unspecified);
		var (bars, iv) = BuildDailyCaches(spxwOpen: 7400m, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var optionBars = new HistoricalOptionBarCache(_tmpDir);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: 0.036, optionBars: optionBars);

		var snap = await quotes.GetQuotesAsync(asOf,
			new HashSet<string>(new[] { occ }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(occ, out var q));
		// Synthetic path should produce IV ≈ VIX1D (0.25), not the captured 0.155.
		Assert.NotNull(q!.ImpliedVolatility);
		Assert.InRange(q.ImpliedVolatility!.Value, 0.20m, 0.30m);
		// Volume is null when synthetic (no captured bar).
		Assert.Null(q.Volume);
	}

	[Fact]
	public async Task GetQuotesAsync_BarPresentButZeroClose_FallsThroughToSynthetic()
	{
		// A bar with close=0 is treated as "no trade print" and shouldn't be used as a mid;
		// otherwise the backtest would mark a position to $0 from one bad data point.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 30, 0, DateTimeKind.Unspecified);
		var asOfUtc = ToUtcOpen(asOf);
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(asOfUtc, 0m, 0m, 0m, 0m, 0, null),
		});

		var (bars, iv) = BuildDailyCaches(spxwOpen: 7400m, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var optionBars = new HistoricalOptionBarCache(_tmpDir);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: 0.036, optionBars: optionBars);

		var snap = await quotes.GetQuotesAsync(asOf,
			new HashSet<string>(new[] { occ }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(occ, out var q));
		// Should NOT be the zero close — synthetic path took over.
		Assert.True(q!.LastPrice > 0m);
	}

	[Fact]
	public async Task GetQuotesAsync_AsOfIsIntradayMinute_LooksUpThatMinute()
	{
		// Regression for the slice-3 bug where ToUtcMinute forced 09:30 ET regardless of asOf's
		// time-of-day. The intraday opener evaluates at non-open minutes (10:00, 10:32, …); each
		// must find its own bar, not the day's open bar. Bar is flat (open=close=25), so the
		// time-midpoint pricing returns the same $25 regardless of the (open+close)/2 averaging.
		var occ = "SPXW260526C07400000";
		var asOf1032 = new DateTime(2026, 5, 26, 10, 32, 0, DateTimeKind.Unspecified);
		var asOf1032Utc = ToUtcExact(asOf1032);
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(asOf1032Utc, 25m, 26m, 24m, 25m, 50, 12.5m),
		});

		var (bars, iv) = BuildDailyCaches(spxwOpen: 7400m, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf1032);
		var optionBars = new HistoricalOptionBarCache(_tmpDir);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: 0.036, optionBars: optionBars);

		var snap = await quotes.GetQuotesAsync(asOf1032,
			new HashSet<string>(new[] { occ }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(occ, out var q));
		Assert.Equal(25m, q!.LastPrice);
		// Back-solved IV from $25 mid (not the captured 0.125 column). Both are valid-range; assert
		// finiteness rather than the literal captured value, since the back-solve will differ.
		Assert.NotNull(q.ImpliedVolatility);
		Assert.InRange(q.ImpliedVolatility!.Value, 0.05m, 1.5m);
	}

	[Fact]
	public async Task GetQuotesAsync_BarPresentNullIv_BackSolvesFromMid()
	{
		// Some bars have no IV column (massive-sourced or otherwise unreported). With the new
		// midpoint-pricing model the captured iv column is *never* trusted directly anyway — we
		// always back-solve from (Open+Close)/2. So this case just verifies that a null iv column
		// doesn't blow up the path: we still produce a usable IV from the back-solver.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 30, 0, DateTimeKind.Unspecified);
		var asOfUtc = ToUtcOpen(asOf);
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(asOfUtc, 50m, 51m, 49m, 50m, 0, null),
		});

		var (bars, iv) = BuildDailyCaches(spxwOpen: 7400m, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var optionBars = new HistoricalOptionBarCache(_tmpDir);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: 0.036, optionBars: optionBars);

		var snap = await quotes.GetQuotesAsync(asOf,
			new HashSet<string>(new[] { occ }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(occ, out var q));
		Assert.Equal(50m, q!.LastPrice);   // captured midpoint (open=close=50)
		Assert.NotNull(q.ImpliedVolatility);
		Assert.InRange(q.ImpliedVolatility!.Value, 0.05m, 1.5m);
	}

	[Fact]
	public async Task GetQuotesAsync_TargetExpiryEmpty_BracketingNeighbors_InterpolatesTotalVariance()
	{
		// Target expiry (06-05, 10 DTE) has NO captured bar → same-expiry surface is null. Two neighbor
		// expiries bracket it: 06-01 (6 DTE) and 06-09 (14 DTE), each with one captured ATM strike at the
		// minute. Cross-expiry pricing should back-solve each neighbor's IV and interpolate in total-variance
		// (w = σ²·T) to the target's TTE — NOT fall through to the VIX1D (0.25) parametric anchor.
		const decimal spot = 7400m, strike = 7400m;
		const double r = 0.036;
		var asOf = new DateTime(2026, 5, 26, 9, 30, 0, DateTimeKind.Unspecified);
		var asOfUtc = ToUtcOpen(asOf);

		SeedCsv("SPXW260601C07400000", new List<OptionMinuteBar> { new(asOfUtc, 70m, 70m, 70m, 70m, 100, null) });  // 6 DTE
		SeedCsv("SPXW260609C07400000", new List<OptionMinuteBar> { new(asOfUtc, 110m, 110m, 110m, 110m, 100, null) }); // 14 DTE
		var target = "SPXW260605C07400000"; // 10 DTE, not seeded

		var (bars, iv) = BuildDailyCaches(spxwOpen: spot, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: r, optionBars: new HistoricalOptionBarCache(_tmpDir));

		var snap = await quotes.GetQuotesAsync(asOf, new HashSet<string>(new[] { target }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase), CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(target, out var q));
		Assert.NotNull(q!.ImpliedVolatility);

		// Independently reconstruct the expected interpolated IV: back-solve each neighbor's IV at its own
		// TTE, take total variance, interpolate linearly in T to the target TTE, back out σ.
		double T1 = 6.0 / 365.0, T = 10.0 / 365.0, T2 = 14.0 / 365.0;
		var iv1 = (double)OptionMath.ImpliedVol(spot, strike, T1, r, 70m, "C");
		var iv2 = (double)OptionMath.ImpliedVol(spot, strike, T2, r, 110m, "C");
		double w1 = iv1 * iv1 * T1, w2 = iv2 * iv2 * T2;
		var expected = Math.Sqrt((w1 + (w2 - w1) * (T - T1) / (T2 - T1)) / T);

		Assert.Equal(expected, (double)q.ImpliedVolatility!.Value, 3);          // matches the total-variance interp
		Assert.False(System.Math.Abs((double)q.ImpliedVolatility!.Value - 0.25) < 1e-6); // NOT the VIX1D parametric anchor
	}

	[Fact]
	public async Task GetQuotesAsync_TargetExpiryEmpty_OneSidedNeighbor_ExtrapolatesFlat()
	{
		// Only a single neighbor expiry (06-01, below the 06-05 target) has a captured strike. With no anchor
		// on the far side, cross-expiry flat-extrapolates that neighbor's IV rather than interpolating.
		const decimal spot = 7400m, strike = 7400m;
		const double r = 0.036;
		var asOf = new DateTime(2026, 5, 26, 9, 30, 0, DateTimeKind.Unspecified);
		var asOfUtc = ToUtcOpen(asOf);

		SeedCsv("SPXW260601C07400000", new List<OptionMinuteBar> { new(asOfUtc, 70m, 70m, 70m, 70m, 100, null) }); // 6 DTE only
		var target = "SPXW260605C07400000"; // 10 DTE, not seeded; nothing above it within window

		var (bars, iv) = BuildDailyCaches(spxwOpen: spot, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: r, optionBars: new HistoricalOptionBarCache(_tmpDir));

		var snap = await quotes.GetQuotesAsync(asOf, new HashSet<string>(new[] { target }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase), CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(target, out var q));
		Assert.NotNull(q!.ImpliedVolatility);
		var iv1 = (double)OptionMath.ImpliedVol(spot, strike, 6.0 / 365.0, r, 70m, "C");
		Assert.Equal(iv1, (double)q.ImpliedVolatility!.Value, 3); // flat-extrapolated from the single neighbor
	}

	private string SeedCsv(string occ, List<OptionMinuteBar> bars)
	{
		var parsed = ParsingHelpers.ParseOptionSymbol(occ);
		Assert.NotNull(parsed);
		var expiryDir = parsed!.ExpiryDate.ToString("yyyy-MM-dd");
		var path = Path.Combine(_tmpDir, parsed.Root.ToUpperInvariant(), expiryDir, occ + ".csv");
		AIHistoryOptionsBackfill.WriteOptionCsv(path, bars);
		return path;
	}

	private static DateTimeOffset ToUtcOpen(DateTime asOfEt) =>
		new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
			DateTime.SpecifyKind(asOfEt.Date.AddHours(9).AddMinutes(30), DateTimeKind.Unspecified),
			TimeZoneInfo.FindSystemTimeZoneById("America/New_York")), TimeSpan.Zero);

	private static DateTimeOffset ToUtcExact(DateTime etWallClock) =>
		new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
			DateTime.SpecifyKind(etWallClock, DateTimeKind.Unspecified),
			TimeZoneInfo.FindSystemTimeZoneById("America/New_York")), TimeSpan.Zero);

	/// <summary>Builds a minimal HistoricalBarCache + IV provider where SPXW's prior-day close is set
	/// (so the cache treats today's open as <paramref name="spxwOpen"/>) and the VIX-family seeds
	/// drive the synthetic IV path.</summary>
	private (HistoricalBarCache Bars, BacktestIVProvider Iv) BuildDailyCaches(decimal spxwOpen, decimal vix1d, decimal vix9d, decimal vix, DateTime asOf)
	{
		var prior = asOf.Date.AddDays(-1);
		// Spxw open lives in today's bar.Open; seed with high=low=open=close=spxwOpen.
		var data = new Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>(StringComparer.OrdinalIgnoreCase)
		{
			["SPXW"] = new() { [asOf.Date] = MakeBar(asOf.Date, spxwOpen) },
			["VIX"] = new() { [prior] = MakeBar(prior, vix) },
			["VIX1D"] = new() { [prior] = MakeBar(prior, vix1d) },
			["VIX9D"] = new() { [prior] = MakeBar(prior, vix9d) },
		};

		var cacheDir = Path.Combine(_tmpDir, "bars");
		Directory.CreateDirectory(cacheDir);
		var bars = new HistoricalBarCache(
			cacheDir,
			(ticker, from, to, ct) => Task.FromResult(data.TryGetValue(ticker, out var map) ? map : new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>()),
			utcNow: () => new DateTimeOffset(asOf.Date.AddDays(1).AddHours(18), TimeSpan.Zero).UtcDateTime);
		var iv = new BacktestIVProvider(bars, smileEnabled: false);
		return (bars, iv);
	}

	private static YahooOptionsClient.HistoricalBar MakeBar(DateTime date, decimal value) =>
		new(date, value, value, value, value, value, null);
}

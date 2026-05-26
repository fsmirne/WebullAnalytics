using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
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
	public async Task GetQuotesAsync_BarPresent_UsesCapturedCloseAndIv()
	{
		// SPXW 0DTE, spot $7400, captured bar reports close $50 and IV 15.5% — far from any
		// reasonable Black-Scholes value. If the test passes, we know the captured path won.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 31, 0, DateTimeKind.Unspecified);
		var asOfUtc = ToUtcOpen(asOf);
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(asOfUtc, 50m, 51m, 49m, 50m, 100, 15.5m),
		});

		var (bars, iv) = BuildDailyCaches(spxwOpen: 7400m, vix1d: 25m, vix9d: 22m, vix: 20m, asOf: asOf);
		var optionBars = new HistoricalOptionBarCache(_tmpDir);
		var quotes = new BacktestQuoteSource(bars, iv, riskFreeRate: 0.036, optionBars: optionBars);

		var snap = await quotes.GetQuotesAsync(asOf,
			new HashSet<string>(new[] { occ }),
			new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(occ, out var q));
		Assert.Equal(50m, q!.LastPrice);
		// CSV stored 15.5 (percentage); internal IV is the fraction 0.155.
		Assert.Equal(0.155m, q.ImpliedVolatility);
		// Bid/ask still synthetic: half-spread around the captured mid. SPXW is index-class → tight.
		Assert.NotNull(q.Bid);
		Assert.NotNull(q.Ask);
		Assert.True(q.Bid!.Value < 50m && q.Ask!.Value > 50m);
		// Volume comes through from the bar.
		Assert.Equal(100, q.Volume);
	}

	[Fact]
	public async Task GetQuotesAsync_BarMissing_FallsThroughToSynthetic()
	{
		// No CSV seeded → cache returns null → synthetic path runs.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 31, 0, DateTimeKind.Unspecified);
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
		var asOf = new DateTime(2026, 5, 26, 9, 31, 0, DateTimeKind.Unspecified);
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
		// must find its own bar, not the day's open bar.
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
		Assert.Equal(0.125m, q.ImpliedVolatility);
	}

	[Fact]
	public async Task GetQuotesAsync_BarPresentNullIv_KeepsSyntheticIv()
	{
		// Some bars have no IV column (the contract was illiquid that minute). We still use bar.Close
		// for the mid, but the IV falls back to the VIX-anchored value.
		var occ = "SPXW260526C07400000";
		var asOf = new DateTime(2026, 5, 26, 9, 31, 0, DateTimeKind.Unspecified);
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
		Assert.Equal(50m, q!.LastPrice);   // captured close used
		// IV from VIX1D fallback, not from the (null) bar IV.
		Assert.NotNull(q.ImpliedVolatility);
		Assert.InRange(q.ImpliedVolatility!.Value, 0.20m, 0.30m);
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
			DateTime.SpecifyKind(asOfEt.Date.AddHours(9).AddMinutes(31), DateTimeKind.Unspecified),
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

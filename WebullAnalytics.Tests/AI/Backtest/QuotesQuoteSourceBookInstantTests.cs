using Microsoft.Data.Sqlite;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

/// <summary>A quote-store row labeled M is the book struck at M+1:00 (<see cref="QuoteStoreCache.BookInstant"/>), so the
/// spot QuotesQuoteSource pairs with it must be the underlying's price at that same instant — the close of bar M — not
/// bar M's open (M:00) or the day's open. Without minute bars it falls back to the day's open.</summary>
public class QuotesQuoteSourceBookInstantTests : IDisposable
{
	private readonly string _tmpDir;

	public QuotesQuoteSourceBookInstantTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-qqs-book-instant-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	private static readonly DateTime Day = new(2026, 10, 16);
	private static readonly DateTime LabelEt = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Unspecified);
	private const decimal DailyOpen = 7700m;

	[Fact]
	public void BookInstant_IsOneMinuteAfterTheLabel()
	{
		Assert.Equal(new DateTime(2026, 10, 16, 9, 31, 0), QuoteStoreCache.BookInstant(new DateTime(2026, 10, 16, 9, 30, 0)));
	}

	[Fact]
	public async Task DefaultSpot_IsThePriceAtTheBookInstant_TheLabelBarsClose()
	{
		// Bars 10:59, 11:00, 11:01 ET (14:59Z/15:00Z/15:01Z on an EDT day). Label 11:00 → book at 11:01:00 → 11:00 bar's close.
		WriteMinuteBars("SPXW", ("2026-10-16T14:59:00Z", 7710m, 7711m), ("2026-10-16T15:00:00Z", 7711m, 7712m), ("2026-10-16T15:01:00Z", 7712m, 7713m));
		var source = BuildSource(withMinuteBars: true);

		var snap = await source.GetQuotesAsync(LabelEt, new HashSet<string>(), new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase), CancellationToken.None);

		Assert.Equal(7712m, snap.Underlyings["SPXW"]);
	}

	[Fact]
	public async Task DefaultSpot_FallsBackToTheDaysOpen_WithoutMinuteBars()
	{
		var source = BuildSource(withMinuteBars: false);

		var snap = await source.GetQuotesAsync(LabelEt, new HashSet<string>(), new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase), CancellationToken.None);

		Assert.Equal(DailyOpen, snap.Underlyings["SPXW"]);
	}

	private QuotesQuoteSource BuildSource(bool withMinuteBars)
	{
		var data = new Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>(StringComparer.OrdinalIgnoreCase)
		{
			["SPXW"] = new() { [Day] = new YahooOptionsClient.HistoricalBar(Day, DailyOpen, DailyOpen, DailyOpen, DailyOpen, DailyOpen, null) },
		};
		var barsDir = Path.Combine(_tmpDir, "bars");
		Directory.CreateDirectory(barsDir);
		var bars = new HistoricalBarCache(barsDir,
			(ticker, from, to, ct) => Task.FromResult(data.TryGetValue(ticker, out var map) ? map : new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>()),
			utcNow: () => Day.AddDays(1).AddHours(18));
		var store = new QuoteStoreCache(WriteEmptyStore());
		var parametric = new BacktestQuoteSource(bars, new BacktestIVProvider(bars));
		var minuteBars = withMinuteBars ? new IntradayBarCache(BacktestRunner.NoopIntradayFetcher, Path.Combine(_tmpDir, "intraday")) : null;
		return new QuotesQuoteSource(bars, store, parametric, intradayBars: minuteBars);
	}

	private void WriteMinuteBars(string ticker, params (string TimestampUtc, decimal Open, decimal Close)[] rows)
	{
		var dir = Path.Combine(_tmpDir, "intraday", ticker);
		Directory.CreateDirectory(dir);
		var lines = rows.Select(r => $"{r.TimestampUtc},{r.Open},{Math.Max(r.Open, r.Close)},{Math.Min(r.Open, r.Close)},{r.Close},100");
		File.WriteAllText(Path.Combine(dir, $"{Day:yyyy-MM-dd}.csv"), "timestamp_utc,open,high,low,close,volume\n" + string.Join("\n", lines) + "\n");
	}

	private string WriteEmptyStore()
	{
		var dbPath = Path.Combine(_tmpDir, "quotes.db");
		using var conn = new SqliteConnection($"Data Source={dbPath}");
		conn.Open();
		using var schema = conn.CreateCommand();
		schema.CommandText = "CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";
		schema.ExecuteNonQuery();
		return dbPath;
	}
}

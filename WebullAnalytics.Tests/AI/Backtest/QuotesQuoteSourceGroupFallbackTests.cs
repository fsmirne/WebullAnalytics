using Microsoft.Data.Sqlite;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

/// <summary>Locks QuotesQuoteSource's real-data-only pricing: a one-sided book (bid genuinely 0 — the
/// far-OTM/expiry-day norm) is real data and prices from its live side directly, with NO parametric
/// substitution — a prior WIP fix routed such legs through the parametric Black-Scholes model instead,
/// which let two legs of the same spread price off two different processes and (combined with an unrelated
/// smile-model bug) let a 2025-07-30 short put vertical close for MORE than the 100% max a credit spread can
/// ever return. Only a print with no real side at all, or an inverted print, is treated as unusable.</summary>
public class QuotesQuoteSourceGroupFallbackTests : IDisposable
{
	private readonly string _tmpDir;

	public QuotesQuoteSourceGroupFallbackTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-qqs-group-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	private const string Near = "SPY250919P00590000"; // near-the-money leg — real one-sided (bid=0) quote
	private const string Far = "SPY250919P00580000"; // further-OTM leg — real two-sided quote
	private static readonly DateTime AsOf = new(2025, 7, 30, 11, 21, 0, DateTimeKind.Unspecified);
	private static readonly DateTime Expiry = new(2025, 9, 19);

	[Fact]
	public async Task OneSidedLeg_PricesFromItsRealAsk_NotAParametricSubstitute()
	{
		var dbPath = WriteRawStore(
			(Near, StrikeMilli: 590000, BidTicks: 0, AskTicks: 30000),          // bid=0, real one-sided quote: $0.00 x $3.00
			(Far, StrikeMilli: 580000, BidTicks: 12000, AskTicks: 12200));       // real two-sided quote: 1.20 x 1.22

		var store = new QuoteStoreCache(dbPath);
		var (bars, iv) = BuildSpyCaches(spyOpen: 635.35m);
		var parametric = new BacktestQuoteSource(bars, iv);
		var quotes = new QuotesQuoteSource(bars, store, parametric);

		var syms = new HashSet<string>(new[] { Near, Far }, StringComparer.OrdinalIgnoreCase);
		var tickers = new HashSet<string>(new[] { "SPY" }, StringComparer.OrdinalIgnoreCase);
		var snap = await quotes.GetQuotesAsync(AsOf, syms, tickers, CancellationToken.None);

		Assert.True(snap.Options.TryGetValue(Near, out var nearQuote));
		Assert.True(snap.Options.TryGetValue(Far, out var farQuote));

		// The one-sided leg keeps its real bid/ask exactly, and its Mid is the live ask ($3.00) — not half
		// of it, and not a parametric model price.
		Assert.Equal(0m, nearQuote!.Bid);
		Assert.Equal(3.00m, nearQuote.Ask);
		Assert.Equal(3.00m, nearQuote.LastPrice);

		// The two-sided leg keeps its real quote untouched.
		Assert.Equal(1.20m, farQuote!.Bid);
		Assert.Equal(1.22m, farQuote.Ask);
	}

	[Fact]
	public async Task EmptyPrint_IsOmitted_NotPricedAtZero()
	{
		var dbPath = WriteRawStore((Near, StrikeMilli: 590000, BidTicks: 0, AskTicks: 0)); // no print this minute

		var store = new QuoteStoreCache(dbPath);
		var (bars, iv) = BuildSpyCaches(spyOpen: 635.35m);
		var parametric = new BacktestQuoteSource(bars, iv);
		var quotes = new QuotesQuoteSource(bars, store, parametric);

		var syms = new HashSet<string>(new[] { Near }, StringComparer.OrdinalIgnoreCase);
		var tickers = new HashSet<string>(new[] { "SPY" }, StringComparer.OrdinalIgnoreCase);
		var snap = await quotes.GetQuotesAsync(AsOf, syms, tickers, CancellationToken.None);

		Assert.False(snap.Options.ContainsKey(Near));
	}

	private (HistoricalBarCache Bars, BacktestIVProvider Iv) BuildSpyCaches(decimal spyOpen)
	{
		var prior = AsOf.Date.AddDays(-1);
		var data = new Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>(StringComparer.OrdinalIgnoreCase)
		{
			["SPY"] = new() { [AsOf.Date] = MakeBar(AsOf.Date, spyOpen) },
			["VIX"] = new() { [prior] = MakeBar(prior, 17.5m) },
			["VIX1D"] = new() { [prior] = MakeBar(prior, 17.5m) },
			["VIX9D"] = new() { [prior] = MakeBar(prior, 17.5m) },
		};
		var cacheDir = Path.Combine(_tmpDir, "bars-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(cacheDir);
		var bars = new HistoricalBarCache(
			cacheDir,
			(ticker, from, to, ct) => Task.FromResult(data.TryGetValue(ticker, out var map) ? map : new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>()),
			utcNow: () => new DateTimeOffset(AsOf.Date.AddDays(1).AddHours(18), TimeSpan.Zero).UtcDateTime);
		var iv = new BacktestIVProvider(bars);
		return (bars, iv);
	}

	private static YahooOptionsClient.HistoricalBar MakeBar(DateTime date, decimal value) =>
		new(date, value, value, value, value, value, null);

	private const string SchemaSql =
		"CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";

	/// <summary>Writes raw quote rows straight to the store's schema, WITHOUT the two-sided filter that
	/// QuoteStoreCacheTests' helper applies — needed here to plant a genuinely one-sided (bid=0) row, which
	/// is exactly what the real importer keeps (store is faithful to the vendor; see encode_quote in
	/// scripts/import_quotes_sqlite.py).</summary>
	private string WriteRawStore(params (string Occ, long StrikeMilli, long BidTicks, long AskTicks)[] rows)
	{
		var dbPath = Path.Combine(_tmpDir, "quotes-" + Guid.NewGuid().ToString("N") + ".db");
		using var conn = new SqliteConnection($"Data Source={dbPath}");
		conn.Open();
		using (var schema = conn.CreateCommand()) { schema.CommandText = SchemaSql; schema.ExecuteNonQuery(); }
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO quotes VALUES ('SPY',$expiry,$date,$sec,$strike,'P',$bid,$ask,100,100)";
		var timeSec = AsOf.Hour * 3600 + AsOf.Minute * 60 + AsOf.Second;
		foreach (var row in rows)
		{
			cmd.Parameters.Clear();
			cmd.Parameters.AddWithValue("$expiry", int.Parse(Expiry.ToString("yyyyMMdd")));
			cmd.Parameters.AddWithValue("$date", int.Parse(AsOf.ToString("yyyyMMdd")));
			cmd.Parameters.AddWithValue("$sec", timeSec);
			cmd.Parameters.AddWithValue("$strike", row.StrikeMilli);
			cmd.Parameters.AddWithValue("$bid", row.BidTicks);
			cmd.Parameters.AddWithValue("$ask", row.AskTicks);
			cmd.ExecuteNonQuery();
		}
		return dbPath;
	}
}

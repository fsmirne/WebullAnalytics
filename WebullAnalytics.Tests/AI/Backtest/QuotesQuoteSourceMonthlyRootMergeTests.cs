using Microsoft.Data.Sqlite;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

/// <summary>On a standard-monthly (3rd Friday) SPXW expiry, real open interest splits across the legacy
/// SPX (AM-settled, untraded — ThetaData backfills its OI only, no minute-NBBO) and SPXW (PM-settled,
/// traded) roots. QuotesQuoteSource must surface the SPX-rooted contracts too — from the merged OI
/// snapshot alone, since there's no quotes.db row for them — or the backtest's GEX/max-pain factors only
/// ever see the SPXW half of the real book (the live 2026-08-25 finding this fix chain resolves).</summary>
public class QuotesQuoteSourceMonthlyRootMergeTests : IDisposable
{
	private readonly string _tmpDir;

	public QuotesQuoteSourceMonthlyRootMergeTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-qqs-monthly-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	private static readonly DateTime MonthlyExpiry = new(2026, 10, 16); // 3rd Friday of October 2026
	private static readonly DateTime AsOf = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Unspecified);
	private const string SpxwLeg = "SPXW261016C07700000";
	private const string SpxSiblingInBand = "SPX261016C07600000";
	private const string SpxSiblingOutOfBand = "SPX261016C09000000"; // far outside the expansion band

	[Fact]
	public async Task SurfacesInBandSpxSiblingFromOiSnapshotAlone_NoQuotesDbRowNeeded()
	{
		var dbPath = WriteRawStore((SpxwLeg, StrikeMilli: 7700000, BidTicks: 10000, AskTicks: 10200));
		var oiDir = Path.Combine(_tmpDir, "oi");
		WriteOiSnapshot(oiDir, "SPX", MonthlyExpiry,
			(SpxSiblingInBand, Oi: 20000, Iv: 0.15m),
			(SpxSiblingOutOfBand, Oi: 20000, Iv: 0.15m));

		var store = new QuoteStoreCache(dbPath);
		var (bars, iv) = BuildCaches(spot: 7673.73m);
		var parametric = new BacktestQuoteSource(bars, iv);
		var oiCache = new ChainSnapshotOiCache(oiDir);
		var quotes = new QuotesQuoteSource(bars, store, parametric, oiCache: oiCache);

		var syms = new HashSet<string>(new[] { SpxwLeg }, StringComparer.OrdinalIgnoreCase);
		var tickers = new HashSet<string>(new[] { "SPXW" }, StringComparer.OrdinalIgnoreCase);
		var snap = await quotes.GetQuotesAsync(AsOf, syms, tickers, CancellationToken.None);

		// The requested SPXW leg prices normally off the real NBBO.
		Assert.True(snap.Options.TryGetValue(SpxwLeg, out var spxwQuote));
		Assert.Equal(1.00m, spxwQuote!.Bid);

		// The in-band SPX sibling shows up from OI alone: OI+IV present, no price (nothing trades it).
		Assert.True(snap.Options.TryGetValue(SpxSiblingInBand, out var spxQuote));
		Assert.Equal(20000, spxQuote!.OpenInterest);
		Assert.Equal(0.15m, spxQuote.ImpliedVolatility);
		Assert.Null(spxQuote.Bid);
		Assert.Null(spxQuote.Ask);

		// The out-of-band SPX sibling is NOT pulled in — the merge respects the same near-money band as
		// the real-quote expansion, it doesn't dump the whole OI file into the snapshot.
		Assert.False(snap.Options.ContainsKey(SpxSiblingOutOfBand));
	}

	private (HistoricalBarCache Bars, BacktestIVProvider Iv) BuildCaches(decimal spot)
	{
		var prior = AsOf.Date.AddDays(-1);
		var data = new Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>(StringComparer.OrdinalIgnoreCase)
		{
			["SPXW"] = new() { [AsOf.Date] = MakeBar(AsOf.Date, spot) },
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
		var ivProvider = new BacktestIVProvider(bars);
		return (bars, ivProvider);
	}

	private static YahooOptionsClient.HistoricalBar MakeBar(DateTime date, decimal value) =>
		new(date, value, value, value, value, value, null);

	private const string SchemaSql =
		"CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";

	private string WriteRawStore(params (string Occ, long StrikeMilli, long BidTicks, long AskTicks)[] rows)
	{
		var dbPath = Path.Combine(_tmpDir, "quotes-" + Guid.NewGuid().ToString("N") + ".db");
		using var conn = new SqliteConnection($"Data Source={dbPath}");
		conn.Open();
		using (var schema = conn.CreateCommand()) { schema.CommandText = SchemaSql; schema.ExecuteNonQuery(); }
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO quotes VALUES ('SPXW',$expiry,$date,$sec,$strike,'C',$bid,$ask,100,100)";
		var timeSec = AsOf.Hour * 3600 + AsOf.Minute * 60 + AsOf.Second;
		foreach (var row in rows)
		{
			cmd.Parameters.Clear();
			cmd.Parameters.AddWithValue("$expiry", int.Parse(MonthlyExpiry.ToString("yyyyMMdd")));
			cmd.Parameters.AddWithValue("$date", int.Parse(AsOf.ToString("yyyyMMdd")));
			cmd.Parameters.AddWithValue("$sec", timeSec);
			cmd.Parameters.AddWithValue("$strike", row.StrikeMilli);
			cmd.Parameters.AddWithValue("$bid", row.BidTicks);
			cmd.Parameters.AddWithValue("$ask", row.AskTicks);
			cmd.ExecuteNonQuery();
		}
		return dbPath;
	}

	private static void WriteOiSnapshot(string oiDir, string root, DateTime date, params (string Symbol, long Oi, decimal Iv)[] contracts)
	{
		var dir = Path.Combine(oiDir, root);
		Directory.CreateDirectory(dir);
		var opts = string.Join(",", contracts.Select(c => $"{{\"symbol\":\"{c.Symbol}\",\"openInterest\":{c.Oi},\"iv\":{c.Iv}}}"));
		var line = $"{{\"tsEt\":\"{date:yyyy-MM-dd}T09:30:00-05:00\",\"options\":[{opts}]}}";
		File.WriteAllText(Path.Combine(dir, $"{date:yyyy-MM-dd}.jsonl"), line + "\n");
	}
}

using Microsoft.Data.Sqlite;

namespace WebullAnalytics;

/// <summary>One canonical minute-NBBO row: dates as INTEGER yyyymmdd, strike in thousandths, bid/ask in ten-thousandths (an absent
/// side is 0), the WITHOUT-ROWID primary key <c>(root, expiry, date, strike_milli, right, time_sec)</c>.</summary>
internal readonly record struct QuoteRow(string Root, int Expiry, int Date, int TimeSec, long StrikeMilli, char Right, long Bid, long Ask, long BidSize, long AskSize);

/// <summary>One canonical minute trade bar: the <c>quotes</c> key, prices and vwap in ten-thousandths (a NaN side stored as 0),
/// volume and the vendor's print count.</summary>
internal readonly record struct OhlcvRow(string Root, int Expiry, int Date, int TimeSec, long StrikeMilli, char Right, long Open, long High, long Low, long Close, long Volume, long Trades, long Vwap);

/// <summary>The canonical quote store's schema (<c>data/quotes.db</c>) and the single row encoders every writer uses, so the store is
/// byte-identical regardless of which writer or vendor produced a row. Ported verbatim from scripts/import_quotes_sqlite.py
/// (SCHEMA_SQL and siblings, encode_quote, encode_ohlcv, connect_wal).</summary>
internal static class QuoteStoreSchema
{
	internal const string QuotesSql =
		"CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";
	// Same shape as `quotes` (SELECT * copies positionally): a backfill pull lands each window here, then promotes the finished expiry
	// into `quotes` in one DELETE+copy transaction — an aborted pull must never leave the live table half-rebuilt (a 2026-08-16
	// abort left SPY 2026-08-21 truncated at 07-17 and the truncation traveled through backup/restore).
	internal const string QuotesStagingSql =
		"CREATE TABLE IF NOT EXISTS quotes_staging (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";
	internal const string SealedSql = "CREATE TABLE IF NOT EXISTS sealed (root TEXT, expiry INTEGER, PRIMARY KEY (root, expiry)) WITHOUT ROWID";
	// Interior trading days PROVEN absent at the vendor for an expiry (reproducible across pulls) — the seal-time completeness check
	// accepts exactly these. Lives in the DB so the knowledge travels with the store; curated by hand.
	internal const string KnownHolesSql = "CREATE TABLE IF NOT EXISTS known_holes (root TEXT, expiry INTEGER, date INTEGER, PRIMARY KEY (root, expiry, date)) WITHOUT ROWID";
	// Per-(session, strike) contracts proven absent at the vendor — the strike-level analog the thin-ladder seal gate subtracts.
	internal const string KnownStrikeHolesSql = "CREATE TABLE IF NOT EXISTS known_strike_holes (root TEXT, expiry INTEGER, date INTEGER, strike_milli INTEGER, right TEXT, PRIMARY KEY (root, expiry, date, strike_milli, right)) WITHOUT ROWID";

	// Per-minute trade bars (option_history_ohlc) — the TAPE beside the quotes table's BOOK. Its own table, never new columns on
	// `quotes`: quote rows exist for every disseminated minute while trade bars exist only for traded minutes, and the bars arrive
	// START-of-bar labeled (the quotes pull's -60s relabel does not apply). Its own seal and proven-hole tables, because volume
	// completeness certifies a different pull than quote completeness.
	internal const string OhlcvSql =
		"CREATE TABLE IF NOT EXISTS ohlcv (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, open INTEGER, high INTEGER, low INTEGER, close INTEGER, " +
		"volume INTEGER, trades INTEGER, vwap INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";
	internal const string OhlcvStagingSql =
		"CREATE TABLE IF NOT EXISTS ohlcv_staging (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, open INTEGER, high INTEGER, low INTEGER, close INTEGER, " +
		"volume INTEGER, trades INTEGER, vwap INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";
	internal const string OhlcvSealedSql = "CREATE TABLE IF NOT EXISTS ohlcv_sealed (root TEXT, expiry INTEGER, PRIMARY KEY (root, expiry)) WITHOUT ROWID";
	internal const string OhlcvKnownHolesSql = "CREATE TABLE IF NOT EXISTS ohlcv_known_holes (root TEXT, expiry INTEGER, date INTEGER, PRIMARY KEY (root, expiry, date)) WITHOUT ROWID";

	/// <summary>Opens the store in WAL mode with a generous busy-timeout — the multi-writer setting that lets the backfill and the
	/// scraper write while the backtest reads — creating the data and staging tables if absent.</summary>
	internal static SqliteConnection OpenWal(string dbPath)
	{
		var conn = new SqliteConnection($"Data Source={dbPath}");
		conn.Open();
		foreach (var sql in new[] { "PRAGMA busy_timeout=60000", "PRAGMA journal_mode=WAL", QuotesSql, QuotesStagingSql, OhlcvSql, OhlcvStagingSql })
			Exec(conn, sql);
		return conn;
	}

	internal static void Exec(SqliteConnection conn, string sql)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	internal static int YmdInt(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;
	internal static DateOnly FromYmdInt(long ymd) => new((int)(ymd / 10000), (int)(ymd / 100 % 100), (int)(ymd % 100));

	/// <summary>A price in ten-thousandths, or null for NaN (that side wasn't disseminated). Math.Round's default (to-even) matches
	/// Python's round(), so float-noisy penny ticks land on the same integer the Python writer stored.</summary>
	internal static long? Tick(double price) => double.IsNaN(price) ? null : (long)Math.Round(price * 10000);

	/// <summary>A strike in thousandths (Python round(strike * 1000)).</summary>
	internal static long StrikeMilli(double strike) => (long)Math.Round(strike * 1000);

	/// <summary>One quote as its canonical row, or null when it carries no numeric side at all (no quote disseminated that minute) or a
	/// negative one. The store is FAITHFUL to the vendor: one-sided books and empty 0/0 auction rows are kept with an absent side
	/// stored as 0 (an NBBO bid/ask of 0 IS absence); policy lives in the readers. Until 2026-07-22 the encoder required bid>0 AND
	/// ask>0, which annihilated one-sided books store-wide and left management rules blind on expiry-day buybacks.</summary>
	internal static QuoteRow? EncodeQuote(string root, int expiry, int date, int timeSec, double strike, char right, double bid, double ask, long bidSize, long askSize)
	{
		var (bidTick, askTick) = (Tick(bid), Tick(ask));
		if (bidTick == null && askTick == null) return null;
		if (bidTick < 0 || askTick < 0) return null;
		return new QuoteRow(root, expiry, date, timeSec, StrikeMilli(strike), right, bidTick ?? 0, askTick ?? 0, bidSize, askSize);
	}

	/// <summary>One trade bar as its canonical row, or null when it carries no trades (the vendor serves a dense minute grid of
	/// volume-0 filler; only traded minutes are stored) or has no numeric close (vendor noise). Other price sides that are NaN or
	/// negative store as 0.</summary>
	internal static OhlcvRow? EncodeOhlcv(string root, int expiry, int date, int timeSec, double strike, char right, double open, double high, double low, double close, long volume, long trades, double vwap)
	{
		if (volume <= 0) return null;
		if (Tick(close) is not { } closeTick || closeTick < 0) return null;
		return new OhlcvRow(root, expiry, date, timeSec, StrikeMilli(strike), right, ZeroIfAbsent(open), ZeroIfAbsent(high), ZeroIfAbsent(low), closeTick, volume, trades, ZeroIfAbsent(vwap));
	}

	private static long ZeroIfAbsent(double price) => Tick(price) is { } tick && tick >= 0 ? tick : 0;
}

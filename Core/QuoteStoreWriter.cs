using System.Globalization;
using Microsoft.Data.Sqlite;
using WebullAnalytics.Pricing;

namespace WebullAnalytics;

/// <summary>Appends live-captured minute NBBO into the canonical SQLite quote store (<c>data/quotes.db</c>) —
/// the write counterpart to <see cref="AI.Backtest.QuoteStoreCache"/>'s read path, used by the wa-scraper.
/// Encoding is identical to the ThetaData backfill / importer: dates as INTEGER yyyymmdd, bid/ask as scaled
/// integers (ten-thousandths), the WITHOUT-ROWID PK <c>(root,expiry,date,strike_milli,right,time_sec)</c>.
/// Inserts are additive and idempotent (INSERT OR IGNORE), so re-ticking the same minute is a no-op and the
/// scraper never clobbers backfilled rows. Opened in WAL mode with a busy-timeout so it coexists with the
/// backfill (another writer) and the backtest (reader). One instance is held open for the scraper session.</summary>
internal sealed class QuoteStoreWriter : IDisposable
{
	// Must match scripts/import_quotes_sqlite.py SCHEMA_SQL exactly — the one canonical schema.
	private const string SchemaSql =
		"CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";

	private readonly SqliteConnection _conn;

	public QuoteStoreWriter(string? dbPath = null)
	{
		var path = dbPath ?? Program.ResolvePath("data/quotes.db");
		_conn = new SqliteConnection($"Data Source={path}");
		_conn.Open();
		Exec("PRAGMA busy_timeout=60000");
		Exec("PRAGMA journal_mode=WAL");
		Exec(SchemaSql);
	}

	private void Exec(string sql)
	{
		using var c = _conn.CreateCommand();
		c.CommandText = sql;
		c.ExecuteNonQuery();
	}

	/// <summary>Writes one tick's two-sided quotes for <paramref name="root"/> at the given ET date/time.
	/// Contracts missing a positive bid AND ask are skipped (the same two-sided filter the reader applies),
	/// so a one-sided book contributes no row. Returns the number of rows written.</summary>
	public int WriteTick(string root, string dateStr, string timeStr, IEnumerable<OptionContractQuote> contracts)
	{
		var dateYmd = YmdInt(dateStr);
		var sec = SecOfDay(timeStr);
		if (sec < 0) return 0;
		var rootUp = root.ToUpperInvariant();

		using var tx = _conn.BeginTransaction();
		using var cmd = _conn.CreateCommand();
		cmd.CommandText = "INSERT OR IGNORE INTO quotes VALUES ($root,$exp,$date,$sec,$strike,$right,$bid,$ask,$bsz,$asz)";
		var pRoot = cmd.CreateParameter(); pRoot.ParameterName = "$root"; pRoot.Value = rootUp; cmd.Parameters.Add(pRoot);
		var pExp = Param(cmd, "$exp"); var pDate = Param(cmd, "$date"); var pSec = Param(cmd, "$sec");
		var pStrike = Param(cmd, "$strike"); var pRight = Param(cmd, "$right");
		var pBid = Param(cmd, "$bid"); var pAsk = Param(cmd, "$ask"); var pBsz = Param(cmd, "$bsz"); var pAsz = Param(cmd, "$asz");
		pDate.Value = dateYmd; pSec.Value = sec;

		var written = 0;
		foreach (var q in contracts)
		{
			if (q.Bid is not decimal bid || bid <= 0m) continue;
			if (q.Ask is not decimal ask || ask <= 0m) continue;
			var p = ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
			if (p?.CallPut is not string cp || cp.Length == 0) continue;
			pExp.Value = YmdInt(p.ExpiryDate);
			pStrike.Value = (long)Math.Round(p.Strike * 1000m);
			pRight.Value = char.ToUpperInvariant(cp[0]).ToString();
			pBid.Value = (long)Math.Round(bid * 10000m);
			pAsk.Value = (long)Math.Round(ask * 10000m);
			pBsz.Value = q.BidSize ?? 0;
			pAsz.Value = q.AskSize ?? 0;
			written += cmd.ExecuteNonQuery();
		}
		tx.Commit();
		return written;
	}

	private static SqliteParameter Param(SqliteCommand cmd, string name)
	{
		var p = cmd.CreateParameter();
		p.ParameterName = name;
		cmd.Parameters.Add(p);
		return p;
	}

	private static int YmdInt(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
	private static int YmdInt(string yyyyMMdd) => int.Parse(yyyyMMdd[..4] + yyyyMMdd[5..7] + yyyyMMdd[8..10], CultureInfo.InvariantCulture);

	private static int SecOfDay(string hms)  // "HH:MM:SS" -> seconds, or -1 if malformed
	{
		var parts = hms.Split(':');
		if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return -1;
		var s = parts.Length > 2 && int.TryParse(parts[2], out var ss) ? ss : 0;
		return h * 3600 + m * 60 + s;
	}

	public void Dispose() => _conn.Dispose();
}

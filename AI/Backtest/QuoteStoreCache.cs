using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using nietras.SeparatedValues;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Reads the ThetaData minute-NBBO quote store —
/// <c>data/quotes/&lt;root&gt;/&lt;expiry&gt;.csv</c> with columns
/// <c>date,time,strike,right,bid,ask,bid_size,ask_size</c> — and answers "real NBBO for this contract at
/// this ET minute" with a bounded stale-quote lookback. This is the quotes-only price foundation that
/// replaces massive trade-bars + the synthetic spread model (see <see cref="BacktestQuoteSource"/>).
/// Per-expiration files are loaded lazily and cached. Quote age (exact vs stale-by-N-min vs missing) is
/// the new pricing-provenance signal — the analog of "captured bar vs synthetic" on the trade-bar path.</summary>
internal sealed class QuoteStoreCache
{
	/// <summary>A real two-sided quote, with how stale it is vs the requested minute (0 = exact-minute print).</summary>
	internal readonly record struct QuoteAt(decimal Bid, decimal Ask, int BidSize, int AskSize, int AgeMinutes)
	{
		public decimal Mid => (Bid + Ask) / 2m;
	}

	private readonly string _dir;
	private readonly int _maxStaleMinutes;
	private readonly ConcurrentDictionary<(string Root, DateTime Exp), ExpiryQuotes> _cache = new();

	/// <param name="maxStaleMinutes">How far back to accept the most recent quote when the exact minute has
	/// none. Minute NBBO is dense for liquid near-money contracts, so a small window (default 5 min) keeps
	/// staleness honest; raise it for thin far-dated legs.</param>
	private readonly DateTime _since;
	private readonly DateTime _until;
	// When set, only rows whose trade-date equals the file's expiry are parsed — i.e. the 0DTE slice.
	// A file is named by expiry and (for SPY) carries the whole 45DTE→0DTE life of every contract that
	// expires that day; a strictly-0DTE strategy only ever queries the same-day rows, so parsing the
	// longer-dated tail is pure waste. Set by the caller only when it has PROVEN the run is same-day
	// (every enabled opener structure is 0DTE AND no roll-to-future rule is active).
	private readonly bool _sameDayExpiryOnly;
	// PROTOTYPE: when set, rows are read from a SQLite store (one indexed table mirroring the CSVs) instead
	// of per-expiry CSV files. The index on (root, expiry, date) lets the per-expiry load fetch exactly the
	// rows it needs — no full-file scan, so the 45DTE tail SPY files carry is never read. The in-memory
	// ExpiryQuotes structure and all lookups are unchanged, so results are identical to the CSV path.
	private readonly string? _dbPath;
	// Expiry-eviction watermark: the backtest queries the store only at the current sim minute and never
	// looks back at an expired contract (settlement prices from the underlying bar, not the option NBBO —
	// see BacktestRunner.SettleExpirationsAsync), so once the sim date passes an expiry its parsed rows are
	// dead weight. Without this a full-year run accumulates every expiry it ever touched (~tens of GB in the
	// in-memory decimal form, several× the on-disk CSV) and GC-thrashes mid-year. We drop them as the sim
	// date advances, bounding the cache to the live DTE band. Invariant: asOf advances monotonically forward.
	private long _lastSweepDateTicks;
	private readonly object _evictLock = new();

	/// <param name="since">/<param name="until">When set to the backtest window, each per-expiration file
	/// only parses rows whose date is inside [since, until] — a 45-day file that a short tuning run barely
	/// touches skips the out-of-window rows before parsing them (a big cold-load win for short windows; a
	/// no-op for a full-period run). Defaults to all-dates.</param>
	public QuoteStoreCache(string? dir = null, int maxStaleMinutes = 5, DateTime? since = null, DateTime? until = null, bool sameDayExpiryOnly = false, string? dbPath = null)
	{
		_dir = dir ?? Program.ResolvePath("data/quotes");
		_maxStaleMinutes = maxStaleMinutes;
		_since = (since ?? DateTime.MinValue).Date;
		_until = (until ?? DateTime.MaxValue).Date;
		_sameDayExpiryOnly = sameDayExpiryOnly;
		_dbPath = dbPath;
	}

	/// <summary>Latest real two-sided NBBO at or before <paramref name="asOfEt"/> (ET wall-clock) for the
	/// contract, within the staleness window; null if none → the caller's missing-quote policy decides.</summary>
	public QuoteAt? NbboAt(string occ, DateTime asOfEt)
	{
		EvictExpiredBefore(asOfEt.Date);
		var p = ParsingHelpers.ParseOptionSymbol(occ);
		if (p?.CallPut == null) return null;
		var eq = _cache.GetOrAdd((p.Root.ToUpperInvariant(), p.ExpiryDate.Date),
			key => ExpiryQuotes.Load(_dir, key.Root, key.Exp, _since, _until, _sameDayExpiryOnly, _dbPath));
		return eq.Lookup(asOfEt.Date, (long)Math.Round(p.Strike * 1000m), p.CallPut[0],
			(int)asOfEt.TimeOfDay.TotalSeconds, _maxStaleMinutes);
	}

	/// <summary>Drops every cached expiration that has already expired before <paramref name="asOfDate"/> —
	/// the backtest never queries them again, so their parsed rows are pure memory pressure. Runs at most once
	/// per sim day: the fast path is a lock-free atomic read of the watermark; only the first call of a new day
	/// takes the lock and sweeps. ConcurrentDictionary.Keys is a snapshot, so removing during enumeration (and
	/// concurrent reads from the still-active minute scan) is safe.</summary>
	private void EvictExpiredBefore(DateTime asOfDate)
	{
		if (asOfDate.Ticks <= Volatile.Read(ref _lastSweepDateTicks)) return;
		lock (_evictLock)
		{
			if (asOfDate.Ticks <= _lastSweepDateTicks) return;
			foreach (var key in _cache.Keys)
				if (key.Exp.Date < asOfDate) _cache.TryRemove(key, out _);
			Volatile.Write(ref _lastSweepDateTicks, asOfDate.Ticks);
		}
	}

	/// <summary>True if a quote file exists for this expiration (cheap coverage check, no load).</summary>
	public bool HasExpiry(string root, DateTime exp)
		=> File.Exists(Path.Combine(_dir, root.ToUpperInvariant(), $"{exp.Date:yyyy-MM-dd}.csv"));

	/// <summary>True if the store holds ANY real quote row dated within [<paramref name="since"/>, <paramref name="until"/>]
	/// for <paramref name="root"/>. Distinguishes "the strategy declined to trade" from "the quote store has not been
	/// backfilled for this window yet" (the common trap: running <c>--since &lt;today&gt;</c> before the evening
	/// backfill has landed the day's quotes). Streams only the date column, skips expiry files that pre-date the
	/// window start (a contract's rows are dated at or before its expiry, so such files can't hold in-window rows),
	/// and returns on the first match — so the has-coverage path is near-instant; only a genuinely-empty window
	/// pays a full scan.</summary>
	public bool HasAnyQuoteInWindow(string root, DateTime since, DateTime until)
	{
		var rootDir = Path.Combine(_dir, root.ToUpperInvariant());
		if (!Directory.Exists(rootDir)) return false;
		var lo = since.Date;
		var hi = until.Date;
		foreach (var path in Directory.EnumerateFiles(rootDir, "*.csv"))
		{
			var name = Path.GetFileNameWithoutExtension(path);
			if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp) && exp.Date < lo) continue;
			var first = true;
			foreach (var line in File.ReadLines(path))
			{
				if (first) { first = false; continue; }   // header
				var comma = line.IndexOf(',');
				if (comma <= 0) continue;
				if (DateTime.TryParse(line.AsSpan(0, comma), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Date >= lo && d.Date <= hi)
					return true;
			}
		}
		return false;
	}

	/// <summary>OCC symbols for <paramref name="root"/> at <paramref name="expiry"/> that carry a real quote on
	/// <paramref name="date"/> with strike in [<paramref name="loStrike"/>, <paramref name="hiStrike"/>]. Lets a
	/// single probe expand into that expiry's real near-money chain: the live broker returns the whole chain for
	/// any one symbol, but this store returns only exact matches, so the backtest opener (which probes a
	/// placeholder strike per band-expiry) would otherwise see no contracts and enumerate nothing.</summary>
	public IEnumerable<string> ContractsOn(string root, DateTime expiry, DateTime date, decimal loStrike, decimal hiStrike)
	{
		EvictExpiredBefore(date.Date);
		var eq = _cache.GetOrAdd((root.ToUpperInvariant(), expiry.Date),
			key => ExpiryQuotes.Load(_dir, key.Root, key.Exp, _since, _until, _sameDayExpiryOnly, _dbPath));
		var loMilli = (long)Math.Round(loStrike * 1000m);
		var hiMilli = (long)Math.Round(hiStrike * 1000m);
		var yy = expiry.ToString("yyMMdd", CultureInfo.InvariantCulture);
		var rootUp = root.ToUpperInvariant();
		foreach (var (strikeMilli, cp) in eq.ContractsOn(date.Date, loMilli, hiMilli))
			yield return $"{rootUp}{yy}{cp}{strikeMilli:D8}";
	}

	private sealed class ExpiryQuotes
	{
		private readonly Dictionary<(DateTime Date, long StrikeMilli, char Cp),
			List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>> _rows;
		// Per-date index of the (strike, call/put) contracts present, so ContractsOn is O(contracts-on-that-day)
		// instead of re-scanning every key each minute (the chain-expansion hot path queries this per tick).
		private readonly Dictionary<DateTime, List<(long StrikeMilli, char Cp)>> _byDate;

		private ExpiryQuotes(Dictionary<(DateTime Date, long StrikeMilli, char Cp),
			List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>> rows)
		{
			_rows = rows;
			_byDate = new Dictionary<DateTime, List<(long, char)>>();
			foreach (var key in rows.Keys)
			{
				if (!_byDate.TryGetValue(key.Date, out var list)) { list = new(); _byDate[key.Date] = list; }
				list.Add((key.StrikeMilli, key.Cp));
			}
		}

		public static ExpiryQuotes Load(string dir, string root, DateTime exp, DateTime since, DateTime until, bool sameDayExpiryOnly, string? dbPath)
		{
			var rows = new Dictionary<(DateTime Date, long StrikeMilli, char Cp),
				List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>>();
			if (dbPath != null) return LoadFromSqlite(rows, dbPath, root, exp, since, until, sameDayExpiryOnly);
			var path = Path.Combine(dir, root, $"{exp:yyyy-MM-dd}.csv");
			if (!File.Exists(path)) return new ExpiryQuotes(rows);

			// Parsed with Sep (nietras.SeparatedValues): ~7% faster cold-load than File.ReadLines +
			// string.Split on the multi-year sweeps, results byte-identical (verified against the prior
			// hand-split parser). Semantics preserved exactly — header consumed by the reader, >=6 cols,
			// early [since,until] date-window skip, time->sec, strike parse, right->C/P, BOTH bid>0 AND
			// ask>0 required, optional sizes.
			using var reader = Sep.Reader().FromFile(path);
			foreach (var row in reader)
			{
				if (row.ColCount < 6) continue;
				if (!DateTime.TryParse(row[0].Span, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
				if (d.Date < since || d.Date > until) continue;
				// 0DTE-only runs use just the same-day slice; skip the longer-dated tail without parsing it.
				if (sameDayExpiryOnly && d.Date != exp.Date) continue;
				var sec = ParseTimeSec(row[1].ToString());
				if (sec < 0) continue;
				// Times are already START-of-bar (09:30 = first RTH minute): the ThetaData pull normalizes
				// its end-of-bar stamps -60s at ingest, the same place WebullChartsClient does for Webull
				// (see WebullChartsClient.WebullBarShift). The store is canonical on disk, so we never shift here.
				if (!decimal.TryParse(row[2].Span, NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) continue;
				var rt = row[3].Span;
				var c = rt.Length > 0 ? char.ToUpperInvariant(rt[0]) : '?';
				if (c != 'C' && c != 'P') continue;
				// Require BOTH sides for a usable two-sided quote (the pull keeps rows with bid OR ask).
				if (!decimal.TryParse(row[4].Span, NumberStyles.Any, CultureInfo.InvariantCulture, out var bid) || bid <= 0m) continue;
				if (!decimal.TryParse(row[5].Span, NumberStyles.Any, CultureInfo.InvariantCulture, out var ask) || ask <= 0m) continue;
				var bsz = row.ColCount > 6 ? ParseIntLoose(row[6].ToString()) : 0;
				var asz = row.ColCount > 7 ? ParseIntLoose(row[7].ToString()) : 0;
				var key = (d.Date, (long)Math.Round(strike * 1000m), c);
				if (!rows.TryGetValue(key, out var list)) { list = new(); rows[key] = list; }
				list.Add((sec, bid, ask, bsz, asz));
			}
			foreach (var list in rows.Values) list.Sort((x, y) => x.Sec.CompareTo(y.Sec));
			return new ExpiryQuotes(rows);
		}

		/// <summary>SQLite-backed mirror of <see cref="Load"/>. The DB holds canonical, already-validated rows
		/// (the import applied the same filters: ≥6 cols, valid time, strike, right C/P, BOTH bid&gt;0 AND ask&gt;0),
		/// with bid/ask as scaled integers in ten-thousandths (price = value / 10000). That rounds away the
		/// float noise the source CSVs carry (penny-tick data written as raw floats, e.g. 0.35000000000000003)
		/// — a lossless cleanup for real ticks, so backtest results match the CSV path to the cent. The index on
		/// (root, expiry, date) makes this fetch exactly the needed rows; the 45DTE tail is never touched.</summary>
		// yyyymmdd as a single int — the on-disk date encoding. MinValue→10101, MaxValue→99991231 both fit.
		private static int YmdInt(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;

		private static ExpiryQuotes LoadFromSqlite(
			Dictionary<(DateTime Date, long StrikeMilli, char Cp), List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>> rows,
			string dbPath, string root, DateTime exp, DateTime since, DateTime until, bool sameDayExpiryOnly)
		{
			using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
			conn.Open();
			using var cmd = conn.CreateCommand();
			// date/expiry stored as INTEGER yyyymmdd (monotonic, so range comparison is correct).
			cmd.CommandText = sameDayExpiryOnly
				? "SELECT date, time_sec, strike_milli, right, bid, ask, bid_size, ask_size FROM quotes WHERE root=$root AND expiry=$exp AND date=$exp"
				: "SELECT date, time_sec, strike_milli, right, bid, ask, bid_size, ask_size FROM quotes WHERE root=$root AND expiry=$exp AND date>=$since AND date<=$until";
			cmd.Parameters.AddWithValue("$root", root.ToUpperInvariant());
			cmd.Parameters.AddWithValue("$exp", YmdInt(exp));
			if (!sameDayExpiryOnly)
			{
				cmd.Parameters.AddWithValue("$since", YmdInt(since));
				cmd.Parameters.AddWithValue("$until", YmdInt(until));
			}
			using var r = cmd.ExecuteReader();
			while (r.Read())
			{
				var di = r.GetInt32(0);
				var d = new DateTime(di / 10000, di / 100 % 100, di % 100);
				var sec = r.GetInt32(1);
				var strikeMilli = r.GetInt64(2);
				var c = r.GetString(3)[0];
				var bid = r.GetInt64(4) / 10000m;
				var ask = r.GetInt64(5) / 10000m;
				var bsz = r.GetInt32(6);
				var asz = r.GetInt32(7);
				var key = (d.Date, strikeMilli, c);
				if (!rows.TryGetValue(key, out var list)) { list = new(); rows[key] = list; }
				list.Add((sec, bid, ask, bsz, asz));
			}
			foreach (var list in rows.Values) list.Sort((x, y) => x.Sec.CompareTo(y.Sec));
			return new ExpiryQuotes(rows);
		}

		public QuoteAt? Lookup(DateTime date, long strikeMilli, char cp, int asOfSec, int maxStaleMinutes)
		{
			if (!_rows.TryGetValue((date, strikeMilli, cp), out var list) || list.Count == 0) return null;
			// Latest sec <= asOfSec (binary search; list is sorted ascending by Sec).
			int lo = 0, hi = list.Count - 1, idx = -1;
			while (lo <= hi)
			{
				var m = (lo + hi) / 2;
				if (list[m].Sec <= asOfSec) { idx = m; lo = m + 1; } else hi = m - 1;
			}
			if (idx < 0) return null;
			var r = list[idx];
			var age = (asOfSec - r.Sec) / 60;
			return age <= maxStaleMinutes ? new QuoteAt(r.Bid, r.Ask, r.BidSz, r.AskSz, age) : null;
		}

		/// <summary>Distinct (strikeMilli, call/put) contracts that have at least one row on <paramref name="date"/>
		/// with strike in [<paramref name="loMilli"/>, <paramref name="hiMilli"/>] (strike×1000).</summary>
		public IEnumerable<(long StrikeMilli, char Cp)> ContractsOn(DateTime date, long loMilli, long hiMilli)
		{
			if (!_byDate.TryGetValue(date, out var list)) yield break;
			foreach (var (strikeMilli, cp) in list)
				if (strikeMilli >= loMilli && strikeMilli <= hiMilli)
					yield return (strikeMilli, cp);
		}

		private static int ParseTimeSec(string hms)   // "HH:MM:SS"
		{
			var p = hms.Split(':');
			if (p.Length < 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var mi)) return -1;
			var s = p.Length > 2 && int.TryParse(p[2], out var ss) ? ss : 0;
			return h * 3600 + mi * 60 + s;
		}

		private static int ParseIntLoose(string s)     // tolerate "55", "55.0", ""
		{
			var dot = s.IndexOf('.');
			var t = dot >= 0 ? s[..dot] : s;
			return int.TryParse(t, out var v) ? v : 0;
		}
	}
}

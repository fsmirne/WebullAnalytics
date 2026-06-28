using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Reads the canonical ThetaData minute-NBBO quote store — the SQLite <c>quotes.db</c> table
/// <c>(root, expiry, date, time_sec, strike_milli, right, bid, ask, bid_size, ask_size)</c> — and answers
/// "real NBBO for this contract at this ET minute" with a bounded stale-quote lookback. This is the
/// quotes-only price foundation that replaces massive trade-bars + the synthetic spread model (see
/// <see cref="BacktestQuoteSource"/>). Per-expiration slices are loaded lazily (the index on
/// <c>(root, expiry, date)</c> fetches exactly the needed rows) and cached. Quote age (exact vs
/// stale-by-N-min vs missing) is the pricing-provenance signal — the analog of "captured bar vs
/// synthetic" on the trade-bar path. The store is built/kept current by scripts/import_quotes_sqlite.py +
/// the daily backfill; there is no CSV read path.</summary>
internal sealed class QuoteStoreCache
{
	/// <summary>A real two-sided quote, with how stale it is vs the requested minute (0 = exact-minute print).</summary>
	internal readonly record struct QuoteAt(decimal Bid, decimal Ask, int BidSize, int AskSize, int AgeMinutes)
	{
		public decimal Mid => (Bid + Ask) / 2m;
	}

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
	// Canonical SQLite store. The index on (root, expiry, date) lets the per-expiry load fetch exactly the
	// rows it needs — no full-file scan, so the 45DTE tail an expiry slice carries is never read.
	private readonly string _dbPath;
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
	public QuoteStoreCache(string dbPath, int maxStaleMinutes = 5, DateTime? since = null, DateTime? until = null, bool sameDayExpiryOnly = false)
	{
		_dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
		_maxStaleMinutes = maxStaleMinutes;
		_since = (since ?? DateTime.MinValue).Date;
		_until = (until ?? DateTime.MaxValue).Date;
		_sameDayExpiryOnly = sameDayExpiryOnly;
	}

	// yyyymmdd as a single int — the on-disk date encoding. MinValue→10101, MaxValue→99991231 both fit.
	private static int YmdInt(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;

	/// <summary>Latest real two-sided NBBO at or before <paramref name="asOfEt"/> (ET wall-clock) for the
	/// contract, within the staleness window; null if none → the caller's missing-quote policy decides.</summary>
	public QuoteAt? NbboAt(string occ, DateTime asOfEt)
	{
		EvictExpiredBefore(asOfEt.Date);
		var p = ParsingHelpers.ParseOptionSymbol(occ);
		if (p?.CallPut == null) return null;
		var eq = _cache.GetOrAdd((p.Root.ToUpperInvariant(), p.ExpiryDate.Date),
			key => ExpiryQuotes.Load(key.Root, key.Exp, _since, _until, _sameDayExpiryOnly, _dbPath));
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

	/// <summary>True if the store holds ANY real quote row dated within [<paramref name="since"/>, <paramref name="until"/>]
	/// for <paramref name="root"/>. Distinguishes "the strategy declined to trade" from "the quote store has not been
	/// backfilled for this window yet" (the common trap: running <c>--since <today></c> before the evening
	/// backfill has landed the day's quotes). A contract's rows are dated at or before its expiry, so the
	/// <c>expiry >= since</c> bound prunes earlier expiry slices; with the <c>(root, expiry, date)</c> index and
	/// <c>LIMIT 1</c> the has-coverage path is near-instant.</summary>
	public bool HasAnyQuoteInWindow(string root, DateTime since, DateTime until)
	{
		using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Cache=Shared");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT 1 FROM quotes WHERE root=$root AND expiry>=$since AND date>=$since AND date<=$until LIMIT 1";
		cmd.Parameters.AddWithValue("$root", root.ToUpperInvariant());
		cmd.Parameters.AddWithValue("$since", YmdInt(since.Date));
		cmd.Parameters.AddWithValue("$until", YmdInt(until.Date));
		using var r = cmd.ExecuteReader();
		return r.Read();
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
			key => ExpiryQuotes.Load(key.Root, key.Exp, _since, _until, _sameDayExpiryOnly, _dbPath));
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

		/// <summary>Loads the expiry slice from the canonical SQLite store. The DB holds already-validated rows
		/// (the import applied the filters: ≥6 cols, valid time, strike, right C/P, BOTH bid>0 AND ask>0),
		/// with bid/ask as scaled integers in ten-thousandths (price = value / 10000) — penny-tick data with the
		/// source float noise (e.g. 0.35000000000000003) rounded away. The index on (root, expiry, date) makes
		/// this fetch exactly the needed rows; the 45DTE tail an expiry slice carries is never touched.</summary>
		public static ExpiryQuotes Load(string root, DateTime exp, DateTime since, DateTime until, bool sameDayExpiryOnly, string dbPath)
		{
			var rows = new Dictionary<(DateTime Date, long StrikeMilli, char Cp),
				List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>>();
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
	}
}

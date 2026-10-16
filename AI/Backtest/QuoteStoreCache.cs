using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Reads the canonical ThetaData minute-NBBO quote store — the SQLite <c>quotes.db</c> table
/// <c>(root, expiry, date, time_sec, strike_milli, right, bid, ask, bid_size, ask_size)</c> — and answers
/// "real NBBO for this contract at this ET minute" with a bounded stale-quote lookback. This is the
/// quotes-only price foundation that replaces massive trade-bars + the synthetic spread model (see
/// <see cref="BacktestQuoteSource"/>). Per-(expiration, date) slices are loaded lazily (the primary key on
/// <c>(root, expiry, date, …)</c> fetches exactly the needed rows) and cached. Quote age (exact vs
/// stale-by-N-min vs missing) is the pricing-provenance signal — the analog of "captured bar vs
/// synthetic" on the trade-bar path. The store is built/kept current by scripts/import_quotes_sqlite.py +
/// the daily backfill; there is no CSV read path.</summary>
internal sealed class QuoteStoreCache
{
	/// <summary>A real NBBO print, with how stale it is vs the requested minute (0 = exact-minute print). One
	/// side reading 0 is real data (the far-OTM/expiry-day norm — nobody bids a near-worthless contract), NOT
	/// a bug, so it is never overridden here — see <see cref="Mid"/>. <see cref="IsEmpty"/> and
	/// <see cref="IsCrossed"/> flag the two prints that carry no trustworthy price at all.</summary>
	internal readonly record struct QuoteAt(decimal Bid, decimal Ask, int BidSize, int AskSize, int AgeMinutes)
	{
		/// <summary>Blending a live side with an absent (0) side would manufacture a price nobody could
		/// transact at in either direction, so a one-sided print's Mid is just its one real side, not half of
		/// it. Two-sided prints keep the ordinary average.</summary>
		public decimal Mid => Bid > 0m && Ask > 0m ? (Bid + Ask) / 2m : Math.Max(Bid, Ask);
		/// <summary>Neither side printed this minute — an auction/no-dissemination placeholder, not a $0
		/// price. Equivalent to a missing quote for pricing purposes.</summary>
		public bool IsEmpty => Bid <= 0m && Ask <= 0m;
		/// <summary>Both sides printed but inverted (ask below bid) — a genuine feed anomaly, not a real
		/// market state. Equivalent to a missing quote for pricing purposes.</summary>
		public bool IsCrossed => Bid > 0m && Ask > 0m && Ask < Bid;
	}

	private readonly int _maxStaleMinutes;
	// Lazy so concurrent first touches of one slice (parallel scoring threads) share a single load instead of each
	// running the factory — ConcurrentDictionary.GetOrAdd alone may invoke it once per racing thread.
	private readonly ConcurrentDictionary<(string Root, DateTime Exp, DateTime Date), Lazy<DaySlice>> _cache = new();

	/// <param name="maxStaleMinutes">How far back to accept the most recent quote when the exact minute has
	/// none. Minute NBBO is dense for liquid near-money contracts, so a small window (default 5 min) keeps
	/// staleness honest; raise it for thin far-dated legs.</param>
	private readonly DateTime _since;
	private readonly DateTime _until;
	// When set, only same-day-expiry rows (trade date == expiry, the 0DTE slice) are served; any other date reads as
	// empty. Set by the caller only when it has PROVEN the run is same-day (every enabled opener structure is 0DTE
	// AND no roll-to-future rule is active).
	private readonly bool _sameDayExpiryOnly;
	// Canonical SQLite store. The primary key on (root, expiry, date, strike, right, time) lets a slice load fetch
	// exactly its rows, already grouped by contract and time-sorted.
	private readonly string _dbPath;
	// Day-eviction watermark: a lookup reads only its own date's rows (staleness never crosses midnight) and the
	// backtest only moves forward, so once the sim date advances every earlier slice is dead weight. Holding whole
	// expiry lives instead kept the full DTE band × every elapsed date resident — a multi-GB live heap that every GC
	// had to walk. Evicting is safe for any caller: a later query for an evicted date just reloads it.
	private long _lastSweepDateTicks;
	private readonly object _evictLock = new();

	/// <param name="since">/<param name="until">Dates outside [since, until] read as empty. Defaults to all-dates.</param>
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
		EvictBefore(asOfEt.Date);
		var p = ParsingHelpers.ParseOptionSymbol(occ);
		if (p?.CallPut == null) return null;
		return Slice(p.Root, p.ExpiryDate, asOfEt.Date).Lookup((long)Math.Round(p.Strike * 1000m), p.CallPut[0], (int)asOfEt.TimeOfDay.TotalSeconds, _maxStaleMinutes);
	}

	private DaySlice Slice(string root, DateTime expiry, DateTime date)
	{
		var key = (root.ToUpperInvariant(), expiry.Date, date.Date);
		return _cache.GetOrAdd(key, k => new Lazy<DaySlice>(() => LoadSlice(k.Item1, k.Item2, k.Item3))).Value;
	}

	private DaySlice LoadSlice(string root, DateTime expiry, DateTime date)
	{
		var outside = date < _since || date > _until || (_sameDayExpiryOnly && date != expiry);
		return outside ? DaySlice.Empty : DaySlice.Load(root, expiry, date, _dbPath);
	}

	/// <summary>Drops every cached slice dated before <paramref name="asOfDate"/> — the backtest never queries
	/// them again, so their rows are pure memory pressure. Runs at most once per sim day: the fast path is a lock-free atomic read of the watermark; only the first call of a new day
	/// takes the lock and sweeps. ConcurrentDictionary.Keys is a snapshot, so removing during enumeration (and
	/// concurrent reads from the still-active minute scan) is safe.</summary>
	private void EvictBefore(DateTime asOfDate)
	{
		if (asOfDate.Ticks <= Volatile.Read(ref _lastSweepDateTicks)) return;
		lock (_evictLock)
		{
			if (asOfDate.Ticks <= _lastSweepDateTicks) return;
			foreach (var key in _cache.Keys)
				if (key.Date < asOfDate) _cache.TryRemove(key, out _);
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
		EvictBefore(date.Date);
		var slice = Slice(root, expiry, date);
		var loMilli = (long)Math.Round(loStrike * 1000m);
		var hiMilli = (long)Math.Round(hiStrike * 1000m);
		var yy = expiry.ToString("yyMMdd", CultureInfo.InvariantCulture);
		var rootUp = root.ToUpperInvariant();
		foreach (var (strikeMilli, cp) in slice.ContractsOn(loMilli, hiMilli))
			yield return $"{rootUp}{yy}{cp}{strikeMilli:D8}";
	}

	// One NBBO print as stored: bid/ask in ten-thousandths of a dollar (the on-disk encoding), decoded to decimal only
	// when a lookup returns it. Ints instead of decimals keep a slice ~2.5x smaller and the load free of decimal math.
	private readonly record struct Row(int Sec, int Bid, int Ask, int BidSz, int AskSz);

	/// <summary>Every contract's prints for one (root, expiry, trade date).</summary>
	private sealed class DaySlice
	{
		public static readonly DaySlice Empty = new([], new());

		// Contracts in store order (strike, then right) so ContractsOn enumerates deterministically.
		private readonly List<(long StrikeMilli, char Cp)> _contracts;
		private readonly Dictionary<(long StrikeMilli, char Cp), Row[]> _rows;

		private DaySlice(List<(long StrikeMilli, char Cp)> contracts, Dictionary<(long StrikeMilli, char Cp), Row[]> rows)
		{
			_contracts = contracts;
			_rows = rows;
		}

		/// <summary>Loads the slice from the canonical SQLite store. The DB holds already-validated rows (the import
		/// validates structure only — valid time, strike, right C/P; the store is FAITHFUL to the vendor as of
		/// 2026-07-22, so one-sided books arrive here with the absent side stored as 0 and policy belongs to
		/// consumers: opener gates require two-sided books, management prices from what exists), with bid/ask as
		/// scaled integers in ten-thousandths (price = value / 10000) — penny-tick data with the source float noise
		/// (e.g. 0.35000000000000003) rounded away. The ORDER BY matches the primary key, so SQLite streams rows
		/// grouped by contract and time-sorted at no sort cost and each contract's run is cut straight to an array.</summary>
		public static DaySlice Load(string root, DateTime exp, DateTime date, string dbPath)
		{
			var contracts = new List<(long StrikeMilli, char Cp)>();
			var rows = new Dictionary<(long StrikeMilli, char Cp), Row[]>();
			using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
			conn.Open();
			using var cmd = conn.CreateCommand();
			// date/expiry stored as INTEGER yyyymmdd; unicode(right) reads the C/P code point without a string per row.
			cmd.CommandText = "SELECT strike_milli, unicode(right), time_sec, bid, ask, bid_size, ask_size FROM quotes WHERE root=$root AND expiry=$exp AND date=$date ORDER BY strike_milli, right, time_sec";
			cmd.Parameters.AddWithValue("$root", root.ToUpperInvariant());
			cmd.Parameters.AddWithValue("$exp", YmdInt(exp));
			cmd.Parameters.AddWithValue("$date", YmdInt(date));
			using var r = cmd.ExecuteReader();
			var run = new List<Row>();
			(long StrikeMilli, char Cp) current = default;
			while (r.Read())
			{
				var key = (r.GetInt64(0), (char)r.GetInt32(1));
				if (key != current && run.Count > 0) { Add(contracts, rows, current, run); run.Clear(); }
				current = key;
				run.Add(new Row(r.GetInt32(2), checked((int)r.GetInt64(3)), checked((int)r.GetInt64(4)), r.GetInt32(5), r.GetInt32(6)));
			}
			if (run.Count > 0) Add(contracts, rows, current, run);
			return new DaySlice(contracts, rows);
		}

		private static void Add(List<(long StrikeMilli, char Cp)> contracts, Dictionary<(long StrikeMilli, char Cp), Row[]> rows, (long StrikeMilli, char Cp) key, List<Row> run)
		{
			contracts.Add(key);
			rows[key] = run.ToArray();
		}

		public QuoteAt? Lookup(long strikeMilli, char cp, int asOfSec, int maxStaleMinutes)
		{
			if (!_rows.TryGetValue((strikeMilli, cp), out var list)) return null;
			// Latest sec <= asOfSec (binary search; rows are sorted ascending by Sec).
			int lo = 0, hi = list.Length - 1, idx = -1;
			while (lo <= hi)
			{
				var m = (lo + hi) / 2;
				if (list[m].Sec <= asOfSec) { idx = m; lo = m + 1; } else hi = m - 1;
			}
			if (idx < 0) return null;
			var r = list[idx];
			var age = (asOfSec - r.Sec) / 60;
			return age <= maxStaleMinutes ? new QuoteAt(r.Bid / 10000m, r.Ask / 10000m, r.BidSz, r.AskSz, age) : null;
		}

		/// <summary>Distinct (strikeMilli, call/put) contracts with strike in [<paramref name="loMilli"/>, <paramref name="hiMilli"/>] (strike×1000).</summary>
		public IEnumerable<(long StrikeMilli, char Cp)> ContractsOn(long loMilli, long hiMilli)
		{
			foreach (var (strikeMilli, cp) in _contracts)
				if (strikeMilli >= loMilli && strikeMilli <= hiMilli)
					yield return (strikeMilli, cp);
		}
	}
}

using System.Collections.Concurrent;
using System.Globalization;

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
	public QuoteStoreCache(string? dir = null, int maxStaleMinutes = 5)
	{
		_dir = dir ?? Program.ResolvePath("data/quotes");
		_maxStaleMinutes = maxStaleMinutes;
	}

	/// <summary>Latest real two-sided NBBO at or before <paramref name="asOfEt"/> (ET wall-clock) for the
	/// contract, within the staleness window; null if none → the caller's missing-quote policy decides.</summary>
	public QuoteAt? NbboAt(string occ, DateTime asOfEt)
	{
		var p = ParsingHelpers.ParseOptionSymbol(occ);
		if (p?.CallPut == null) return null;
		var eq = _cache.GetOrAdd((p.Root.ToUpperInvariant(), p.ExpiryDate.Date),
			key => ExpiryQuotes.Load(_dir, key.Root, key.Exp));
		return eq.Lookup(asOfEt.Date, (long)Math.Round(p.Strike * 1000m), p.CallPut[0],
			(int)asOfEt.TimeOfDay.TotalSeconds, _maxStaleMinutes);
	}

	/// <summary>True if a quote file exists for this expiration (cheap coverage check, no load).</summary>
	public bool HasExpiry(string root, DateTime exp)
		=> File.Exists(Path.Combine(_dir, root.ToUpperInvariant(), $"{exp.Date:yyyy-MM-dd}.csv"));

	private sealed class ExpiryQuotes
	{
		private readonly Dictionary<(DateTime Date, long StrikeMilli, char Cp),
			List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>> _rows;

		private ExpiryQuotes(Dictionary<(DateTime Date, long StrikeMilli, char Cp),
			List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>> rows) => _rows = rows;

		public static ExpiryQuotes Load(string dir, string root, DateTime exp)
		{
			var rows = new Dictionary<(DateTime Date, long StrikeMilli, char Cp),
				List<(int Sec, decimal Bid, decimal Ask, int BidSz, int AskSz)>>();
			var path = Path.Combine(dir, root, $"{exp:yyyy-MM-dd}.csv");
			if (!File.Exists(path)) return new ExpiryQuotes(rows);

			var first = true;
			foreach (var line in File.ReadLines(path))
			{
				if (first) { first = false; continue; }            // header
				if (line.Length == 0) continue;
				var f = line.Split(',');
				if (f.Length < 6) continue;
				if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
				var sec = ParseTimeSec(f[1]);
				if (sec < 0) continue;
				// Times are already START-of-bar (09:30 = first RTH minute): the ThetaData pull normalizes
				// its end-of-bar stamps -60s at ingest, the same place WebullChartsClient does for Webull
				// (see WebullChartsClient.WebullBarShift). The store is canonical on disk, so we never shift here.
				if (!decimal.TryParse(f[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) continue;
				var c = f[3].Length > 0 ? char.ToUpperInvariant(f[3][0]) : '?';
				if (c != 'C' && c != 'P') continue;
				// Require BOTH sides for a usable two-sided quote (the pull keeps rows with bid OR ask).
				if (!decimal.TryParse(f[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var bid) || bid <= 0m) continue;
				if (!decimal.TryParse(f[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var ask) || ask <= 0m) continue;
				var bsz = f.Length > 6 ? ParseIntLoose(f[6]) : 0;
				var asz = f.Length > 7 ? ParseIntLoose(f[7]) : 0;
				var key = (d.Date, (long)Math.Round(strike * 1000m), c);
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

using System.Globalization;
using System.Text;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Disk-cached historical dividend schedule (ex-date + cash amount) per ticker, the backtest analog of the
/// live dividend-aware pricing path. Files live under <c>data/dividends/{TICKER}.csv</c> (header
/// <c>ex_date,amount</c>, one actual past payment per row). The schedule is fed to
/// <see cref="BacktestQuoteSource"/>, which subtracts each in-window dividend's present value from spot
/// (<see cref="WebullAnalytics.Pricing.OptionMath.DividendAdjustedSpot"/>) on every Black-Scholes
/// forward/inverse — so a backtested leg straddling an ex-date prices on the same reduced forward the live
/// feed uses, and a calendar's two legs no longer mis-split the dividend.
///
/// <para>Source is the crumb-free Yahoo chart endpoint (<c>events=div</c>) via
/// <see cref="YahooCalendarClient.FetchDividendHistoryAsync"/> — the SAME endpoint the live path uses, but
/// keeping the full ACTUAL history rather than projecting only the next payment. The backtest sees the real
/// ex-dates/amounts that occurred; because SPY's dividends are announced weeks ahead and are near-constant,
/// using actuals is within sub-cent of what the live engine would have estimated as-of each decision day.</para>
///
/// <para>Online (during <c>wa ai history</c>): refetch + overwrite, but a transient empty fetch never
/// clobbers a good on-disk file. Offline (during <c>wa ai backtest</c>): read disk only; a missing file
/// means "no known dividends" → the pricer leaves that root's legs unadjusted (correct for cash-settled
/// index roots like SPX/SPXW/XSP, which never write a file).</para>
/// </summary>
internal sealed class HistoricalDividendCache
{
	/// <summary>Chart-endpoint range pulled by the online refresh. 5y comfortably covers the 2-year default
	/// backtest lookback plus headroom, while staying one cheap request.</summary>
	internal const string FetchRange = "5y";

	private readonly string _cacheDir;
	private readonly Func<string, CancellationToken, Task<IReadOnlyList<DividendEvent>>> _fetch;
	private readonly bool _offline;
	private readonly Dictionary<string, IReadOnlyList<DividendEvent>> _memory = new(StringComparer.OrdinalIgnoreCase);

	public HistoricalDividendCache(string? cacheDir = null, bool offline = false)
		: this(cacheDir, (t, c) => YahooCalendarClient.FetchDividendHistoryAsync(t, FetchRange, c), offline) { }

	internal HistoricalDividendCache(string? cacheDir, Func<string, CancellationToken, Task<IReadOnlyList<DividendEvent>>> fetch, bool offline = false)
	{
		_cacheDir = cacheDir ?? Program.ResolvePath("data/dividends");
		_fetch = fetch;
		_offline = offline;
		Directory.CreateDirectory(_cacheDir);
	}

	/// <summary>Dividend schedule for one ticker, oldest-first. Empty when none are known. Online, this
	/// refetches from Yahoo and overwrites the on-disk file (keeping the old file on a transient empty
	/// fetch); offline, it reads only what's on disk.</summary>
	public async Task<IReadOnlyList<DividendEvent>> GetAsync(string ticker, CancellationToken cancellation)
	{
		var key = ticker.ToUpperInvariant();
		if (_memory.TryGetValue(key, out var cached)) return cached;

		var path = Path.Combine(_cacheDir, $"{key}.csv");
		var onDisk = File.Exists(path) ? ParseCsv(await File.ReadAllTextAsync(path, cancellation)) : new List<DividendEvent>();

		IReadOnlyList<DividendEvent> result = onDisk;
		if (!_offline)
		{
			var fetched = await _fetch(key, cancellation);
			// Don't let a transient empty fetch wipe a good cached schedule; only overwrite when we actually
			// got data (or when there was nothing on disk to lose).
			if (fetched.Count > 0)
			{
				var sorted = fetched.OrderBy(d => d.ExDate).ToList();
				await File.WriteAllTextAsync(path, SerializeCsv(sorted), cancellation);
				result = sorted;
			}
		}

		_memory[key] = result;
		return result;
	}

	/// <summary>Builds the root → schedule map consumed by <see cref="BacktestQuoteSource"/>. Roots with no
	/// known dividends are simply absent (the pricer then leaves their legs unadjusted).</summary>
	public async Task<IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>> BuildScheduleMapAsync(IEnumerable<string> tickers, CancellationToken cancellation)
	{
		var map = new Dictionary<string, IReadOnlyList<DividendEvent>>(StringComparer.OrdinalIgnoreCase);
		foreach (var ticker in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var schedule = await GetAsync(ticker, cancellation);
			if (schedule.Count > 0) map[ticker] = schedule;
		}
		return map;
	}

	private static List<DividendEvent> ParseCsv(string content)
	{
		var list = new List<DividendEvent>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Trim().Split(',');
			if (parts.Length < 2) continue;
			if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) || amt <= 0m) continue;
			list.Add(new DividendEvent(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), amt));
		}
		list.Sort((a, b) => a.ExDate.CompareTo(b.ExDate));
		return list;
	}

	private static string SerializeCsv(IReadOnlyList<DividendEvent> divs)
	{
		var sb = new StringBuilder("ex_date,amount\n");
		foreach (var d in divs.OrderBy(d => d.ExDate))
			sb.Append(d.ExDate.ToString("yyyy-MM-dd")).Append(',').Append(d.Amount.ToString(CultureInfo.InvariantCulture)).Append('\n');
		return sb.ToString();
	}
}

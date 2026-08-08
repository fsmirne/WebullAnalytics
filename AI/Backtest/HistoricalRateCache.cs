using System.Globalization;
using System.Text;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Disk-cached historical risk-free rate series (13-week T-bill yield, ^IRX daily closes), the backtest
/// analog of the live ^IRX fetch that <see cref="YahooOptionsClient.ApplyToOptionMath"/> applies at scan
/// start. One file at <c>data/rates/IRX.csv</c> (header <c>date,rate</c>, rate as a decimal fraction,
/// e.g. 0.0421), refreshed online during <c>wa ai history</c> and read-only during <c>wa ai backtest</c>.
///
/// <para><see cref="RateOn"/> serves the last close STRICTLY BEFORE the asked day — at a day's 09:30 scan
/// only the prior session's close is knowable, and ^IRX moves basis points per day, so the prior close is
/// both causal and accurate. The backtest runner applies it to <see cref="Pricing.OptionMath.RiskFreeRate"/>
/// per trading day, which keeps the quote-source IV back-solve and every scorer Black-Scholes/greeks call
/// on the SAME rate (they previously disagreed: hardcoded 0.036 vs the 0.043 compile-time default).</para>
///
/// <para><see cref="FetchLiveOrLatestAsync"/> is the live-path entry: real-time ^IRX first, falling back
/// to the newest cached close when the fetch fails — so live pricing degrades to yesterday's real rate,
/// not a hardcoded constant.</para>
/// </summary>
internal sealed class HistoricalRateCache
{
	/// <summary>Chart-endpoint range pulled by the online refresh. 5y comfortably covers the 2-year default
	/// backtest lookback plus full-history runs from 2022, one cheap request.</summary>
	internal const string FetchRange = "5y";

	private const string FileName = "IRX.csv";
	private const string YahooSymbol = "^IRX";

	private readonly string _cacheDir;
	private readonly Func<CancellationToken, Task<IReadOnlyList<(DateTime Date, decimal Close)>>> _fetch;
	private readonly bool _offline;
	private IReadOnlyList<(DateTime Date, double Rate)>? _memory;

	public HistoricalRateCache(string? cacheDir = null, bool offline = false)
		: this(cacheDir, c => YahooCalendarClient.FetchDailyCloseHistoryAsync(YahooSymbol, FetchRange, c), offline) { }

	internal HistoricalRateCache(string? cacheDir, Func<CancellationToken, Task<IReadOnlyList<(DateTime Date, decimal Close)>>> fetch, bool offline = false)
	{
		_cacheDir = cacheDir ?? Program.ResolvePath("data/rates");
		_fetch = fetch;
		_offline = offline;
		Directory.CreateDirectory(_cacheDir);
	}

	/// <summary>The rate series, oldest-first, as decimal fractions. Online, this refetches ^IRX and
	/// overwrites the on-disk file (keeping the old file on a transient empty fetch); offline, it reads
	/// only what's on disk. Empty when nothing is cached and the fetch fails/is offline.</summary>
	public async Task<IReadOnlyList<(DateTime Date, double Rate)>> GetAsync(CancellationToken cancellation)
	{
		if (_memory != null) return _memory;

		var path = Path.Combine(_cacheDir, FileName);
		IReadOnlyList<(DateTime, double)> result = File.Exists(path) ? ParseCsv(await File.ReadAllTextAsync(path, cancellation)) : new List<(DateTime, double)>();

		if (!_offline)
		{
			var fetched = await _fetch(cancellation);
			// ^IRX closes are percentage points (4.21 => 4.21%); the model stores fractions. Don't let a
			// transient empty fetch wipe a good cached series.
			if (fetched.Count > 0)
			{
				var rows = fetched.OrderBy(r => r.Date).Select(r => (r.Date, (double)(r.Close / 100m))).ToList();
				await File.WriteAllTextAsync(path, SerializeCsv(rows), cancellation);
				result = rows;
			}
		}

		_memory = result;
		return result;
	}

	/// <summary>The last close strictly before <paramref name="day"/> (the rate knowable at that day's
	/// open), or null when the series is empty or starts after <paramref name="day"/>.</summary>
	public static double? RateOn(IReadOnlyList<(DateTime Date, double Rate)> series, DateTime day)
	{
		double? rate = null;
		foreach (var (date, r) in series)
		{
			if (date >= day.Date) break;
			rate = r;
		}
		return rate;
	}

	/// <summary>Live-path rate: real-time ^IRX, falling back to the newest cached close when the fetch
	/// fails. Null only when both are unavailable (caller keeps the current OptionMath value).</summary>
	public static async Task<double?> FetchLiveOrLatestAsync(CancellationToken cancellation)
	{
		var live = await YahooOptionsClient.FetchRiskFreeRateAsync(cancellation);
		if (live.HasValue) return live;
		var cached = ParseCsv(TryReadCacheFile());
		return cached.Count > 0 ? cached[^1].Rate : null;
	}

	private static string TryReadCacheFile()
	{
		try
		{
			var path = Path.Combine(Program.ResolvePath("data/rates"), FileName);
			return File.Exists(path) ? File.ReadAllText(path) : "";
		}
		catch (IOException)
		{
			return "";
		}
	}

	private static List<(DateTime Date, double Rate)> ParseCsv(string content)
	{
		var list = new List<(DateTime, double)>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Trim().Split(',');
			if (parts.Length < 2) continue;
			if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) || rate <= 0 || rate >= 1) continue;
			list.Add((DateTime.SpecifyKind(d, DateTimeKind.Unspecified), rate));
		}
		list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
		return list;
	}

	private static string SerializeCsv(IReadOnlyList<(DateTime Date, double Rate)> rows)
	{
		var sb = new StringBuilder("date,rate\n");
		foreach (var (date, rate) in rows.OrderBy(r => r.Date))
			sb.Append(date.ToString("yyyy-MM-dd")).Append(',').Append(rate.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
		return sb.ToString();
	}
}

using System.Collections.Concurrent;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Sources;

/// <summary>Bridges <see cref="IntradayBarCache"/> to Webull's chart endpoint. Resolves the ticker
/// symbol to a Webull tickerId on first use (cached in-process for the daemon's lifetime), then
/// delegates to <see cref="WebullChartsClient.FetchIntradayBarsAsync"/>.
///
/// Special handling for the SPX index family (SPXW, SPX): the cash index has no extended-hours
/// quotes, so a request for SPXW pre/post-market would return nothing useful. To close the gap, the
/// fetcher transparently merges SPX RTH bars with SPY extended-hours bars (multiplied by the
/// SPX/SPY ratio derived from the prior session's closes). The merged series is in SPX scale
/// throughout — the indicator and the cache never know SPY was involved. No separate SPY folder is
/// created; the resulting bars land in <c>data/intraday/SPXW/</c> alongside RTH SPX data.</summary>
internal static class WebullIntradayBars
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	// SPX family — tickers whose underlying is the S&P 500 cash index (or a fixed fraction of it),
	// which Webull serves with no extended-hours coverage. These transparently merge SPY pre/post-
	// market bars (converted to the family ticker's price scale via the RTH overlap ratio) with the
	// ticker's RTH bars. XSP is the Mini-SPX index (= SPX/10), same cash-settled, no-ext-hours profile.
	internal static readonly HashSet<string> SpxFamilyTickers = new(StringComparer.OrdinalIgnoreCase) { "SPXW", "SPX", "XSP" };

	// SPX cash-index chart tickerId (verified via Webull's getQuote endpoint — the option-chain
	// namespace value 913324359 is actually SPXC stock, not the S&P 500 index). Fallback only; the
	// RTH chart id is resolved per-ticker via ResolveRthChartTickerId so XSP fetches its own ~758 tape.
	private const long SpxChartTickerId = 913354362L;

	// Resolves the RTH chart tickerId for an SPX-family symbol: SPX/SPXW → S&P 500 cash index
	// (913354362), XSP → Mini-SPX index (925377660). The SPY ext-hours merge then scales to this
	// ticker's price level via the RTH overlap ratio, so XSP lands at ~1/10 SPX scale automatically.
	private static long ResolveRthChartTickerId(string ticker) =>
		WebullChartsClient.TryResolveKnownChartTickerId(ticker, out var id) ? id : SpxChartTickerId;

	// Hard-coded internal fetch counts for the hybrid path. Webull's chart endpoint has an
	// undocumented max-count cap somewhere above ~600 — requests with very large counts silently
	// degrade to a single-bar response. 500 is conservative and proven to return full historical
	// bars (RTH series). For SPY ext-hours, 800 bars at m1 covers ~13 hours = today's pre-market +
	// RTH + AH, enough to overlap with SPX RTH for ratio derivation.
	private const int SpxRthFetchCount = 500;
	private const int SpyExtFetchCount = 800;

	public static IntradayBarFetcher CreateFetcher(ApiConfig apiConfig)
	{
		var tickerIds = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		return async (ticker, interval, count, includeExtended, cancellation) =>
		{
			if (SpxFamilyTickers.Contains(ticker))
			{
				return await FetchSpxWithSpyExtendedAsync(apiConfig, ticker, tickerIds, interval, cancellation);
			}

			if (!tickerIds.TryGetValue(ticker, out var tickerId))
			{
				// Check chart-namespace overrides FIRST. The option-chain endpoint and the chart
				// endpoint use different tickerId namespaces for cash indexes; ResolveTickerIdsAsync
				// returns chain-namespace IDs from KnownTickerIds, which produce nonsense data on the
				// chart endpoint. Tradable securities (SPY, QQQ, etc.) share the same ID across both
				// endpoints and fall through to the search resolver.
				if (WebullChartsClient.TryResolveKnownChartTickerId(ticker, out tickerId))
				{
					tickerIds.TryAdd(ticker, tickerId);
				}
				else
				{
					var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { ticker }, cancellation);
					if (!resolved.TryGetValue(ticker, out tickerId))
					{
						Console.WriteLine($"Webull intraday: could not resolve tickerId for '{ticker}'; intraday signal disabled for this ticker.");
						return Array.Empty<MinuteBar>();
					}
					tickerIds.TryAdd(ticker, tickerId);
				}
			}

			return await WebullChartsClient.FetchIntradayBarsAsync(apiConfig, tickerId, interval, count, includeExtended, cancellation);
		};
	}

	/// <summary>Fetches SPX RTH bars and SPY extended-hours bars in parallel, converts SPY bars to
	/// SPX scale using <c>spxPrevClose / spyPrevClose</c>, and returns the merged series. SPX bars
	/// win on any timestamp covered by both; SPY-converted bars fill pre/post-market gaps. Falls
	/// back to SPX-only on SPY resolution failure or insufficient data to compute the ratio.</summary>
	private static async Task<IReadOnlyList<MinuteBar>> FetchSpxWithSpyExtendedAsync(
		ApiConfig apiConfig,
		string ticker,
		ConcurrentDictionary<string, long> tickerIds,
		BarInterval interval,
		CancellationToken cancellation)
	{
		var spxTask = WebullChartsClient.FetchIntradayBarsAsync(apiConfig, ResolveRthChartTickerId(ticker), interval, SpxRthFetchCount, includeExtended: false, cancellation);

		long spyId;
		if (!tickerIds.TryGetValue("SPY", out spyId))
		{
			var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { "SPY" }, cancellation);
			if (!resolved.TryGetValue("SPY", out spyId))
			{
				Console.WriteLine("Webull intraday: could not resolve SPY tickerId for pre-market proxy; using SPX RTH only.");
				return await spxTask;
			}
			tickerIds.TryAdd("SPY", spyId);
		}

		var spyTask = WebullChartsClient.FetchIntradayBarsAsync(apiConfig, spyId, interval, SpyExtFetchCount, includeExtended: true, cancellation);

		var spxBars = await spxTask;
		var spyBars = await spyTask;

		if (spyBars.Count == 0 || spxBars.Count == 0) return spxBars;

		// Ratio = SPX / SPY at an overlapping timestamp during today's RTH. Picking a common minute
		// is more reliable than yesterday's close (which would require Webull to return historical
		// bars going back across the overnight gap — often capped to the recent session only). The
		// SPX/SPY ratio drifts very little intraday; one overlap is sufficient for converting the
		// pre/post-market SPY bars into SPX scale.
		var ratio = TryDeriveRatioFromOverlap(spxBars, spyBars);
		if (!ratio.HasValue || ratio.Value <= 0m) return spxBars;

		var spxTimestamps = new HashSet<long>(spxBars.Count);
		foreach (var b in spxBars) spxTimestamps.Add(b.Timestamp.ToUnixTimeSeconds());

		var ratioValue = ratio.Value;
		var merged = new List<MinuteBar>(spxBars.Count + spyBars.Count);
		merged.AddRange(spxBars);
		foreach (var b in spyBars)
		{
			if (spxTimestamps.Contains(b.Timestamp.ToUnixTimeSeconds())) continue;
			// Round to 2 decimals to match SPX's native precision. Decimal arithmetic preserves full
			// precision through the multiplication; without rounding, the CSV would carry 28-digit
			// values which are noisy and break visual comparison with native SPX rows.
			merged.Add(new MinuteBar(
				b.Timestamp,
				Math.Round(b.Open * ratioValue, 2),
				Math.Round(b.High * ratioValue, 2),
				Math.Round(b.Low * ratioValue, 2),
				Math.Round(b.Close * ratioValue, 2),
				b.Volume));
		}
		merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return merged;
	}

	/// <summary>Range-based historical fetch that paginates Webull's <c>query-mini</c> from
	/// <paramref name="endNyDate"/> backward through <paramref name="startNyDate"/> rather than
	/// anchoring one call per day. Per-day anchoring silently truncates to 1 bar on ~34% of deep
	/// past dates; paginating from "now" backward — the way Webull's web app does it — sidesteps
	/// that. For SPX-family input we paginate SPX RTH and SPY ext-hours separately, then merge
	/// per NY date using the same SPX-wins/SPY-scaled logic the live capture uses. Returns one
	/// list of bars per NY date in the range that had at least one usable bar; days with neither
	/// SPX nor SPY coverage are absent from the dictionary.</summary>
	public static async Task<Dictionary<DateTime, List<MinuteBar>>> FetchHistoricalRangeAsync(
		ApiConfig apiConfig,
		string ticker,
		DateTime startNyDate,
		DateTime endNyDate,
		TimeSpan delayBetweenPages,
		Action<string>? log,
		CancellationToken cancellation)
	{
		// Cover full ext-hours envelope on both sides. SPY pre-market starts 04:00 ET; SPY post-market
		// runs to 20:00 ET. The pagination anchor is end-side; we walk backward to the start-side.
		var startEt = new DateTime(startNyDate.Year, startNyDate.Month, startNyDate.Day, 4, 0, 0, DateTimeKind.Unspecified);
		var endEt = new DateTime(endNyDate.Year, endNyDate.Month, endNyDate.Day, 20, 0, 0, DateTimeKind.Unspecified);
		var startUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();
		var endUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();

		IReadOnlyList<MinuteBar> primaryBars;
		IReadOnlyList<MinuteBar> spyBars = Array.Empty<MinuteBar>();

		if (SpxFamilyTickers.Contains(ticker))
		{
			log?.Invoke($"paginating {ticker} RTH bars…");
			primaryBars = await WebullChartsClient.FetchPaginatedHistoricalMinuteBarsAsync(
				apiConfig, ResolveRthChartTickerId(ticker), startUnix, endUnix, includeExtended: false,
				countPerPage: 800, delayBetweenPages,
				onPageProgress: (page, oldestSec, totalBars) =>
					log?.Invoke($"  {ticker} page {page}: back to {DateTimeOffset.FromUnixTimeSeconds(oldestSec).UtcDateTime:yyyy-MM-dd HH:mm} UTC, {totalBars} unique bars so far"),
				cancellation);

			var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { "SPY" }, cancellation);
			if (!resolved.TryGetValue("SPY", out var spyId))
			{
				log?.Invoke("SPY tickerId unresolved; using SPX RTH only (no pre/post-market in output)");
			}
			else
			{
				log?.Invoke("paginating SPY ext-hours bars…");
				spyBars = await WebullChartsClient.FetchPaginatedHistoricalMinuteBarsAsync(
					apiConfig, spyId, startUnix, endUnix, includeExtended: true,
					countPerPage: 800, delayBetweenPages,
					onPageProgress: (page, oldestSec, totalBars) =>
						log?.Invoke($"  SPY page {page}: back to {DateTimeOffset.FromUnixTimeSeconds(oldestSec).UtcDateTime:yyyy-MM-dd HH:mm} UTC, {totalBars} unique bars so far"),
					cancellation);
			}
		}
		else
		{
			if (!WebullChartsClient.TryResolveKnownChartTickerId(ticker, out var tickerId))
			{
				var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { ticker }, cancellation);
				if (!resolved.TryGetValue(ticker, out tickerId))
				{
					log?.Invoke($"could not resolve tickerId for '{ticker}'");
					return new Dictionary<DateTime, List<MinuteBar>>();
				}
			}
			log?.Invoke($"paginating {ticker} bars (incl. ext-hours)…");
			primaryBars = await WebullChartsClient.FetchPaginatedHistoricalMinuteBarsAsync(
				apiConfig, tickerId, startUnix, endUnix, includeExtended: true,
				countPerPage: 800, delayBetweenPages,
				onPageProgress: (page, oldestSec, totalBars) =>
					log?.Invoke($"  {ticker} page {page}: back to {DateTimeOffset.FromUnixTimeSeconds(oldestSec).UtcDateTime:yyyy-MM-dd HH:mm} UTC, {totalBars} unique bars so far"),
				cancellation);
		}

		// Group both series by NY date, then merge per date.
		var primaryByDate = GroupByNyDate(primaryBars);
		var spyByDate = GroupByNyDate(spyBars);
		var result = new Dictionary<DateTime, List<MinuteBar>>();
		var allDates = new SortedSet<DateTime>(primaryByDate.Keys);
		foreach (var d in spyByDate.Keys) allDates.Add(d);
		foreach (var d in allDates)
		{
			primaryByDate.TryGetValue(d, out var primary);
			spyByDate.TryGetValue(d, out var spy);
			var merged = MergeSpxAndSpyForDate(primary ?? new List<MinuteBar>(), spy ?? new List<MinuteBar>());
			if (merged.Count > 0) result[d] = merged;
		}
		return result;
	}

	private static Dictionary<DateTime, List<MinuteBar>> GroupByNyDate(IReadOnlyList<MinuteBar> bars)
	{
		var byDate = new Dictionary<DateTime, List<MinuteBar>>();
		foreach (var b in bars)
		{
			var d = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date;
			if (!byDate.TryGetValue(d, out var list)) byDate[d] = list = new List<MinuteBar>();
			list.Add(b);
		}
		foreach (var list in byDate.Values) list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return byDate;
	}

	private static List<MinuteBar> MergeSpxAndSpyForDate(List<MinuteBar> primary, List<MinuteBar> spy)
	{
		if (primary.Count == 0 && spy.Count == 0) return new List<MinuteBar>();
		// Non-SPX-family path: primary already includes ext-hours, no SPY to merge.
		if (spy.Count == 0) return new List<MinuteBar>(primary);
		// SPX-family with no SPX (truncated day): no fallback — we'd be emitting SPY values without
		// a meaningful overlap to compute the ratio at. Caller decides what to do; we return empty.
		if (primary.Count == 0) return new List<MinuteBar>();

		var ratio = TryDeriveRatioFromOverlap(primary, spy);
		if (!ratio.HasValue || ratio.Value <= 0m) return new List<MinuteBar>(primary);

		var primaryTimestamps = new HashSet<long>(primary.Count);
		foreach (var b in primary) primaryTimestamps.Add(b.Timestamp.ToUnixTimeSeconds());

		var ratioValue = ratio.Value;
		var merged = new List<MinuteBar>(primary.Count + spy.Count);
		merged.AddRange(primary);
		foreach (var b in spy)
		{
			if (primaryTimestamps.Contains(b.Timestamp.ToUnixTimeSeconds())) continue;
			merged.Add(new MinuteBar(
				b.Timestamp,
				Math.Round(b.Open * ratioValue, 2),
				Math.Round(b.High * ratioValue, 2),
				Math.Round(b.Low * ratioValue, 2),
				Math.Round(b.Close * ratioValue, 2),
				b.Volume));
		}
		merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return merged;
	}

	/// <summary>Historical analog of <see cref="FetchSpxWithSpyExtendedAsync"/> used by `wa ai history`
	/// backfill. Anchors at <paramref name="nyDate"/>'s 16:00 ET and pulls one session of SPX RTH bars
	/// plus enough SPY bars to cover full pre/post-market via the same SPX/SPY ratio merge the live
	/// capture uses. Each backfilled CSV therefore comes from the same source the bot actually sees
	/// during live trading — no Yahoo/Polygon synth, no vendor disagreement on the 09:31 open.
	///
	/// For non-SPX-family tickers, delegates to a direct <see cref="WebullChartsClient.FetchHistoricalMinuteBarsAsync"/>
	/// call on the ticker's own chart id.</summary>
	public static async Task<IReadOnlyList<MinuteBar>> FetchHistoricalSessionAsync(
		ApiConfig apiConfig,
		string ticker,
		DateTime nyDate,
		CancellationToken cancellation)
	{
		var sessionEndEt = new DateTime(nyDate.Year, nyDate.Month, nyDate.Day, 16, 0, 0, DateTimeKind.Unspecified);
		var anchorUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(sessionEndEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();

		if (SpxFamilyTickers.Contains(ticker))
			return await FetchHistoricalSpxSessionAsync(apiConfig, nyDate, anchorUnix, cancellation);

		long tickerId;
		if (!WebullChartsClient.TryResolveKnownChartTickerId(ticker, out tickerId))
		{
			var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { ticker }, cancellation);
			if (!resolved.TryGetValue(ticker, out tickerId))
			{
				Console.WriteLine($"Webull historical: could not resolve tickerId for '{ticker}'.");
				return Array.Empty<MinuteBar>();
			}
		}

		// Generic tickers: 800 bars covers pre-market (04:00) + RTH (09:30) + after-hours (20:00) of one
		// session with plenty of room. Filter to the target NY date — the response includes some bars
		// from the previous session because we anchor at session end and walk backward.
		var bars = await WebullChartsClient.FetchHistoricalMinuteBarsAsync(apiConfig, tickerId, anchorUnix, count: 800, includeExtended: true, cancellation);
		return FilterToNyDate(bars, nyDate);
	}

	private static async Task<IReadOnlyList<MinuteBar>> FetchHistoricalSpxSessionAsync(
		ApiConfig apiConfig,
		DateTime nyDate,
		long anchorUnix,
		CancellationToken cancellation)
	{
		// SPX RTH alone is 390 minutes; 500 gives margin for the day-boundary overlap we filter out below.
		// SPX has no extended-hours coverage so includeExtended doesn't matter here.
		var spxBars = FilterToNyDate(
			await WebullChartsClient.FetchHistoricalMinuteBarsAsync(apiConfig, SpxChartTickerId, anchorUnix, count: 500, includeExtended: false, cancellation),
			nyDate);
		if (spxBars.Count == 0) return Array.Empty<MinuteBar>();

		var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { "SPY" }, cancellation);
		if (!resolved.TryGetValue("SPY", out var spyId))
		{
			Console.WriteLine("Webull historical: could not resolve SPY tickerId for pre/post-market proxy; using SPX RTH only.");
			return spxBars;
		}

		// SPY: pre-market (04:00–09:30 = 330 mins) + RTH (09:30–16:00 = 390 mins) + post (16:00–20:00 =
		// 240 mins) = 960 minutes. Pull 1000 to cover a full ext-hours session with overlap margin.
		// includeExtended must be true to get pre/post-market bars at all — the default response is
		// RTH-only.
		var spyBars = FilterToNyDate(
			await WebullChartsClient.FetchHistoricalMinuteBarsAsync(apiConfig, spyId, anchorUnix, count: 1000, includeExtended: true, cancellation),
			nyDate);
		if (spyBars.Count == 0) return spxBars;

		var ratio = TryDeriveRatioFromOverlap(spxBars, spyBars);
		if (!ratio.HasValue || ratio.Value <= 0m) return spxBars;

		var spxTimestamps = new HashSet<long>(spxBars.Count);
		foreach (var b in spxBars) spxTimestamps.Add(b.Timestamp.ToUnixTimeSeconds());

		var ratioValue = ratio.Value;
		var merged = new List<MinuteBar>(spxBars.Count + spyBars.Count);
		merged.AddRange(spxBars);
		foreach (var b in spyBars)
		{
			if (spxTimestamps.Contains(b.Timestamp.ToUnixTimeSeconds())) continue;
			merged.Add(new MinuteBar(
				b.Timestamp,
				Math.Round(b.Open * ratioValue, 2),
				Math.Round(b.High * ratioValue, 2),
				Math.Round(b.Low * ratioValue, 2),
				Math.Round(b.Close * ratioValue, 2),
				b.Volume));
		}
		merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return merged;
	}

	private static IReadOnlyList<MinuteBar> FilterToNyDate(IReadOnlyList<MinuteBar> bars, DateTime nyDate)
	{
		var filtered = new List<MinuteBar>(bars.Count);
		foreach (var b in bars)
		{
			if (TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date == nyDate) filtered.Add(b);
		}
		return filtered;
	}

	/// <summary>Returns SPX/SPY ratio computed from the most-recent bar that exists in both series at
	/// the same minute timestamp. Walks SPX bars from newest backward, looking for a matching SPY
	/// timestamp; falls back to null when no overlap exists (e.g., one series is empty or both are
	/// in different sessions). Using the most recent overlap rather than the oldest keeps the ratio
	/// closest to current basis.</summary>
	private static decimal? TryDeriveRatioFromOverlap(IReadOnlyList<MinuteBar> spxBars, IReadOnlyList<MinuteBar> spyBars)
	{
		if (spxBars.Count == 0 || spyBars.Count == 0) return null;

		var spyByTimestamp = new Dictionary<long, decimal>(spyBars.Count);
		foreach (var b in spyBars)
		{
			if (b.Close <= 0m) continue;
			spyByTimestamp[b.Timestamp.ToUnixTimeSeconds()] = b.Close;
		}
		if (spyByTimestamp.Count == 0) return null;

		for (int i = spxBars.Count - 1; i >= 0; i--)
		{
			var ts = spxBars[i].Timestamp.ToUnixTimeSeconds();
			if (spxBars[i].Close <= 0m) continue;
			if (spyByTimestamp.TryGetValue(ts, out var spyClose) && spyClose > 0m)
				return spxBars[i].Close / spyClose;
		}
		return null;
	}
}

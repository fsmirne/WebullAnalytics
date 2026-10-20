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

	/// <summary>Fetches SPX RTH bars and SPY extended-hours bars in parallel and splices them via
	/// <see cref="SpxFamilyTape.Merge"/> (SPX wins on overlap, SPY scaled into index units, volume 0). Falls
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
				return SpxFamilyTape.Merge(await spxTask, Array.Empty<MinuteBar>());
			}
			tickerIds.TryAdd("SPY", spyId);
		}

		var spyTask = WebullChartsClient.FetchIntradayBarsAsync(apiConfig, spyId, interval, SpyExtFetchCount, includeExtended: true, cancellation);

		var spxBars = await spxTask;
		var spyBars = await spyTask;

		// Ratio = SPX / SPY at an overlapping timestamp during today's RTH. Picking a common minute
		// is more reliable than yesterday's close (which would require Webull to return historical
		// bars going back across the overnight gap — often capped to the recent session only). The
		// SPX/SPY ratio drifts very little intraday; one overlap is sufficient for converting the
		// pre/post-market SPY bars into SPX scale.
		return SpxFamilyTape.Merge(spxBars, spyBars);
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

		var isSpxFamily = SpxFamilyTickers.Contains(ticker);
		if (isSpxFamily)
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
			// Non-SPX tickers have no SPY splice: primary already carries their own ext-hours bars and real volume.
			var merged = isSpxFamily ? SpxFamilyTape.Merge(primary ?? new List<MinuteBar>(), spy ?? new List<MinuteBar>()) : primary ?? new List<MinuteBar>();
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
}

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

	// SPX family — tickers whose underlying is the S&P 500 cash index, which Webull serves with no
	// extended-hours coverage. These transparently merge SPY pre/post-market bars (converted to SPX
	// scale) with SPX RTH bars.
	private static readonly HashSet<string> SpxFamilyTickers = new(StringComparer.OrdinalIgnoreCase) { "SPXW", "SPX" };

	// SPX cash-index chart tickerId (verified via Webull's getQuote endpoint — the option-chain
	// namespace value 913324359 is actually SPXC stock, not the S&P 500 index).
	private const long SpxChartTickerId = 913354362L;

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
				return await FetchSpxWithSpyExtendedAsync(apiConfig, tickerIds, interval, cancellation);
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
		ConcurrentDictionary<string, long> tickerIds,
		BarInterval interval,
		CancellationToken cancellation)
	{
		var spxTask = WebullChartsClient.FetchIntradayBarsAsync(apiConfig, SpxChartTickerId, interval, SpxRthFetchCount, includeExtended: false, cancellation);

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

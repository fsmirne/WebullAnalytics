using System.Collections.Concurrent;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Sources;

/// <summary>Bridges <see cref="IntradayBarCache"/> to Webull's chart endpoint. Resolves the ticker
/// symbol to a Webull tickerId on first use (cached in-process for the daemon's lifetime), then
/// delegates to <see cref="WebullChartsClient.FetchIntradayBarsAsync"/>.
///
/// SPX, NDX, DJX, VIX and other index roots are pre-registered in
/// <see cref="WebullOptionsClient"/>'s KnownTickerIds. ETFs like SPY (used as a pre-market proxy when
/// SPX has no extended-hours quotes) resolve lazily through the search endpoint on first use.
/// Unknown tickers return an empty result rather than throwing — keeps the bias-mixing path
/// regressing to macro-only on resolution failure.</summary>
internal static class WebullIntradayBars
{
	public static IntradayBarFetcher CreateFetcher(ApiConfig apiConfig)
	{
		var tickerIds = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		return async (ticker, interval, count, includeExtended, cancellation) =>
		{
			if (!tickerIds.TryGetValue(ticker, out var tickerId))
			{
				// Check chart-namespace overrides FIRST. The option-chain endpoint and the chart
				// endpoint use different tickerId namespaces for cash indexes (SPX/NDX/etc.);
				// ResolveTickerIdsAsync returns chain-namespace IDs from KnownTickerIds, which
				// produce nonsense data on the chart endpoint. Tradable securities (SPY, QQQ, etc.)
				// share the same ID across both endpoints and fall through to the search resolver.
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
}

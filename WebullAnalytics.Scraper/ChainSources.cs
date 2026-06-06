using WebullAnalytics.Api;

namespace WebullAnalytics.Scraper;

/// <summary>A swappable provider of a one-shot option-chain snapshot for the scrape loop. Implementations return
/// the underlying spot plus every quoted contract whose expiry falls in [<paramref name="fromExpiry"/>,
/// <paramref name="toExpiry"/>]; the loop applies the root/DTE filter and persistence. This is the seam that lets
/// the scraper source from Schwab (real NBBO, default) or fall back to Webull via a config change.</summary>
internal interface IChainSource
{
	Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainAsync(
		string ticker, DateOnly fromExpiry, DateOnly toExpiry, decimal farStrikeRangeFraction, CancellationToken ct);
}

/// <summary>Webull-backed source. Mirrors the loop's original behavior: a single-day window (MaxDte 0) uses the
/// plain chain fetch; a multi-day window uses the expiry-refresh fetch so far-dated legs get OI/IV filled in.</summary>
internal sealed class WebullChainSource : IChainSource
{
	private readonly ApiConfig _apiConfig;
	public WebullChainSource(ApiConfig apiConfig) => _apiConfig = apiConfig;

	public async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainAsync(
		string ticker, DateOnly fromExpiry, DateOnly toExpiry, decimal farStrikeRangeFraction, CancellationToken ct)
	{
		if (toExpiry <= fromExpiry)
		{
			var (quotes, spot, _) = await WebullOptionsClient.FetchChainAsync(_apiConfig, ticker, ct);
			return (spot, quotes.Values.ToList());
		}

		var expiries = new List<DateTime>();
		for (var d = fromExpiry; d <= toExpiry; d = d.AddDays(1)) expiries.Add(d.ToDateTime(TimeOnly.MinValue));
		var (refreshed, refreshedSpot, _) = await WebullOptionsClient.FetchChainWithExpiryRefreshAsync(_apiConfig, ticker, expiries, farStrikeRangeFraction, ct);
		return (refreshedSpot, refreshed.Values.ToList());
	}
}

/// <summary>Schwab-backed source. One chains GET returns the full window with real NBBO + OI; the access token is
/// refreshed (and re-persisted) on demand. <paramref name="farStrikeRangeFraction"/> is unused — Schwab returns
/// all strikes in one response and the loop doesn't strike-filter.</summary>
internal sealed class SchwabChainSource : IChainSource
{
	private readonly ApiConfig _apiConfig;
	private readonly string _apiConfigPath;
	public SchwabChainSource(ApiConfig apiConfig, string apiConfigPath)
	{
		_apiConfig = apiConfig;
		_apiConfigPath = apiConfigPath;
	}

	public async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainAsync(
		string ticker, DateOnly fromExpiry, DateOnly toExpiry, decimal farStrikeRangeFraction, CancellationToken ct)
	{
		var token = await SchwabAuthClient.GetAccessTokenAsync(_apiConfig.Schwab!, _apiConfigPath, ct);
		return await SchwabOptionsClient.FetchChainAsync(token, ticker, fromExpiry, toExpiry, ct);
	}
}

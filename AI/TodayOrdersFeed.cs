using System.Collections.Concurrent;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>
/// Process-wide source of one account's orders for today, shared by every caller in a live tick. Webull's order-history
/// endpoint is capped at 2 requests / 2 s per app_key in production, and a `wa ai watch --submit` tick used to hit it
/// twice back-to-back — <see cref="Sources.LivePositionSource"/> (OpenedAt stamping) and <see cref="BrokerStateService"/>
/// (pre-submit dedup) — which sat exactly at the cap and drew an intermittent HTTP 429 whenever anything else sharing
/// the key landed in the same window. Callers holding the same per-tick cycle token now share one pull.
/// <para>A null token always fetches (the one-shot `wa trade` paths). A failed fetch is never reused, so a 429 on the
/// first caller leaves the next caller in the tick free to try again.</para>
/// </summary>
internal sealed class TodayOrdersFeed
{
	private static readonly ConcurrentDictionary<(string BaseUrl, string AccountId), TodayOrdersFeed> Feeds = new();

	private readonly Func<CancellationToken, Task<List<WebullOpenApiClient.OpenOrder>>> _fetch;
	private object? _lastToken;
	private Task<List<WebullOpenApiClient.OpenOrder>>? _lastPull;

	internal TodayOrdersFeed(Func<CancellationToken, Task<List<WebullOpenApiClient.OpenOrder>>> fetch) => _fetch = fetch;

	public static TodayOrdersFeed For(TradeAccount account) => Feeds.GetOrAdd((account.BaseUrl, account.AccountId), _ => new TodayOrdersFeed(ct => FetchAsync(account, ct)));

	public Task<List<WebullOpenApiClient.OpenOrder>> GetAsync(object? cycleToken, CancellationToken cancellation)
	{
		if (cycleToken != null && ReferenceEquals(cycleToken, _lastToken) && _lastPull is { IsCompletedSuccessfully: true }) return _lastPull;
		var pull = _fetch(cancellation);
		if (cycleToken != null) { _lastToken = cycleToken; _lastPull = pull; }
		return pull;
	}

	private static async Task<List<WebullOpenApiClient.OpenOrder>> FetchAsync(TradeAccount account, CancellationToken cancellation)
	{
		using var client = new WebullOpenApiClient(account);
		return await client.ListTodayOrdersAsync(cancellation);
	}
}

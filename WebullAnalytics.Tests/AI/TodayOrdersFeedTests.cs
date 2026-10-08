using WebullAnalytics.AI;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI;

/// <summary>Locks the per-tick coalescing that keeps a `wa ai watch --submit` tick to ONE Webull order-history call
/// (the endpoint is capped at 2 req / 2 s per app_key; two calls per tick drew intermittent HTTP 429s).</summary>
public class TodayOrdersFeedTests
{
	private sealed class CountingFetch
	{
		public int Calls;
		public bool FailNext;

		public Task<List<WebullOpenApiClient.OpenOrder>> Invoke(CancellationToken _)
		{
			Calls++;
			if (!FailNext) return Task.FromResult(new List<WebullOpenApiClient.OpenOrder>());
			FailNext = false;
			return Task.FromException<List<WebullOpenApiClient.OpenOrder>>(new WebullOpenApiException(null, "Too many requests", 429));
		}
	}

	[Fact]
	public async Task SameToken_SharesOnePull()
	{
		var fetch = new CountingFetch();
		var feed = new TodayOrdersFeed(fetch.Invoke);
		var tick = new object();
		await feed.GetAsync(tick, CancellationToken.None);
		await feed.GetAsync(tick, CancellationToken.None);
		Assert.Equal(1, fetch.Calls);
	}

	[Fact]
	public async Task NewToken_PullsAgain()
	{
		var fetch = new CountingFetch();
		var feed = new TodayOrdersFeed(fetch.Invoke);
		await feed.GetAsync(new object(), CancellationToken.None);
		await feed.GetAsync(new object(), CancellationToken.None);
		Assert.Equal(2, fetch.Calls);
	}

	[Fact]
	public async Task NullToken_AlwaysPulls()
	{
		var fetch = new CountingFetch();
		var feed = new TodayOrdersFeed(fetch.Invoke);
		await feed.GetAsync(null, CancellationToken.None);
		await feed.GetAsync(null, CancellationToken.None);
		Assert.Equal(2, fetch.Calls);
	}

	[Fact]
	public async Task FailedPull_IsNotReused_WithinTheTick()
	{
		var fetch = new CountingFetch { FailNext = true };
		var feed = new TodayOrdersFeed(fetch.Invoke);
		var tick = new object();
		await Assert.ThrowsAsync<WebullOpenApiException>(() => feed.GetAsync(tick, CancellationToken.None));
		var orders = await feed.GetAsync(tick, CancellationToken.None);
		Assert.Empty(orders);
		Assert.Equal(2, fetch.Calls);
	}
}

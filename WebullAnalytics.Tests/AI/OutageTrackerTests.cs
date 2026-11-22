using System.Net;
using System.Net.Sockets;
using WebullAnalytics.AI;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// Guards the watch loop against exiting on an outage: with a 5 s management interval, five network failures in a row — a Wi-Fi handoff, a
// resume from sleep, a pulled cable — used to trip the circuit breaker in ~25 s and leave every position unmanaged.
public class OutageTrackerTests
{
	public static TheoryData<Exception> Outages() =>
	[
		new HttpRequestException(HttpRequestError.ConnectionError, "network unreachable", new SocketException((int)SocketError.NetworkUnreachable)),
		new HttpRequestException(HttpRequestError.NameResolutionError, "dns"),
		new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway),
		new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
		new WebullOpenApiException(null, "Webull API unavailable (HTTP 503)", 503),
		new WebullOpenApiException(null, "rate limited", 429),
		// A Schwab token refresh that failed only because the network is down.
		new SchwabAuthException("Schwab token refresh failed", new HttpRequestException(HttpRequestError.ConnectionError, "unreachable")),
	];

	public static TheoryData<Exception> Faults() =>
	[
		new WebullOpenApiException("ACCESS_DENIED", "Access denied", 403),
		new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
		new SchwabAuthException("HTTP 400: invalid_grant"),
		new InvalidOperationException("a bug"),
		new NullReferenceException(),
	];

	[Theory, MemberData(nameof(Outages))]
	public void Outage_IsWaitedOut(Exception ex) => Assert.True(OutageTracker.IsOutage(ex));

	[Theory, MemberData(nameof(Faults))]
	public void Fault_StillCountsTowardTheBreaker(Exception ex) => Assert.False(OutageTracker.IsOutage(ex));

	[Fact]
	public void Failed_ReportsWhetherToSkipTheBreaker()
	{
		var tracker = new OutageTracker("test ticks");
		var t0 = new DateTime(2026, 11, 23, 10, 0, 0);

		Assert.True(tracker.Failed(new TaskCanceledException(), t0));
		Assert.True(tracker.Failed(new TaskCanceledException(), t0.AddSeconds(5)));
		Assert.False(tracker.Failed(new InvalidOperationException("a bug"), t0.AddSeconds(10)));
		tracker.Succeeded(t0.AddSeconds(15));
	}

	[Fact]
	public void StaleFrame_IsHeldOnlyPastTheLimit()
	{
		var read = new DateTime(2026, 11, 23, 10, 0, 0);
		Assert.False(LiveTick.IsStaleForSubmit(read, read.AddSeconds(20)));
		Assert.False(LiveTick.IsStaleForSubmit(read, read + LiveTick.MaxSubmitFrameAge));
		Assert.True(LiveTick.IsStaleForSubmit(read, read.AddHours(2)));   // woke from a suspend mid-run
	}
}

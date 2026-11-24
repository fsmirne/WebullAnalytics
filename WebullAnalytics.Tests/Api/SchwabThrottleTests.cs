using System.Net;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

// Schwab's chains 429 came back with a whitespace-only body, which printed as a line break inside the log line and said nothing; the reason must stay on one line and carry the headers.
public class SchwabThrottleTests
{
	[Fact]
	public void AWhitespaceBodyIsReportedAsEmptyWithTheHeaders()
	{
		using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("\n            \n        ") };
		response.Headers.Add("Schwab-Client-CorrelId", "abc-123");

		var reason = SchwabOptionsClient.DescribeThrottle(response, "\n            \n        ");

		Assert.StartsWith("empty body; headers: ", reason);
		Assert.Contains("Schwab-Client-CorrelId: abc-123", reason);
		Assert.DoesNotContain('\n', reason);
	}

	[Fact]
	public void ABodyIsCollapsedToOneLine()
	{
		using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

		var reason = SchwabOptionsClient.DescribeThrottle(response, "{\n  \"error\": \"rate\"\n}");

		Assert.StartsWith("{ \"error\": \"rate\" }; headers:", reason);
	}
}

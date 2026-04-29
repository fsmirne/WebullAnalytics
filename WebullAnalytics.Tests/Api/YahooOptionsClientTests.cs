using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class YahooOptionsClientTests
{
	[Fact]
	public void ToUnixTimeSecondsUtcAcceptsLocalDateTime()
	{
		var local = new DateTime(2026, 4, 24, 0, 0, 0, DateTimeKind.Local);
		var utc = new DateTime(2026, 4, 24, 0, 0, 0, DateTimeKind.Utc);

		var localUnix = YahooOptionsClient.ToUnixTimeSecondsUtc(local);
		var utcUnix = YahooOptionsClient.ToUnixTimeSecondsUtc(utc);

		Assert.Equal(utcUnix, localUnix);
	}
}

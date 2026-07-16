using System.Net;
using System.Net.Sockets;
using WebullAnalytics.Api;
using WebullAnalytics.Schwab;
using Xunit;

namespace WebullAnalytics.Tests.Schwab;

public class SchwabRedirectListenerTests
{
	private static int GetFreePort()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}

	[Fact]
	public async Task WaitForCode_captures_and_unescapes_code_from_https_redirect()
	{
		var port = GetFreePort();
		var schwab = new SchwabConfig { RedirectUri = $"https://127.0.0.1:{port}" };

		var listen = SchwabRedirectListener.WaitForCodeAsync(schwab, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

		// Let the listener bind before the browser-equivalent request hits it.
		await Task.Delay(400, TestContext.Current.CancellationToken);

		using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
		using var client = new HttpClient(handler);
		var resp = await client.GetAsync($"https://127.0.0.1:{port}/?code=ABC123%40&session=xyz", TestContext.Current.CancellationToken);

		Assert.True(resp.IsSuccessStatusCode);
		// Schwab URL-encodes the trailing '@' as %40; ExtractCode unescapes it.
		Assert.Equal("ABC123@", await listen);
	}

	[Fact]
	public async Task WaitForCode_times_out_to_null_when_no_redirect_arrives()
	{
		var port = GetFreePort();
		var schwab = new SchwabConfig { RedirectUri = $"https://127.0.0.1:{port}" };

		var code = await SchwabRedirectListener.WaitForCodeAsync(schwab, TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);

		Assert.Null(code);
	}

	[Fact]
	public async Task WaitForCode_returns_null_for_non_loopback_redirect()
	{
		var schwab = new SchwabConfig { RedirectUri = "https://example.com/cb" };

		var code = await SchwabRedirectListener.WaitForCodeAsync(schwab, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		Assert.Null(code);
	}
}

namespace WebullAnalytics.Api;

/// <summary>Process-wide shared <see cref="HttpClient"/> for the Schwab host (<c>api.schwabapi.com</c>). Both the
/// OAuth token path (<see cref="SchwabAuthClient"/>) and the market-data chains path (<see cref="SchwabOptionsClient"/>)
/// send through this one client so they share a single connection pool to the host.
///
/// The alternative — a <c>using var client = new HttpClient()</c> per call — churns through TCP sockets and ports;
/// under the sustained per-tick churn of the live watch loop (each ~65s tick, times retries, times recursive
/// window splits) Schwab's edge starts dropping connections at the TLS handshake, which surfaces as "The SSL
/// connection could not be established" with an inner SocketException "forcibly closed by the remote host". A single
/// long-lived client with connection pooling (keep-alive) avoids it. Auth is passed per-request (Bearer on chains,
/// Basic on token), never on the client, so sharing one client across both paths is safe; HttpClient is thread-safe
/// for concurrent requests, so it also holds up under concurrent scraper calls. Mirrors
/// <see cref="WebullChartsClient"/>'s SharedClient (a separate pool — different host and default headers).</summary>
internal static class SchwabHttp
{
	public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
}

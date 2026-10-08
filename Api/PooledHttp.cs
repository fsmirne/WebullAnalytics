using System.Net;

namespace WebullAnalytics.Api;

/// <summary>Handler factory for the process-wide pooled clients on the live watch cadence (Schwab chains, Webull OpenAPI,
/// Webull quotes and charts). Pooling only helps if a connection survives between uses: .NET's default
/// <c>PooledConnectionIdleTimeout</c> is 1 minute, and a watch tick reaches each host every ~61 s, so the pool closed the
/// connection just before the next tick needed it and every tick paid a fresh TLS handshake — the exact step these hosts'
/// edges intermittently reset ("The SSL connection could not be established" -> "forcibly closed by the remote host";
/// 2026-10-08 10:09-10:10, watch and a one-shot `analyze gex` failed it on Schwab while the per-minute scraper, whose
/// connection never idled past 60 s, was untouched). A 5-minute idle window keeps one connection alive across ticks. A
/// connection the server closes first is detected and discarded when it is next taken from the pool.</summary>
internal static class PooledHttp
{
	internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

	internal static SocketsHttpHandler CreateHandler(DecompressionMethods decompression = DecompressionMethods.None) =>
		new() { PooledConnectionIdleTimeout = IdleTimeout, AutomaticDecompression = decompression };
}

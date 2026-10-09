using System.Net;

namespace WebullAnalytics.Api;

/// <summary>What a non-success status from Webull's quote/chart endpoints means for the user. Only an auth failure warrants
/// re-sniffing the session headers; a 5xx (e.g. the 504 gateway timeouts Webull's edge returns intermittently) or a 429 is
/// server-side and transient — the next call retries — and every status used to be reported as an expired session.</summary>
internal static class WebullStatusHint
{
	internal static string For(HttpStatusCode status) => (int)status switch
	{
		401 or 403 => "Session may have expired — run 'sniff' to refresh.",
		429 => "Rate-limited by Webull; the next call retries.",
		>= 500 => "Webull server-side error (transient); the next call retries.",
		_ => "Unexpected response.",
	};
}

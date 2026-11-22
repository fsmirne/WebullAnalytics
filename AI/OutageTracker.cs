using Spectre.Console;
using System.Net.Sockets;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI;

/// <summary>
/// Carries one half of the watch loop (management ticks, or opener runs) through an outage — the machine's network down, or one API
/// unreachable or failing server-side. An outage is not a fault of the loop: ticking on is what resumes management the moment it ends,
/// while exiting leaves every position unmanaged until someone restarts the watch. So an outage failure does not count toward the circuit
/// breaker; it is announced once, reminded every <see cref="ReminderInterval"/>, and its end is announced with its length. Every other
/// failure (an auth rejection, a bug, a 4xx) still counts.
/// </summary>
internal sealed class OutageTracker(string what)
{
	internal static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

	private DateTime? _since;
	private DateTime _lastNotice;
	private int _failures;

	/// <summary>Records a failure at <paramref name="now"/>. True when it is an outage failure (see <see cref="IsOutage"/>), which the caller
	/// must not count toward its circuit breaker; false when it is any other failure, which the caller handles as before.</summary>
	public bool Failed(Exception ex, DateTime now)
	{
		if (!IsOutage(ex)) return false;
		_failures++;
		if (_since == null)
		{
			_since = _lastNotice = now;
			AnsiConsole.MarkupLine($"[red bold]{now:HH:mm:ss}  {Markup.Escape(what)} failing: network or API unavailable[/] [red]({Markup.Escape(WatchOpenerRunner.ExceptionChain(ex))})[/]");
			AnsiConsole.MarkupLine("[red]  Still running and retrying every tick. Positions are not being managed until it recovers; broker-side stops stay in place.[/]");
		}
		else if (now - _lastNotice >= ReminderInterval)
		{
			_lastNotice = now;
			AnsiConsole.MarkupLine($"[yellow]{now:HH:mm:ss}  {Markup.Escape(what)} still failing after {Format(now - _since.Value)} ({_failures} attempts): {Markup.Escape(WatchOpenerRunner.ExceptionChain(ex))}[/]");
		}
		return true;
	}

	/// <summary>Records a success at <paramref name="now"/>, announcing the end of an outage in progress.</summary>
	public void Succeeded(DateTime now)
	{
		if (_since is not DateTime since) return;
		AnsiConsole.MarkupLine($"[green]{now:HH:mm:ss}  {Markup.Escape(what)} recovered after {Format(now - since)} ({_failures} failed attempts).[/]");
		_since = null;
		_failures = 0;
	}

	/// <summary>True when <paramref name="ex"/> or an exception it wraps is a transport failure (no connection, a reset, a timeout) or a
	/// server-side refusal to serve (5xx or 429, from Webull or any HTTP API) — what an outage looks like from here, whether the machine is offline or one API is
	/// down. A wrapper counts (a Schwab token refresh that failed because the network is down surfaces as a <see cref="SchwabAuthException"/>
	/// around the transport error); the same wrapper without one (the refresh token rejected) does not.</summary>
	internal static bool IsOutage(Exception ex)
	{
		for (var e = ex; e != null; e = e.InnerException)
			if (e is SocketException or TimeoutException or TaskCanceledException
				|| e is HttpRequestException h && (h.StatusCode == null || IsServerSide((int)h.StatusCode))
				|| e is WebullOpenApiException w && IsServerSide(w.HttpStatus))
				return true;
		return false;
	}

	/// <summary>A status the server answers when it cannot serve right now (5xx, or 429 rate limited) — unlike any other 4xx, which refuses this request.</summary>
	private static bool IsServerSide(int status) => status >= 500 || status == 429;

	private static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{t.Seconds}s";
}

using System.Security.Cryptography;
using System.Text;

namespace WebullAnalytics.Api;

/// <summary>
/// Spaces the calls every process on this machine sends to one rate-limited API endpoint, so together they stay under its cap: two
/// watch loops plus a `wa trade` or `wa report` share one app key, and the call that draws a 429 fails its whole tick.
///
/// The gate is a token bucket: a burst of calls may go at once from an idle bucket, then one call per interval. Each endpoint has one
/// small file holding the time its bucket is next full-rate (the theoretical arrival time of the generic cell rate algorithm). A caller
/// takes the file exclusively, reserves the earliest send time the bucket allows, advances the stored time, releases the file, and only
/// then waits — so callers queue in the order they asked and nobody sleeps holding the lock. Different endpoints never wait on each other.
///
/// Best effort: if the file cannot be taken in time the call goes ahead ungated (a trade must not stall on a lock), and a process
/// on another machine sharing the key is invisible here. Each client's bounded retry on a 429 covers both.
/// </summary>
internal sealed class RateGate
{
	private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
	private const int LockRetryMs = 10;

	private readonly string _directory;
	/// <summary>Time for one call's worth of the bucket to refill.</summary>
	public TimeSpan Interval { get; }
	/// <summary>How far ahead of full rate the bucket may run: the burst beyond the first call.</summary>
	private readonly TimeSpan _tolerance;

	/// <param name="burst">Calls that may go at once from an idle bucket.</param>
	/// <param name="interval">Time for one call's worth of the bucket to refill.</param>
	public RateGate(string directory, int burst, TimeSpan interval)
	{
		_directory = directory;
		Interval = interval;
		_tolerance = interval * (burst - 1);
	}

	private static string GateDirectory => Program.ResolvePath("data/rate-gate");

	/// <summary>Webull OpenAPI read endpoints (positions, balance, open orders, order history, order detail), each capped per app key.
	/// Documented as "2 requests / 2 s", the cap behaves as a token bucket: a burst of 2, refilled at one request per 2 seconds
	/// (measured 2026-11-14 — two at once always pass; calls at 0, 1.0 and 2.0 s drew a 429 on the third; sends at 0, 1, 3, 4, 6, 7 s
	/// drew one on the sixth; a pair sent 3 s after a pair lost one of its two). A sliding "2 per N seconds" window does not fit those,
	/// and a gate built as one still drew 429s. So this is the same bucket with margin: a burst of 2, then one call per 3 s.</summary>
	internal static RateGate Webull { get; } = new(GateDirectory, burst: 2, interval: TimeSpan.FromSeconds(3));

	/// <summary>Schwab market-data chains. Schwab publishes no figure, and the commonly cited 120 requests a minute is not its cap: measured 2026-11-23 with no
	/// watch running, 10 cheap requests at once, 180 at 0.2 s spacing (~270 in the trailing minute) and 12 heavy $SPX range=ALL windows at once all returned 200.
	/// The SPXW and SPY watches together send ~25 a minute, in bursts of 3–4 (a cascade read's windows plus a management tick), and still draw an occasional 429
	/// with no Retry-After (a cascade read, 2026-11-20 and 2026-11-23): Schwab throttling on its own side, not a rate this gate could hold under. The chains
	/// client's bounded retry is what absorbs those. One call per 0.5 s after a burst of 4 never binds on the watches' traffic; it stays as a ceiling on a
	/// runaway fetch loop.</summary>
	internal static RateGate Schwab { get; } = new(GateDirectory, burst: 4, interval: TimeSpan.FromMilliseconds(500));

	/// <summary>The wait a 429 asks for: its Retry-After (delta or date) clamped to <paramref name="max"/>, else <paramref name="fallback"/>.</summary>
	internal static TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback, TimeSpan max)
	{
		var retryAfter = response.Headers.RetryAfter;
		var requested = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
		if (requested is not { } wait || wait <= TimeSpan.Zero) return fallback;
		return wait > max ? max : wait;
	}

	/// <summary>Waits until this call may be sent to <paramref name="endpoint"/> (the request path) under <paramref name="appKey"/> on <paramref name="host"/>.</summary>
	public async Task WaitAsync(string host, string appKey, string endpoint, CancellationToken ct)
	{
		var wait = Reserve($"{host}|{appKey}|{endpoint}", DateTime.UtcNow) - DateTime.UtcNow;
		if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
	}

	/// <summary>Reserves and returns the earliest send time at or after <paramref name="nowUtc"/> that <paramref name="key"/>'s bucket
	/// allows. Returns <paramref name="nowUtc"/> itself when the gate file cannot be taken.</summary>
	internal DateTime Reserve(string key, DateTime nowUtc)
	{
		try
		{
			Directory.CreateDirectory(_directory);
			using var file = OpenExclusive(Path.Combine(_directory, FileName(key)));
			if (file == null) return nowUtc;

			// The stored time is when the bucket is back at full rate. A call may go up to the tolerance ahead of it, and moves it one interval on.
			var fullRateAt = ReadTime(file);
			var slot = fullRateAt - _tolerance > nowUtc ? fullRateAt - _tolerance : nowUtc;
			var next = (fullRateAt > slot ? fullRateAt : slot) + Interval;

			file.SetLength(0);
			file.Position = 0;
			using var writer = new StreamWriter(file, Encoding.ASCII, leaveOpen: true);
			writer.WriteLine(next.Ticks);
			return slot;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return nowUtc;
		}
	}

	/// <summary>The stored full-rate time, or the distant past for a new, empty or torn file (an idle bucket).</summary>
	private static DateTime ReadTime(FileStream file)
	{
		using var reader = new StreamReader(file, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
		return long.TryParse(reader.ReadLine(), out var ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue.AddYears(1);
	}

	/// <summary>The gate file held exclusively, or null when another process kept it for longer than <see cref="LockTimeout"/>.</summary>
	private static FileStream? OpenExclusive(string path)
	{
		var deadline = DateTime.UtcNow + LockTimeout;
		while (true)
		{
			try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
			catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(LockRetryMs); }
			catch (IOException) { return null; }
		}
	}

	/// <summary>A stable file name that does not spell out the app key.</summary>
	private static string FileName(string key) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant() + ".gate";
}

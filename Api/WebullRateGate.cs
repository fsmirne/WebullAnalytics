using System.Security.Cryptography;
using System.Text;

namespace WebullAnalytics.Api;

/// <summary>
/// Spaces the read calls every process on this machine sends to one Webull OpenAPI endpoint, so together they stay under its cap.
/// Webull's production read endpoints (positions, balance, open orders, order history, order detail) are each rate-limited per app
/// key, counted across every process using the key — two watch loops plus a `wa trade` or `wa report` landing together is one
/// request too many, and the call that draws the 429 fails its whole tick.
///
/// Documented as "2 requests / 2 s", the cap behaves as a token bucket: a burst of 2, refilled at one request per 2 seconds
/// (measured 2026-11-14 — two at once always pass; calls at 0, 1.0 and 2.0 s drew a 429 on the third; sends at 0, 1, 3, 4, 6, 7 s
/// drew one on the sixth; a pair sent 3 s after a pair lost one of its two). A sliding "2 per N seconds" window does not fit those,
/// and a gate built as one still drew 429s. So this gate is the same bucket with margin: a burst of <see cref="DefaultBurst"/>, then
/// one call per <see cref="DefaultInterval"/>.
///
/// Each endpoint has one small file holding the time its bucket is next full-rate (the theoretical arrival time of the generic cell
/// rate algorithm). A caller takes the file exclusively, reserves the earliest send time the bucket allows, advances the stored
/// time, releases the file, and only then waits — so callers queue in the order they asked and nobody sleeps holding the lock.
/// Different endpoints never wait on each other.
///
/// Best effort: if the file cannot be taken in time the call goes ahead ungated (a trade must not stall on a lock), and a process
/// on another machine sharing the key is invisible here. The transport's bounded retry on a 429 covers both.
/// </summary>
internal sealed class WebullRateGate
{
	internal const int DefaultBurst = 2;
	internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
	private const int LockRetryMs = 10;

	private readonly string _directory;
	private readonly TimeSpan _interval;
	/// <summary>How far ahead of full rate the bucket may run: the burst beyond the first call.</summary>
	private readonly TimeSpan _tolerance;

	/// <param name="burst">Calls that may go at once from an idle bucket.</param>
	/// <param name="interval">Time for one call's worth of the bucket to refill.</param>
	public WebullRateGate(string directory, int burst, TimeSpan interval)
	{
		_directory = directory;
		_interval = interval;
		_tolerance = interval * (burst - 1);
	}

	internal static WebullRateGate Default { get; } = new(Program.ResolvePath("data/rate-gate"), DefaultBurst, DefaultInterval);

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
			var next = (fullRateAt > slot ? fullRateAt : slot) + _interval;

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

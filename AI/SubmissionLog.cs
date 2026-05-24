using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

/// <summary>
/// Cross-process per-day auto-execute ledger. Backs daily-rate caps so they
/// hold across multiple `wa ai scan` / `wa ai watch` invocations on the same
/// machine (in-memory counters alone reset on every process start).
///
/// Used by both the OPENER (kind = <see cref="Kind.Open"/>) and the
/// MANAGEMENT executor's LegIn path (kind = <see cref="Kind.LegIn"/>). Each
/// kind keeps its own daily count; callers filter by kind when checking caps.
///
/// Storage: one JSONL line per LIVE submission at
/// <c>data/submissions.jsonl</c>. Concurrency is serialized via an OS-level
/// file lock on a sentinel file (<c>submissions.lock</c>) — any process
/// performing read-then-submit-then-append holds the lock through the entire
/// cycle, so two concurrent submitters never both see the same pre-submit
/// count and double-fire.
///
/// Scope: same-machine coordination only. Different machines submitting against
/// the same broker account do not coordinate — accept that as a deliberate
/// limit. The lock file path is local to the install's data directory.
/// </summary>
internal static class SubmissionLog
{
	private const string LogFileName = "data/submissions.jsonl";
	private const string LockFileName = "data/submissions.lock";
	private static readonly TimeSpan LockAcquireTimeout = TimeSpan.FromSeconds(10);

	/// <summary>Per-submission action kind. Drives the per-day count caps each
	/// caller enforces independently (opener vs LegIn).</summary>
	internal static class Kind
	{
		public const string Open = "Open";
		public const string LegIn = "LegIn";
	}

	internal sealed record Entry(
		[property: JsonPropertyName("date")] string Date,
		[property: JsonPropertyName("ts")] string Ts,
		[property: JsonPropertyName("kind")] string Kind,
		[property: JsonPropertyName("ticker")] string Ticker,
		[property: JsonPropertyName("rule")] string? Rule,
		[property: JsonPropertyName("fingerprint")] string? Fingerprint,
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId);

	/// <summary>Acquires an exclusive cross-process lock. Hold for the read +
	/// submit + append cycle so no other process can observe an outdated
	/// pre-submit count. Dispose to release.</summary>
	internal static IDisposable AcquireLock()
	{
		var lockPath = Program.ResolvePath(LockFileName);
		var dir = Path.GetDirectoryName(lockPath);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

		var deadline = DateTime.UtcNow + LockAcquireTimeout;
		while (true)
		{
			try
			{
				return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(50);
			}
		}
	}

	/// <summary>Counts entries in the log whose <c>date</c> field equals
	/// <paramref name="today"/> (yyyy-MM-dd) and <c>kind</c> matches. Returns 0
	/// if the log doesn't exist. Caller is responsible for holding
	/// <see cref="AcquireLock"/> when this count gates a submission decision.</summary>
	internal static int CountForDate(DateOnly today, string kind)
	{
		var path = Program.ResolvePath(LogFileName);
		if (!File.Exists(path)) return 0;

		var dateStr = today.ToString("yyyy-MM-dd");
		int count = 0;
		foreach (var line in File.ReadAllLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			try
			{
				using var doc = JsonDocument.Parse(line);
				if (doc.RootElement.TryGetProperty("date", out var d) && d.GetString() == dateStr
					&& doc.RootElement.TryGetProperty("kind", out var k) && k.GetString() == kind)
					count++;
			}
			catch (JsonException) { /* skip malformed lines */ }
		}
		return count;
	}

	/// <summary>Returns fingerprints already submitted today for the given
	/// <paramref name="kind"/>, for dedup of the same exact proposal across
	/// processes. Caller holds <see cref="AcquireLock"/>.</summary>
	internal static HashSet<string> FingerprintsForDate(DateOnly today, string kind)
	{
		var path = Program.ResolvePath(LogFileName);
		var result = new HashSet<string>(StringComparer.Ordinal);
		if (!File.Exists(path)) return result;

		var dateStr = today.ToString("yyyy-MM-dd");
		foreach (var line in File.ReadAllLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			try
			{
				using var doc = JsonDocument.Parse(line);
				if (doc.RootElement.TryGetProperty("date", out var d) && d.GetString() == dateStr
					&& doc.RootElement.TryGetProperty("kind", out var k) && k.GetString() == kind
					&& doc.RootElement.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String)
				{
					var fp = f.GetString();
					if (!string.IsNullOrEmpty(fp)) result.Add(fp);
				}
			}
			catch (JsonException) { /* skip */ }
		}
		return result;
	}

	/// <summary>Appends a new entry. Caller holds <see cref="AcquireLock"/>.</summary>
	internal static void Append(Entry entry)
	{
		var path = Program.ResolvePath(LogFileName);
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
	}
}

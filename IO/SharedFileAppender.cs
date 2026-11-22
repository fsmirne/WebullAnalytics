using System.Text;

namespace WebullAnalytics.IO;

/// <summary>
/// Appends whole lines to a file that several writers share — `wa ai watch` writes each proposal log from two sinks
/// (management + open), a concurrent `wa ai scan` writes the same file, and the order ledger and the IV captures have
/// writers in several processes. The old writers each held one long-lived <c>FileMode.Append</c> stream with
/// <c>FileShare.ReadWrite</c>; .NET writes at the stream's cached position, so a writer that had not seen another's
/// appends overwrote them, and a record longer than the StreamWriter buffer went out in several chunks. Both produced
/// fused, unparseable JSONL lines (two in ai-proposals.SPY.DC2.jsonl by 2026-10-08).
///
/// Each call takes an exclusive lock on a sidecar file (<c>{path}.append.lock</c>), opens the file, lands at its true end,
/// writes the text in one call and closes. The lock is on a sidecar, not on the file itself, because only <c>FileShare.None</c>
/// excludes other writers on every platform: on Unix .NET takes no lock at all for a write handle shared any other way (so
/// writers overwrote each other there — 145 of 400 test lines lost on Linux), and an exclusive lock on the file itself would
/// also shut out readers (see <see cref="SharedFileReader"/>), which never touch the sidecar. A writer that finds the lock
/// held retries briefly.
/// </summary>
internal static class SharedFileAppender
{
	internal const string LockSuffix = ".append.lock";
	private const int MaxAttempts = 40;
	private const int RetryDelayMs = 25;

	public static void AppendLine(string path, string line) => Append(path, line + Environment.NewLine);

	/// <summary>Appends <paramref name="text"/> (whole lines, each newline-terminated) to <paramref name="path"/>. When the file
	/// is new or empty, <paramref name="headerIfNew"/> is written first, under the same lock, so two writers starting the file
	/// cannot both write it.</summary>
	public static void Append(string path, string text, string? headerIfNew = null)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				using var writerLock = new FileStream(path + LockSuffix, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
				using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
				if (headerIfNew != null && stream.Length == 0) stream.Write(Encoding.UTF8.GetBytes(headerIfNew));
				stream.Write(bytes);
				return;
			}
			catch (IOException ex) when (IsLockConflict(ex) && attempt < MaxAttempts)
			{
				Thread.Sleep(RetryDelayMs);
			}
		}
	}

	/// <summary>The error a lock or share conflict raises: a Windows sharing/lock violation, or EWOULDBLOCK from the flock .NET
	/// takes on Unix (11 on Linux, 35 on macOS).</summary>
	private static bool IsLockConflict(IOException ex) =>
		OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation
		: ex.HResult == (OperatingSystem.IsMacOS() ? MacEWouldBlock : LinuxEWouldBlock);

	private const int ErrorSharingViolation = 32, ErrorLockViolation = 33, LinuxEWouldBlock = 11, MacEWouldBlock = 35;
}

using System.Text;

namespace WebullAnalytics.IO;

/// <summary>
/// Appends whole lines to a log that several writers share — `wa ai watch` writes each proposal log from two sinks
/// (management + open), and a concurrent `wa ai scan` writes the same file. The old writers each held one long-lived
/// <c>FileMode.Append</c> stream with <c>FileShare.ReadWrite</c>; .NET writes at the stream's cached position, so a
/// writer that had not seen another's appends overwrote them, and a record longer than the StreamWriter buffer went out
/// in several chunks. Both produced fused, unparseable JSONL lines (two in ai-proposals.SPY.DC2.jsonl by 2026-10-08).
/// Each call here opens with <c>FileShare.Read</c> (no other writer may hold the file meanwhile, readers still can — see
/// <see cref="SharedFileReader"/>), lands at the true end, writes the full line in one call and closes. A writer that
/// finds the file locked retries briefly.
/// </summary>
internal static class SharedFileAppender
{
	private const int MaxAttempts = 40;
	private const int RetryDelayMs = 25;
	private const int ErrorSharingViolation = 32, ErrorLockViolation = 33;

	public static void AppendLine(string path, string line)
	{
		var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
				stream.Write(bytes);
				return;
			}
			catch (IOException ex) when (IsLockConflict(ex) && attempt < MaxAttempts)
			{
				Thread.Sleep(RetryDelayMs);
			}
		}
	}

	private static bool IsLockConflict(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
}

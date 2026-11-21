namespace WebullAnalytics.IO;

/// <summary>
/// Replaces a file with a fully written temp file in one rename, so a reader never sees a torn write. On Windows the rename fails
/// with a sharing violation (<see cref="IOException"/>) or "access denied" (<see cref="UnauthorizedAccessException"/>) while another
/// process — antivirus or the search indexer scanning the file just written, a reader that opened it without delete sharing — briefly
/// holds the destination. Those clear within milliseconds, so the replace is retried a few times before the error is rethrown.
/// </summary>
internal static class AtomicFile
{
	private static readonly int[] RetryDelaysMs = [10, 25, 50, 100, 200];

	public static void Replace(string tempPath, string destinationPath)
	{
		for (var attempt = 0; ; attempt++)
		{
			try
			{
				File.Move(tempPath, destinationPath, overwrite: true);
				return;
			}
			catch (Exception ex) when (IsTransientLock(ex) && attempt < RetryDelaysMs.Length)
			{
				Thread.Sleep(RetryDelaysMs[attempt]);
			}
		}
	}

	/// <summary>A lock another process holds for a moment — not a missing source or directory, which no retry fixes.</summary>
	private static bool IsTransientLock(Exception ex) =>
		ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException and not DirectoryNotFoundException);
}

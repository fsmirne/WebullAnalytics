namespace WebullAnalytics.Data.Backfill;

/// <summary>The backfill's run log: timestamped lines to the console and, for the long pulls, appended to a file under
/// <c>data/logs/</c> so a nightly run stays auditable after the terminal is gone. Thread-safe — expirations log from parallel workers.</summary>
internal sealed class BackfillLog : IDisposable
{
	private readonly object _gate = new();
	private readonly StreamWriter? _file;

	internal string? FilePath { get; }

	/// <param name="mode">The pull being logged (quotes / ohlcv / eod) — names the default file.</param>
	/// <param name="path">An explicit log path, or null for data/logs/backfill_&lt;mode&gt;_&lt;timestamp&gt;.log.</param>
	internal BackfillLog(string mode, string? path)
	{
		FilePath = path ?? Program.ResolvePath($"data/logs/backfill_{mode}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
		Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
		_file = new StreamWriter(FilePath, append: true) { AutoFlush = true, NewLine = "\n" };
	}

	internal void Info(string message)
	{
		var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
		lock (_gate)
		{
			Console.WriteLine(line);
			_file?.WriteLine(line);
		}
	}

	public void Dispose() => _file?.Dispose();
}

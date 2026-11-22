using System.Collections.Concurrent;
using System.Text;

namespace WebullAnalytics;

/// <summary>
/// One expiry's volume captures from a day's data/iv file (<see cref="IvDumpStore"/>), kept for the life of the process and topped up with only
/// the bytes appended since the last read. A live cascade read needs the session's captures up to now; the day file grows all session (2026-11-20:
/// 371 MB, 3.8M rows), so re-parsing it on every read cost ~2 s of CPU by the afternoon, once a minute. Only whole lines are consumed: a writer's
/// half-written last line is picked up on the next read. A file that shrank (rewritten) is read again from the start.
///
/// The returned map is the tail's own and grows on the next read: a caller reads it before its next <see cref="Captures"/> call and does not keep
/// it. Tails of other days are dropped when a new day's is opened, so a watch running past midnight does not hold yesterday's.
/// </summary>
internal sealed class IvVolumeTail
{
	private static readonly ConcurrentDictionary<(string Ticker, DateTime Date, string Source, DateTime Expiry), IvVolumeTail> Tails = new();

	private readonly string _path;
	private readonly string _source;
	private readonly DateTime _expiry;
	private readonly SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>> _series = new();
	private readonly Lock _gate = new();
	private long _offset;

	internal IvVolumeTail(string path, string source, DateTime expiry)
	{
		_path = path;
		_source = source;
		_expiry = expiry;
	}

	/// <summary>Capture time → (expiry, strike, right) → cumulative day volume for <paramref name="expiry"/>, every strike, as of the file's
	/// current end. Empty while the day file does not exist.</summary>
	internal static SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>> Captures(string ticker, DateTime date, string source, DateTime expiry)
	{
		var key = (ticker.ToUpperInvariant(), date.Date, source.ToLowerInvariant(), expiry.Date);
		foreach (var stale in Tails.Keys.Where(k => k.Date != date.Date)) Tails.TryRemove(stale, out _);
		return Tails.GetOrAdd(key, k => new IvVolumeTail(IvDumpStore.PathFor(k.Ticker, k.Date), k.Source, k.Expiry)).Read();
	}

	internal SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>> Read()
	{
		lock (_gate)
		{
			if (!File.Exists(_path)) return _series;
			using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length < _offset)
			{
				_offset = 0;
				_series.Clear();
			}
			stream.Seek(_offset, SeekOrigin.Begin);
			_offset += ConsumeLines(stream);
			return _series;
		}
	}

	/// <summary>Adds every whole line from <paramref name="stream"/>'s position to its end, and returns the bytes those lines took (a trailing
	/// partial line is left for the next read).</summary>
	private long ConsumeLines(Stream stream)
	{
		var buffer = new byte[1 << 20];
		var pending = new List<byte>();
		long consumed = 0;
		for (int read; (read = stream.Read(buffer, 0, buffer.Length)) > 0;)
		{
			var start = 0;
			for (var i = 0; i < read; i++)
			{
				if (buffer[i] != (byte)'\n') continue;
				var line = pending.Count == 0 ? Encoding.UTF8.GetString(buffer, start, i - start) : Encoding.UTF8.GetString([.. pending, .. buffer.AsSpan(start, i - start)]);
				consumed += pending.Count + (i - start) + 1;
				pending.Clear();
				IvDumpStore.AddVolumeRow(line.TrimEnd('\r'), _source, IsTracked, _series);
				start = i + 1;
			}
			pending.AddRange(buffer.AsSpan(start, read - start));
		}
		return consumed;
	}

	private bool IsTracked(DateTime expiry, decimal strike) => expiry == _expiry;
}

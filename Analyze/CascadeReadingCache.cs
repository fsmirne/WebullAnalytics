using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebullAnalytics.IO;

namespace WebullAnalytics.Analyze;

/// <summary>One cascade reading of a replayed session: the instant it reads (ET time of day), the reading, and the spot it was scored at.</summary>
internal sealed record CascadeMoment(TimeSpan TimeEt, CascadeReading Reading, decimal Spot);

/// <summary>
/// Each replayed session's cascade readings on a read grid, kept on disk so a backtest sweep replays the regime once and every later
/// run (an exit rule, a strike offset, a gate — anything that does not change the reading) reads it back in moments. A reading depends
/// on the engine build and the session's stored inputs, never on a strategy knob, so the cache is keyed by exactly those:
/// <list type="bullet">
///   <item><description>the build — the scoring assembly's module id, which changes on every compile, so a scorer edit can never be served
///   a reading the old scorer made;</description></item>
///   <item><description>the read grid (the instants of the session);</description></item>
///   <item><description>each session's captured chain and minute tape, by size and write time, checked on every read.</description></item>
/// </list>
/// The quote store is not fingerprinted (one SQLite file for every day): after re-backfilling a past session, run once with
/// <c>--recache</c>. Files are gzip JSON under data/cascade-cache/; only the newest few builds' directories are kept.
/// </summary>
internal sealed class CascadeReadingCache
{
	private const int KeptBuilds = 3;
	private static readonly JsonSerializerOptions Options = CascadeLog.Options;

	private readonly string _directory;
	private readonly bool _recache;

	/// <param name="recache">Ignore what is stored and overwrite it.</param>
	public CascadeReadingCache(string ticker, IReadOnlyList<TimeSpan> times, bool recache, string? root = null, string? build = null)
	{
		var grid = string.Join(",", times.Select(t => t.ToString(@"hh\:mm")));
		var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{build ?? BuildId()}|{grid}")))[..16].ToLowerInvariant();
		var tickerDir = Path.Combine(root ?? Program.ResolvePath("data/cascade-cache"), ticker.ToUpperInvariant());
		_directory = Path.Combine(tickerDir, key);
		_recache = recache;
		Directory.CreateDirectory(_directory);
		try { Directory.SetLastWriteTimeUtc(_directory, DateTime.UtcNow); }   // marks this build as the most recently used, for Prune
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
		Prune(tickerDir, _directory);
	}

	/// <summary>The stored readings of <paramref name="date"/>, or null when there are none for these inputs.</summary>
	public IReadOnlyList<CascadeMoment>? TryLoad(DateTime date, string fingerprint)
	{
		if (_recache) return null;
		var path = PathFor(date);
		if (!File.Exists(path)) return null;
		try
		{
			using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
			var stored = JsonSerializer.Deserialize<Stored>(gz, Options);
			return stored != null && stored.Fingerprint == fingerprint ? stored.Moments : null;
		}
		catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or NotSupportedException)
		{
			return null;   // torn or from an incompatible layout: recompute
		}
	}

	public void Save(DateTime date, string fingerprint, IReadOnlyList<CascadeMoment> moments)
	{
		var path = PathFor(date);
		var temp = path + $".{Environment.ProcessId}.tmp";
		using (var gz = new GZipStream(File.Create(temp), CompressionLevel.Fastest))
			JsonSerializer.Serialize(gz, new Stored(fingerprint, moments), Options);
		AtomicFile.Replace(temp, path);
	}

	/// <summary>The session inputs a reading is built from, by size and write time; empty parts for a missing file.</summary>
	public static string Fingerprint(string ticker, DateTime date)
	{
		ticker = ticker.ToUpperInvariant();
		static string Of(string path) => File.Exists(path) ? $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}" : "-";
		return $"{Of(Program.ResolvePath($"data/oi/{ticker}/{date:yyyy-MM-dd}.jsonl"))}|{Of(Program.ResolvePath($"data/intraday/{ticker}/{date:yyyy-MM-dd}.csv"))}";
	}

	private string PathFor(DateTime date) => Path.Combine(_directory, $"{date:yyyy-MM-dd}.json.gz");

	private static string BuildId() => typeof(CascadeScreener).Assembly.ManifestModule.ModuleVersionId.ToString("N");

	/// <summary>Deletes all but the <see cref="KeptBuilds"/> most recently used build directories of the ticker.</summary>
	private static void Prune(string tickerDir, string current)
	{
		try
		{
			foreach (var stale in Directory.GetDirectories(tickerDir).Where(d => d != current).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(KeptBuilds - 1))
				Directory.Delete(stale, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private sealed record Stored(string Fingerprint, IReadOnlyList<CascadeMoment> Moments);
}

using System.Text.Json;
using WebullAnalytics.IO;
using Xunit;

namespace WebullAnalytics.Tests.IO;

/// <summary>Locks the proposal-log write path: many writers appending records larger than a StreamWriter buffer must
/// leave every line intact. The old long-lived FileMode.Append writers overwrote and fused lines under exactly this
/// load (two corrupt lines in ai-proposals.SPY.DC2.jsonl by 2026-10-08).</summary>
public class SharedFileAppenderTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"shared-appender-test.{Guid.NewGuid():N}.jsonl");

	public void Dispose()
	{
		if (File.Exists(_path)) File.Delete(_path);
	}

	[Fact]
	public void ConcurrentWriters_LeaveEveryLineIntact()
	{
		const int writers = 8, perWriter = 50;
		var padding = new string('x', 6000);   // > the 4 KB StreamWriter buffer that used to split records into chunks
		Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = writers }, w =>
		{
			for (var i = 0; i < perWriter; i++)
				SharedFileAppender.AppendLine(_path, JsonSerializer.Serialize(new { w, i, padding }));
		});

		var lines = SharedFileReader.ReadAllLines(_path);
		Assert.Equal(writers * perWriter, lines.Length);
		var seen = lines.Select(l => JsonDocument.Parse(l).RootElement).Select(e => (e.GetProperty("w").GetInt32(), e.GetProperty("i").GetInt32())).ToHashSet();
		Assert.Equal(writers * perWriter, seen.Count);
	}

	[Fact]
	public void ReaderCanReadBetweenAppends()
	{
		SharedFileAppender.AppendLine(_path, "{\"a\":1}");
		Assert.Single(SharedFileReader.ReadAllLines(_path));
		SharedFileAppender.AppendLine(_path, "{\"a\":2}");
		Assert.Equal(2, SharedFileReader.ReadAllLines(_path).Length);
	}
}

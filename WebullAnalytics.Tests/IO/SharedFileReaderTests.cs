using WebullAnalytics.IO;
using Xunit;

namespace WebullAnalytics.Tests.IO;

public class SharedFileReaderTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"shared-reader-test.{Guid.NewGuid():N}.jsonl");

	public void Dispose()
	{
		if (File.Exists(_path)) File.Delete(_path);
	}

	/// <summary>The wa ai watch scenario: the sink holds the file open for append (FileShare.ReadWrite,
	/// as OpenProposalSink/ProposalSink do) while a backtest --replay reads it. File.ReadLines demands
	/// deny-write sharing and throws IOException against that handle; SharedFileReader must not.</summary>
	[Fact]
	public void ReadsWhileWriterHoldsAppendHandle()
	{
		using var writer = new StreamWriter(File.Open(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
		writer.WriteLine("line1");
		writer.WriteLine("line2");

		Assert.Throws<IOException>(() => File.ReadLines(_path).ToArray());
		Assert.Equal(new[] { "line1", "line2" }, SharedFileReader.ReadAllLines(_path));
	}
}

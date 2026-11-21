using WebullAnalytics.IO;
using Xunit;

namespace WebullAnalytics.Tests.IO;

public class AtomicFileTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "wa-atomic-tests-" + Guid.NewGuid().ToString("N"));

	public AtomicFileTests() => Directory.CreateDirectory(_dir);

	public void Dispose()
	{
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	[Fact]
	public async Task Replace_WaitsOutABriefLockOnTheDestination()
	{
		var dest = Path.Combine(_dir, "data.json");
		var tmp = dest + ".tmp";
		File.WriteAllText(dest, "old");
		File.WriteAllText(tmp, "new");

		// A scanner-style handle: shares read but not delete, which blocks the rename until it is released.
		var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
		var release = Task.Delay(30, TestContext.Current.CancellationToken).ContinueWith(_ => holder.Dispose(), TestContext.Current.CancellationToken);

		AtomicFile.Replace(tmp, dest);
		await release;

		Assert.Equal("new", File.ReadAllText(dest));
		Assert.False(File.Exists(tmp));
	}

	[Fact]
	public void Replace_DoesNotRetryAMissingSource()
	{
		var dest = Path.Combine(_dir, "data.json");
		Assert.Throws<FileNotFoundException>(() => AtomicFile.Replace(dest + ".missing", dest));
	}
}

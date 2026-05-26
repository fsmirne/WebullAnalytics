using System.Text.Json;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

[Collection("DerivativeIdRegistry")]
public class DerivativeIdRegistryTests : IDisposable
{
	private readonly string _tmpDir;
	private readonly string _path;

	public DerivativeIdRegistryTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-deriv-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
		_path = Path.Combine(_tmpDir, "derivative-ids.json");
		DerivativeIdRegistry.ResetForTests(_path);
	}

	public void Dispose()
	{
		DerivativeIdRegistry.ResetForTests(null);
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	[Fact]
	public void Register_NewEntries_FlushesToDisk()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long>
		{
			["SPXW260526C07530000"] = 1060402178,
			["SPXW260526P07400000"] = 1060402177,
		});

		Assert.True(File.Exists(_path));
		var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_path));
		Assert.NotNull(loaded);
		Assert.Equal(1060402178, loaded!["SPXW260526C07530000"]);
		Assert.Equal(1060402177, loaded!["SPXW260526P07400000"]);
	}

	[Fact]
	public void Register_NoChanges_DoesNotRewriteFile()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["SPXW260526C07530000"] = 1060402178 });
		var firstWrite = File.GetLastWriteTimeUtc(_path);

		Thread.Sleep(50);
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["SPXW260526C07530000"] = 1060402178 });

		Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_path));
	}

	[Fact]
	public void Register_ChangedIdForSameSymbol_OverwritesAndFlushes()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["SPXW260526C07530000"] = 1060402178 });
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["SPXW260526C07530000"] = 9999999999 });

		var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_path));
		Assert.Equal(9999999999, loaded!["SPXW260526C07530000"]);
	}

	[Fact]
	public void Register_RejectsBlankSymbolOrZeroId()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long>
		{
			[""] = 1234,
			["   "] = 5678,
			["SPXW260526C07530000"] = 0,
			["SPXW260526P07400000"] = -1,
		});

		Assert.False(File.Exists(_path));
		Assert.Empty(DerivativeIdRegistry.Snapshot());
	}

	[Fact]
	public void Snapshot_AfterMultipleRegistrations_ReturnsAllEntries()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["A"] = 1 });
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["B"] = 2, ["C"] = 3 });

		var snap = DerivativeIdRegistry.Snapshot();
		Assert.Equal(3, snap.Count);
		Assert.Equal(1, snap["A"]);
		Assert.Equal(2, snap["B"]);
		Assert.Equal(3, snap["C"]);
	}

	[Fact]
	public void LoadFromDisk_PreservesExistingEntries()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, long>
		{
			["PREEXISTING"] = 42
		}));
		DerivativeIdRegistry.ResetForTests(_path);

		Assert.Equal(42, DerivativeIdRegistry.Snapshot()["PREEXISTING"]);

		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["NEW"] = 100 });
		Assert.Equal(2, DerivativeIdRegistry.Snapshot().Count);
	}

	[Fact]
	public void LoadFromDisk_CorruptFile_StartsEmpty()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.WriteAllText(_path, "not valid json");
		DerivativeIdRegistry.ResetForTests(_path);

		// Snapshot should be empty (corrupt file ignored).
		Assert.Empty(DerivativeIdRegistry.Snapshot());

		// New writes should still work.
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["A"] = 1 });
		Assert.Single(DerivativeIdRegistry.Snapshot());
	}
}

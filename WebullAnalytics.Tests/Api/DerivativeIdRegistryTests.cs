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

	[Fact]
	public void RecordSnapshot_MarksTradeableStrikes_AndQueriesByTicker()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long>
		{
			["XSP260605C00760000"] = 111, // $760, tradeable
			["XSP260605C00761000"] = 112, // $761, dead
			["XSP260605C00765000"] = 113, // $765, tradeable
		});
		DerivativeIdRegistry.RecordSnapshot("2026-06-01", new Dictionary<string, (bool, long?)>
		{
			["XSP260605C00760000"] = (true, 1200),
			["XSP260605C00761000"] = (false, null),
			["XSP260605C00765000"] = (true, 800),
		});

		Assert.True(DerivativeIdRegistry.HasSnapshot("XSP", "2026-06-01"));
		Assert.False(DerivativeIdRegistry.HasSnapshot("XSP", "2026-06-02")); // wrong date
		Assert.False(DerivativeIdRegistry.HasSnapshot("SPXW", "2026-06-01")); // wrong ticker

		var tradeable = DerivativeIdRegistry.TradeableOccs("XSP", "2026-06-01");
		Assert.Equal(2, tradeable.Count);
		Assert.Contains("XSP260605C00760000", tradeable);
		Assert.Contains("XSP260605C00765000", tradeable);
		Assert.DoesNotContain("XSP260605C00761000", tradeable); // dead strike excluded
	}

	[Fact]
	public void CoveredExpiries_ReturnsSweptExpiries_RegardlessOfTradeable_FilteredByTickerAndDate()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long>
		{
			["XSP260605C00760000"] = 111, // expiry 2026-06-05, tradeable
			["XSP260605C00761000"] = 112, // expiry 2026-06-05, dead — still a covered expiry
			["XSP260619C00760000"] = 113, // expiry 2026-06-19, tradeable
			["SPXW260605C04000000"] = 114, // different ticker, same date
		});
		DerivativeIdRegistry.RecordSnapshot("2026-06-01", new Dictionary<string, (bool, long?)>
		{
			["XSP260605C00760000"] = (true, 1200),
			["XSP260605C00761000"] = (false, null),
			["XSP260619C00760000"] = (true, 50),
			["SPXW260605C04000000"] = (true, 999),
		});

		var covered = DerivativeIdRegistry.CoveredExpiries("XSP", "2026-06-01");
		Assert.Equal(2, covered.Count);
		Assert.Contains(new DateTime(2026, 6, 5), covered);   // included even though one strike was dead
		Assert.Contains(new DateTime(2026, 6, 19), covered);

		Assert.Empty(DerivativeIdRegistry.CoveredExpiries("XSP", "2026-06-02")); // wrong date
		Assert.Single(DerivativeIdRegistry.CoveredExpiries("SPXW", "2026-06-01")); // ticker-scoped
	}

	[Fact]
	public void RecordSnapshot_SkipsContractsNotYetHarvested()
	{
		DerivativeIdRegistry.RecordSnapshot("2026-06-01", new Dictionary<string, (bool, long?)>
		{
			["XSP260605C00760000"] = (true, 1200), // never Register'd → must be ignored
		});
		Assert.Empty(DerivativeIdRegistry.TradeableOccs("XSP", "2026-06-01"));
	}

	[Fact]
	public void Register_PreservesExistingLiquiditySnapshot()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["XSP260605C00760000"] = 111 });
		DerivativeIdRegistry.RecordSnapshot("2026-06-01", new Dictionary<string, (bool, long?)> { ["XSP260605C00760000"] = (true, 1200) });
		// Re-harvesting the same id must not wipe the snapshot.
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["XSP260605C00760000"] = 111 });
		Assert.Contains("XSP260605C00760000", DerivativeIdRegistry.TradeableOccs("XSP", "2026-06-01"));
	}

	[Fact]
	public void EnrichedEntries_RoundTripThroughDisk_AndIdsStillReadable()
	{
		DerivativeIdRegistry.Register(new Dictionary<string, long> { ["XSP260605C00760000"] = 111, ["XSP260605C00761000"] = 112 });
		DerivativeIdRegistry.RecordSnapshot("2026-06-01", new Dictionary<string, (bool, long?)> { ["XSP260605C00760000"] = (true, 1200) });

		// Reload from disk: enriched + id-only entries must both survive.
		DerivativeIdRegistry.ResetForTests(_path);
		Assert.Contains("XSP260605C00760000", DerivativeIdRegistry.TradeableOccs("XSP", "2026-06-01"));
		var ids = DerivativeIdRegistry.Snapshot();
		Assert.Equal(111, ids["XSP260605C00760000"]); // enriched entry's id still readable
		Assert.Equal(112, ids["XSP260605C00761000"]); // bare-number entry still readable
	}

	[Fact]
	public void LoadFromDisk_MixedLegacyAndEnrichedForm()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.WriteAllText(_path, """
		{
		  "XSP260605C00761000": 112,
		  "XSP260605C00760000": { "id": 111, "asof": "2026-06-01", "tradeable": true, "oi": 1200 }
		}
		""");
		DerivativeIdRegistry.ResetForTests(_path);

		var ids = DerivativeIdRegistry.Snapshot();
		Assert.Equal(112, ids["XSP260605C00761000"]);
		Assert.Equal(111, ids["XSP260605C00760000"]);
		Assert.Equal(new[] { "XSP260605C00760000" }, DerivativeIdRegistry.TradeableOccs("XSP", "2026-06-01"));
	}
}

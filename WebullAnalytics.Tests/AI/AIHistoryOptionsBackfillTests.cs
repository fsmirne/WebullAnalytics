using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI;

[Collection("DerivativeIdRegistry")]
public class AIHistoryOptionsBackfillTests : IDisposable
{
	private readonly string _tmpDir;

	public AIHistoryOptionsBackfillTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-backfill-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
		DerivativeIdRegistry.ResetForTests(Path.Combine(_tmpDir, "derivative-ids.json"));
	}

	public void Dispose()
	{
		DerivativeIdRegistry.ResetForTests(null);
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	[Fact]
	public void BuildCsvPath_ProducesExpectedShape()
	{
		var parsed = new OptionParsed("SPXW", new DateTime(2026, 5, 26), "C", 7530m);
		var path = AIHistoryOptionsBackfill.BuildCsvPath("/tmp/data/options/SPXW", parsed, "SPXW260526C07530000");

		// Path separators differ across OSes — assert on the structural pieces, not the literal string.
		var parts = path.Replace('\\', '/').Split('/');
		Assert.Contains("SPXW", parts);
		Assert.Contains("2026-05-26", parts);
		Assert.EndsWith("SPXW260526C07530000.csv", parts[^1]);
	}

	[Fact]
	public void WriteOptionCsv_IncludesIvColumn_NullsAsBlank()
	{
		var bars = new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1779806220), 22.70m, 23.91m, 22.60m, 22.80m, 76, 15.75m),
			new(DateTimeOffset.FromUnixTimeSeconds(1779806280), 22.70m, 23.50m, 22.50m, 23.30m, 31, null),
		};
		var path = Path.Combine(_tmpDir, "test.csv");
		AIHistoryOptionsBackfill.WriteOptionCsv(path, bars);

		var lines = File.ReadAllLines(path);
		Assert.Equal("timestamp_utc,open,high,low,close,volume,iv", lines[0]);
		// Bar 1: IV present
		Assert.EndsWith(",15.75", lines[1]);
		// Bar 2: IV missing — column trailing empty (line ends with comma)
		Assert.EndsWith(",", lines[2]);
	}

	[Fact]
	public void CountUnbackfilledContracts_EmptyRegistry_ReturnsZero()
	{
		Assert.Equal(0, AIHistoryOptionsBackfill.CountUnbackfilledContracts("SPXW"));
	}

	[Fact]
	public void LoadTouchedSymbolsFromPaths_ExtractsFromProposalsAndOrders()
	{
		// One proposal (SPXW LongCall) and one order (GME call). Should yield exactly those two.
		var proposalsPath = Path.Combine(_tmpDir, "ai-proposals.jsonl");
		File.WriteAllText(proposalsPath, """
			{"type":"open","ticker":"SPXW","legs":[{"action":"buy","symbol":"SPXW260526C07530000","qty":2}]}
			{"type":"open","ticker":"GME","legs":[{"action":"buy","symbol":"GME260529C00027500","qty":1}]}
			""");
		var ordersPath = Path.Combine(_tmpDir, "orders.jsonl");
		File.WriteAllText(ordersPath, """
			{"orderList":[{"tickerType":"OPTION","symbol":"GME $26.50","subSymbol":"29 May 26 Call 100"}]}
			""");

		var spxw = AIHistoryOptionsBackfill.LoadTouchedSymbolsFromPaths(proposalsPath, ordersPath, "SPXW");
		Assert.Single(spxw);
		Assert.Contains("SPXW260526C07530000", spxw);

		var gme = AIHistoryOptionsBackfill.LoadTouchedSymbolsFromPaths(proposalsPath, ordersPath, "GME");
		Assert.Equal(2, gme.Count); // one from proposals, one from orders
		Assert.Contains("GME260529C00027500", gme);
		Assert.Contains("GME260529C00026500", gme);
	}

	[Fact]
	public void LoadTouchedSymbolsFromPaths_MalformedLine_Skipped()
	{
		var proposalsPath = Path.Combine(_tmpDir, "ai-proposals.jsonl");
		// First line is a partial JSON write (crashed mid-line); second is valid.
		File.WriteAllText(proposalsPath, """
			{"type":"open","legs":[{"symbol":"SPXW260526C0753
			{"type":"open","ticker":"SPXW","legs":[{"action":"buy","symbol":"SPXW260526P07400000","qty":1}]}
			""");
		var ordersPath = Path.Combine(_tmpDir, "orders.jsonl");

		var symbols = AIHistoryOptionsBackfill.LoadTouchedSymbolsFromPaths(proposalsPath, ordersPath, "SPXW");
		Assert.Single(symbols);
		Assert.Contains("SPXW260526P07400000", symbols);
	}

	// Note: the "CSV already exists → skip" branch of CountUnbackfilledContracts isn't tested here
	// because Program.ResolvePath("data/options") resolves to the user's prod AppData dir in this
	// environment — planting a test CSV there would pollute live data. The branch is straightforward
	// (single File.Exists check) and exercised by the slice-2 manual e2e run.

	[Fact]
	public void TryReconstructOcc_RoundTripsWebullOrderShape()
	{
		// One real fill shape from orders.jsonl: GME 26.50 call expiring 29 May 2026
		var json = """{"tickerType":"OPTION","symbol":"GME $26.50","subSymbol":"29 May 26 Call 100"}""";
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var occ = InvokeReconstruct(doc.RootElement, "GME");
		Assert.Equal("GME260529C00026500", occ);
	}

	[Fact]
	public void TryReconstructOcc_PutWorks()
	{
		var json = """{"tickerType":"OPTION","symbol":"SPY $580.00","subSymbol":"7 Jul 25 Put 100"}""";
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var occ = InvokeReconstruct(doc.RootElement, "SPY");
		Assert.Equal("SPY250707P00580000", occ);
	}

	[Fact]
	public void TryReconstructOcc_NonOptionFill_ReturnsNull()
	{
		var json = """{"tickerType":"STOCK","symbol":"AAPL","subSymbol":""}""";
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var occ = InvokeReconstruct(doc.RootElement, "AAPL");
		Assert.Null(occ);
	}

	[Fact]
	public void TryReconstructOcc_RootMismatch_ReturnsNull()
	{
		// Order is for SPY but caller wants SPXW — should be filtered out.
		var json = """{"tickerType":"OPTION","symbol":"SPY $580.00","subSymbol":"7 Jul 25 Put 100"}""";
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var occ = InvokeReconstruct(doc.RootElement, "SPXW");
		Assert.Null(occ);
	}

	// Reflection helper since TryReconstructOcc is private — exposing it as internal+InternalsVisibleTo
	// would be overkill for one method. The signature is small and stable.
	private static string? InvokeReconstruct(System.Text.Json.JsonElement order, string ticker)
	{
		var method = typeof(AIHistoryOptionsBackfill).GetMethod("TryReconstructOcc",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
		Assert.NotNull(method);
		return (string?)method!.Invoke(null, new object[] { order, ticker });
	}

	[Fact]
	public void MergeByTimestamp_DisjointSets_UnionedSorted()
	{
		var existing = new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1000), 1m, 1m, 1m, 1m, 10, 0.15m),
			new(DateTimeOffset.FromUnixTimeSeconds(1060), 2m, 2m, 2m, 2m, 20, 0.16m),
		};
		var incoming = new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1120), 3m, 3m, 3m, 3m, 30, 0.17m),
			new(DateTimeOffset.FromUnixTimeSeconds(1180), 4m, 4m, 4m, 4m, 40, 0.18m),
		};
		var (merged, newMinutes) = AIHistoryOptionsBackfill.MergeByTimestamp(existing, incoming);
		Assert.Equal(4, merged.Count);
		Assert.Equal(2, newMinutes);
		Assert.Equal(1000, merged[0].Timestamp.ToUnixTimeSeconds());
		Assert.Equal(1180, merged[^1].Timestamp.ToUnixTimeSeconds());
	}

	[Fact]
	public void MergeByTimestamp_OverlapBarsAreReplacedByIncoming()
	{
		// Same timestamp present in both — incoming should win (Webull revises late-reporting bars).
		var existing = new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1000), 1m, 1m, 1m, 1m, 10, 0.15m),
		};
		var incoming = new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1000), 1m, 2m, 0.5m, 1.5m, 99, 0.20m),
		};
		var (merged, newMinutes) = AIHistoryOptionsBackfill.MergeByTimestamp(existing, incoming);
		Assert.Single(merged);
		Assert.Equal(0, newMinutes); // not a new timestamp — just an update
		Assert.Equal(1.5m, merged[0].Close);
		Assert.Equal(99, merged[0].Volume);
		Assert.Equal(0.20m, merged[0].ImpliedVolatility);
	}

	[Fact]
	public void WriteOptionCsv_ReadBack_RoundTripsAllFields()
	{
		var bars = new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1779806220), 22.70m, 23.91m, 22.60m, 22.80m, 76, 15.75m),
			new(DateTimeOffset.FromUnixTimeSeconds(1779806280), 22.70m, 23.50m, 22.50m, 23.30m, 31, null),
		};
		var path = Path.Combine(_tmpDir, "rt.csv");
		AIHistoryOptionsBackfill.WriteOptionCsv(path, bars);

		var loaded = AIHistoryOptionsBackfill.ReadOptionCsv(path);
		Assert.Equal(2, loaded.Count);
		Assert.Equal(22.80m, loaded[0].Close);
		Assert.Equal(15.75m, loaded[0].ImpliedVolatility);
		Assert.Null(loaded[1].ImpliedVolatility);
		Assert.Equal(31, loaded[1].Volume);
	}

	[Fact]
	public void ReadOptionCsv_MissingFile_ReturnsEmpty()
	{
		var bars = AIHistoryOptionsBackfill.ReadOptionCsv(Path.Combine(_tmpDir, "does-not-exist.csv"));
		Assert.Empty(bars);
	}
}

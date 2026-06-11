using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// Guards the broker-history-lag fix: Webull's today-orders endpoint can lag a fill by 10+ minutes
// (live double-open 2026-06-11), so BrokerStateService merges a persisted ledger of locally-placed
// orders into its snapshot until the broker reports each order's client_order_id.
public class LocalOrderLedgerTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"wa-ledger-test-{Guid.NewGuid():N}.jsonl");

	public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

	[Fact]
	public void AppendThenRead_RoundTripsTodayOnly()
	{
		var ledger = new LocalOrderLedger(_path);
		var today = new DateTime(2026, 6, 11);
		ledger.Append("SPY", open: true, "BUY:SPY:2026-06-30:734.00:C|SELL:SPY:2026-06-16:730.00:C", "cid-1", today);
		ledger.Append("SPY", open: false, "SELL:SPY:2026-07-02:740.00:P|BUY:SPY:2026-06-18:740.00:P", "cid-2", today.AddDays(-1));

		var entries = ledger.ReadFor(today);
		var e = Assert.Single(entries);
		Assert.Equal("SPY", e.Root);
		Assert.True(e.Open);
		Assert.Equal("cid-1", e.ClientOrderId);
	}

	[Fact]
	public void Read_SkipsCorruptLines()
	{
		var ledger = new LocalOrderLedger(_path);
		var today = new DateTime(2026, 6, 11);
		ledger.Append("SPY", open: true, "fp-good", "cid-1", today);
		File.AppendAllText(_path, "{torn-concurrent-write\n");
		ledger.Append("SPY", open: true, "fp-good-2", "cid-2", today);

		Assert.Equal(2, ledger.ReadFor(today).Count);
	}

	[Fact]
	public void Merge_UnreportedOpenEntry_CountsTowardCapAndDedup()
	{
		// The live failure: order placed and filled, broker history hasn't caught up yet.
		var entries = new[] { new LocalOrderLedger.Entry("2026-06-11", "SPY", true, "fp-dd-call", "cid-1") };
		var fingerprints = new HashSet<string>(StringComparer.Ordinal);
		var rootCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		BrokerStateService.MergeLedgerEntries(entries, new HashSet<string>(StringComparer.Ordinal), fingerprints, rootCount);

		Assert.Contains("fp-dd-call", fingerprints);
		Assert.Equal(1, rootCount["SPY"]);
	}

	[Fact]
	public void Merge_BrokerReportedEntry_IsSkipped_BrokerTruthGoverns()
	{
		// Once the broker reports the client_order_id (any status — including canceled, which must
		// free the slot), the ledger entry must stop contributing.
		var entries = new[] { new LocalOrderLedger.Entry("2026-06-11", "SPY", true, "fp-dd-call", "cid-1") };
		var reported = new HashSet<string>(StringComparer.Ordinal) { "cid-1" };
		var fingerprints = new HashSet<string>(StringComparer.Ordinal);
		var rootCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		BrokerStateService.MergeLedgerEntries(entries, reported, fingerprints, rootCount);

		Assert.Empty(fingerprints);
		Assert.False(rootCount.ContainsKey("SPY"));
	}

	[Fact]
	public void Merge_FingerprintAlreadyActiveAtBroker_DoesNotDoubleCount()
	{
		var entries = new[] { new LocalOrderLedger.Entry("2026-06-11", "SPY", true, "fp-dd-call", null) };
		var fingerprints = new HashSet<string>(StringComparer.Ordinal) { "fp-dd-call" };
		var rootCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["SPY"] = 1 };

		BrokerStateService.MergeLedgerEntries(entries, new HashSet<string>(StringComparer.Ordinal), fingerprints, rootCount);

		Assert.Equal(1, rootCount["SPY"]);
	}

	[Fact]
	public void Merge_CloseEntry_DedupsButDoesNotConsumeCap()
	{
		var entries = new[] { new LocalOrderLedger.Entry("2026-06-11", "SPY", false, "fp-close", "cid-9") };
		var fingerprints = new HashSet<string>(StringComparer.Ordinal);
		var rootCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		BrokerStateService.MergeLedgerEntries(entries, new HashSet<string>(StringComparer.Ordinal), fingerprints, rootCount);

		Assert.Contains("fp-close", fingerprints);
		Assert.False(rootCount.ContainsKey("SPY"));
	}
}

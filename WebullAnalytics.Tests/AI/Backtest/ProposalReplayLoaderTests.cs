using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

public class ProposalReplayLoaderTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"ai-proposals.SPY.TEST.{Guid.NewGuid():N}.jsonl");

	public void Dispose()
	{
		if (File.Exists(_path)) File.Delete(_path);
	}

	private static readonly DateTime Since = new(2026, 7, 13);
	private static readonly DateTime Until = new(2026, 7, 17);

	/// <summary>An open record shaped like OpenProposalSink.SerializeRecord output. <paramref name="ts"/> carries an
	/// explicit ET offset (-04:00) so the loader's ET conversion is machine-timezone-independent in tests.</summary>
	private static string OpenRecord(string ts, string structure = "LongCalendar", int qty = 2, decimal finalScore = 0.5m, string? informational = null, bool blocked = false,
		string legsJson = "[{\"action\":\"sell\",\"symbol\":\"SPY   260715C00620000\",\"qty\":2},{\"action\":\"buy\",\"symbol\":\"SPY   260814C00620000\",\"qty\":2}]",
		string quotesJson = "[{\"symbol\":\"SPY   260715C00620000\",\"bid\":0.90,\"ask\":0.92},{\"symbol\":\"SPY   260814C00620000\",\"bid\":2.33,\"ask\":2.34}]")
		=> $"{{\"type\":\"open\",\"ts\":\"{ts}\",\"mode\":\"watch\",{(informational != null ? $"\"informational\":{informational}," : "")}\"ticker\":\"SPY\",\"strategy\":\"TEST\",\"structure\":\"{structure}\",\"legs\":{legsJson},\"qty\":{qty},\"finalScore\":{finalScore},\"cashReserveBlocked\":{(blocked ? "true" : "false")},\"diagnostic\":{{\"spotAtEvaluation\":620.5,\"probe\":{{\"legQuotes\":{quotesJson}}}}}}}";

	[Fact]
	public void SelectsFirstQualifyingRecordPerDayAtOrAfter0930()
	{
		File.WriteAllLines(_path, new[]
		{
			OpenRecord("2026-07-15T09:15:00.0000000-04:00", finalScore: 0.9m),   // pre-market — never placed
			OpenRecord("2026-07-15T09:31:02.0000000-04:00", qty: 3),             // first RTH tick — the day's pick
			OpenRecord("2026-07-15T09:32:02.0000000-04:00", qty: 7),             // later tick — ignored
			OpenRecord("2026-07-16T10:05:00.0000000-04:00", qty: 1),             // next day — its own pick
		});
		var (opens, warnings) = ProposalReplayLoader.Load(_path, Since, Until, minScoreToOpen: 0.1m);
		Assert.Empty(warnings);
		Assert.Equal(2, opens.Count);
		Assert.Equal(new DateTime(2026, 7, 15, 9, 31, 2), opens[0].OpenEt);
		Assert.Equal(3, opens[0].Qty);
		Assert.Equal(new DateTime(2026, 7, 16, 10, 5, 0), opens[1].OpenEt);
	}

	[Fact]
	public void SkipsInformationalBlockedAndBelowGateRecords()
	{
		File.WriteAllLines(_path, new[]
		{
			OpenRecord("2026-07-15T09:31:00.0000000-04:00", informational: "true", finalScore: 0.9m),  // flagged informational
			OpenRecord("2026-07-15T09:31:01.0000000-04:00", blocked: true),                            // cash-reserve blocked
			OpenRecord("2026-07-15T09:31:02.0000000-04:00", finalScore: 0.05m),                        // legacy record (no flag) below gate
			OpenRecord("2026-07-15T09:31:03.0000000-04:00", qty: 0),                                   // unsizeable
			OpenRecord("2026-07-15T09:31:04.0000000-04:00", qty: 4),                                   // the actual pick
		});
		var (opens, _) = ProposalReplayLoader.Load(_path, Since, Until, minScoreToOpen: 0.1m);
		var open = Assert.Single(opens);
		Assert.Equal(4, open.Qty);
	}

	[Fact]
	public void PricesLegsAtStoredMidsWithComboTickRounding()
	{
		// Mids: buy 2.335, sell 0.91 → signed net -1.425; SPY 2-leg combo tick is $0.01 → limit 1.43
		// (away-from-zero). The half-cent delta lands on the highest-priced leg (the 2.335 buy → 2.34).
		File.WriteAllLines(_path, new[]
		{
			OpenRecord("2026-07-15T09:31:00.0000000-04:00",
				quotesJson: "[{\"symbol\":\"SPY   260715C00620000\",\"bid\":0.90,\"ask\":0.92},{\"symbol\":\"SPY   260814C00620000\",\"bid\":2.33,\"ask\":2.34}]"),
		});
		var (opens, _) = ProposalReplayLoader.Load(_path, Since, Until, minScoreToOpen: 0.1m);
		var open = Assert.Single(opens);
		var sell = open.Legs.Single(l => l.Action == "sell");
		var buy = open.Legs.Single(l => l.Action == "buy");
		Assert.Equal(0.91m, sell.PricePerShare);
		Assert.Equal(2.34m, buy.PricePerShare);
		Assert.Equal(sell.PricePerShare, sell.ExecutionPricePerShare);
		Assert.Equal(620.5m, open.Spot);
		Assert.Equal(OpenStructureKind.LongCalendar, open.StructureKind);
	}

	[Fact]
	public void UnpriceableDayPickWarnsAndReplaysNothingThatDay()
	{
		// The day's top pick has no stored quote for one leg: falling through to the later record would
		// replay a trade the live executor never chose, so the day must yield a warning and no trade.
		File.WriteAllLines(_path, new[]
		{
			OpenRecord("2026-07-15T09:31:00.0000000-04:00", quotesJson: "[{\"symbol\":\"SPY   260715C00620000\",\"bid\":0.90,\"ask\":0.92}]"),
			OpenRecord("2026-07-15T09:32:00.0000000-04:00"),
		});
		var (opens, warnings) = ProposalReplayLoader.Load(_path, Since, Until, minScoreToOpen: 0.1m);
		Assert.Empty(opens);
		Assert.Single(warnings);
		Assert.Contains("SPY   260814C00620000", warnings[0]);
	}

	[Fact]
	public void IgnoresManagementRecordsAndOutOfWindowDates()
	{
		File.WriteAllLines(_path, new[]
		{
			"{\"type\":\"management\",\"ts\":\"2026-07-15T10:00:00.0000000-04:00\",\"rule\":\"TakeProfitRule\",\"proposal\":{\"type\":\"close\",\"legs\":[]}}",
			OpenRecord("2026-07-10T09:31:00.0000000-04:00"),   // before --since
			OpenRecord("2026-07-20T09:31:00.0000000-04:00"),   // after --until
			OpenRecord("2026-07-15T09:31:00.0000000-04:00"),
		});
		var (opens, _) = ProposalReplayLoader.Load(_path, Since, Until, minScoreToOpen: 0.1m);
		var open = Assert.Single(opens);
		Assert.Equal(new DateTime(2026, 7, 15).Date, open.OpenEt.Date);
	}

	[Fact]
	public void ConvertsUtcTimestampsToEasternWallClock()
	{
		File.WriteAllLines(_path, new[] { OpenRecord("2026-07-15T13:31:02.0000000Z") });   // 09:31:02 ET during DST
		var (opens, _) = ProposalReplayLoader.Load(_path, Since, Until, minScoreToOpen: 0.1m);
		var open = Assert.Single(opens);
		Assert.Equal(new DateTime(2026, 7, 15, 9, 31, 2), open.OpenEt);
	}
}

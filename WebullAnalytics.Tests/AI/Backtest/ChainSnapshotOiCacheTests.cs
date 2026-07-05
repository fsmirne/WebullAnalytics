using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Guards the OI-snapshot causality rule: OI is report-date keyed (published each morning) so day T's OI
// is served as-is, but an EOD-stamped record's IV was back-solved from day T's 16:00 mids — serving it
// intraday would leak end-of-day vol into the 09:30 GEX weighting. EOD-stamped files must take IV from
// the prior trading day's snapshot; morning-stamped (live scraper) records keep their same-day IV.
public class ChainSnapshotOiCacheTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("wa-oi-cache-test-").FullName;

	public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

	// 2026-06-08 is a Monday; the prior trading day is Friday 2026-06-05 (exercises the weekend walk-back).
	private static readonly DateTime Monday = new(2026, 6, 8);
	private static readonly DateTime PriorFriday = new(2026, 6, 5);

	private void WriteSnapshot(DateTime date, string tsEt, params (string Sym, long Oi, decimal Iv)[] options)
	{
		var dir = Path.Combine(_dir, "SPY");
		Directory.CreateDirectory(dir);
		var opts = string.Join(",", options.Select(o => $"{{\"symbol\":\"{o.Sym}\",\"openInterest\":{o.Oi},\"iv\":{o.Iv}}}"));
		File.WriteAllText(Path.Combine(dir, $"{date:yyyy-MM-dd}.jsonl"), $"{{\"tsEt\":\"{tsEt}\",\"ticker\":\"SPY\",\"underlyingPrice\":600.0,\"options\":[{opts}]}}\n");
	}

	[Fact]
	public void EodStampedSnapshot_ServesDayOiWithPriorDayIv()
	{
		WriteSnapshot(PriorFriday, "2026-06-05T16:00:00-04:00", ("SPY260608C00600000", 1000, 0.21m));
		WriteSnapshot(Monday, "2026-06-08T16:00:00-04:00", ("SPY260608C00600000", 1500, 0.35m));

		var day = new ChainSnapshotOiCache(_dir).ForDay("SPY", Monday);
		var (oi, iv) = Assert.Contains("SPY260608C00600000", (IReadOnlyDictionary<string, (long Oi, decimal Iv)>)day);
		Assert.Equal(1500, oi);       // OI is Monday's own (report-date keyed → knowable at Monday's open)
		Assert.Equal(0.21m, iv);      // IV is Friday's (Monday's was solved from Monday's EOD mids → lookahead)
	}

	[Fact]
	public void EodStampedSnapshot_ContractAbsentFromPriorDay_DropsIvKeepsOi()
	{
		WriteSnapshot(PriorFriday, "2026-06-05T16:00:00-04:00", ("SPY260608C00590000", 500, 0.25m));
		WriteSnapshot(Monday, "2026-06-08T16:00:00-04:00", ("SPY260608C00600000", 1500, 0.35m));

		var day = new ChainSnapshotOiCache(_dir).ForDay("SPY", Monday);
		var (oi, iv) = day["SPY260608C00600000"];
		Assert.Equal(1500, oi);
		Assert.Equal(0m, iv);         // no causal IV available → 0 (ComputeGex skips gamma, max-pain keeps OI)
	}

	[Fact]
	public void MorningStampedSnapshot_KeepsSameDayIv()
	{
		WriteSnapshot(PriorFriday, "2026-06-05T16:00:00-04:00", ("SPY260608C00600000", 1000, 0.21m));
		WriteSnapshot(Monday, "2026-06-08T09:31:00-04:00", ("SPY260608C00600000", 1500, 0.28m));

		var day = new ChainSnapshotOiCache(_dir).ForDay("SPY", Monday);
		var (oi, iv) = day["SPY260608C00600000"];
		Assert.Equal(1500, oi);
		Assert.Equal(0.28m, iv);      // scraper's morning capture — observable at capture time, no substitution
	}

	[Fact]
	public void EodStampedSnapshot_NoPriorDayFile_DropsIv()
	{
		WriteSnapshot(Monday, "2026-06-08T16:00:00-04:00", ("SPY260608C00600000", 1500, 0.35m));

		var day = new ChainSnapshotOiCache(_dir).ForDay("SPY", Monday);
		var (oi, iv) = day["SPY260608C00600000"];
		Assert.Equal(1500, oi);
		Assert.Equal(0m, iv);
	}
}

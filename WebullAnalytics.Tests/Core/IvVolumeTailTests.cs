using Xunit;

namespace WebullAnalytics.Tests.Core;

// A live cascade read used to re-parse the whole growing data/iv day file (371 MB by 2026-11-20's close) every minute. The tail parses it once
// and then only what was appended — and must end up with exactly what a full parse of the file would give.
public class IvVolumeTailTests : IDisposable
{
	private const string Header = "date,time,source,expiry,strike,right,bid,ask,iv,oi,spot,volume\n";
	private static readonly DateTime Front = new(2026, 11, 20);
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"iv-tail-test-{Guid.NewGuid():N}.csv");

	public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

	private static string Row(string time, string source, string expiry, decimal strike, string right, long volume) =>
		$"2026-11-20,{time},{source},{expiry},{strike},{right},1.0,1.1,0.2,100,7100,{volume}\n";

	private IvVolumeTail Tail() => new(_path, "schwab", Front);

	[Fact]
	public void ReadsOnlyWhatWasAppended_AndMatchesAFullParse()
	{
		File.WriteAllText(_path, Header + Row("09:31:00", "schwab", "2026-11-20", 7100m, "C", 10));
		var tail = Tail();
		Assert.Single(tail.Read());

		File.AppendAllText(_path, Row("09:32:00", "schwab", "2026-11-20", 7100m, "C", 25) + Row("09:32:00", "schwab", "2026-11-20", 7105m, "P", 7));
		var series = tail.Read();

		Assert.Equal(2, series.Count);
		Assert.Equal(25, series[new TimeSpan(9, 32, 0)][(Front, 7100m, "C")]);
		var full = IvDumpStoreParse();
		Assert.Equal(full.Keys, series.Keys);
		foreach (var (ts, map) in full) Assert.Equal(map.OrderBy(kv => kv.Key.Strike), series[ts].OrderBy(kv => kv.Key.Strike));
	}

	[Fact]
	public void AHalfWrittenLine_IsPickedUpOnceItIsWhole()
	{
		File.WriteAllText(_path, Header + "2026-11-20,09:31:00,schwab,2026-11-20,7100,C,1.0,1.1,0.2,100,71");
		var tail = Tail();
		Assert.Empty(tail.Read());

		File.AppendAllText(_path, "00,42\n");
		Assert.Equal(42, Assert.Single(tail.Read()).Value[(Front, 7100m, "C")]);
	}

	[Fact]
	public void OtherExpiriesAndVendors_AreLeftOut()
	{
		File.WriteAllText(_path, Header
			+ Row("09:31:00", "webull", "2026-11-20", 7100m, "C", 5)
			+ Row("09:31:00", "schwab", "2026-11-21", 7100m, "C", 6)
			+ Row("09:31:00", "schwab", "2026-11-20", 7100m, "C", 7));
		var capture = Assert.Single(Tail().Read()).Value;
		Assert.Equal(7, Assert.Single(capture).Value);
	}

	[Fact]
	public void AFileThatShrank_IsReadAgainFromTheStart()
	{
		File.WriteAllText(_path, Header + Row("09:31:00", "schwab", "2026-11-20", 7100m, "C", 10) + Row("09:32:00", "schwab", "2026-11-20", 7100m, "C", 20));
		var tail = Tail();
		Assert.Equal(2, tail.Read().Count);

		File.WriteAllText(_path, Header + Row("09:40:00", "schwab", "2026-11-20", 7100m, "C", 3));
		var series = tail.Read();
		Assert.Equal(new TimeSpan(9, 40, 0), Assert.Single(series).Key);
	}

	private SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>> IvDumpStoreParse()
	{
		var series = new SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>>();
		foreach (var line in File.ReadAllLines(_path).Skip(1)) IvDumpStore.AddVolumeRow(line, "schwab", (exp, _) => exp == Front, series);
		return series;
	}
}

using System.Text.Json;
using WebullAnalytics.AI.Open.ZeroDte;
using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// The cascade backtest's reading cache: a stored session reads back exactly as computed, and only for the same build, grid and inputs.
public class CascadeReadingCacheTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"cascade-cache-{Guid.NewGuid():N}");
	private static readonly DateTime Day = new(2026, 11, 12);
	private static readonly TimeSpan[] Grid = [new(9, 40, 0), new(9, 50, 0)];

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	/// <summary>A full reading — every setup scored with factors and notes, a tape, VIX, flow and volume — as the screener builds it.</summary>
	private static CascadeReading Reading()
	{
		const decimal spot = 7596m;
		var terrain = new CascadeTerrain(new SqueezeTerrain(spot, spot + 30m, true, 7635m, 7565m, 70m), Day, 7600m, 7598m, 0.4m, 7610m, -0.22m, 1.5e9m, 0.1m, 20,
			OtmCallWall0Dte: 7650m, OtmPutWall0Dte: 7550m, CallWallMulti: 7700m, PutWallMulti: 7500m, FrontNetGexFraction: -0.3m, CallWallAtHigh0Dte: 7635m, PutWallAtLow0Dte: 7550m);
		var tape = new SessionTape(7610m, 7680m, spot, 7590m, -0.9m, GapDirection.Bear, VwapSide.Above, 6, 2, 7612m, 7550.84m, 7612m, 7550.84m, 1, 2, 0, 20, 10, 10);
		var inputs = new CascadeInputs(0.12m, new VolumePace(1.4m, new TimeSpan(9, 30, 0), new TimeSpan(9, 40, 0), VolumeBasis.SameWindowHistory, 20), -0.1m, Day.AddDays(-1), tape, 18.4m, 19.1m, 18.2m, 1.05m, 1.3m, 140m, 2.5m, 0.4m);
		return CascadeScreener.Evaluate(terrain, inputs);
	}

	private static string Json(object o) => JsonSerializer.Serialize(o, CascadeLog.Options);

	private CascadeReadingCache Cache(string build = "build-a", TimeSpan[]? grid = null, bool recache = false) => new("SPXW", grid ?? Grid, recache, _root, build);

	[Fact]
	public void AStoredSessionReadsBackExactlyAsComputed()
	{
		var moments = new[] { new CascadeMoment(Grid[0], Reading(), 7596.04m), new CascadeMoment(Grid[1], Reading(), 7601.5m) };
		Cache().Save(Day, "inputs-1", moments);

		var loaded = Cache().TryLoad(Day, "inputs-1");
		Assert.NotNull(loaded);
		Assert.Equal(Json(moments), Json(loaded!));
		// What the opener reads off it survives as typed values, not just as JSON.
		var r = loaded![0].Reading;
		Assert.Equal(CascadeRegime.BearTrap, r.Setups[CascadeRegime.BearTrap].Regime);
		Assert.Equal(7550m, r.Terrain.PutWallAtLow0Dte);
		Assert.Equal(18.2m, r.Inputs.VixNow);
		Assert.Equal(Grid[1], loaded[1].TimeEt);
	}

	[Fact]
	public void ANewBuildAGridOrChangedInputsMissTheCache()
	{
		Cache().Save(Day, "inputs-1", [new CascadeMoment(Grid[0], Reading(), 7596m)]);
		Assert.Null(Cache().TryLoad(Day, "inputs-2"));                           // the chain or tape file changed
		Assert.Null(Cache(build: "build-b").TryLoad(Day, "inputs-1"));          // recompiled
		Assert.Null(Cache(grid: [new(9, 40, 0)]).TryLoad(Day, "inputs-1"));     // another cadence
		Assert.Null(Cache(recache: true).TryLoad(Day, "inputs-1"));             // --recache
		Assert.Null(Cache().TryLoad(Day.AddDays(1), "inputs-1"));               // another session
		Assert.NotNull(Cache().TryLoad(Day, "inputs-1"));
	}

	[Fact]
	public void ATornFileIsRecomputedNotThrown()
	{
		Cache().Save(Day, "inputs-1", [new CascadeMoment(Grid[0], Reading(), 7596m)]);
		foreach (var f in Directory.GetFiles(_root, "*.json.gz", SearchOption.AllDirectories)) File.WriteAllBytes(f, [1, 2, 3]);
		Assert.Null(Cache().TryLoad(Day, "inputs-1"));
	}

	[Fact]
	public void OnlyTheNewestBuildsAreKept()
	{
		foreach (var build in new[] { "b1", "b2", "b3", "b4" })
		{
			Cache(build).Save(Day, "x", []);
			Thread.Sleep(20);
		}
		Assert.Equal(3, Directory.GetDirectories(Path.Combine(_root, "SPXW")).Length);
		Assert.Null(Cache("b1").TryLoad(Day, "x"));   // pruned (and recreated empty by this call)
		Assert.NotNull(Cache("b4").TryLoad(Day, "x"));
	}
}

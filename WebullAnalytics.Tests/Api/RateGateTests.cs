using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

// The gate spaces every local process's reads of one Webull endpoint as a token bucket: a burst of 2, then one call per 3 s.
public class RateGateTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rate-gate-{Guid.NewGuid():N}");
	private static readonly DateTime T0 = new(2026, 11, 16, 14, 30, 0, DateTimeKind.Utc);
	private const string Positions = "api.webull.com|key|/openapi/assets/positions";
	private const string Balance = "api.webull.com|key|/openapi/assets/balance";

	private RateGate Gate() => new(_dir, burst: 2, interval: TimeSpan.FromSeconds(3));

	/// <summary>Asks for a slot <paramref name="at"/> seconds after T0 and returns when it was given, in seconds after T0.</summary>
	private static double Slot(RateGate gate, double at, string key = Positions) => (gate.Reserve(key, T0.AddSeconds(at)) - T0).TotalSeconds;

	public void Dispose()
	{
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public void AnIdleBucketLetsTheBurstGoAtOnce_ThenOneCallPerInterval()
	{
		// Six asked at once: sent at 0, 0, 3, 6, 9, 12.
		var gate = Gate();
		Assert.Equal([0, 0, 3, 6, 9, 12], Enumerable.Range(0, 6).Select(_ => Slot(gate, 0)));
	}

	[Fact]
	public void OneCallASecondIsHeldToTheRefillRate()
	{
		// The pattern that drew a 429 on the third call in production: asked at 0, 1 and 2 s.
		var gate = Gate();
		Assert.Equal(0, Slot(gate, 0));
		Assert.Equal(1, Slot(gate, 1));     // the burst's second call
		Assert.Equal(3, Slot(gate, 2));     // the bucket is empty: waits for one call's worth to refill
		Assert.Equal(6, Slot(gate, 4.1));   // and the next waits a full interval after that
	}

	[Fact]
	public void TheBucketRefillsWhileIdle()
	{
		var gate = Gate();
		Slot(gate, 0);
		Slot(gate, 0);
		Assert.Equal(3, Slot(gate, 3));       // one call's worth is back after one interval
		Assert.Equal(6, Slot(gate, 3));       // but only one
		Assert.Equal(15, Slot(gate, 15));     // a watch tick later the bucket is full again:
		Assert.Equal(15, Slot(gate, 15));     // both burst calls go at once
		Assert.Equal(18, Slot(gate, 15));
	}

	[Fact]
	public void AWatchTickNeverWaits()
	{
		// One call per endpoint every 15 s, from two watch loops landing together.
		var gate = Gate();
		foreach (var tick in new[] { 0.0, 15, 30, 45 })
		{
			Assert.Equal(tick, Slot(gate, tick));
			Assert.Equal(tick, Slot(gate, tick));
		}
	}

	[Fact]
	public void EndpointsDoNotWaitOnEachOther()
	{
		var gate = Gate();
		Slot(gate, 0);
		Slot(gate, 0);
		Assert.Equal(0, Slot(gate, 0, Balance));
		Assert.Equal(0, Slot(gate, 0, Balance));
	}

	[Fact]
	public void SeparateProcessesShareTheCap()
	{
		// Two watch loops and a `wa trade`: three gate instances over one directory.
		Assert.Equal(0, Slot(Gate(), 0));
		Assert.Equal(0.2, Slot(Gate(), 0.2));
		Assert.Equal(3, Slot(Gate(), 0.5));
	}

	[Fact]
	public void ATornGateFileIsTreatedAsEmpty()
	{
		Directory.CreateDirectory(_dir);
		var gate = Gate();
		Slot(gate, 0);
		foreach (var f in Directory.GetFiles(_dir)) File.WriteAllText(f, "not-a-number\n\0\0\0garbage\n");
		Assert.Equal([0, 0, 3], Enumerable.Range(0, 3).Select(_ => Slot(gate, 0)));
	}

	[Fact]
	public void AGateFileHeldByAnotherProcessDoesNotBlockTheCall()
	{
		Directory.CreateDirectory(_dir);
		var gate = Gate();
		Slot(gate, 0);
		Slot(gate, 0);
		using (new FileStream(Directory.GetFiles(_dir).Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			Assert.Equal(0, Slot(gate, 0));   // could not take the file within its timeout: goes ungated
	}

	[Fact]
	public void ConcurrentCallersNeverExceedTheCap()
	{
		var gate = Gate();
		var slots = new System.Collections.Concurrent.ConcurrentBag<DateTime>();
		Parallel.For(0, 12, _ => slots.Add(gate.Reserve(Positions, T0)));
		var sorted = slots.OrderBy(t => t).ToList();
		Assert.Equal(12, sorted.Count);
		// The burst of two, then exactly one per interval: no lost or doubled reservation under contention.
		Assert.Equal([0, 0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30], sorted.Select(t => (t - T0).TotalSeconds));
	}
}

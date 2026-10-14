using WebullAnalytics.AI;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class OrderRepricerTests
{
	/// <summary>Scripted broker: GetOrderAsync returns the next queued status (repeating the last); replaces are recorded.</summary>
	private sealed class FakeGateway : IOrderGateway
	{
		public readonly Queue<string> Statuses = new();
		public readonly List<(string Id, int Qty, decimal Limit, IReadOnlyList<(string Id, int Quantity)>? Legs)> Replaces = new();
		public bool FailNextReplace;
		private string _last = "SUBMITTED";

		public Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct)
		{
			if (Statuses.Count > 0) _last = Statuses.Dequeue();
			var legs = new List<WebullOpenApiClient.OrderDetailLeg>
			{
				new("SPY", "SELL", "15", "PUT", "770", "2026-10-19", Id: "leg-short"),
				new("SPY", "BUY", "15", "PUT", "771", "2026-11-13", Id: "leg-long"),
			};
			var order = new WebullOpenApiClient.OrderDetailOrder(clientOrderId, "oid", _last, "SPY", "BUY", "15", "0", null, null, null, null, null, null, null, null, legs);
			return Task.FromResult(new WebullOpenApiClient.OrderDetail("NORMAL", null, [order]));
		}

		public Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct)
		{
			if (FailNextReplace) { FailNextReplace = false; throw new HttpRequestException("boom"); }
			Replaces.Add((clientOrderId, quantity, limitPrice, legs));
			return Task.CompletedTask;
		}
	}

	private static readonly DateTime T0 = new(2026, 10, 14, 12, 11, 0);
	private static readonly RepriceConfig Config = new() { Enabled = true, StepSeconds = 60, MaxTicks = 2 };

	private sealed class TempStore : IDisposable
	{
		public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reprice-{Guid.NewGuid():N}.jsonl");
		public RepriceStore Store => new(Path);
		public void Dispose()
		{
			File.Delete(Path);
			File.Delete(Path + ".lock");
		}
	}

	private static (OrderRepricer Repricer, FakeGateway Gateway, TempStore Store) Setup(string side = "BUY", int maxTicks = 2)
	{
		var store = new TempStore();
		var gw = new FakeGateway();
		var r = new OrderRepricer(new RepriceConfig { Enabled = true, StepSeconds = 60, MaxTicks = maxTicks }, gw, store.Store, "acct");
		var legs = TradeLegParser.Parse("sell:SPY261019P00770000:15,buy:SPY261113P00771000:15");
		r.Track(new OrderRequestBuilder.BuildParams("acct", legs, StrategyClassifier.Classify(legs)!, side, "LIMIT", 7.19m, "GTC"), "orig", "SPY", isOpen: true);
		return (r, gw, store);
	}

	private static void Tick(OrderRepricer r, double seconds) => r.TickAsync(T0.AddSeconds(seconds), default).Wait();

	[Fact]
	public void WaitsAStepIntervalBeforeTouchingTheOrder()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		Tick(r, 0);                 // first sighting starts the clock
		Tick(r, 30);
		Assert.Empty(gw.Replaces);
	}

	[Fact]
	public void BuyStepsOneTickUpInPlace_ListingEveryLegById()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		Tick(r, 0);
		Tick(r, 60);
		var (id, qty, limit, legs) = Assert.Single(gw.Replaces);
		Assert.Equal("orig", id);
		Assert.Equal(15, qty);
		Assert.Equal(7.20m, limit);
		Assert.Equal([("leg-short", 15), ("leg-long", 15)], legs!);
	}

	[Fact]
	public void SellStepsOneTickDown()
	{
		var (r, gw, tmp) = Setup(side: "SELL");
		using var _ = tmp;
		Tick(r, 0);
		Tick(r, 60);
		Assert.Equal(7.18m, gw.Replaces[0].Limit);
	}

	[Fact]
	public void StopsAtTheTickCapAndLeavesTheOrderResting()
	{
		var (r, gw, tmp) = Setup(maxTicks: 2);
		using var _ = tmp;
		Tick(r, 0);
		Tick(r, 60);                // 7.19 → 7.20
		Tick(r, 120);               // 7.20 → 7.21
		Tick(r, 180);               // at cap
		Assert.Equal([7.20m, 7.21m], gw.Replaces.Select(x => x.Limit));
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void AFilledOrderStopsBeingTracked()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		gw.Statuses.Enqueue("FILLED");
		Tick(r, 0);
		Tick(r, 60);
		Assert.Empty(gw.Replaces);
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void APartiallyFilledOrderIsLeftResting()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		gw.Statuses.Enqueue("PARTIALLY_FILLED");
		Tick(r, 0);
		Tick(r, 60);
		Assert.Empty(gw.Replaces);
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void ACancelledOrderStopsBeingTracked()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		gw.Statuses.Enqueue("CANCELLED");
		Tick(r, 0);
		Tick(r, 60);
		Assert.Empty(gw.Replaces);
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void AFailedReplaceKeepsTheLimitAndRetriesNextInterval()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		gw.FailNextReplace = true;
		Tick(r, 0);
		Tick(r, 60);                // replace throws: nothing recorded, still tracked
		Assert.Empty(gw.Replaces);
		Assert.Equal(1, r.TrackedCount);
		Tick(r, 90);                // within the interval of the failed attempt
		Assert.Empty(gw.Replaces);
		Tick(r, 120);               // retried from the unchanged limit
		Assert.Equal(7.20m, Assert.Single(gw.Replaces).Limit);
	}

	[Fact]
	public void ARestartedRepricerResumesFromTheStore()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		Tick(r, 0);
		Tick(r, 60);                // 7.19 → 7.20 by the first process
		var resumed = new OrderRepricer(Config, gw, tmp.Store, "acct");
		Assert.Equal(1, resumed.TrackedCount);
		Tick(resumed, 120);         // 7.20 → 7.21: continues from the persisted limit and step count
		Tick(resumed, 180);         // at cap — the original limit survived the restart
		Assert.Equal([7.20m, 7.21m], gw.Replaces.Select(x => x.Limit));
		Assert.Equal(0, resumed.TrackedCount);
	}

	[Fact]
	public void OrdersOnAnotherAccountAreNotTouched()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		var other = new OrderRepricer(Config, gw, tmp.Store, "other-acct");
		Assert.Equal(0, other.TrackedCount);
		Tick(other, 0);
		Tick(other, 60);
		Assert.Empty(gw.Replaces);
		Assert.Equal(1, r.TrackedCount);
	}

	[Fact]
	public void ATickSkipsWhileAnotherProcessHoldsTheLock()
	{
		var (r, gw, tmp) = Setup();
		using var _ = tmp;
		Tick(r, 0);
		using (tmp.Store.TryLock())
			Tick(r, 60);            // another process is mid-pass: this one leaves the order alone
		Assert.Empty(gw.Replaces);
		Tick(r, 61);
		Assert.Single(gw.Replaces);
	}
}

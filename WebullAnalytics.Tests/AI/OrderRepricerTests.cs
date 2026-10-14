using WebullAnalytics.AI;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class OrderRepricerTests
{
	/// <summary>Scripted broker: each GetOrderAsync returns the next queued (status, filled) for that client id, repeating the last.</summary>
	private sealed class FakeGateway : IOrderGateway
	{
		public readonly Dictionary<string, Queue<(string Status, int Filled)>> Script = new();
		public readonly List<string> Cancelled = new();
		public readonly List<OrderRequestBody> Placed = new();
		private (string, int) _last = ("WORKING", 0);

		public Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct)
		{
			if (Script.TryGetValue(clientOrderId, out var q) && q.Count > 0) _last = q.Dequeue();
			var (status, filled) = _last;
			var order = new WebullOpenApiClient.OrderDetailOrder(clientOrderId, null, status, "SPY", null, "15", filled.ToString(), null, null, null, null, null, null, null, null, null);
			return Task.FromResult(new WebullOpenApiClient.OrderDetail("NORMAL", null, [order]));
		}

		public Task CancelOrderAsync(string clientOrderId, CancellationToken ct) { Cancelled.Add(clientOrderId); return Task.CompletedTask; }

		public Task<string> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct)
		{
			Placed.Add(body);
			var id = $"new{Placed.Count}";
			Script[id] = new Queue<(string, int)>([("WORKING", 0)]);
			_last = ("WORKING", 0);
			return Task.FromResult(id);
		}
	}

	private static readonly DateTime T0 = new(2026, 10, 14, 12, 11, 0);
	private static RepriceConfig Cfg(int maxTicks = 2) => new() { Enabled = true, StepSeconds = 60, MaxTicks = maxTicks };
	private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

	private static OrderRequestBuilder.BuildParams Diagonal(string side = "BUY", decimal limit = 7.19m)
	{
		var legs = TradeLegParser.Parse("sell:SPY261019P00770000:15,buy:SPY261113P00771000:15");
		return new OrderRequestBuilder.BuildParams("acct", legs, StrategyClassifier.Classify(legs)!, side, "LIMIT", limit, "GTC", OrderRequestBuilder.DeriveOptionIntent(side, opening: true));
	}

	private static (OrderRepricer Repricer, FakeGateway Gateway) Setup(string side = "BUY", int maxTicks = 2)
	{
		var gw = new FakeGateway();
		var r = new OrderRepricer(Cfg(maxTicks), gw, brokerState: null, NoDelay);
		r.Track(Diagonal(side), "orig", "SPY", isOpen: true);
		return (r, gw);
	}

	private static decimal LimitOf(OrderRequestBody body) => decimal.Parse(body.NewOrders[0].LimitPrice!, System.Globalization.CultureInfo.InvariantCulture);
	private static int QtyOf(OrderRequestBody body) => body.NewOrders[0].Legs!.Select(l => int.Parse(l.Quantity!)).Min();

	[Fact]
	public void WaitsAStepIntervalBeforeTouchingTheOrder()
	{
		var (r, gw) = Setup();
		r.TickAsync(T0, default).Wait();                 // first sighting starts the clock
		r.TickAsync(T0.AddSeconds(30), default).Wait();
		Assert.Empty(gw.Cancelled);
		Assert.Empty(gw.Placed);
	}

	[Fact]
	public void BuyStepsOneTickUpAfterAConfirmedCancel()
	{
		var (r, gw) = Setup();
		gw.Script["orig"] = new Queue<(string, int)>([("WORKING", 0), ("CANCELLED", 0)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();
		Assert.Equal(["orig"], gw.Cancelled);
		Assert.Single(gw.Placed);
		Assert.Equal(7.20m, LimitOf(gw.Placed[0]));
		Assert.Equal(15, QtyOf(gw.Placed[0]));
	}

	[Fact]
	public void SellStepsOneTickDown()
	{
		var (r, gw) = Setup(side: "SELL");
		gw.Script["orig"] = new Queue<(string, int)>([("WORKING", 0), ("CANCELLED", 0)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();
		Assert.Equal(7.18m, LimitOf(gw.Placed[0]));
	}

	[Fact]
	public void AFillThatBeatsTheCancelPlacesNothing()
	{
		var (r, gw) = Setup();
		gw.Script["orig"] = new Queue<(string, int)>([("WORKING", 0), ("FILLED", 15)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();
		Assert.Single(gw.Cancelled);
		Assert.Empty(gw.Placed);
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void APartialFillDuringTheCancelReplacesOnlyTheRemainder()
	{
		var (r, gw) = Setup();
		gw.Script["orig"] = new Queue<(string, int)>([("WORKING", 0), ("CANCELLED", 5)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();
		Assert.Equal(10, QtyOf(gw.Placed[0]));
	}

	[Fact]
	public void AnOrderAlreadyPartiallyFilledIsLeftResting()
	{
		var (r, gw) = Setup();
		gw.Script["orig"] = new Queue<(string, int)>([("PARTIALLY_FILLED", 5)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();
		Assert.Empty(gw.Cancelled);
		Assert.Empty(gw.Placed);
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void AnUnconfirmedCancelPlacesNothingUntilTheBrokerConfirms()
	{
		var (r, gw) = Setup();
		gw.Script["orig"] = new Queue<(string, int)>([("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("WORKING", 0), ("CANCELLED", 0)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();     // cancel sent, 8 confirm polls all WORKING
		Assert.Empty(gw.Placed);
		Assert.Equal(1, r.TrackedCount);
		r.TickAsync(T0.AddSeconds(65), default).Wait();     // pending cancel re-checked immediately: now CANCELLED
		Assert.Single(gw.Cancelled);
		Assert.Single(gw.Placed);
	}

	[Fact]
	public void StopsAtTheTickCapAndLeavesTheOrderResting()
	{
		var (r, gw) = Setup(maxTicks: 2);
		gw.Script["orig"] = new Queue<(string, int)>([("WORKING", 0), ("CANCELLED", 0)]);
		r.TickAsync(T0, default).Wait();
		r.TickAsync(T0.AddSeconds(60), default).Wait();     // 7.19 → 7.20
		gw.Script["new1"] = new Queue<(string, int)>([("WORKING", 0), ("CANCELLED", 0)]);
		r.TickAsync(T0.AddSeconds(120), default).Wait();    // 7.20 → 7.21
		r.TickAsync(T0.AddSeconds(180), default).Wait();    // at cap: no third cancel
		Assert.Equal(2, gw.Placed.Count);
		Assert.Equal(7.21m, LimitOf(gw.Placed[1]));
		Assert.Equal(2, gw.Cancelled.Count);
		Assert.Equal(0, r.TrackedCount);
	}

	[Fact]
	public void ScaleLegsKeepsRatioStructuresInProportion()
	{
		var legs = TradeLegParser.Parse("buy:SPY261113P00771000:5,sell:SPY261019P00770000:10");
		Assert.Equal(5, OrderRepricer.ComboQuantity(legs));
		var scaled = OrderRepricer.ScaleLegs(legs, 2);
		Assert.Equal([2, 4], scaled.Select(l => l.Quantity));
	}
}

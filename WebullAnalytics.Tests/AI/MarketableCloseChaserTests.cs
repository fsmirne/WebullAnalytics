using WebullAnalytics.AI;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// A marketable close (a stop) left resting after the market moved through it is re-priced in place to the current market, only
// ever toward a fill and with no cap.
public class MarketableCloseChaserTests
{
	private sealed class FakeGateway : IOrderGateway
	{
		public string Status = "SUBMITTED";
		public string Side = "SELL";
		public string? Limit = "10.00";
		public string Quantity = "3";
		public readonly List<(string Id, int Qty, decimal Limit, IReadOnlyList<(string Id, int Quantity)>? Legs)> Replaces = new();
		public readonly List<string> Cancels = new();

		public Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct)
		{
			if (Status == "MISSING") return Task.FromResult(new WebullOpenApiClient.OrderDetail("NORMAL", null, []));
			var legs = new List<WebullOpenApiClient.OrderDetailLeg> { new("SPXW", Side, Quantity, "CALL", "7600", "2026-11-12", Id: "leg-1") };
			var order = new WebullOpenApiClient.OrderDetailOrder(clientOrderId, "oid", Status, "SPXW", Side, Quantity, "0", null, Limit, null, null, null, null, null, null, legs);
			return Task.FromResult(new WebullOpenApiClient.OrderDetail("NORMAL", null, [order]));
		}

		public Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal? limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct, decimal? stopPrice = null)
		{
			Replaces.Add((clientOrderId, quantity, limitPrice!.Value, legs));
			return Task.CompletedTask;
		}

		public Task CancelOrderAsync(string clientOrderId, CancellationToken ct)
		{
			Cancels.Add(clientOrderId);
			return Task.CompletedTask;
		}

		public Task<WebullOpenApiClient.PlaceResult> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct) => throw new NotSupportedException();
	}

	private static MarketableCloseChaser.Outcome Chase(FakeGateway gw, string side, decimal limit) =>
		new MarketableCloseChaser(gw).ChaseAsync("stop-1", side, limit, default).Result.Outcome;

	[Fact]
	public void ASellRestingAboveTheMarketIsMovedDownToIt_HoweverFar()
	{
		var gw = new FakeGateway { Limit = "10.00" };
		Assert.Equal(MarketableCloseChaser.Outcome.Repriced, Chase(gw, "SELL", 6.50m));   // 35 ticks in one move: no cap
		var r = Assert.Single(gw.Replaces);
		Assert.Equal(("stop-1", 3, 6.50m), (r.Id, r.Qty, r.Limit));
		Assert.Equal([("leg-1", 3)], r.Legs);
	}

	[Fact]
	public void ABuyRestingBelowTheMarketIsMovedUpToIt()
	{
		var gw = new FakeGateway { Side = "BUY", Limit = "1.20" };
		Assert.Equal(MarketableCloseChaser.Outcome.Repriced, Chase(gw, "BUY", 2.30m));
		Assert.Equal(2.30m, Assert.Single(gw.Replaces).Limit);
	}

	[Fact]
	public void AnOrderAlreadyAtOrThroughTheMarketIsLeftAlone()
	{
		var sell = new FakeGateway { Limit = "10.00" };
		Assert.Equal(MarketableCloseChaser.Outcome.Resting, Chase(sell, "SELL", 10.00m));
		Assert.Equal(MarketableCloseChaser.Outcome.Resting, Chase(sell, "SELL", 10.40m));   // the bid came back: never raise a stop's sell
		var buy = new FakeGateway { Side = "BUY", Limit = "2.30" };
		Assert.Equal(MarketableCloseChaser.Outcome.Resting, Chase(buy, "BUY", 1.90m));
		Assert.Empty(sell.Replaces);
		Assert.Empty(buy.Replaces);
	}

	[Fact]
	public void AFilledOrderIsReportedAndNotTouched()
	{
		var gw = new FakeGateway { Status = "FILLED" };
		Assert.Equal(MarketableCloseChaser.Outcome.Filled, Chase(gw, "SELL", 6.50m));
		Assert.Empty(gw.Replaces);
	}

	[Fact]
	public void AnOrderThatEndedUnfilledIsGone()
	{
		foreach (var status in new[] { "CANCELLED", "REJECTED", "EXPIRED", "MISSING" })
			Assert.Equal(MarketableCloseChaser.Outcome.Gone, Chase(new FakeGateway { Status = status }, "SELL", 6.50m));
	}

	[Fact]
	public void APartiallyFilledOrderIsNeitherRepricedNorCancelled()
	{
		var gw = new FakeGateway { Status = "PARTIALLY_FILLED" };
		Assert.Equal(MarketableCloseChaser.Outcome.PartiallyFilled, Chase(gw, "SELL", 6.50m));
		Assert.Empty(gw.Replaces);
		Assert.Empty(gw.Cancels);
	}

	[Fact]
	public void ACloseWhoseSideFlippedIsLeftAlone()
	{
		var gw = new FakeGateway { Side = "SELL", Limit = "0.10" };
		Assert.Equal(MarketableCloseChaser.Outcome.Resting, Chase(gw, "BUY", 0.05m));
		Assert.Empty(gw.Replaces);
	}

	[Fact]
	public void AnUnreadableQuantitySendsNothing()
	{
		var gw = new FakeGateway { Quantity = "" };
		Assert.Equal(MarketableCloseChaser.Outcome.Resting, Chase(gw, "SELL", 6.50m));
		Assert.Empty(gw.Replaces);
	}
}

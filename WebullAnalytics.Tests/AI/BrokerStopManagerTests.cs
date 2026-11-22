using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// The live trailing stop: a stop order at the broker that is placed once a position exists and raised as the peak rises. The prices come from the same
// reading the close rule uses; these tests pin the order it asks Webull for, and that it only ever touches a stop it placed.
public class BrokerStopManagerTests
{
	private const string Long = "SPY261211C00731000";
	private const string Short = "SPY261123C00732000";
	private static readonly DateTime Now = new(2026, 11, 18, 11, 0, 0);

	private sealed class FakeGateway : IOrderGateway
	{
		public readonly List<OrderRequestBody> Placed = new();
		public readonly List<(string Id, int Qty, decimal? Limit, decimal? Stop)> Replaced = new();
		public readonly List<string> Cancelled = new();
		public bool RejectGtc;

		public Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct) => throw new NotSupportedException();

		public Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal? limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct, decimal? stopPrice = null)
		{
			Replaced.Add((clientOrderId, quantity, limitPrice, stopPrice));
			return Task.CompletedTask;
		}

		public Task CancelOrderAsync(string clientOrderId, CancellationToken ct) { Cancelled.Add(clientOrderId); return Task.CompletedTask; }

		public Task<WebullOpenApiClient.PlaceResult> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct)
		{
			if (RejectGtc && body.NewOrders[0].TimeInForce == "GTC") throw new WebullOpenApiException("INVALID", "GTC not allowed", 400);
			Placed.Add(body);
			return Task.FromResult(new WebullOpenApiClient.PlaceResult(body.NewOrders[0].ClientOrderId, null, null, "oid-" + Placed.Count));
		}
	}

	private static OpenPosition Diagonal(int qty = 7, decimal debit = 3.00m) => new(
		Key: "SPY_LongDiagonal_731", Ticker: "SPY", StrategyKind: "LongDiagonal",
		Legs: new[] { new PositionLeg(Short, Side.Sell, 732m, new DateTime(2026, 11, 23), "C", qty), new PositionLeg(Long, Side.Buy, 731m, new DateTime(2026, 12, 11), "C", qty) },
		InitialNetDebit: debit, AdjustedNetDebit: debit, Quantity: qty, MaxLossPerShare: debit);

	private static OptionContractQuote Q(string symbol, decimal bid, decimal ask) => new(symbol, null, bid, ask, null, null, 100, 1000, 0.20m);

	/// <summary>Closing value 5.00 − 2.00 = 3.00 = the entry, so the first peak is 3.00 and the trail (20%) sits at 2.40.</summary>
	private static EvaluationContext Ctx(OpenPosition p, decimal longBid = 5.00m, decimal shortAsk = 2.00m) => new(
		Now: Now,
		OpenPositions: new Dictionary<string, OpenPosition> { [p.Key] = p },
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = 731m },
		Quotes: new[] { Q(Long, longBid, longBid + 0.10m), Q(Short, shortAsk - 0.10m, shortAsk) }.ToDictionary(q => q.ContractSymbol),
		AccountCash: 0m, AccountValue: 0m, TechnicalSignals: new Dictionary<string, TechnicalBias>());

	private static WebullOpenApiClient.OpenOrder WorkingStop(string clientId, string orderType, decimal stop, decimal? limit, int qty, string? side = "SELL")
	{
		var legs = new List<WebullOpenApiClient.OrderDetailLeg>
		{
			new("SPY", "SELL", qty.ToString(), "CALL", "731", "2026-12-11", Id: "leg-long"),
			new("SPY", "BUY", qty.ToString(), "CALL", "732", "2026-11-23", Id: "leg-short"),
		};
		var order = new WebullOpenApiClient.OrderDetailOrder(clientId, "oid", "SUBMITTED", "SPY", side, qty.ToString(), "0", null, limit?.ToString("0.00"), null, null, null, null, "SELL_TO_CLOSE", "DIAGONAL", legs, orderType, stop.ToString("0.00"));
		return new WebullOpenApiClient.OpenOrder(clientId, "NORMAL", "combo", [order]);
	}

	private sealed class Rig
	{
		public readonly FakeGateway Gateway = new();
		public readonly BrokerStopStore Store = new();
		public readonly TrailingStopConfig Config = new() { Enabled = true, TrailPct = 0.20m, StopLimitPct = 0.05m };
		public readonly BrokerStateService Broker = new(new TradeAccount { AccountId = "acct" });
		public readonly BrokerStopManager Manager;

		/// <summary>The tick token the broker snapshot stands for, so the manager's refresh reuses it instead of calling Webull.</summary>
		public readonly object Tick = new();

		public Rig(bool submit = true, params WebullOpenApiClient.OpenOrder[] brokerOrders)
		{
			Broker.LoadSnapshot([], brokerOrders, null, Tick);
			Manager = new BrokerStopManager(Config, new TrailingStopStore(), Store, Gateway, Broker, "acct", submit);
		}
	}

	// ─── Price planning ───────────────────────────────────────────────────────

	[Fact]
	public void Plan_PutsTheLimitStopLimitPctUnderTheStop_RoundedToTheTick()
	{
		var combo = BrokerStopManager.Plan(2.40m, legCount: 2, "SPY", 0.05m, stopMarket: false)!.Value;
		Assert.Equal(2.40m, combo.Stop);
		Assert.Equal(2.28m, combo.Limit);   // 2.40 − 5% = 2.28; a SPY combo ticks a cent

		var single = BrokerStopManager.Plan(2.40m, legCount: 1, "SPY", 0.05m, stopMarket: false)!.Value;
		Assert.Equal(2.40m, single.Stop);
		Assert.Equal(2.30m, single.Limit);  // a single option ticks 0.05: 2.28 rounds to 2.30
	}

	[Fact]
	public void Plan_LimitIsAtLeastOneTickUnderTheStop_WhenThePercentIsSmallerThanATick()
	{
		var plan = BrokerStopManager.Plan(0.10m, legCount: 2, "SPY", 0.05m, stopMarket: false)!.Value;   // 5% of 0.10 = half a cent, a tick is a cent
		Assert.Equal(0.10m, plan.Stop);
		Assert.Equal(0.09m, plan.Limit);
		var zeroPct = BrokerStopManager.Plan(2.00m, legCount: 2, "SPY", 0m, stopMarket: false)!.Value;
		Assert.Equal(1.99m, zeroPct.Limit);
	}

	[Fact]
	public void Plan_StopMarketHasNoLimit_AndANearZeroTriggerHasNoPlan()
	{
		Assert.Null(BrokerStopManager.Plan(2.40m, 1, "SPY", 0.05m, stopMarket: true)!.Value.Limit);
		Assert.Null(BrokerStopManager.Plan(0.04m, 1, "SPY", 0.05m, stopMarket: false));
	}

	[Fact]
	public void EntryStop_IsTrailPctUnderTheEntryOfALongSingle_AndOnlyThen()
	{
		var rig = new Rig();
		Assert.Equal(4.00m, rig.Manager.EntryStop("SPY", "LongCall", 1, "BUY", 5.00m));   // 5.00 × 0.80
		Assert.Null(rig.Manager.EntryStop("SPY", "LongCall", 2, "BUY", 5.00m));           // a spread has no bracket
		Assert.Null(rig.Manager.EntryStop("SPY", "ShortCall", 1, "SELL", 5.00m));         // a credit has none either
		rig.Config.Exclude.Add("LongCall");
		Assert.Null(rig.Manager.EntryStop("SPY", "LongCall", 1, "BUY", 5.00m));
		rig.Config.Exclude.Clear();
		rig.Config.BracketSingles = false;
		Assert.Null(rig.Manager.EntryStop("SPY", "LongCall", 1, "BUY", 5.00m));
	}

	[Fact]
	public void EntryStop_IsOff_WhenTheStopArmsOnlyAtAProfit()
	{
		// A bracket would put a stop 20% under the entry from the first second — exactly what an activation threshold rules out.
		var rig = new Rig();
		rig.Config.ActivateAtProfitPct = 0.50m;
		Assert.Null(rig.Manager.EntryStop("SPY", "LongCall", 1, "BUY", 5.00m));
	}

	[Fact]
	public async Task WithAnActivationThreshold_NoStopIsPlacedUntilThePeakArmsIt()
	{
		var rig = new Rig();
		rig.Config.ActivateAtProfitPct = 0.50m;
		var position = Diagonal(debit: 3.00m);

		await rig.Manager.SyncAsync(Ctx(position, longBid: 5.00m), rig.Tick, TestContext.Current.CancellationToken);   // closing value 3.00: flat
		await rig.Manager.SyncAsync(Ctx(position, longBid: 6.40m), rig.Tick, TestContext.Current.CancellationToken);   // 4.40: +47%
		Assert.Empty(rig.Gateway.Placed);

		await rig.Manager.SyncAsync(Ctx(position, longBid: 6.50m), rig.Tick, TestContext.Current.CancellationToken);   // 4.50: +50%, armed
		var order = Assert.Single(Assert.Single(rig.Gateway.Placed).NewOrders);
		Assert.Equal("STOP_LOSS_LIMIT", order.OrderType);
		Assert.Equal("3.6", order.StopPrice);   // the trail from the 4.50 peak: 4.50 − 20% × 4.50
	}

	[Fact]
	public void EntryStop_IsOffInADryRun()
	{
		Assert.Null(new Rig(submit: false).Manager.EntryStop("SPY", "LongCall", 1, "BUY", 5.00m));
	}

	// ─── Sync ─────────────────────────────────────────────────────────────────

	[Fact]
	public async Task APositionWithNoStop_GetsAClosingStopLimitAtTheTrailLevel()
	{
		var rig = new Rig();
		var position = Diagonal();
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);

		var order = Assert.Single(Assert.Single(rig.Gateway.Placed).NewOrders);
		Assert.Equal("STOP_LOSS_LIMIT", order.OrderType);
		Assert.Equal("2.4", order.StopPrice);
		Assert.Equal("2.28", order.LimitPrice);
		Assert.Equal("SELL", order.Side);
		Assert.Equal("SELL_TO_CLOSE", order.PositionIntent);
		Assert.Equal("GTC", order.TimeInForce);
		Assert.Equal("7", order.Quantity);
		Assert.Equal(2, order.Legs!.Count);
		Assert.True(rig.Manager.IsProtected(position));
		Assert.True(rig.Store.Owns("acct", order.ClientOrderId));
	}

	[Fact]
	public async Task AStopJustPlacedIsNotPlacedAgainWhileTheBrokerListIsStillBehind()
	{
		var rig = new Rig();
		var position = Diagonal();
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);   // the broker's order list has not caught up yet
		Assert.Single(rig.Gateway.Placed);
	}

	[Fact]
	public async Task IfGtcIsRefused_TheStopIsPlacedForTheDay()
	{
		var rig = new Rig();
		rig.Gateway.RejectGtc = true;
		var position = Diagonal();
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);
		Assert.Equal("DAY", Assert.Single(rig.Gateway.Placed).NewOrders[0].TimeInForce);
		Assert.True(rig.Manager.IsProtected(position));
	}

	[Fact]
	public async Task OurStopBelowTheTrailLevel_IsRaisedInPlace_LimitFollowing()
	{
		var stop = WorkingStop("stop-1", "STOP_LOSS_LIMIT", 2.00m, 1.90m, qty: 7);
		var rig = new Rig(true, stop);
		rig.Store.Add("acct", "stop-1", BrokerStateService.Fingerprint([(Long, "sell"), (Short, "buy")]), Now.AddDays(-1));
		await rig.Manager.SyncAsync(Ctx(Diagonal()), rig.Tick, TestContext.Current.CancellationToken);

		var replaced = Assert.Single(rig.Gateway.Replaced);
		Assert.Equal(("stop-1", 7, (decimal?)2.28m, (decimal?)2.40m), replaced);
		Assert.Empty(rig.Gateway.Placed);
	}

	[Fact]
	public async Task OurStopAtOrAboveTheTrailLevel_IsNeverLowered()
	{
		var stop = WorkingStop("stop-1", "STOP_LOSS_LIMIT", 2.90m, 2.75m, qty: 7);
		var rig = new Rig(true, stop);
		rig.Store.Add("acct", "stop-1", BrokerStateService.Fingerprint([(Long, "sell"), (Short, "buy")]), Now.AddDays(-1));
		var position = Diagonal();
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);

		Assert.Empty(rig.Gateway.Replaced);
		Assert.Empty(rig.Gateway.Placed);
		Assert.True(rig.Manager.IsProtected(position));
	}

	[Fact]
	public async Task AStopWhoseQuantityNoLongerMatchesThePosition_IsResizedWithoutLoweringIt()
	{
		var stop = WorkingStop("stop-1", "STOP_LOSS_LIMIT", 2.90m, 2.75m, qty: 7);
		var rig = new Rig(true, stop);
		rig.Store.Add("acct", "stop-1", BrokerStateService.Fingerprint([(Long, "sell"), (Short, "buy")]), Now.AddDays(-1));
		await rig.Manager.SyncAsync(Ctx(Diagonal(qty: 4)), rig.Tick, TestContext.Current.CancellationToken);

		Assert.Equal(("stop-1", 4, (decimal?)2.75m, (decimal?)2.90m), Assert.Single(rig.Gateway.Replaced));
	}

	[Fact]
	public async Task ABracketStopMarketChild_IsRaisedWithNoLimit()
	{
		var stop = WorkingStop("child-1", "STOP_LOSS", 2.00m, null, qty: 7);
		var rig = new Rig(true, stop);
		rig.Store.Add("acct", "child-1", BrokerStateService.Fingerprint([(Long, "sell"), (Short, "buy")]), Now.AddDays(-1));
		await rig.Manager.SyncAsync(Ctx(Diagonal()), rig.Tick, TestContext.Current.CancellationToken);

		Assert.Equal(("child-1", 7, (decimal?)null, (decimal?)2.40m), Assert.Single(rig.Gateway.Replaced));
	}

	[Theory]
	[InlineData("STOP_LOSS_LIMIT")]
	[InlineData("TRAILING_STOP_LOSS")]
	public async Task AStopTheUserPlacedByHand_IsLeftAloneAndCountsAsProtection(string orderType)
	{
		var rig = new Rig(true, WorkingStop("app-stop", orderType, 2.00m, orderType == "STOP_LOSS_LIMIT" ? 1.90m : null, qty: 7));
		var position = Diagonal();
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);

		Assert.Empty(rig.Gateway.Placed);
		Assert.Empty(rig.Gateway.Replaced);
		Assert.True(rig.Manager.IsProtected(position));
	}

	[Fact]
	public async Task ADryRun_PlacesNothingAndProtectsNothing()
	{
		var rig = new Rig(submit: false);
		var position = Diagonal();
		await rig.Manager.SyncAsync(Ctx(position), rig.Tick, TestContext.Current.CancellationToken);
		Assert.Empty(rig.Gateway.Placed);
		Assert.False(rig.Manager.IsProtected(position));
	}

	[Fact]
	public async Task ACreditStructure_IsNotHandled_SoTheCloseRuleKeepsIt()
	{
		var rig = new Rig();
		var credit = Diagonal(debit: -0.90m);
		await rig.Manager.SyncAsync(Ctx(credit), rig.Tick, TestContext.Current.CancellationToken);
		Assert.Empty(rig.Gateway.Placed);
		Assert.False(rig.Manager.IsProtected(credit));
	}

	[Fact]
	public async Task CancelFor_CancelsOnlyStopsWePlaced()
	{
		var rig = new Rig(true, WorkingStop("ours", "STOP_LOSS_LIMIT", 2.00m, 1.90m, qty: 7), WorkingStop("app-stop", "STOP_LOSS_LIMIT", 1.00m, 0.90m, qty: 7));
		rig.Store.Add("acct", "ours", BrokerStateService.Fingerprint([(Long, "sell"), (Short, "buy")]), Now.AddDays(-1));
		var cancelled = await rig.Manager.CancelForAsync([(Long, "sell"), (Short, "buy")], TestContext.Current.CancellationToken);

		Assert.Equal(1, cancelled);
		Assert.Equal(["ours"], rig.Gateway.Cancelled);
		Assert.False(rig.Store.Owns("acct", "ours"));
	}

	[Theory]
	[InlineData("STOP_LOSS_LIMIT")]
	[InlineData("TRAILING_STOP_LOSS")]
	public void ARestingStop_DoesNotNetAgainstTheDaysOpensOrBlockACloseOfTheSameLegs(string orderType)
	{
		var rig = new Rig(true, WorkingStop("ours", orderType, 2.00m, orderType == "STOP_LOSS_LIMIT" ? 1.90m : null, qty: 7));
		Assert.False(rig.Broker.HasPendingMatching([(Long, "sell"), (Short, "buy")]));
		Assert.Single(rig.Broker.FindPendingMatching([(Long, "sell"), (Short, "buy")]));
	}

	// ─── The close rule defers to a broker stop ───────────────────────────────

	[Fact]
	public void TheCloseRule_StandsDownOnAProtectedPosition_AndStillClosesAnUnprotectedOne()
	{
		var config = new TrailingStopConfig { Enabled = true, TrailPct = 0.20m };
		var position = Diagonal();
		var store = new TrailingStopStore();
		new TrailingStopRule(config, store).Evaluate(position, Ctx(position));                // peak 3.00
		var crashed = Ctx(position, longBid: 4.40m);                                                // closing value 2.40 − … below the 2.40 trail
		Assert.NotNull(new TrailingStopRule(config, store).Evaluate(position, crashed));
		Assert.Null(new TrailingStopRule(config, store, brokerProtected: _ => true).Evaluate(position, crashed));
	}
}

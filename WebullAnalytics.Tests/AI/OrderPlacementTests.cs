using WebullAnalytics.AI;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// Guards the lost-answer duplicate: an order Webull accepted but whose answer never came back (a timeout, a dropped connection) used to
// leave no trace, so the next run re-sent it while Webull's history still lagged. OrderPlacement records the order before sending and
// settles it by what came back.
public class OrderPlacementTests : IDisposable
{
	private static readonly (string Symbol, string Action)[] Legs = [("SPY260630C00734000", "buy"), ("SPY260616C00730000", "sell")];
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"wa-placement-test-{Guid.NewGuid():N}.jsonl");
	private readonly LocalOrderLedger _ledger;
	private readonly BrokerStateService _broker;

	public OrderPlacementTests()
	{
		_ledger = new LocalOrderLedger(_path);
		_broker = new BrokerStateService(new TradeAccount { AccountId = "acct" }, _ledger);
		_broker.LoadSnapshot([], [], _ledger.ReadFor(MarketTime.TodayEt()));
	}

	public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

	private static OrderRequestBody Body(string clientOrderId = "cid-1") => new() { NewOrders = [new NewOrder { ClientOrderId = clientOrderId }] };

	private Task<WebullOpenApiClient.PlaceResult> Place(Func<CancellationToken, Task<WebullOpenApiClient.PlaceResult>> send) =>
		OrderPlacement.PlaceAsync(send, Body(), _broker, "SPY", Legs, isOpen: true, TestContext.Current.CancellationToken);

	private static Task<WebullOpenApiClient.PlaceResult> Throw(Exception ex) => Task.FromException<WebullOpenApiClient.PlaceResult>(ex);

	private LocalOrderLedger.Entry? Recorded() => _ledger.ReadFor(MarketTime.TodayEt()).SingleOrDefault();

	[Fact]
	public async Task Success_RecordsPlaced_AndBlocksAResend()
	{
		await Place(_ => Task.FromResult(new WebullOpenApiClient.PlaceResult("cid-1", null, null, "oid-1")));

		var entry = Assert.IsType<LocalOrderLedger.Entry>(Recorded());
		Assert.Equal(LocalOrderLedger.Placed, entry.State);
		Assert.True(_broker.HasNetOpenMatching(Legs));
		Assert.False(_broker.HasUnconfirmedMatching(Legs));
		Assert.Equal(1, _broker.TodaysActiveOrderCount("SPY"));
	}

	[Fact]
	public async Task Timeout_IsOutcomeUnknown_AndTheOrderStaysRecorded()
	{
		// The live hazard: Webull took the order, the answer timed out. Nothing may re-send it.
		var ex = await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => Place(_ => Throw(new TaskCanceledException("timed out"))));

		Assert.Equal("cid-1", ex.ClientOrderId);
		Assert.True(Assert.IsType<LocalOrderLedger.Entry>(Recorded()).Unconfirmed);
		Assert.True(_broker.HasNetOpenMatching(Legs));
		Assert.True(_broker.HasUnconfirmedMatching(Legs));
		Assert.Equal(1, _broker.TodaysActiveOrderCount("SPY"));
	}

	[Fact]
	public async Task UnknownOutcome_SurvivesARestart()
	{
		await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => Place(_ => Throw(new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset"))));

		var restarted = new BrokerStateService(new TradeAccount { AccountId = "acct" }, _ledger);
		restarted.LoadSnapshot([], [], _ledger.ReadFor(MarketTime.TodayEt()));
		Assert.True(restarted.HasNetOpenMatching(Legs));
		Assert.True(restarted.HasUnconfirmedMatching(Legs));
	}

	[Fact]
	public async Task ServerError_IsOutcomeUnknown()
	{
		// A 5xx can come after the order was taken, so it proves nothing.
		await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => Place(_ => Throw(new WebullOpenApiException(null, "unavailable", 502))));
		Assert.True(_broker.HasNetOpenMatching(Legs));
	}

	[Fact]
	public async Task BrokerRefusal_VoidsTheOrder_SoItCanBeRetried()
	{
		await Assert.ThrowsAsync<WebullOpenApiException>(() => Place(_ => Throw(new WebullOpenApiException("OAUTH_OPENAPI_PRICE", "bad price", 400))));

		Assert.Null(Recorded());
		Assert.False(_broker.HasNetOpenMatching(Legs));
		Assert.False(_broker.HasPendingMatching(Legs));
		Assert.Equal(0, _broker.TodaysActiveOrderCount("SPY"));
	}

	[Fact]
	public async Task NoConnection_VoidsTheOrder()
	{
		await Assert.ThrowsAsync<HttpRequestException>(() => Place(_ => Throw(new HttpRequestException(HttpRequestError.ConnectionError, "network unreachable"))));

		Assert.Null(Recorded());
		Assert.False(_broker.HasNetOpenMatching(Legs));
	}

	[Fact]
	public async Task CallerCancellation_IsRethrown_AndLeavesTheOrderRecorded()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(() => OrderPlacement.PlaceAsync(ct => Throw(new OperationCanceledException(ct)), Body(), _broker, "SPY", Legs, isOpen: true, cts.Token));
		Assert.True(Assert.IsType<LocalOrderLedger.Entry>(Recorded()).Unconfirmed);
	}

	[Fact]
	public async Task BrokerAnswersWithAnotherId_TheOrderIsReKeyed()
	{
		await Place(_ => Task.FromResult(new WebullOpenApiClient.PlaceResult("cid-broker", null, null, "oid-1")));

		var entry = Assert.IsType<LocalOrderLedger.Entry>(Recorded());
		Assert.Equal("cid-broker", entry.ClientOrderId);
		Assert.Equal(LocalOrderLedger.Placed, entry.State);
	}

	[Fact]
	public void OrderNotFound_MatchesWebullsAnswerForAnUnknownId()
	{
		// Webull's order detail for a client_order_id it has no order for (observed 2026-11-21).
		Assert.True(new WebullOpenApiException("OPENAPI_PARAM_ERR", "Parameter error, Order not present.", 400).IsOrderNotFound);
		Assert.False(new WebullOpenApiException("OPENAPI_PARAM_ERR", "Parameter error, account_id is required.", 400).IsOrderNotFound);
		Assert.False(new WebullOpenApiException(null, "Webull API unavailable (HTTP 503)", 503).IsOrderNotFound);
	}

	[Fact]
	public void UnconfirmedOrder_IsVoidedOnlyAfterTheGrace()
	{
		var sent = new DateTime(2026, 11, 23, 10, 0, 0);
		Assert.False(BrokerStateService.PastNotFoundGrace(sent, sent.AddSeconds(30)));
		Assert.True(BrokerStateService.PastNotFoundGrace(sent, sent + BrokerStateService.UnconfirmedNotFoundGrace));
		Assert.False(BrokerStateService.PastNotFoundGrace(null, sent.AddHours(1)));
	}

	[Fact]
	public void DefinitelyNotPlaced_OnlyForRefusalsAndUnsentRequests()
	{
		Assert.True(OrderPlacement.DefinitelyNotPlaced(new WebullOpenApiException(null, "rate limited", 429)));
		Assert.True(OrderPlacement.DefinitelyNotPlaced(new HttpRequestException(HttpRequestError.NameResolutionError, "dns")));
		Assert.True(OrderPlacement.DefinitelyNotPlaced(new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")));
		Assert.False(OrderPlacement.DefinitelyNotPlaced(new WebullOpenApiException(null, "Empty response body", 200)));
		Assert.False(OrderPlacement.DefinitelyNotPlaced(new HttpRequestException(HttpRequestError.ResponseEnded, "reset")));
		Assert.False(OrderPlacement.DefinitelyNotPlaced(new TaskCanceledException()));
	}
}

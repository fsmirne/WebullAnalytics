using WebullAnalytics.Api;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>An order was sent but no definite answer came back — a timeout, a dropped connection, a 5xx, an unreadable reply — so Webull may
/// have accepted it. It stays recorded as placed (see <see cref="LocalOrderLedger.Sending"/>) until the broker reports it or a lookup settles it.</summary>
internal sealed class OrderOutcomeUnknownException(string clientOrderId, Exception inner)
	: Exception($"no answer from Webull ({inner.Message}) — the order may have been placed; it is held as placed until the broker reports it (check: wa trade status {clientOrderId})", inner)
{
	public string ClientOrderId { get; } = clientOrderId;
}

/// <summary>The one way an order is sent: recorded in the broker-state ledger before the send, then settled by what came back. A lost answer
/// must not look like a failed order, or the next tick sends the same order again while the first one may be filling (a duplicate open).</summary>
internal static class OrderPlacement
{
	/// <summary>Sends <paramref name="body"/> through <paramref name="send"/>. With a <paramref name="broker"/>, the order is recorded as sent
	/// first, marked placed on success and voided when it definitely never became an order (rethrown as is). Any other failure is rethrown as
	/// <see cref="OrderOutcomeUnknownException"/> with the order left recorded, so nothing re-sends it. A cancellation of the caller's own
	/// <paramref name="ct"/> is rethrown as is, and also leaves the order recorded.</summary>
	public static async Task<WebullOpenApiClient.PlaceResult> PlaceAsync(Func<CancellationToken, Task<WebullOpenApiClient.PlaceResult>> send, OrderRequestBody body, BrokerStateService? broker, string ticker, IEnumerable<(string Symbol, string Action)> legs, bool isOpen, CancellationToken ct)
	{
		var clientOrderId = body.NewOrders[0].ClientOrderId;
		var pending = broker?.RecordSending(ticker, legs, clientOrderId, isOpen);
		WebullOpenApiClient.PlaceResult placed;
		try { placed = await send(ct); }
		catch (Exception ex) when (DefinitelyNotPlaced(ex))
		{
			if (pending != null) broker!.RecordVoid(pending);
			throw;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
		catch (Exception ex) { throw new OrderOutcomeUnknownException(clientOrderId, ex); }
		if (pending != null) broker!.RecordPlaced(pending, placed.ClientOrderId);
		return placed;
	}

	/// <summary>True when the failure proves no order exists: Webull answered with a 4xx (it read the request and refused it), or the request
	/// never left this machine (the name did not resolve, or no connection or TLS session could be set up). A 5xx, a timeout or a connection
	/// lost mid-request proves nothing — the order may have been taken before the answer was lost.</summary>
	internal static bool DefinitelyNotPlaced(Exception ex) => ex switch
	{
		WebullOpenApiException w => w.HttpStatus is >= 400 and < 500,
		HttpRequestException h => h.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError,
		_ => false,
	};
}

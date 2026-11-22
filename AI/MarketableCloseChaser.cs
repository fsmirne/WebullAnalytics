using WebullAnalytics.Api;

namespace WebullAnalytics.AI;

/// <summary>
/// Keeps a marketable close (a stop: <see cref="ManagementProposal.Marketable"/>) at the market. Such a close is placed at the price
/// that trades now; if the market moves through it before it fills, a limit left where it was placed never fills. Each tick the
/// stop still fires, <see cref="ChaseAsync"/> moves the resting order in place to the current marketable price — only ever in the
/// direction that fills sooner (a sell down, a buy up), with no cap on how far.
///
/// A partially filled order is left resting: its remaining quantity is the broker's to report, and re-sending a close sized from a
/// position count that may not show the fill yet could sell more than is held.
/// </summary>
internal sealed class MarketableCloseChaser(IOrderGateway gateway)
{
	/// <summary><c>Gone</c> = the order ended unfilled (cancelled, rejected, missing), so the position still needs a close.</summary>
	internal enum Outcome { Repriced, Resting, PartiallyFilled, Filled, Gone }

	/// <param name="side">The side the close is now priced on ("SELL" / "BUY").</param>
	/// <param name="limit">The current marketable limit, already rounded to the exchange tick.</param>
	public async Task<(Outcome Outcome, decimal? RestingLimit)> ChaseAsync(string clientOrderId, string side, decimal limit, CancellationToken ct)
	{
		WebullOpenApiClient.OrderDetailOrder? order;
		// Webull answers a lookup of an id it has no order for with an error, not an empty list: that order is missing, so the position still
		// needs a close. Read as a failure instead, a close whose send went unanswered and never reached Webull would block its own replacement.
		try { order = (await gateway.GetOrderAsync(clientOrderId, ct)).Orders?.FirstOrDefault(); }
		catch (WebullOpenApiException ex) when (ex.IsOrderNotFound) { return (Outcome.Gone, null); }
		var resting = BrokerOrder.Number(order?.LimitPrice);
		switch (BrokerOrder.State(order?.Status))
		{
			case OrderState.Filled: return (Outcome.Filled, resting);
			case OrderState.PartiallyFilled: return (Outcome.PartiallyFilled, resting);
			case OrderState.Ended: return (Outcome.Gone, resting);
		}
		// A close whose net flipped side since it was placed (a spread gone worthless) cannot be compared by price: leave it.
		if (!string.Equals(order!.Side, side, StringComparison.OrdinalIgnoreCase)) return (Outcome.Resting, resting);
		var selling = string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase);
		if (resting is decimal at && (selling ? limit >= at : limit <= at)) return (Outcome.Resting, resting);
		return await BrokerOrder.ReplaceLimitAsync(gateway, order, clientOrderId, limit, ct) ? (Outcome.Repriced, resting) : (Outcome.Resting, resting);
	}
}

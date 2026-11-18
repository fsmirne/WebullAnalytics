using Spectre.Console;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>The broker calls the repricer needs, behind an interface so its stepping logic is testable offline.</summary>
internal interface IOrderGateway
{
	Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct);
	Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal? limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct, decimal? stopPrice = null);
	Task CancelOrderAsync(string clientOrderId, CancellationToken ct);
	Task<WebullOpenApiClient.PlaceResult> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct);
}

internal sealed class WebullOrderGateway(TradeAccount account) : IOrderGateway
{
	public async Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		return await client.GetOrderAsync(clientOrderId, ct);
	}

	public async Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal? limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct, decimal? stopPrice = null)
	{
		using var client = new WebullOpenApiClient(account);
		await client.ReplaceOrderAsync(clientOrderId, quantity, limitPrice, legs, stopPrice, ct);
	}

	public async Task<WebullOpenApiClient.PlaceResult> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		return await client.PlaceOrderAsync(body, ct);
	}

	public async Task CancelOrderAsync(string clientOrderId, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		await client.CancelOrderAsync(clientOrderId, ct);
	}
}

/// <summary>What the broker says an order is doing.</summary>
internal enum OrderState { Working, PartiallyFilled, Filled, Ended }

/// <summary>Reads and in-place edits of one broker order, shared by <see cref="OrderRepricer"/> and <see cref="MarketableCloseChaser"/>.</summary>
internal static class BrokerOrder
{
	internal static OrderState State(string? status)
	{
		var s = status?.ToUpperInvariant() ?? "";
		if (s.StartsWith("FILLED", StringComparison.Ordinal)) return OrderState.Filled;
		if (s.StartsWith("PARTIAL", StringComparison.Ordinal)) return OrderState.PartiallyFilled;
		if (s.StartsWith("CANCEL", StringComparison.Ordinal) || s.StartsWith("REJECT", StringComparison.Ordinal) || s.StartsWith("FAIL", StringComparison.Ordinal) || s.StartsWith("EXPIRE", StringComparison.Ordinal) || s.Length == 0)
			return OrderState.Ended;
		return OrderState.Working;
	}

	/// <summary>True for a stop order type (<c>STOP_LOSS</c> / <c>STOP_LOSS_LIMIT</c>), which rests until its trigger and so is not yet a close.</summary>
	internal static bool IsStop(string? orderType) => orderType != null && orderType.StartsWith("STOP_LOSS", StringComparison.OrdinalIgnoreCase);

	internal static decimal? Number(string? raw) =>
		decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

	/// <summary>Moves a working order's limit in place (Webull's order replace: same order id, no cancel, so there is never a moment with
	/// nothing working). False when the order's quantity cannot be read, in which case nothing is sent.</summary>
	internal static async Task<bool> ReplaceLimitAsync(IOrderGateway gateway, WebullOpenApiClient.OrderDetailOrder order, string clientOrderId, decimal limit, CancellationToken ct)
	{
		if (Number(order.TotalQuantity) is not decimal total || total <= 0m) return false;
		var quantity = (int)Math.Round(total);
		// An option replace must list every leg by its broker id with its quantity (unchanged here); equities have no legs.
		var legs = order.Legs?.Where(l => !string.IsNullOrEmpty(l.Id)).Select(l => (l.Id!, Number(l.Quantity) is decimal q ? (int)Math.Round(q) : quantity)).ToList();
		await gateway.ReplaceOrderAsync(clientOrderId, quantity, limit, legs is { Count: > 0 } ? legs : null, ct);
		return true;
	}
}

/// <summary>
/// Walks a resting auto-execute limit toward the market so live fills match the backtest's fill model (mid + one
/// cent of slippage per share, in the decision minute). Auto-execute prices every order at the pure net mid; a combo
/// book a cent wide can leave it resting for an hour (2026-10-14: a DC2 open filled 100 minutes after placement),
/// which is the entry delay the DC2 entry-time sweep priced at −17%…−30% of P&amp;L.
///
/// Each watch tick, an order resting at least <see cref="RepriceConfig.StepSeconds"/> since its last step is modified in
/// place (Webull's order replace — same order id, no cancel, so there is never a moment with nothing working and no race
/// against a fill) one exchange tick more aggressive (BUY up, SELL down), never beyond <see cref="RepriceConfig.MaxTicks"/>
/// ticks from the original limit — the scorer priced the trade at that mid, so the repricer never chases a market that
/// moved away. A partially filled order is left resting. Tracking lives in <see cref="RepriceStore"/>, so a restarted
/// watch loop resumes where the last one stopped, and any watch loop on the account steps orders placed by another
/// process (e.g. `wa ai scan --submit`).
///
/// With <see cref="RepriceConfig.CancelSeconds"/> set, an OPENING order still working that long after the repricer first saw it is
/// cancelled: an entry on a signal that is stale within minutes must not fill later at a price the market came back to. Until then
/// an order at its tick cap stays tracked. The deadline is stamped on the order when it is placed, so a loop running another
/// strategy's config neither cancels an order that has none nor drops one that has. Closing orders are never cancelled.
/// </summary>
internal sealed class OrderRepricer
{
	private readonly RepriceConfig _config;
	private readonly IOrderGateway _gateway;
	private readonly RepriceStore _store;
	private readonly string _accountId;
	/// <summary>Event sinks of orders this process placed; orders resumed from the store report to the console only.</summary>
	private readonly Dictionary<string, Action<string, string>> _onEvent = new();

	public OrderRepricer(RepriceConfig config, IOrderGateway gateway, RepriceStore store, string accountId)
	{
		_config = config;
		_gateway = gateway;
		_store = store;
		_accountId = accountId;
	}

	public bool Enabled => _config.Enabled && (_config.MaxTicks > 0 || _config.CancelSeconds > 0);

	public int TrackedCount => _store.Active(_accountId).Count;

	/// <summary>Registers a just-placed live limit order. <paramref name="onEvent"/> receives (outcome, detail) for every step.</summary>
	public void Track(OrderRequestBuilder.BuildParams build, string clientOrderId, string ticker, bool isOpen, Action<string, string>? onEvent = null)
	{
		if (!Enabled || build.OrderType != "LIMIT" || build.LimitPrice is not decimal limit) return;
		_store.Append(new RepriceStore.State(_accountId, clientOrderId, ticker, build.Side, build.Legs.Count, isOpen, limit, limit, Steps: 0, LastStepAt: null, CancelSeconds: isOpen ? _config.CancelSeconds : 0));
		if (onEvent != null) _onEvent[clientOrderId] = onEvent;
	}

	public async Task TickAsync(DateTime now, CancellationToken ct)
	{
		using var held = _store.TryLock();
		if (held == null) return;
		foreach (var t in _store.Active(_accountId))
		{
			if (t.LastStepAt is not DateTime last) { _store.Append(t with { LastStepAt = now, FirstSeenAt = now }); continue; }
			if ((now - last).TotalSeconds < _config.StepSeconds) continue;
			try
			{
				_store.Append(await StepAsync(t, now, ct));
			}
			catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException)
			{
				// A transient failure, or a replace rejected because the order just filled / ended: keep tracking — the next
				// tick re-reads broker truth before acting again.
				_store.Append(t with { LastStepAt = now });
				Report(t, "reprice error", ex.Message);
			}
		}
	}

	/// <summary>One step for one order. Returns its next state (<c>Done</c> when it should no longer be tracked).</summary>
	private async Task<RepriceStore.State> StepAsync(RepriceStore.State t, DateTime now, CancellationToken ct)
	{
		var done = t with { Done = true };
		var order = (await _gateway.GetOrderAsync(t.ClientOrderId, ct)).Orders?.FirstOrDefault();
		switch (BrokerOrder.State(order?.Status))
		{
			case OrderState.Filled: Report(t, "filled", $"@ ≤ ${t.CurrentLimit:F2} after {t.Steps} step(s)"); return done;
			case OrderState.PartiallyFilled: Report(t, "left resting", "partially filled — not repricing the remainder"); return done;
			case OrderState.Ended: Report(t, "ended", $"order {order?.Status ?? "missing"} at the broker"); return done;
		}
		// The deadline is the order's own, set by the process that placed it: several watch loops with different configs step one store.
		var cancelArmed = t.CancelSeconds > 0 && t.FirstSeenAt.HasValue;
		if (cancelArmed && (now - t.FirstSeenAt!.Value).TotalSeconds >= t.CancelSeconds)
		{
			await _gateway.CancelOrderAsync(t.ClientOrderId, ct);
			Report(t, "cancelled", $"unfilled at ${t.CurrentLimit:F2} {(now - t.FirstSeenAt.Value).TotalSeconds:F0}s after placement (cancelSeconds {t.CancelSeconds})");
			return done;
		}
		if (t.Steps >= _config.MaxTicks)
		{
			if (cancelArmed) return t with { LastStepAt = now };   // keep watching until it fills or its cancel time comes
			Report(t, "at cap", $"resting at ${t.CurrentLimit:F2} ({t.Steps} tick(s) from ${t.OriginalLimit:F2})");
			return done;
		}

		var buying = string.Equals(t.Side, "BUY", StringComparison.OrdinalIgnoreCase);
		var tick = OptionPriceRounding.Tick(t.CurrentLimit, t.LegCount, t.Ticker);
		var next = OptionPriceRounding.RoundToTick(t.CurrentLimit + (buying ? tick : -tick), t.LegCount, t.Ticker);
		if (next <= 0m) next = OptionPriceRounding.MinTick(t.LegCount, t.Ticker);
		if (!await BrokerOrder.ReplaceLimitAsync(_gateway, order!, t.ClientOrderId, next, ct)) { Report(t, "ended", "could not read the order quantity — not repricing"); return done; }
		Report(t, "repriced", $"${t.CurrentLimit:F2} → ${next:F2} (step {t.Steps + 1}/{_config.MaxTicks})");
		return t with { CurrentLimit = next, Steps = t.Steps + 1, LastStepAt = now };
	}

	private void Report(RepriceStore.State t, string outcome, string detail)
	{
		var color = outcome is "repriced" or "filled" ? "green" : "yellow";
		AnsiConsole.MarkupLine($"[{color}]repricer {Markup.Escape(outcome)}:[/] {Markup.Escape(t.Ticker)} {(t.IsOpen ? "open" : "close")} {Markup.Escape(t.Side)} — {Markup.Escape(detail)}");
		if (_onEvent.TryGetValue(t.ClientOrderId, out var onEvent)) onEvent(outcome, detail);
	}
}

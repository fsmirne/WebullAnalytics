using Spectre.Console;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>The broker calls the repricer needs, behind an interface so its stepping logic is testable offline.</summary>
internal interface IOrderGateway
{
	Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct);
	Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct);
}

internal sealed class WebullOrderGateway(TradeAccount account) : IOrderGateway
{
	public async Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		return await client.GetOrderAsync(clientOrderId, ct);
	}

	public async Task ReplaceOrderAsync(string clientOrderId, int quantity, decimal limitPrice, IReadOnlyList<(string Id, int Quantity)>? legs, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		await client.ReplaceOrderAsync(clientOrderId, quantity, limitPrice, legs, ct);
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
/// moved away. A partially filled order is left resting. Tracking is in-memory: orders from before a restart are not
/// repriced.
/// </summary>
internal sealed class OrderRepricer
{
	private sealed class Tracked
	{
		public required string ClientOrderId;
		public required string Ticker;
		public required string Side;
		public required int LegCount;
		public required bool IsOpen;
		public required decimal OriginalLimit;
		public required decimal CurrentLimit;
		/// <summary>Null until the first tick that sees the order — every timestamp comes from the watch loop's clock.</summary>
		public DateTime? LastStepAt;
		public int Steps;
		public Action<string, string>? OnEvent;
	}

	private enum OrderState { Working, PartiallyFilled, Filled, Ended }

	private readonly RepriceConfig _config;
	private readonly IOrderGateway _gateway;
	private readonly List<Tracked> _orders = new();

	public OrderRepricer(RepriceConfig config, IOrderGateway gateway)
	{
		_config = config;
		_gateway = gateway;
	}

	public bool Enabled => _config.Enabled && _config.MaxTicks > 0;

	public int TrackedCount => _orders.Count;

	/// <summary>Registers a just-placed live limit order. <paramref name="onEvent"/> receives (outcome, detail) for every step.</summary>
	public void Track(OrderRequestBuilder.BuildParams build, string clientOrderId, string ticker, bool isOpen, Action<string, string>? onEvent = null)
	{
		if (!Enabled || build.OrderType != "LIMIT" || build.LimitPrice is not decimal limit) return;
		_orders.Add(new Tracked { ClientOrderId = clientOrderId, Ticker = ticker, Side = build.Side, LegCount = build.Legs.Count, IsOpen = isOpen, OriginalLimit = limit, CurrentLimit = limit, OnEvent = onEvent });
	}

	public async Task TickAsync(DateTime now, CancellationToken ct)
	{
		foreach (var t in _orders.ToList())
		{
			t.LastStepAt ??= now;
			if ((now - t.LastStepAt.Value).TotalSeconds < _config.StepSeconds) continue;
			try
			{
				if (!await StepAsync(t, now, ct)) _orders.Remove(t);
			}
			catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException)
			{
				// A transient failure, or a replace rejected because the order just filled / ended: keep tracking — the next
				// tick re-reads broker truth before acting again.
				t.LastStepAt = now;
				Report(t, "reprice error", ex.Message);
			}
		}
	}

	/// <summary>One step for one order. Returns false when the order should no longer be tracked.</summary>
	private async Task<bool> StepAsync(Tracked t, DateTime now, CancellationToken ct)
	{
		var order = (await _gateway.GetOrderAsync(t.ClientOrderId, ct)).Orders?.FirstOrDefault();
		switch (Read(order?.Status))
		{
			case OrderState.Filled: Report(t, "filled", $"@ ≤ ${t.CurrentLimit:F2} after {t.Steps} step(s)"); return false;
			case OrderState.PartiallyFilled: Report(t, "left resting", "partially filled — not repricing the remainder"); return false;
			case OrderState.Ended: Report(t, "ended", $"order {order?.Status ?? "missing"} at the broker"); return false;
		}
		if (t.Steps >= _config.MaxTicks) { Report(t, "at cap", $"resting at ${t.CurrentLimit:F2} ({t.Steps} tick(s) from ${t.OriginalLimit:F2})"); return false; }
		if (ParseQty(order?.TotalQuantity) is not int quantity || quantity <= 0) { Report(t, "ended", "could not read the order quantity — not repricing"); return false; }

		var buying = string.Equals(t.Side, "BUY", StringComparison.OrdinalIgnoreCase);
		var tick = OptionPriceRounding.Tick(t.CurrentLimit, t.LegCount, t.Ticker);
		var next = OptionPriceRounding.RoundToTick(t.CurrentLimit + (buying ? tick : -tick), t.LegCount, t.Ticker);
		if (next <= 0m) next = OptionPriceRounding.MinTick(t.LegCount, t.Ticker);
		// An option replace must list every leg by its broker id with its quantity (unchanged here); equities have no legs.
		var legs = order!.Legs?.Where(l => !string.IsNullOrEmpty(l.Id)).Select(l => (l.Id!, ParseQty(l.Quantity) ?? quantity)).ToList();

		await _gateway.ReplaceOrderAsync(t.ClientOrderId, quantity, next, legs is { Count: > 0 } ? legs : null, ct);
		Report(t, "repriced", $"${t.CurrentLimit:F2} → ${next:F2} (step {t.Steps + 1}/{_config.MaxTicks})");
		t.CurrentLimit = next;
		t.Steps++;
		t.LastStepAt = now;
		return true;
	}

	private static OrderState Read(string? status)
	{
		var s = status?.ToUpperInvariant() ?? "";
		if (s.StartsWith("FILLED", StringComparison.Ordinal)) return OrderState.Filled;
		if (s.StartsWith("PARTIAL", StringComparison.Ordinal)) return OrderState.PartiallyFilled;
		if (s.StartsWith("CANCEL", StringComparison.Ordinal) || s.StartsWith("REJECT", StringComparison.Ordinal) || s.StartsWith("FAIL", StringComparison.Ordinal) || s.StartsWith("EXPIRE", StringComparison.Ordinal) || s.Length == 0)
			return OrderState.Ended;
		return OrderState.Working;
	}

	private static int? ParseQty(string? raw) =>
		decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var q) ? (int)Math.Round(q) : null;

	private static void Report(Tracked t, string outcome, string detail)
	{
		var color = outcome is "repriced" or "filled" ? "green" : "yellow";
		AnsiConsole.MarkupLine($"[{color}]repricer {Markup.Escape(outcome)}:[/] {Markup.Escape(t.Ticker)} {(t.IsOpen ? "open" : "close")} {Markup.Escape(t.Side)} — {Markup.Escape(detail)}");
		t.OnEvent?.Invoke(outcome, detail);
	}
}

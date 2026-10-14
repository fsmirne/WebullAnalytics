using Spectre.Console;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>The broker calls the repricer needs, behind an interface so its state machine is testable offline.</summary>
internal interface IOrderGateway
{
	Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct);
	Task CancelOrderAsync(string clientOrderId, CancellationToken ct);
	/// <summary>Places the order and returns its client order id.</summary>
	Task<string> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct);
}

internal sealed class WebullOrderGateway(TradeAccount account) : IOrderGateway
{
	public async Task<WebullOpenApiClient.OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		return await client.GetOrderAsync(clientOrderId, ct);
	}

	public async Task CancelOrderAsync(string clientOrderId, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		await client.CancelOrderAsync(clientOrderId, ct);
	}

	public async Task<string> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct)
	{
		using var client = new WebullOpenApiClient(account);
		var placed = await client.PlaceOrderAsync(body, ct);
		return placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId;
	}
}

/// <summary>
/// Walks a resting auto-execute limit toward the market so live fills match the backtest's fill model (mid + one
/// cent of slippage per share, in the decision minute). Auto-execute prices every order at the pure net mid; a combo
/// book a cent wide can leave it resting for an hour (2026-10-14: a DC2 open filled 100 minutes after placement),
/// which is the entry delay the DC2 entry-time sweep priced at −17%…−30% of P&amp;L.
///
/// Each watch tick, an order resting at least <see cref="RepriceConfig.StepSeconds"/> since its last step is moved one
/// exchange tick more aggressive (BUY up, SELL down), never beyond <see cref="RepriceConfig.MaxTicks"/> ticks from the
/// original limit — the scorer priced the trade at that mid, so the repricer never chases a market that moved away.
/// The OpenAPI client has no verified combo replace, so a step is cancel → confirm → re-place: nothing new is placed
/// until the broker confirms the cancel, a fill that beats the cancel ends tracking, and a partial fill during the
/// cancel re-places only the remainder. An order already partially filled before a step is left resting untouched.
/// Tracking is in-memory: orders from before a restart are not repriced.
/// </summary>
internal sealed class OrderRepricer
{
	private sealed class Tracked
	{
		public required OrderRequestBuilder.BuildParams Build;
		public required string Ticker;
		public required bool IsOpen;
		public required string ClientOrderId;
		public required decimal OriginalLimit;
		public required decimal CurrentLimit;
		/// <summary>Null until the first tick that sees the order — every timestamp comes from the watch loop's clock.</summary>
		public DateTime? LastStepAt;
		public int Steps;
		public bool CancelPending;
		public Action<string, string>? OnEvent;
	}

	private enum OrderState { Working, PartiallyFilled, Filled, Cancelled, Ended }

	private const int CancelConfirmPolls = 8;
	private static readonly TimeSpan CancelConfirmInterval = TimeSpan.FromMilliseconds(750);

	private readonly RepriceConfig _config;
	private readonly IOrderGateway _gateway;
	private readonly BrokerStateService? _brokerState;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly List<Tracked> _orders = new();

	public OrderRepricer(RepriceConfig config, IOrderGateway gateway, BrokerStateService? brokerState, Func<TimeSpan, CancellationToken, Task>? delay = null)
	{
		_config = config;
		_gateway = gateway;
		_brokerState = brokerState;
		_delay = delay ?? Task.Delay;
	}

	public bool Enabled => _config.Enabled && _config.MaxTicks > 0;

	public int TrackedCount => _orders.Count;

	/// <summary>Registers a just-placed live limit order. <paramref name="onEvent"/> receives (outcome, detail) for every step.</summary>
	public void Track(OrderRequestBuilder.BuildParams build, string clientOrderId, string ticker, bool isOpen, Action<string, string>? onEvent = null)
	{
		if (!Enabled || build.OrderType != "LIMIT" || build.LimitPrice is not decimal limit) return;
		_orders.Add(new Tracked { Build = build, Ticker = ticker, IsOpen = isOpen, ClientOrderId = clientOrderId, OriginalLimit = limit, CurrentLimit = limit, OnEvent = onEvent });
	}

	public async Task TickAsync(DateTime now, CancellationToken ct)
	{
		foreach (var t in _orders.ToList())
		{
			t.LastStepAt ??= now;
			if (!t.CancelPending && (now - t.LastStepAt.Value).TotalSeconds < _config.StepSeconds) continue;
			try
			{
				if (!await StepAsync(t, now, ct)) _orders.Remove(t);
			}
			catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException)
			{
				// Transient read/cancel failure: keep tracking; the next tick re-reads broker truth before acting.
				Report(t, "reprice error", ex.Message);
			}
		}
	}

	/// <summary>One step for one order. Returns false when the order should no longer be tracked.</summary>
	private async Task<bool> StepAsync(Tracked t, DateTime now, CancellationToken ct)
	{
		var (state, remaining) = Read(await _gateway.GetOrderAsync(t.ClientOrderId, ct), t);
		if (!t.CancelPending)
		{
			switch (state)
			{
				case OrderState.Filled: Report(t, "filled", $"@ ≤ ${t.CurrentLimit:F2} after {t.Steps} step(s)"); return false;
				case OrderState.PartiallyFilled: Report(t, "left resting", "partially filled — not repricing the remainder"); return false;
				case OrderState.Cancelled or OrderState.Ended: Report(t, "ended", "order no longer working at the broker"); return false;
			}
			if (t.Steps >= _config.MaxTicks) { Report(t, "at cap", $"resting at ${t.CurrentLimit:F2} ({t.Steps} tick(s) from ${t.OriginalLimit:F2})"); return false; }

			try { await _gateway.CancelOrderAsync(t.ClientOrderId, ct); }
			catch (WebullOpenApiException) { /* e.g. it filled first — the confirm read below decides */ }
			t.CancelPending = true;
			(state, remaining) = await ConfirmCancelAsync(t, ct);
		}

		switch (state)
		{
			case OrderState.Filled: Report(t, "filled", $"before the cancel landed @ ≤ ${t.CurrentLimit:F2}"); return false;
			case OrderState.Ended: Report(t, "ended", "order ended without a cancel confirmation"); return false;
			case OrderState.Working or OrderState.PartiallyFilled:
				t.LastStepAt = now;
				Report(t, "cancel unconfirmed", "will re-check next tick before placing anything");
				return true;
		}

		// Confirmed cancelled: re-place whatever didn't fill, one tick more aggressive.
		t.CancelPending = false;
		if (remaining <= 0) { Report(t, "filled", "fully filled during the cancel"); return false; }
		var buying = string.Equals(t.Build.Side, "BUY", StringComparison.OrdinalIgnoreCase);
		var legCount = t.Build.Legs.Count;
		var tick = OptionPriceRounding.Tick(t.CurrentLimit, legCount, t.Ticker);
		var next = OptionPriceRounding.RoundToTick(t.CurrentLimit + (buying ? tick : -tick), legCount, t.Ticker);
		if (next <= 0m) next = OptionPriceRounding.MinTick(legCount, t.Ticker);
		var build = t.Build with { Legs = ScaleLegs(t.Build.Legs, remaining), LimitPrice = next };
		try
		{
			var body = OrderRequestBuilder.Build(build);
			var clientOrderId = await _gateway.PlaceOrderAsync(body, ct);
			_brokerState?.RecordLocalPlacement(t.Ticker, build.Legs.Select(l => (l.Symbol, l.Action == LegAction.Buy ? "buy" : "sell")), clientOrderId, t.IsOpen);
			Report(t, "repriced", $"${t.CurrentLimit:F2} → ${next:F2} x{remaining} (step {t.Steps + 1}/{_config.MaxTicks})");
			t.Build = build;
			t.ClientOrderId = clientOrderId;
			t.CurrentLimit = next;
			t.Steps++;
			t.LastStepAt = now;
			return true;
		}
		catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException or InvalidOperationException)
		{
			Report(t, "failed", $"order CANCELLED but the replacement was rejected — nothing is working for this trade: {ex.Message}");
			return false;
		}
	}

	private async Task<(OrderState State, int Remaining)> ConfirmCancelAsync(Tracked t, CancellationToken ct)
	{
		var result = (State: OrderState.Working, Remaining: 0);
		for (var i = 0; i < CancelConfirmPolls; i++)
		{
			await _delay(CancelConfirmInterval, ct);
			result = Read(await _gateway.GetOrderAsync(t.ClientOrderId, ct), t);
			if (result.State is OrderState.Cancelled or OrderState.Filled or OrderState.Ended) break;
		}
		return result;
	}

	/// <summary>Broker state of the order and the combo quantity that did NOT fill.</summary>
	private static (OrderState State, int Remaining) Read(WebullOpenApiClient.OrderDetail detail, Tracked t)
	{
		var order = detail.Orders?.FirstOrDefault();
		var status = order?.Status?.ToUpperInvariant() ?? "";
		var total = ParseQty(order?.TotalQuantity) ?? ComboQuantity(t.Build.Legs);
		var filled = ParseQty(order?.FilledQuantity) ?? 0;
		var state = status switch
		{
			_ when status.StartsWith("FILLED", StringComparison.Ordinal) => OrderState.Filled,
			_ when status.StartsWith("PARTIAL", StringComparison.Ordinal) => OrderState.PartiallyFilled,
			_ when status.StartsWith("CANCEL", StringComparison.Ordinal) => OrderState.Cancelled,
			_ when status.StartsWith("REJECT", StringComparison.Ordinal) || status.StartsWith("FAIL", StringComparison.Ordinal) || status.StartsWith("EXPIRE", StringComparison.Ordinal) => OrderState.Ended,
			_ => OrderState.Working,
		};
		return (state, Math.Max(0, total - filled));
	}

	private static int? ParseQty(string? raw) =>
		decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var q) ? (int)Math.Round(q) : null;

	/// <summary>Combo quantity = GCD of the leg quantities (a 15-lot diagonal is two 15-qty legs; a 1x2 ratio of 5 is 5/10).</summary>
	internal static int ComboQuantity(IReadOnlyList<ParsedLeg> legs) => legs.Select(l => l.Quantity).Aggregate(Gcd);

	internal static IReadOnlyList<ParsedLeg> ScaleLegs(IReadOnlyList<ParsedLeg> legs, int comboQuantity)
	{
		var unit = ComboQuantity(legs);
		return legs.Select(l => l with { Quantity = l.Quantity / unit * comboQuantity }).ToList();
	}

	private static int Gcd(int a, int b) => b == 0 ? Math.Abs(a) : Gcd(b, a % b);

	private static void Report(Tracked t, string outcome, string detail)
	{
		var color = outcome is "repriced" or "filled" ? "green" : outcome is "failed" ? "red bold" : "yellow";
		AnsiConsole.MarkupLine($"[{color}]repricer {Markup.Escape(outcome)}:[/] {Markup.Escape(t.Ticker)} {(t.IsOpen ? "open" : "close")} {Markup.Escape(t.Build.Side)} — {Markup.Escape(detail)}");
		t.OnEvent?.Invoke(outcome, detail);
	}
}

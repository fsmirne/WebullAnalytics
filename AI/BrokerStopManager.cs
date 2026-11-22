using Spectre.Console;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>What a broker stop order should look like: the trigger, and the limit under it (null for a stop-market).</summary>
internal readonly record struct StopPlan(decimal Stop, decimal? Limit);

/// <summary>
/// Live trailing stop, carried by Webull instead of the watch loop. Webull's API has no trailing stop for options, but it does have
/// <c>STOP_LOSS</c> / <c>STOP_LOSS_LIMIT</c> orders whose stop price can be changed in place, so the stop is an ordinary order at the broker that
/// this class raises as <see cref="TrailingStopRule"/>'s peak rises. The broker triggers it, so a fast move in a single option no longer waits for
/// the next tick; the tick only moves the stop up.
///
/// <list type="bullet">
/// <item>A long single option's entry goes out as a bracket (<see cref="EntryStop"/>): the stop is attached to the entry and activates on its fill, so
/// the position is never open without one. Webull documents that only for single options, rejects a stop-limit child, and rejected every spread bracket
/// tried live, so the child is a stop-market.</item>
/// <item>Anything else (every spread, or a single when <c>bracketSingles</c> is off) gets a closing stop-limit once it shows up as a position.</item>
/// <item>Each tick <see cref="SyncAsync"/> measures every debit position with the same reading the close rule uses and raises its stop to the trail level;
/// it never lowers one. It also resizes a stop whose quantity no longer matches the position (a partial close, an add).</item>
/// <item>It only touches stops it placed (<see cref="BrokerStopStore"/>). A stop the user placed by hand is left alone, and the position counts as guarded.</item>
/// </list>
/// A credit structure is not handled here (its stop is a buy above the market, which this does not place): <see cref="TrailingStopRule"/> still closes it.
/// A closing order from any other rule must cancel the stop first (<see cref="CancelForAsync"/>), or a stop sized for the whole position could fire on the
/// remainder.
/// </summary>
internal sealed class BrokerStopManager
{
	/// <summary>How long a stop just placed is assumed to be live though the broker's order list does not show it yet.</summary>
	private static readonly TimeSpan ListingLag = TimeSpan.FromMinutes(3);

	private readonly TrailingStopConfig _config;
	private readonly TrailingStopStore _peaks;
	private readonly BrokerStopStore _ours;
	private readonly IOrderGateway _gateway;
	private readonly BrokerStateService _brokerState;
	private readonly string _accountId;
	private readonly bool _submit;
	private readonly HashSet<string> _protected = new(StringComparer.Ordinal);
	/// <summary>The stop last announced per position, so a dry run (and a stop that cannot be placed) says it once per level, not every tick.</summary>
	private readonly Dictionary<string, decimal> _announced = new(StringComparer.Ordinal);

	public BrokerStopManager(TrailingStopConfig config, TrailingStopStore peaks, BrokerStopStore ours, IOrderGateway gateway, BrokerStateService brokerState, string accountId, bool submit)
	{
		_config = config;
		_peaks = peaks;
		_ours = ours;
		_gateway = gateway;
		_brokerState = brokerState;
		_accountId = accountId;
		_submit = submit;
	}

	public bool Enabled => _config.Enabled;

	/// <summary>The trailing-stop settings the broker stop runs on, or null when no manager would exist for this config (the rule off, or management not allowed to act
	/// on it). The one place that decides it: <see cref="AIContext.BuildAutoExecutors"/> builds the manager from it, and the cascade panel's order lines use it to show the
	/// stop an entry would carry.</summary>
	internal static TrailingStopConfig? ConfigFor(AIConfig config) =>
		config.Rules.TrailingStop.Enabled && config.AutoExecute.Management.Enabled && config.AutoExecute.Management.Rules.Contains("TrailingStopRule", StringComparer.Ordinal) ? config.Rules.TrailingStop : null;

	public TrailingStopConfig Config => _config;

	/// <summary>True when a broker stop order guards the position as of the last <see cref="SyncAsync"/>, so the close rule stands down.</summary>
	public bool IsProtected(OpenPosition position) => _protected.Contains(position.Key);

	// ─── Price planning ───────────────────────────────────────────────────────

	/// <summary>The stop for a trail level: the trigger rounded to the exchange tick, and the limit <see cref="TrailingStopConfig.StopLimitPct"/> under it (at least one tick, and
	/// never below the smallest price the exchange accepts). Null when the trigger is not above the smallest price the exchange accepts.</summary>
	internal static StopPlan? Plan(decimal trigger, int legCount, string ticker, decimal stopLimitPct, bool stopMarket)
	{
		var stop = OptionPriceRounding.RoundToTick(trigger, legCount, ticker);
		var min = OptionPriceRounding.MinTick(legCount, ticker);
		if (stop <= min) return null;
		if (stopMarket) return new StopPlan(stop, null);
		var offset = Math.Max(stop * stopLimitPct, OptionPriceRounding.Tick(stop, legCount, ticker));
		var limit = Math.Max(OptionPriceRounding.RoundToTick(stop - offset, legCount, ticker), min);
		return new StopPlan(stop, limit);
	}

	/// <summary>The stop price to attach to a long single option's entry (<paramref name="side"/> BUY at <paramref name="limit"/>): trailPct under the entry, which is the
	/// trail level before any peak exists. Null when the entry is not bracketed (stop off, bracket off, a dry run, a credit, a structure the stop excludes).</summary>
	public decimal? EntryStop(string ticker, string structure, int legCount, string side, decimal limit) =>
		_submit ? EntryStopPrice(_config, ticker, structure, legCount, side, limit) : null;

	/// <summary><see cref="EntryStop"/> without the live gate: the stop an entry carries when live, which a dry run's printed command shows.</summary>
	internal static decimal? EntryStopPrice(TrailingStopConfig config, string ticker, string structure, int legCount, string side, decimal limit)
	{
		if (!config.Enabled || !config.BracketSingles || legCount != 1 || !string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase)) return null;
		// A stop that arms only at a profit has nothing to attach at entry: the first one is placed by the per-tick sync once the peak arms it.
		if (config.ActivateAtProfitPct != null) return null;
		if (config.Exclude.Contains(structure, StringComparer.OrdinalIgnoreCase)) return null;
		return Plan(limit * (1m - config.TrailPct), 1, ticker, config.StopLimitPct, stopMarket: true)?.Stop;
	}

	/// <summary>Records the stop child of a just-placed bracket, so the next ticks adopt it instead of placing a second stop.</summary>
	public void RecordBracket(string childClientOrderId, IEnumerable<(string Symbol, string Action)> entryLegs, DateTime now) =>
		_ours.Add(_accountId, childClientOrderId, BrokerStateService.Fingerprint(entryLegs.Select(l => (l.Symbol, Invert(l.Action)))), now);

	// ─── Per-tick sync ────────────────────────────────────────────────────────

	/// <summary>Places, resizes or raises the stop of every trailed debit position. A broker or network failure on one position is reported and does not
	/// stop the rest; the close rule covers whatever ends the tick unguarded.</summary>
	public async Task SyncAsync(EvaluationContext ctx, object? cycleToken, CancellationToken ct)
	{
		if (!_config.Enabled) return;
		if (_submit && !await _brokerState.TryRefreshAsync(ct, cycleToken)) return;

		foreach (var position in ctx.OpenPositions.Values)
		{
			if (position.Quantity <= 0 || position.InitialNetDebit <= 0m) continue;
			if (TrailingStopRule.Measure(_config, _peaks, position, ctx.Quotes, ctx.Now) is not { } reading) continue;
			try { await SyncPositionAsync(position, reading, ctx.Now, ct); }
			catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException or InvalidOperationException)
			{
				_protected.Remove(position.Key);
				var detail = ex is WebullOpenApiException w ? $"[[{Markup.Escape(w.ErrorCode ?? "?")}]] {Markup.Escape(w.Message)}" : Markup.Escape(ex.Message);
				AnsiConsole.MarkupLine($"[red]broker stop failed:[/] {Markup.Escape(position.Key)} — {detail}; the trailing-stop rule covers it this tick.");
			}
		}
	}

	private async Task SyncPositionAsync(OpenPosition position, TrailingStopRule.Reading reading, DateTime now, CancellationToken ct)
	{
		var closeLegs = reading.CloseLegs.Select(l => (l.Symbol, l.Action)).ToList();
		var fingerprint = BrokerStateService.Fingerprint(closeLegs);
		var working = _brokerState.FindPendingMatching(closeLegs).Where(o => BrokerOrder.IsStop(o.OrderType) && BrokerOrder.State(o.Status) == OrderState.Working).ToList();
		var mine = working.FirstOrDefault(o => _ours.Owns(_accountId, o.ClientOrderId));

		if (mine == null && working.Count > 0)
		{
			// A stop the user placed: it guards the position, and a second one would sell it twice.
			if (_protected.Add(position.Key)) AnsiConsole.MarkupLine($"[dim]broker stop: {Markup.Escape(position.Key)} already has a stop order the app did not place ({Markup.Escape(working[0].ClientOrderId ?? "-")}) — left alone.[/]");
			return;
		}
		if (mine == null && _ours.Recent(_accountId, fingerprint, now, ListingLag) != null) { _protected.Add(position.Key); return; }

		var stopMarket = mine != null ? !string.Equals(mine.OrderType, "STOP_LOSS_LIMIT", StringComparison.OrdinalIgnoreCase) : false;
		if (Plan(reading.Stop, position.Legs.Count, position.Ticker, _config.StopLimitPct, stopMarket) is not { } plan) return;

		if (mine == null) await PlaceAsync(position, closeLegs, plan, fingerprint, now, ct);
		else await RaiseAsync(position, mine, plan, ct);
	}

	private async Task PlaceAsync(OpenPosition position, List<(string Symbol, string Action)> closeLegs, StopPlan plan, string fingerprint, DateTime now, CancellationToken ct)
	{
		var summary = $"{position.Ticker} {position.StrategyKind} x{position.Quantity}: stop ${plan.Stop:F2}, limit ${plan.Limit:F2}";
		if (!_submit)
		{
			if (!_announced.TryGetValue(position.Key, out var shown) || shown != plan.Stop)
				AnsiConsole.MarkupLine($"[cyan]broker stop (dry-run):[/] would place a closing stop-limit — {Markup.Escape(summary)}");
			_announced[position.Key] = plan.Stop;
			return;
		}

		var legs = TradeLegParser.Parse(string.Join(",", closeLegs.Select(l => $"{l.Action}:{l.Symbol}:{position.Legs.First(p => p.Symbol == l.Symbol).Qty}")));
		string strategy;
		try { strategy = StrategyClassifier.Classify(legs) ?? "Single"; }
		catch (Exception) { strategy = "Single"; }
		// Only debit positions are handled (see SyncAsync), and closing one is a sell of the combo at a net credit.
		const string side = "SELL";
		// A stop-limit may rest GTC (a diagonal outlives the day); if Webull will not take GTC for it, a DAY stop is better than none.
		foreach (var tif in new[] { "GTC", "DAY" })
		{
			var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(_accountId, legs, strategy, side, "STOP_LOSS_LIMIT", plan.Limit, tif,
				PositionIntent: OrderRequestBuilder.DeriveOptionIntent(side, opening: false), StopPrice: plan.Stop));
			try
			{
				var placed = await _gateway.PlaceOrderAsync(body, ct);
				var id = placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId;
				_ours.Add(_accountId, id, fingerprint, now);
				_protected.Add(position.Key);
				_announced[position.Key] = plan.Stop;
				AnsiConsole.MarkupLine($"[green]broker stop placed:[/] {Markup.Escape(summary)} ({tif})  order_id={Markup.Escape(placed.OrderId ?? "-")}");
				return;
			}
			catch (WebullOpenApiException) when (tif == "GTC") { /* retry as DAY */ }
		}
	}

	private async Task RaiseAsync(OpenPosition position, WebullOpenApiClient.OrderDetailOrder mine, StopPlan plan, CancellationToken ct)
	{
		_protected.Add(position.Key);
		var current = BrokerOrder.Number(mine.StopPrice);
		var currentQty = BrokerOrder.Number(mine.TotalQuantity) is decimal q ? (int)Math.Round(q) : 0;
		var tick = OptionPriceRounding.Tick(plan.Stop, position.Legs.Count, position.Ticker);
		var raise = current is not decimal c || plan.Stop >= c + tick;
		var resize = currentQty != position.Quantity;
		if (!raise && !resize) return;

		var stop = raise ? plan.Stop : current!.Value;
		var limit = plan.Limit == null ? (decimal?)null : raise ? plan.Limit : BrokerOrder.Number(mine.LimitPrice);
		var legs = mine.Legs?.Where(l => !string.IsNullOrEmpty(l.Id)).Select(l => (l.Id!, ScaledLegQuantity(l, currentQty, position.Quantity))).ToList();
		var summary = $"{position.Ticker} {position.StrategyKind}: stop {(current is decimal was ? $"${was:F2}" : "?")} → ${stop:F2}{(resize ? $", qty {currentQty} → {position.Quantity}" : "")}";
		await _gateway.ReplaceOrderAsync(mine.ClientOrderId!, position.Quantity, limit, legs is { Count: > 0 } ? legs : null, ct, stop);
		AnsiConsole.MarkupLine($"[green]broker stop raised:[/] {Markup.Escape(summary)}");
	}

	private static int ScaledLegQuantity(WebullOpenApiClient.OrderDetailLeg leg, int currentQty, int newQty)
	{
		var legQty = BrokerOrder.Number(leg.Quantity) is decimal n ? (int)Math.Round(n) : currentQty;
		return currentQty > 0 ? legQty * newQty / currentQty : newQty;
	}

	// ─── Cancel ───────────────────────────────────────────────────────────────

	/// <summary>Cancels the stops this manager placed on <paramref name="closeLegs"/> (the legs a closing order is about to trade). Returns how many were cancelled.</summary>
	public async Task<int> CancelForAsync(IEnumerable<(string Symbol, string Action)> closeLegs, CancellationToken ct)
	{
		var cancelled = await CancelOursAsync(_brokerState, _ours, _gateway, _accountId, closeLegs, ct);
		if (cancelled > 0) _protected.Clear();
		return cancelled;
	}

	/// <summary>The cancel itself, for callers with no manager (`wa trade close`): the working stops in <paramref name="ours"/> on <paramref name="closeLegs"/>, per the broker snapshot.</summary>
	internal static async Task<int> CancelOursAsync(BrokerStateService brokerState, BrokerStopStore ours, IOrderGateway gateway, string accountId, IEnumerable<(string Symbol, string Action)> closeLegs, CancellationToken ct)
	{
		var cancelled = 0;
		foreach (var order in brokerState.FindPendingMatching(closeLegs).Where(o => BrokerOrder.IsStop(o.OrderType) && BrokerOrder.State(o.Status) == OrderState.Working && ours.Owns(accountId, o.ClientOrderId)).ToList())
		{
			await gateway.CancelOrderAsync(order.ClientOrderId!, ct);
			ours.Remove(order.ClientOrderId!);
			AnsiConsole.MarkupLine($"[yellow]broker stop cancelled:[/] {Markup.Escape(order.ClientOrderId!)} — a closing order is replacing it.");
			cancelled++;
		}
		return cancelled;
	}

	private static string Invert(string action) => string.Equals(action, "buy", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";
}

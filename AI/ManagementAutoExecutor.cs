using Spectre.Console;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>
/// In-process scheduler that turns rule-emitted Close proposals into actual order placements during
/// a <c>wa ai watch</c> tick or a <c>wa ai scan</c> evaluation. Holds tranche state across calls so a
/// 730-contract close can be split into three time-windowed pieces (default 10:00, 12:30, 15:00 ET)
/// instead of one all-at-once order. Designed to grow: the rule allow-list lets future rules opt in to
/// auto-execution without changing this class.
///
/// Behavior summary:
/// - Per (rule, position) target: tracks which tranche numbers fired today and the qty submitted.
/// - On each tick: evaluates incoming proposals from the allow-list. For each, checks the current
///   tranche window (1/2/3) and submits the configured fraction of the position's CURRENT qty as a
///   single order. Final tranche always closes the remainder.
/// - Mid-day catch-up: if watch starts after T1's window passed, T1 catches up immediately on the
///   first tick where the proposal still fires. T2 and T3 fire normally at their windows.
/// - Emergency proposals (rationale prefixed with "[emergency]"): skip the tranche schedule and
///   submit a full close immediately, regardless of window.
/// - Scale-out is bypassed when the close qty is below <c>scaleOut.minQty</c>; the executor sends a
///   single immediate order.
///
/// Submission uses <see cref="OrderRequestBuilder"/> + <see cref="WebullOpenApiClient.PlaceOrderAsync"/>
/// directly (no preview, no interactive confirm) when <c>autoExecute.management.submit</c> is true.
/// Otherwise the executor logs the planned action — useful for validating schedule and pricing before
/// going live.
/// </summary>
internal sealed class ManagementAutoExecutor
{
	private readonly ManagementAutoExecuteConfig _config;
	private readonly TimeZoneInfo _marketTz;
	private readonly TradeAccount? _account;
	private readonly BrokerStateService? _brokerState;
	private readonly HashSet<string> _allowedRules;

	// Per-day per-position tranche bookkeeping. Key: (rule, positionKey). Value: tranches submitted today.
	// Still in-memory: tranches are a within-day schedule mechanic, and a tranche that didn't fill
	// will show up in the broker's pending orders so the leg-set match catches it cross-process.
	private readonly Dictionary<(string Rule, string PositionKey), HashSet<int>> _firedTranches = new();
	private DateOnly _trackingDate;

	public ManagementAutoExecutor(ManagementAutoExecuteConfig config, TradeAccount? account, BrokerStateService? brokerState = null)
	{
		_config = config;
		_account = account;
		_brokerState = brokerState;
		_marketTz = TimeZoneInfo.FindSystemTimeZoneById(config.ScaleOut.Tz);
		_allowedRules = new HashSet<string>(config.Rules, StringComparer.Ordinal);
	}

	public bool Enabled => _config.Enabled;

	/// <summary>Inspects rule results and dispatches eligible Close proposals through the tranche
	/// scheduler. Returns the count of orders submitted (or planned, in dry-run mode) on this tick.</summary>
	public async Task<int> HandleAsync(IReadOnlyList<RuleEvaluator.EvaluationResult> results, EvaluationContext ctx, CancellationToken cancellation)
	{
		if (!_config.Enabled) return 0;

		var marketNow = TimeZoneInfo.ConvertTime(ctx.Now, _marketTz);
		var today = DateOnly.FromDateTime(marketNow);
		if (today != _trackingDate)
		{
			_firedTranches.Clear();
			_trackingDate = today;
		}

		// Broker-truth refresh. If submit is on but the API call fails, do nothing this tick.
		if (_config.Submit && _brokerState != null && !await _brokerState.TryRefreshAsync(cancellation))
			return 0;

		var orders = 0;
		foreach (var r in results)
		{
			var p = r.Proposal;
			// Allow-list applies to every proposal kind. Adding a rule name here is the explicit opt-in.
			if (!_allowedRules.Contains(p.Rule)) continue;

			if (p.Kind == ProposalKind.LegIn)
			{
				if (await TryFireLegInAsync(p, ctx, cancellation)) orders++;
				continue;
			}

			if (p.Kind != ProposalKind.Close) continue;
			if (!ctx.OpenPositions.TryGetValue(p.PositionKey, out var position)) continue;
			if (position.Quantity <= 0) continue;

			var key = (p.Rule, p.PositionKey);
			var fired = _firedTranches.TryGetValue(key, out var existing) ? existing : new HashSet<int>();

			// Emergency proposals collapse the schedule: close everything once, regardless of window.
			var isEmergency = p.Rationale.StartsWith("[emergency]", StringComparison.OrdinalIgnoreCase);
			if (isEmergency)
			{
				if (fired.Contains(0)) continue; // already fired emergency for this position-day
				if (await SubmitClose(p, position, position.Quantity, "emergency", ctx, cancellation))
				{
					fired.Add(0);
					_firedTranches[key] = fired;
					orders++;
				}
				continue;
			}

			// Below the scale-out threshold, fire a single immediate close.
			if (position.Quantity < _config.ScaleOut.MinQty)
			{
				if (fired.Contains(99)) continue; // sentinel for "single shot already fired"
				if (await SubmitClose(p, position, position.Quantity, $"single-shot (qty {position.Quantity} < minQty {_config.ScaleOut.MinQty})", ctx, cancellation))
				{
					fired.Add(99);
					_firedTranches[key] = fired;
					orders++;
				}
				continue;
			}

			var tranche = ResolveTranche(TimeOnly.FromDateTime(marketNow));
			if (tranche == null) continue;
			if (fired.Contains(tranche.Value)) continue;

			var trancheQty = ComputeTrancheQty(tranche.Value, position.Quantity);
			if (trancheQty <= 0) continue;

			if (await SubmitClose(p, position, trancheQty, $"tranche {tranche.Value}", ctx, cancellation))
			{
				fired.Add(tranche.Value);
				_firedTranches[key] = fired;
				orders++;
			}
		}

		// Forget bookkeeping for positions that no longer exist.
		var stale = _firedTranches.Keys.Where(k => !ctx.OpenPositions.ContainsKey(k.PositionKey)).ToList();
		foreach (var k in stale) _firedTranches.Remove(k);

		return orders;
	}

	/// <summary>Fires a single sell-to-open order for a LegIn proposal. Dedup is broker-truth: the
	/// caller refreshes pending orders before the loop, and this method skips the proposal if the
	/// same leg set already shows up as a pending order. That handles "limit placed but unfilled"
	/// (position still looks like a single-leg long, but a working order exists) and survives
	/// process restarts / concurrent processes. Returns true when an order was acted on (live
	/// submitted or dry-run logged).</summary>
	private async Task<bool> TryFireLegInAsync(ManagementProposal p, EvaluationContext ctx, CancellationToken cancellation)
	{
		if (p.Legs.Count != 1) return false; // LegIn always emits exactly one sell-to-open leg
		if (!ctx.OpenPositions.TryGetValue(p.PositionKey, out var position) || position.Quantity <= 0) return false;

		// Broker-truth dedup: if the same sell-to-open order is already pending at the broker,
		// skip. Catches the "limit placed but unfilled" case the in-memory dedup used to cover.
		if (_config.Submit && _brokerState != null && _brokerState.HasPendingMatching(p.Legs.Select(l => (l.Symbol, l.Action))))
		{
			AnsiConsole.MarkupLine($"[yellow]auto-execute skipped (broker pending):[/] [dim]{Markup.Escape(p.Rule)}[/] {Markup.Escape(position.Ticker)} leg-in — matching order already at broker.");
			return false;
		}

		var shortLeg = p.Legs[0];
		if (!shortLeg.PricePerShare.HasValue) return false;

		// Single sell-to-open. Round the limit price to the exchange's accepted tick to avoid
		// OAUTH_OPENAPI_OPTION_PRICE_STEP_GTE rejections (single-leg non-penny-pilot scheme).
		var rawLimit = shortLeg.PricePerShare.Value;
		var limit = OptionPriceRounding.RoundToTick(rawLimit, legCount: 1, position.Ticker);
		var argLegs = $"sell:{shortLeg.Symbol}:{shortLeg.Qty}";
		var summary = $"leg-in sell {shortLeg.Symbol} x{shortLeg.Qty} @ ${limit:F2} ({position.Ticker}: {position.StrategyKind} → vertical)";

		if (!_config.Submit || _account == null)
		{
			var reason = _account == null ? "no account configured" : "submit=false";
			AnsiConsole.MarkupLine($"[cyan]auto-execute (dry-run, {Markup.Escape(reason)}):[/] [dim]{Markup.Escape(p.Rule)}[/] {Markup.Escape(summary)}");
			AnsiConsole.MarkupLine($"  [grey50]wa trade place --trade \"{Markup.Escape(argLegs)}\" --side sell --limit {limit:F2} --submit[/]");
			return true;
		}

		var legs = TradeLegParser.Parse(argLegs);
		string strategy;
		try { strategy = StrategyClassifier.Classify(legs) ?? "Single"; }
		catch (Exception) { strategy = "Single"; }

		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
			AccountId: _account.AccountId,
			Legs: legs,
			Strategy: strategy,
			Side: "SELL",
			OrderType: "LIMIT",
			LimitPrice: limit,
			TimeInForce: "DAY",
			// Leg-in sells a new short against a held long. Without SELL_TO_OPEN, Webull classifies a
			// lone short call as a covered-call write and rejects it for insufficient stock; the intent
			// routes it to the (Level-4) open-short path so it nets against the long as a vertical.
			PositionIntent: OrderRequestBuilder.DeriveOptionIntent("SELL", opening: true)));

		try
		{
			using var client = new WebullOpenApiClient(_account);
			var placed = await client.PlaceOrderAsync(body);
			AnsiConsole.MarkupLine($"[green]auto-execute placed:[/] [dim]{Markup.Escape(p.Rule)}[/] {Markup.Escape(summary)}  order_id={Markup.Escape(placed.OrderId ?? "-")}");
			_brokerState?.RecordLocalPlacement(position.Ticker, p.Legs.Select(l => (l.Symbol, l.Action)), placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId, isOpen: true);
			return true;
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]auto-execute failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]:[/] {Markup.Escape(p.Rule)} {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			PrintFailureDiagnostics(body, ex);
			return false;
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]auto-execute network error:[/] {Markup.Escape(p.Rule)} {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			PrintFailureDiagnostics(body, ex);
			return false;
		}
	}

	private int? ResolveTranche(TimeOnly now)
	{
		if (InWindow(now, _config.ScaleOut.Tranche1Start, _config.ScaleOut.Tranche1End)) return 1;
		if (InWindow(now, _config.ScaleOut.Tranche2Start, _config.ScaleOut.Tranche2End)) return 2;
		if (InWindow(now, _config.ScaleOut.Tranche3Start, _config.ScaleOut.Tranche3End)) return 3;
		return null;
	}

	private static bool InWindow(TimeOnly now, string startStr, string endStr)
	{
		var start = TimeOnly.ParseExact(startStr, "HH\\:mm", CultureInfo.InvariantCulture);
		var end = TimeOnly.ParseExact(endStr, "HH\\:mm", CultureInfo.InvariantCulture);
		return now >= start && now < end;
	}

	private int ComputeTrancheQty(int tranche, int currentQty)
	{
		// T3 always closes the full remainder so we never carry contracts into Saturday.
		if (tranche == 3) return currentQty;
		var fraction = tranche switch
		{
			1 => _config.ScaleOut.Tranche1Fraction,
			2 => _config.ScaleOut.Tranche2Fraction,
			_ => 0m
		};
		return Math.Max(1, (int)Math.Round(currentQty * fraction, MidpointRounding.AwayFromZero));
	}

	/// <summary>Builds a close order at current net mid for the given quantity and either submits it
	/// (when <c>autoExecute.management.submit</c> is true) or logs the planned action. Returns true if
	/// the tranche was acted on; false if pricing or account info was unavailable.</summary>
	private async Task<bool> SubmitClose(ManagementProposal proposal, OpenPosition position, int qty, string label, EvaluationContext ctx, CancellationToken cancellation)
	{
		var legSpecs = position.Legs
			.Select(l => new
			{
				Action = l.Side == Side.Buy ? "sell" : "buy",
				Symbol = l.Symbol,
				LegQty = (int)Math.Round((decimal)l.Qty * qty / position.Quantity, MidpointRounding.AwayFromZero)
			})
			.ToList();

		// Broker-truth dedup: if the same closing combo is already working (or filled today) at the
		// broker, skip — don't stack a second close. The in-memory _firedTranches bookkeeping can't see
		// orders placed before a restart or by a concurrent watch/scan process; this leg-set match (the
		// same mechanism the leg-in path uses) covers the "limit placed but unfilled, then relaunched"
		// case. Return false so we DON'T mark the tranche fired: broker truth keeps gating each tick while
		// the order rests, and we legitimately re-fire only if it's canceled (fingerprint disappears).
		if (_config.Submit && _brokerState != null && _brokerState.HasPendingMatching(legSpecs.Select(l => (l.Symbol, l.Action))))
		{
			AnsiConsole.MarkupLine($"[yellow]auto-execute skipped (broker pending):[/] [dim]{Markup.Escape(label)}[/] {Markup.Escape(position.Key)} close — matching order already at broker.");
			return false;
		}

		// Compute net mid for the closing combo. When we close a long leg we sell at mid (receive),
		// when we close a short leg we buy at mid (pay). Net = sum of leg mids signed by the close direction.
		decimal netMid = 0m;
		foreach (var ls in legSpecs)
		{
			if (!ctx.Quotes.TryGetValue(ls.Symbol, out var q) || q.Bid == null || q.Ask == null)
			{
				AnsiConsole.MarkupLine($"[yellow]auto-execute:[/] [dim]{Markup.Escape(label)}[/] {Markup.Escape(position.Key)} skipped — no quote for {Markup.Escape(ls.Symbol)}");
				return false;
			}
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			netMid += ls.Action == "sell" ? mid : -mid;
		}

		// Round to the exchange-required tick (single-leg vs SPX-complex vs penny-complex) so Webull
		// doesn't reject with OAUTH_OPENAPI_OPTION_PRICE_STEP_GTE. Dry-run output uses the rounded
		// value too so the printed `wa trade place` hint matches what live submission would send.
		var side = netMid >= 0m ? "SELL" : "BUY";
		var limitAbs = OptionPriceRounding.RoundToTick(Math.Abs(netMid), position.Legs.Count, position.Ticker);

		var argLegs = string.Join(",", legSpecs.Select(l => $"{l.Action}:{l.Symbol}:{l.LegQty}"));
		var summary = $"close {qty}/{position.Quantity} {position.Ticker} @ ${limitAbs:F2} ({side.ToLowerInvariant()})";

		if (!_config.Submit || _account == null)
		{
			var reason = _account == null ? "no account configured" : "submit=false";
			AnsiConsole.MarkupLine($"[cyan]auto-execute (dry-run, {Markup.Escape(reason)}):[/] [dim]{Markup.Escape(label)}[/] {Markup.Escape(summary)}");
			AnsiConsole.MarkupLine($"  [grey50]wa trade place --trade \"{Markup.Escape(argLegs)}\" --side {side.ToLowerInvariant()} --limit {limitAbs:F2} --submit[/]");
			return true;
		}

		var legs = TradeLegParser.Parse(argLegs);
		string strategy;
		try { strategy = StrategyClassifier.Classify(legs) ?? "Calendar"; }
		catch (Exception) { strategy = "Calendar"; }

		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
			AccountId: _account.AccountId,
			Legs: legs,
			Strategy: strategy,
			Side: side,
			OrderType: "LIMIT",
			LimitPrice: limitAbs,
			TimeInForce: _config.TimeInForce.ToUpperInvariant(),
			PositionIntent: OrderRequestBuilder.DeriveOptionIntent(side, opening: false)));

		try
		{
			using var client = new WebullOpenApiClient(_account);
			var placed = await client.PlaceOrderAsync(body);
			AnsiConsole.MarkupLine($"[green]auto-execute placed:[/] [dim]{Markup.Escape(label)}[/] {Markup.Escape(summary)}  order_id={Markup.Escape(placed.OrderId ?? "-")}");
			_brokerState?.RecordLocalPlacement(position.Ticker, legSpecs.Select(l => (l.Symbol, l.Action)), placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId, isOpen: false);
			return true;
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]auto-execute failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]:[/] {Markup.Escape(label)} {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			PrintFailureDiagnostics(body, ex);
			return false;
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]auto-execute network error:[/] {Markup.Escape(label)} {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			PrintFailureDiagnostics(body, ex);
			return false;
		}
	}

	private static readonly JsonSerializerOptions DiagnosticJsonOptions = JsonDefaults.IndentedSkipNulls;

	/// <summary>Dump the request payload and (when present) Webull's raw response body so the user
	/// has enough signal to diagnose vague rejections like OAUTH_OPENAPI_SYSTEM_ERROR — the top-level
	/// `message` field is often just "System error." while the response body carries nested error_data
	/// or a request_id that Webull support can trace.</summary>
	private static void PrintFailureDiagnostics(OrderRequestBody body, Exception ex)
	{
		try
		{
			var requestJson = JsonSerializer.Serialize(body, DiagnosticJsonOptions);
			AnsiConsole.MarkupLine($"  [grey50]request:[/]\n[grey50]{Markup.Escape(requestJson)}[/]");
		}
		catch (Exception serEx) { AnsiConsole.MarkupLine($"  [grey50](could not serialize request body: {Markup.Escape(serEx.Message)})[/]"); }

		if (ex is WebullOpenApiException wex && !string.IsNullOrWhiteSpace(wex.RawBody))
			AnsiConsole.MarkupLine($"  [grey50]response:[/]\n[grey50]{Markup.Escape(wex.RawBody)}[/]");
	}
}

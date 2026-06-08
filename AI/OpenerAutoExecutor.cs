using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>
/// Companion to <see cref="ManagementAutoExecutor"/> that handles OPENER proposals (new positions).
/// Same double-flag safety model: <c>enabled</c> instantiates the executor at all; <c>submit</c>
/// flips dry-run logging to live PlaceOrder calls.
///
/// Broker-truth model: when live submission is enabled, the executor consults
/// <see cref="BrokerStateService"/> to fetch the account's pending orders and skips any proposal
/// whose leg-set already matches a pending order. This covers the cases the in-memory dedup used
/// to handle (same proposal across ticks, across processes, across restarts) plus cases it didn't
/// (manually-placed orders, orders that never filled). On any broker API failure, the executor
/// returns 0 without submitting — fail-closed.
/// </summary>
internal sealed class OpenerAutoExecutor
{
	private enum SubmitOutcome { NotActed, DryRun, Submitted }
	private readonly record struct SubmitResult(SubmitOutcome Outcome, string? OrderId = null, string? ClientOrderId = null);

	private readonly OpenerAutoExecuteConfig _config;
	private readonly TradeAccount? _account;
	private readonly BrokerStateService? _brokerState;
	private readonly HashSet<string> _allowedStructures;

	public OpenerAutoExecutor(OpenerAutoExecuteConfig config, TradeAccount? account, BrokerStateService? brokerState = null)
	{
		_config = config;
		_account = account;
		_brokerState = brokerState;
		_allowedStructures = new HashSet<string>(config.Structures, StringComparer.OrdinalIgnoreCase);
	}

	public bool Enabled => _config.Enabled;

	/// <summary>Walks the opener's ranked proposal list and dispatches eligible new-position opens
	/// through the dry-run/submit path. Returns the count of orders submitted (or planned, in dry-run).
	/// Caller is expected to invoke this AFTER <see cref="OpenCandidateEvaluator"/> emits its results.
	/// Daily cap is enforced from the broker's count of today's active orders (filled + pending), so
	/// it survives across fills, restarts, and concurrent processes on the same machine.
	///
	/// Held-position dedup: <paramref name="openPositions"/> (the same set the management executor sees)
	/// is fingerprinted per combo and any proposal whose structure already exists as an open position is
	/// skipped — so the opener never stacks an identical structure onto a position carried over from a
	/// PRIOR day. The broker-order dedup below can't catch this: a prior-day open is a filled order that
	/// is neither in today's orders nor a working order. This guard is submit-independent (no broker call)
	/// so watch's dry-run reports the skip too.</summary>
	public async Task<int> HandleAsync(IReadOnlyList<OpenProposal> proposals, IReadOnlyDictionary<string, OpenPosition> openPositions, DateTime now, CancellationToken cancellation)
	{
		if (!_config.Enabled) return 0;
		if (proposals.Count == 0) return 0;

		// Fingerprint every open position's leg set at the combo grain (positions are grouped one-per-combo,
		// matching StructureOrderSplit). A proposal is "already held" when ALL its combo groups are present —
		// so a partially-held double still completes, consistent with the broker-order dedup.
		var heldFingerprints = new HashSet<string>(StringComparer.Ordinal);
		foreach (var pos in openPositions.Values)
			if (pos.Quantity > 0)
				heldFingerprints.Add(FingerprintLegSet(pos.Legs.Select(l => (l.Side == Side.Buy ? "buy" : "sell", l.Symbol))));

		// Broker-truth refresh. If submit is on but the API call fails, do nothing this tick.
		// Dry-runs proceed without the refresh (informational; users still see "would submit" lines
		// even if a matching order already exists at the broker).
		if (_config.Submit && _brokerState != null && !await _brokerState.TryRefreshAsync(cancellation))
			return 0;

		// Daily cap is scoped to this run's ticker (one ticker per process — proposals share it), so a
		// concurrent watch on another ticker enforces its own cap rather than contending on a shared one.
		var ticker = proposals[0].Ticker;
		var brokerActiveCount = _config.Submit && _brokerState != null && _brokerState.IsReady ? _brokerState.TodaysActiveOrderCount(ticker) : 0;

		var ordersThisTick = 0;
		// Apply the structure allow-list as a SELECTION filter first — a disabled structure must never
		// trade, so fall through to the best allowed pick. Then consider only the top-N by score (the
		// day's order cap) and skip any we can't afford: affordability must not pick the trade by
		// substituting a cheaper, lower-ranked proposal. Dedup below still advances past picks already
		// at the broker.
		// Informational proposals are surfaced for display only (the best candidate of an enabled structure
		// that didn't clear the top-N / MinScoreToOpen bar). They must never be traded.
		var tradeable = proposals.Where(p => !p.Informational);
		var eligible = _allowedStructures.Count > 0
			? tradeable.Where(p => _allowedStructures.Contains(p.StructureKind.ToString()))
			: tradeable;
		foreach (var p in eligible.Take(_config.MaxOrdersPerDay))
		{
			if (p.CashReserveBlocked) continue;
			if (p.Qty < 1) continue;

			// Held-position guard: when the proposed structure already exists as an open position (every
			// combo group is held), the opener must NOT auto-open a duplicate — adding to an existing
			// position is `wa analyze position`'s job, not the opener's. Warn and skip, same as the
			// broker-order dedup below; the proposal itself is still emitted by the sink (with its
			// `wa trade place` hint) so the user can add to it manually if they choose. Catches the
			// prior-day carryover the broker-order dedup misses; submit-independent (no broker call).
			if (heldFingerprints.Count > 0
				&& StructureOrderSplit.Split(p.StructureKind, p.Legs).All(g => heldFingerprints.Contains(FingerprintLegSet(g.Legs.Select(l => (l.Action, l.Symbol))))))
			{
				AnsiConsole.MarkupLine($"[yellow]opener auto-execute skipped (already hold matching position):[/] {Markup.Escape(p.Ticker)} {p.StructureKind} x{p.Qty} [dim]({Markup.Escape(p.Legs.Describe())})[/].");
				continue;
			}

			// Broker-truth dedup: if the structure is already active (pending or filled today), skip.
			// This catches same-proposal-across-ticks, cross-process, cross-restart, and the "limit
			// placed and filled, now we'd otherwise re-fire" case. Split structures (DiagonalVertical /
			// DoubleCalendar / DoubleDiagonal) place as two combo orders, so they're "already active"
			// only when BOTH sub-orders are present — a partial (one leg-set placed, the other not) must
			// fall through so SubmitOpen can finish the structure.
			if (_config.Submit && _brokerState != null
				&& StructureOrderSplit.Split(p.StructureKind, p.Legs).All(g => _brokerState.HasPendingMatching(g.Legs.Select(l => (l.Symbol, l.Action)))))
			{
				AnsiConsole.MarkupLine($"[yellow]opener auto-execute skipped (broker already has matching order):[/] {Markup.Escape(p.Ticker)} {p.StructureKind} x{p.Qty} [dim]({Markup.Escape(p.Legs.Describe())})[/].");
				continue;
			}

			// Per-day live-submission cap. Counts active broker orders today (filled + pending,
			// excluding canceled/rejected) PLUS submissions issued in this tick.
			if (_config.Submit && brokerActiveCount + ordersThisTick >= _config.MaxOrdersPerDay)
			{
				AnsiConsole.MarkupLine($"[yellow]opener auto-execute skipped (daily cap):[/] {_config.MaxOrdersPerDay} order(s) already active at broker today (filled + pending).");
				break;
			}

			var result = await SubmitOpen(p, cancellation);
			if (result.Outcome == SubmitOutcome.NotActed) continue;
			if (result.Outcome == SubmitOutcome.Submitted) ordersThisTick++;
			else if (result.Outcome == SubmitOutcome.DryRun) ordersThisTick++;
		}
		return ordersThisTick;
	}

	/// <summary>Builds the order from the proposal's leg set and either submits (live) or logs (dry-run).
	/// Returns <see cref="SubmitOutcome.Submitted"/> with the broker-assigned ids only on a successful
	/// live PlaceOrder; the live outcome is the gate for per-day fingerprint dedup and persisted-log
	/// append. Dry-run paths return <see cref="SubmitOutcome.DryRun"/> (logged, but caller still
	/// re-emits next tick). <see cref="SubmitOutcome.NotActed"/> means the proposal was skipped
	/// (missing price, submit error) so the caller doesn't count it.</summary>
	private async Task<SubmitResult> SubmitOpen(OpenProposal p, CancellationToken cancellation)
	{
		// Never place a partial structure because of a missing leg price — validate the whole set first.
		var missing = p.Legs.FirstOrDefault(l => !l.PricePerShare.HasValue);
		if (missing != null)
		{
			AnsiConsole.MarkupLine($"[yellow]opener auto-execute:[/] {Markup.Escape(p.Ticker)} {p.StructureKind} skipped — missing price on leg {Markup.Escape(missing.Symbol)}");
			return new SubmitResult(SubmitOutcome.NotActed);
		}

		// Webull rejects a single 4-leg cross-expiry ticket, so DiagonalVertical / DoubleCalendar /
		// DoubleDiagonal go out as two combo orders (see StructureOrderSplit). They count as ONE trade
		// against the daily cap: the cap is gated once before this call, and BOTH orders are placed here
		// within it — so the cap can never let the first fill and then strand the second.
		var groups = StructureOrderSplit.Split(p.StructureKind, p.Legs);

		if (!_config.Submit || _account == null)
		{
			var reason = _account == null ? "no account configured" : "submit=false";
			var orderWord = groups.Count == 1 ? "1 order" : $"{groups.Count} orders";
			AnsiConsole.MarkupLine($"[cyan]opener auto-execute (dry-run, {Markup.Escape(reason)}):[/] open {p.StructureKind} {Markup.Escape(p.Ticker)} x{p.Qty} ({orderWord})");
			foreach (var g in groups)
			{
				var spec = BuildOrderSpec(g.Legs, p.Ticker);
				var suffix = string.IsNullOrEmpty(g.Label) ? "" : $"  # {g.Label}";
				AnsiConsole.MarkupLine($"  [grey50]wa trade place --trade \"{Markup.Escape(spec.ArgLegs)}\" --side {spec.Side.ToLowerInvariant()} --limit {spec.LimitAbs:F2} --submit{suffix}[/]");
			}
			return new SubmitResult(SubmitOutcome.DryRun);
		}

		// Live: place each group. Per-group broker dedup keeps this idempotent — if a prior tick already
		// placed one leg-set (e.g. the process died between the two orders), we place only what's missing
		// rather than duplicating. A failure after the first order leaves a partial structure: warn loudly.
		string? firstOrderId = null, firstClientId = null;
		var placedCount = 0;
		for (var i = 0; i < groups.Count; i++)
		{
			var g = groups[i];
			if (_brokerState != null && _brokerState.HasPendingMatching(g.Legs.Select(l => (l.Symbol, l.Action))))
			{
				AnsiConsole.MarkupLine($"[yellow]opener auto-execute:[/] {Markup.Escape(p.Ticker)} {p.StructureKind}{LabelSuffix(g.Label)} already active at broker — skipping this leg-set.");
				continue;
			}

			var spec = BuildOrderSpec(g.Legs, p.Ticker);
			var legs = TradeLegParser.Parse(spec.ArgLegs);
			// Per-ORDER strategy only — NEVER the composite (DiagonalVertical/DoubleCalendar/DoubleDiagonal),
			// which the Webull API doesn't accept. The split yields 2-leg verticals/calendars/diagonals that
			// classify to Webull-known names. If a group can't be classified, skip the whole structure rather
			// than fall back to the composite kind (which OrderRequestBuilder would reject) — never send the API
			// a strategy it doesn't understand.
			string? strategy;
			try { strategy = StrategyClassifier.Classify(legs); }
			catch (Exception) { strategy = null; }
			if (strategy == null)
			{
				AnsiConsole.MarkupLine($"[yellow]opener auto-execute:[/] {Markup.Escape(p.Ticker)} {p.StructureKind}{LabelSuffix(g.Label)} skipped — could not classify the leg-set to a Webull strategy.");
				if (placedCount > 0)
					AnsiConsole.MarkupLine($"[red bold]WARNING:[/] {Markup.Escape(p.Ticker)} {p.StructureKind} is PARTIALLY open — {placedCount} of {groups.Count} order(s) placed. Complete or cancel the open leg-set manually.");
				break;
			}

			var summary = $"open {p.StructureKind}{LabelSuffix(g.Label)} {p.Ticker} x{p.Qty} @ ${spec.LimitAbs:F2} ({spec.Side.ToLowerInvariant()})";
			OrderRequestBody? body = null;
			try
			{
				// Build inside the try: an unmapped strategy throws InvalidOperationException, which must
				// degrade to a skip — not crash the watch loop.
				body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
					AccountId: _account.AccountId,
					Legs: legs,
					Strategy: strategy,
					Side: spec.Side,
					OrderType: "LIMIT",
					LimitPrice: spec.LimitAbs,
					TimeInForce: _config.TimeInForce.ToUpperInvariant()));

				using var client = new WebullOpenApiClient(_account);
				var placed = await client.PlaceOrderAsync(body);
				AnsiConsole.MarkupLine($"[green]opener auto-execute placed:[/] {Markup.Escape(summary)}  order_id={Markup.Escape(placed.OrderId ?? "-")}");
				placedCount++;
				firstOrderId ??= placed.OrderId;
				firstClientId ??= placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId;
			}
			catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException or InvalidOperationException)
			{
				var tag = ex is WebullOpenApiException w ? $"[[{Markup.Escape(w.ErrorCode ?? "?")}]]" : ex is HttpRequestException ? "network error" : "order-build error";
				AnsiConsole.MarkupLine($"[red]opener auto-execute failed {tag}:[/] {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
				if (body != null) PrintFailureDiagnostics(body, ex);
				if (placedCount > 0)
					AnsiConsole.MarkupLine($"[red bold]WARNING:[/] {Markup.Escape(p.Ticker)} {p.StructureKind} is PARTIALLY open — {placedCount} of {groups.Count} order(s) placed; the rest failed. Complete or cancel the open leg-set manually.");
				break;
			}
		}

		return placedCount > 0
			? new SubmitResult(SubmitOutcome.Submitted, firstOrderId, firstClientId)
			: new SubmitResult(SubmitOutcome.NotActed);
	}

	/// <summary>Per-order limit/side/leg-arg from the leg set. PricePerShare is signed by action ("buy"
	/// pays, "sell" collects); positive net = credit → SELL the combo at the absolute price, negative =
	/// debit → BUY. Rounds to the exchange tick for this order's leg count (single-leg vs complex) so
	/// Webull doesn't reject with OAUTH_OPENAPI_OPTION_PRICE_STEP_GTE. Every leg's PricePerShare is
	/// guaranteed non-null by the caller's up-front validation.</summary>
	private static (string Side, decimal LimitAbs, string ArgLegs) BuildOrderSpec(IReadOnlyList<ProposalLeg> legs, string ticker)
	{
		decimal netPerShare = 0m;
		foreach (var leg in legs)
			netPerShare += string.Equals(leg.Action, "buy", StringComparison.OrdinalIgnoreCase) ? -leg.PricePerShare!.Value : leg.PricePerShare!.Value;
		var side = netPerShare >= 0m ? "SELL" : "BUY";
		var limitAbs = OptionPriceRounding.RoundToTick(Math.Abs(netPerShare), legs.Count, ticker);
		var argLegs = string.Join(",", legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		return (side, limitAbs, argLegs);
	}

	/// <summary>Canonical leg-set fingerprint: sorted "ACTION:OCC" joined. Both proposal legs and position
	/// legs carry the OCC symbol directly, so no per-field reconstruction is needed (unlike the broker path,
	/// where Webull returns root/strike/expiry separately). Quantity is intentionally excluded — a 3-lot
	/// proposal matches a held 6-lot of the same structure.</summary>
	private static string FingerprintLegSet(IEnumerable<(string Action, string Symbol)> legs) =>
		string.Join("|", legs
			.Select(l => $"{l.Action.ToUpperInvariant()}:{l.Symbol.ToUpperInvariant()}")
			.OrderBy(s => s, StringComparer.Ordinal));

	private static string LabelSuffix(string label) => string.IsNullOrEmpty(label) ? "" : $" ({label})";

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

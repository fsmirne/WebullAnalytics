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
/// flips dry-run logging to live PlaceOrder calls. Per-day fingerprint deduplication prevents the
/// same proposal from re-firing across successive ticks — but ONLY after a real live submit. Dry-run
/// emissions are intentionally NOT deduped, so the user keeps seeing the proposed `wa trade place`
/// command on every tick the proposal stays at the top (otherwise the line goes silent after tick 1
/// even though the proposal is still live).
///
/// Why a separate class from <see cref="ManagementAutoExecutor"/>: management closes track tranche
/// state keyed by (rule, position); opener fires track fingerprints keyed by (ticker, fingerprint).
/// Different shapes, different schedules. Sharing one class would mean awkward union state.
/// </summary>
internal sealed class OpenerAutoExecutor
{
	private enum SubmitOutcome { NotActed, DryRun, Submitted }
	private readonly record struct SubmitResult(SubmitOutcome Outcome, string? OrderId = null, string? ClientOrderId = null);

	private readonly OpenerAutoExecuteConfig _config;
	private readonly TradeAccount? _account;
	private readonly HashSet<string> _allowedStructures;

	// Per-day fingerprints already LIVE-submitted. Dry-runs don't populate this set — they re-emit
	// every tick. Cleared at the first tick of each new market day.
	private readonly HashSet<string> _firedFingerprints = new(StringComparer.Ordinal);
	// Per-day LIVE submission counter. Caps total opens placed against the broker per trading day,
	// independent of how many distinct proposal fingerprints the opener emits as spot drifts. Same
	// reset cadence as _firedFingerprints.
	private int _liveSubmittedToday = 0;
	private DateOnly _trackingDate;

	public OpenerAutoExecutor(OpenerAutoExecuteConfig config, TradeAccount? account)
	{
		_config = config;
		_account = account;
		_allowedStructures = new HashSet<string>(config.Structures, StringComparer.OrdinalIgnoreCase);
	}

	public bool Enabled => _config.Enabled;

	/// <summary>Walks the opener's ranked proposal list and dispatches eligible new-position opens
	/// through the dry-run/submit path. Returns the count of orders submitted (or planned, in dry-run).
	/// Caller is expected to invoke this AFTER <see cref="OpenCandidateEvaluator"/> emits its results.</summary>
	public async Task<int> HandleAsync(IReadOnlyList<OpenProposal> proposals, DateTime now, CancellationToken cancellation)
	{
		if (!_config.Enabled) return 0;
		if (proposals.Count == 0) return 0;

		var today = DateOnly.FromDateTime(now);
		if (today != _trackingDate)
		{
			_firedFingerprints.Clear();
			_liveSubmittedToday = 0;
			_trackingDate = today;
		}

		// Cross-process cap enforcement: when live submission is enabled, hold an OS-level file lock
		// across the entire read-decide-submit-append cycle so concurrent `wa ai scan` / `wa ai watch`
		// instances on the same machine never both observe a pre-submit count below the cap and
		// double-fire. Dry-runs skip the lock — there's nothing to coordinate.
		IDisposable? lockHandle = null;
		if (_config.Submit)
		{
			lockHandle = SubmissionLog.AcquireLock();
			// Seed in-memory state from the persisted log so the cap holds across process restarts
			// (the in-memory counter alone would reset to 0 on every launch).
			_liveSubmittedToday = SubmissionLog.CountForDate(today, SubmissionLog.Kind.Open);
			foreach (var fp in SubmissionLog.FingerprintsForDate(today, SubmissionLog.Kind.Open))
				_firedFingerprints.Add(fp);
		}

		try
		{
			var ordersThisTick = 0;
			foreach (var p in proposals)
			{
				if (p.CashReserveBlocked) continue;
				if (p.Qty < 1) continue;
				if (_allowedStructures.Count > 0 && !_allowedStructures.Contains(p.StructureKind.ToString())) continue;
				if (!string.IsNullOrEmpty(p.Fingerprint) && _firedFingerprints.Contains(p.Fingerprint)) continue;

				// Per-day live-submission cap. Only blocks live PlaceOrder calls; dry-runs continue to emit
				// (informational, no actual orders to count).
				if (_config.Submit && _liveSubmittedToday >= _config.MaxOrdersPerDay)
					break;

				var result = await SubmitOpen(p, cancellation);
				if (result.Outcome == SubmitOutcome.NotActed) continue;

				// Only dedup live submissions. Dry-runs are diagnostic output the user expects to see
				// repeat each tick so they can copy the `wa trade place` command at any moment.
				if (result.Outcome == SubmitOutcome.Submitted)
				{
					if (!string.IsNullOrEmpty(p.Fingerprint)) _firedFingerprints.Add(p.Fingerprint);
					_liveSubmittedToday++;
					// Persist immediately so the next process to acquire the lock sees this submission.
					SubmissionLog.Append(new SubmissionLog.Entry(
						Date: today.ToString("yyyy-MM-dd"),
						Ts: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
						Kind: SubmissionLog.Kind.Open,
						Ticker: p.Ticker,
						Rule: null,
						Fingerprint: p.Fingerprint,
						ClientOrderId: result.ClientOrderId,
						OrderId: result.OrderId));
				}
				ordersThisTick++;
			}
			return ordersThisTick;
		}
		finally { lockHandle?.Dispose(); }
	}

	/// <summary>Builds the order from the proposal's leg set and either submits (live) or logs (dry-run).
	/// Returns <see cref="SubmitOutcome.Submitted"/> with the broker-assigned ids only on a successful
	/// live PlaceOrder; the live outcome is the gate for per-day fingerprint dedup and persisted-log
	/// append. Dry-run paths return <see cref="SubmitOutcome.DryRun"/> (logged, but caller still
	/// re-emits next tick). <see cref="SubmitOutcome.NotActed"/> means the proposal was skipped
	/// (missing price, submit error) so the caller doesn't count it.</summary>
	private async Task<SubmitResult> SubmitOpen(OpenProposal p, CancellationToken cancellation)
	{
		// Each leg's PricePerShare is already set by CandidateScorer at evaluation time; signed by the
		// leg's Action ("buy" pays, "sell" collects). Net per-share = sum.
		decimal netPerShare = 0m;
		var legSpecs = new List<(string Action, string Symbol, int Qty, decimal Price)>(p.Legs.Count);
		foreach (var leg in p.Legs)
		{
			if (!leg.PricePerShare.HasValue)
			{
				AnsiConsole.MarkupLine($"[yellow]opener auto-execute:[/] {Markup.Escape(p.Ticker)} {p.StructureKind} skipped — missing price on leg {Markup.Escape(leg.Symbol)}");
				return new SubmitResult(SubmitOutcome.NotActed);
			}
			var price = leg.PricePerShare.Value;
			legSpecs.Add((leg.Action, leg.Symbol, leg.Qty, price));
			netPerShare += string.Equals(leg.Action, "buy", StringComparison.OrdinalIgnoreCase) ? -price : price;
		}

		// Positive netPerShare = net credit (we're receiving) → SELL combo at the absolute price.
		// Negative netPerShare = net debit (we're paying) → BUY combo at the absolute price.
		// Round to the exchange-required tick (single-leg vs SPX-complex vs penny-complex) so Webull
		// doesn't reject with OAUTH_OPENAPI_OPTION_PRICE_STEP_GTE. Dry-run output also shows the
		// rounded value so the printed `wa trade place` hint matches what live submission would send.
		var side = netPerShare >= 0m ? "SELL" : "BUY";
		var limitAbs = OptionPriceRounding.RoundToTick(Math.Abs(netPerShare), p.Legs.Count, p.Ticker);

		var argLegs = string.Join(",", legSpecs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var summary = $"open {p.StructureKind} {p.Ticker} x{p.Qty} @ ${limitAbs:F2} ({side.ToLowerInvariant()})";

		if (!_config.Submit || _account == null)
		{
			var reason = _account == null ? "no account configured" : "submit=false";
			AnsiConsole.MarkupLine($"[cyan]opener auto-execute (dry-run, {Markup.Escape(reason)}):[/] {Markup.Escape(summary)}");
			AnsiConsole.MarkupLine($"  [grey50]wa trade place --trade \"{Markup.Escape(argLegs)}\" --side {side.ToLowerInvariant()} --limit {limitAbs:F2} --submit[/]");
			return new SubmitResult(SubmitOutcome.DryRun);
		}

		var legs = TradeLegParser.Parse(argLegs);
		string strategy;
		try { strategy = StrategyClassifier.Classify(legs) ?? p.StructureKind.ToString(); }
		catch (Exception) { strategy = p.StructureKind.ToString(); }

		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
			AccountId: _account.AccountId,
			Legs: legs,
			Strategy: strategy,
			Side: side,
			OrderType: "LIMIT",
			LimitPrice: limitAbs,
			TimeInForce: _config.TimeInForce.ToUpperInvariant()));

		try
		{
			using var client = new WebullOpenApiClient(_account);
			var placed = await client.PlaceOrderAsync(body);
			AnsiConsole.MarkupLine($"[green]opener auto-execute placed:[/] {Markup.Escape(summary)}  order_id={Markup.Escape(placed.OrderId ?? "-")}");
			return new SubmitResult(SubmitOutcome.Submitted, placed.OrderId, placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId);
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]opener auto-execute failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]:[/] {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			PrintFailureDiagnostics(body, ex);
			return new SubmitResult(SubmitOutcome.NotActed);
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]opener auto-execute network error:[/] {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			PrintFailureDiagnostics(body, ex);
			return new SubmitResult(SubmitOutcome.NotActed);
		}
	}

	private static readonly JsonSerializerOptions DiagnosticJsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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

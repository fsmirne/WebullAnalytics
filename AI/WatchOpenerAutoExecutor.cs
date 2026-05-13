using Spectre.Console;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>
/// Companion to <see cref="WatchAutoExecutor"/> that handles OPENER proposals (new positions). Same
/// double-flag safety model: <c>enabled</c> instantiates the executor at all; <c>submit</c> flips
/// dry-run logging to live PlaceOrder calls. Per-day fingerprint deduplication prevents the same
/// proposal from re-firing across successive ticks (the opener re-evaluates every tick and tends to
/// re-emit the same top candidates until something changes).
///
/// Why a separate class from <see cref="WatchAutoExecutor"/>: management closes track tranche state
/// keyed by (rule, position); opener fires track fingerprints keyed by (ticker, fingerprint). Different
/// shapes, different schedules. Sharing one class would mean awkward union state.
/// </summary>
internal sealed class WatchOpenerAutoExecutor
{
	private readonly OpenerAutoExecuteConfig _config;
	private readonly TradeAccount? _account;
	private readonly HashSet<string> _allowedStructures;

	// Per-day fingerprints already acted on. Cleared at the first tick of each new market day.
	private readonly HashSet<string> _firedFingerprints = new(StringComparer.Ordinal);
	private DateOnly _trackingDate;

	public WatchOpenerAutoExecutor(OpenerAutoExecuteConfig config, TradeAccount? account)
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
			_trackingDate = today;
		}

		var ordersThisTick = 0;
		var perTickerCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		foreach (var p in proposals)
		{
			if (ordersThisTick >= _config.MaxOrdersPerTick) break;

			if (p.CashReserveBlocked) continue;
			if (p.Qty < 1) continue;
			if (_allowedStructures.Count > 0 && !_allowedStructures.Contains(p.StructureKind.ToString())) continue;
			if (!string.IsNullOrEmpty(p.Fingerprint) && _firedFingerprints.Contains(p.Fingerprint)) continue;

			var tickerCount = perTickerCount.TryGetValue(p.Ticker, out var n) ? n : 0;
			if (tickerCount >= _config.MaxPerTickerPerTick) continue;

			if (await SubmitOpen(p, cancellation))
			{
				if (!string.IsNullOrEmpty(p.Fingerprint)) _firedFingerprints.Add(p.Fingerprint);
				perTickerCount[p.Ticker] = tickerCount + 1;
				ordersThisTick++;
			}
		}

		return ordersThisTick;
	}

	/// <summary>Builds the order from the proposal's leg set and either submits (live) or logs (dry-run).
	/// Returns true when the proposal was acted on; false when pricing/account info was missing so the
	/// caller doesn't dedup it (it can retry on the next tick).</summary>
	private async Task<bool> SubmitOpen(OpenProposal p, CancellationToken cancellation)
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
				return false;
			}
			var price = leg.PricePerShare.Value;
			legSpecs.Add((leg.Action, leg.Symbol, leg.Qty, price));
			netPerShare += string.Equals(leg.Action, "buy", StringComparison.OrdinalIgnoreCase) ? -price : price;
		}

		// Positive netPerShare = net credit (we're receiving) → SELL combo at the absolute price.
		// Negative netPerShare = net debit (we're paying) → BUY combo at the absolute price.
		var side = netPerShare >= 0m ? "SELL" : "BUY";
		var limitAbs = Math.Abs(netPerShare);

		var argLegs = string.Join(",", legSpecs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var summary = $"open {p.StructureKind} {p.Ticker} x{p.Qty} @ ${limitAbs:F2} ({side.ToLowerInvariant()})";

		if (!_config.Submit || _account == null)
		{
			var reason = _account == null ? "no account configured" : "submit=false";
			AnsiConsole.MarkupLine($"[cyan]opener auto-execute (dry-run, {Markup.Escape(reason)}):[/] {Markup.Escape(summary)}");
			AnsiConsole.MarkupLine($"  [grey50]wa trade place --trade \"{Markup.Escape(argLegs)}\" --side {side.ToLowerInvariant()} --limit {limitAbs:F2} --submit[/]");
			return true;
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
			TimeInForce: "DAY"));

		try
		{
			using var client = new WebullOpenApiClient(_account);
			var placed = await client.PlaceOrderAsync(body);
			AnsiConsole.MarkupLine($"[green]opener auto-execute placed:[/] {Markup.Escape(summary)}  order_id={Markup.Escape(placed.OrderId ?? "-")}");
			return true;
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]opener auto-execute failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]:[/] {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			return false;
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]opener auto-execute network error:[/] {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
			return false;
		}
	}
}

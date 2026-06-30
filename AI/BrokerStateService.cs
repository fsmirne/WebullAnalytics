using Spectre.Console;
using System.Globalization;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI;

/// <summary>
/// Broker-truth snapshot for the auto-executors. Wraps Webull's pending-orders API
/// (<see cref="WebullOpenApiClient.ListOpenOrdersAsync"/>) so auto-executors can skip
/// proposals that already have a working order at the broker, regardless of which
/// process placed it or how the bot was restarted.
///
/// Lifecycle: caller invokes <see cref="RefreshAsync"/> once at the start of each
/// tick. Both <see cref="OpenerAutoExecutor"/> and <see cref="ManagementAutoExecutor"/>
/// then consult this single snapshot — no per-executor re-query. On any API
/// exception during refresh, <see cref="IsReady"/> stays false and downstream code
/// is expected to do nothing this tick (the "fail-closed" stance: an order not
/// placed is reversible, an order over-placed is not).
///
/// Matching: orders are fingerprinted by their leg set (canonical, sorted by side+symbol).
/// Two proposals with the same leg-set fingerprint are considered the same trade,
/// regardless of limit price, time-in-force, or strategy classification — the broker
/// already has it, don't double-fire.
/// </summary>
internal sealed class BrokerStateService
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private readonly TradeAccount _account;
	// Same-day ledger of orders this machine placed. Webull's today-orders endpoint lags fills by 10+
	// minutes and instantly-filled orders leave /open at once, so without the ledger the snapshot is
	// blind to our own submissions for that window (live double-open 2026-06-11). Null in tests.
	private readonly LocalOrderLedger? _ledger;
	// Union of pending + today's filled/partial orders, keyed by leg-set fingerprint. Canceled and
	// rejected orders are excluded — they represent attempted-but-not-resting state and shouldn't
	// gate fresh submissions of the same shape.
	// Count per fingerprint (not a set): the opener's dedup nets opens against closes — a structure
	// opened AND closed today must not block a deliberate re-open — and netting needs multiplicity
	// (open, close, re-open, ask again → 2 opens vs 1 close must still block).
	private Dictionary<string, int>? _activeFingerprintCounts;
	private Dictionary<string, List<WebullOpenApiClient.OrderDetailOrder>>? _activeByFingerprint;
	// Active-order count keyed by underlying root, so the daily cap can be scoped to the running ticker.
	private Dictionary<string, int>? _activeOrderCountByRoot;

	// Order statuses that should DEDUP a new submission: working (still pending) or successfully
	// filled (already executed today). Canceled/rejected don't count — we're free to try again.
	// Webull status string conventions vary slightly across endpoints, so we match by prefix.
	private static readonly string[] ActiveStatusPrefixes =
	{
		"FILLED", "PARTIALLY_FILLED", "PARTIAL_FILLED", "PARTIAL",
		"WORKING", "PENDING", "NEW", "QUEUED", "ACCEPTED", "SUBMITTED",
	};

	private static bool IsActiveStatus(string? status)
	{
		if (string.IsNullOrEmpty(status)) return true; // missing status — assume active, fail-closed
		var s = status.ToUpperInvariant();
		foreach (var p in ActiveStatusPrefixes)
			if (s.StartsWith(p, StringComparison.Ordinal)) return true;
		return false;
	}

	// Closing orders (BUY_TO_CLOSE / SELL_TO_CLOSE) don't consume the daily opening cap — de-risking an
	// existing position must never block the day's open. Missing/unknown intent counts (fail-closed).
	private static bool IsCloseIntent(string? positionIntent) =>
		positionIntent != null && positionIntent.ToUpperInvariant().EndsWith("_TO_CLOSE", StringComparison.Ordinal);

	public BrokerStateService(TradeAccount account, LocalOrderLedger? ledger = null) { _account = account; _ledger = ledger; }

	/// <summary>True once <see cref="RefreshAsync"/> has succeeded at least once. When false,
	/// callers should skip live submission this tick.</summary>
	public bool IsReady => _activeFingerprintCounts != null;

	/// <summary>Count of distinct ACTIVE OPENING orders today (filled or pending, excluding canceled/rejected
	/// and excluding *_TO_CLOSE intents) for <paramref name="ticker"/>. The daily cap is scoped per ticker, so
	/// concurrent single-ticker watch/scan processes on the same account enforce independent caps. Counts broker
	/// ORDERS: a split structure (diagonal/calendar vertical, double calendar/diagonal) places two orders and
	/// counts as two. Holds across pending → filled transitions and process restarts (the broker remembers;
	/// re-queried each tick).</summary>
	public int TodaysActiveOrderCount(string ticker) =>
		_activeOrderCountByRoot != null && _activeOrderCountByRoot.TryGetValue(ticker.ToUpperInvariant(), out var n) ? n : 0;

	/// <summary>Pull both today's order history (any status) and the currently-open orders from
	/// Webull, then build a unified leg-set index covering anything in an "active" state — filled,
	/// partially filled, working, queued, etc. Canceled / rejected entries are dropped: we should
	/// be free to retry their leg shape. Throws on API/network failure; caller catches and decides
	/// whether to skip the tick. Successive calls overwrite the previous snapshot atomically.</summary>
	public async Task RefreshAsync(CancellationToken cancellation)
	{
		using var client = new WebullOpenApiClient(_account);

		// Pull both endpoints. Today's-orders catches everything (working + filled); /open is a
		// safety net in case today's-orders is missing carried-over orders for some reason. We
		// union them and dedup by client_order_id (within the orders list).
		var todayOrders = await client.ListTodayOrdersAsync(cancellation);
		var openOrders = await client.ListOpenOrdersAsync(cancellation);

		var seenClientIds = new HashSet<string>(StringComparer.Ordinal);
		var fingerprints = new Dictionary<string, int>(StringComparer.Ordinal);
		var byFingerprint = new Dictionary<string, List<WebullOpenApiClient.OrderDetailOrder>>(StringComparer.Ordinal);
		var rootCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		// Every client_order_id the broker reported, ANY status (unlike seenClientIds, which is
		// active-only). A local-ledger entry whose id appears here has been caught up by the broker —
		// its status logic (including canceled/rejected = free to retry) governs from then on.
		var reportedClientIds = new HashSet<string>(StringComparer.Ordinal);

		void Ingest(IEnumerable<WebullOpenApiClient.OpenOrder> combos)
		{
			foreach (var combo in combos)
			{
				if (combo.Orders == null) continue;
				foreach (var order in combo.Orders)
				{
					if (!string.IsNullOrEmpty(order.ClientOrderId)) reportedClientIds.Add(order.ClientOrderId);
					if (!IsActiveStatus(order.Status)) continue;
					if (!string.IsNullOrEmpty(order.ClientOrderId) && !seenClientIds.Add(order.ClientOrderId)) continue;
					var fp = FingerprintLegs(order.Legs);
					if (string.IsNullOrEmpty(fp)) continue;
					fingerprints[fp] = fingerprints.TryGetValue(fp, out var fpCount) ? fpCount + 1 : 1;
					if (!byFingerprint.TryGetValue(fp, out var list)) byFingerprint[fp] = list = new List<WebullOpenApiClient.OrderDetailOrder>();
					list.Add(order);
					var root = RootOf(order.Legs);
					if (root != null && !IsCloseIntent(order.PositionIntent)) rootCount[root] = rootCount.TryGetValue(root, out var c) ? c + 1 : 1;
				}
			}
		}

		Ingest(todayOrders);
		Ingest(openOrders);

		if (_ledger != null)
		{
			var etToday = TimeZoneInfo.ConvertTime(DateTime.UtcNow, NyTz).Date;
			MergeLedgerEntries(_ledger.ReadFor(etToday), reportedClientIds, fingerprints, rootCount);
		}

		_activeFingerprintCounts = fingerprints;
		_activeByFingerprint = byFingerprint;
		_activeOrderCountByRoot = rootCount;
	}

	/// <summary>Folds today's locally-placed orders into the broker snapshot. An entry is skipped once the
	/// broker reports its client_order_id (any status — broker truth, including canceled/rejected = retryable,
	/// governs from then on). Surviving entries count into the fingerprint multiset, and opening entries
	/// count toward the per-root daily cap.</summary>
	internal static void MergeLedgerEntries(IReadOnlyList<LocalOrderLedger.Entry> entries, HashSet<string> reportedClientIds, Dictionary<string, int> fingerprints, Dictionary<string, int> rootCount)
	{
		foreach (var e in entries)
		{
			if (!string.IsNullOrEmpty(e.ClientOrderId) && reportedClientIds.Contains(e.ClientOrderId)) continue;
			fingerprints[e.Fingerprint] = fingerprints.TryGetValue(e.Fingerprint, out var c) ? c + 1 : 1;
			if (e.Open) rootCount[e.Root] = rootCount.TryGetValue(e.Root, out var rc) ? rc + 1 : 1;
		}
	}

	/// <summary>Records a successful PlaceOrder issued by this process, so the snapshot sees it immediately
	/// instead of waiting out the broker's history lag: appends to the persisted ledger (cross-restart,
	/// cross-process) and patches the in-memory snapshot in place for the rest of this tick. Opening orders
	/// (<paramref name="isOpen"/>) count toward the daily cap; closes participate in leg-set dedup only.</summary>
	public void RecordLocalPlacement(string ticker, IEnumerable<(string Symbol, string Action)> legs, string? clientOrderId, bool isOpen)
	{
		var fp = FingerprintProposal(legs);
		var root = ticker.ToUpperInvariant();
		try { _ledger?.Append(root, isOpen, fp, clientOrderId, TimeZoneInfo.ConvertTime(DateTime.UtcNow, NyTz)); }
		catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]local order ledger append failed:[/] {Markup.Escape(ex.Message)} — dedup holds in-memory for this run only."); }
		if (_activeFingerprintCounts != null)
			_activeFingerprintCounts[fp] = _activeFingerprintCounts.TryGetValue(fp, out var c) ? c + 1 : 1;
		if (isOpen && _activeOrderCountByRoot != null)
			_activeOrderCountByRoot[root] = _activeOrderCountByRoot.TryGetValue(root, out var rc) ? rc + 1 : 1;
	}

	/// <summary>Underlying root of an order's leg set (all legs of one combo share it). Used to scope the
	/// daily-cap count per ticker. Returns the first non-empty leg symbol, uppercased, or null.</summary>
	private static string? RootOf(IEnumerable<WebullOpenApiClient.OrderDetailLeg>? legs)
	{
		if (legs == null) return null;
		foreach (var l in legs)
			if (!string.IsNullOrEmpty(l.Symbol)) return l.Symbol!.ToUpperInvariant();
		return null;
	}

	/// <summary>Returns true when the proposal's leg set matches an ACTIVE order at the broker —
	/// pending OR already filled/partial today (i.e. anything that should block a duplicate). Returns
	/// false when <see cref="IsReady"/> is false; callers should have short-circuited beforehand.</summary>
	public bool HasPendingMatching(IEnumerable<(string Symbol, string Action)> proposalLegs)
	{
		if (_activeFingerprintCounts == null) return false;
		var fp = FingerprintProposal(proposalLegs);
		return _activeFingerprintCounts.ContainsKey(fp);
	}

	/// <summary>Opener-side dedup: blocks only while today's matching OPEN orders outnumber the matching
	/// CLOSES (a close is the same legs with sides inverted — a distinct fingerprint). A structure opened
	/// and then closed today nets to zero and may be deliberately re-opened (live 2026-06-11: the morning's
	/// DoubleDiagonal, closed before noon, blocked the afternoon's `scan --submit-override` re-entry); a
	/// just-filled un-closed open stays net-positive and keeps blocking. ONLY for opening proposals — the
	/// management executor's close dedup must stay presence-based (<see cref="HasPendingMatching"/>):
	/// netting a close against its same-day open would report 0 while a close order rests, enabling a
	/// double-close.</summary>
	public bool HasNetOpenMatching(IEnumerable<(string Symbol, string Action)> proposalLegs)
	{
		if (_activeFingerprintCounts == null) return false;
		var legs = proposalLegs.ToList();
		var opens = _activeFingerprintCounts.GetValueOrDefault(FingerprintProposal(legs), 0);
		var closes = _activeFingerprintCounts.GetValueOrDefault(FingerprintProposal(legs.Select(InvertAction)), 0);
		return opens > closes;
	}

	private static (string Symbol, string Action) InvertAction((string Symbol, string Action) leg) =>
		(leg.Symbol, string.Equals(leg.Action, "buy", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy");

	/// <summary>Returns the active orders at the broker that match the proposal's leg set, or an
	/// empty list when there are none / <see cref="IsReady"/> is false. Used by `wa trade place` to
	/// surface details of the existing duplicates in the warning line (client_order_id, qty, limit,
	/// status). Includes both pending and filled-today orders.</summary>
	public IReadOnlyList<WebullOpenApiClient.OrderDetailOrder> FindPendingMatching(IEnumerable<(string Symbol, string Action)> proposalLegs)
	{
		if (_activeByFingerprint == null) return Array.Empty<WebullOpenApiClient.OrderDetailOrder>();
		var fp = FingerprintProposal(proposalLegs);
		return _activeByFingerprint.TryGetValue(fp, out var list) ? list : Array.Empty<WebullOpenApiClient.OrderDetailOrder>();
	}

	/// <summary>Canonical fingerprint of a Webull pending order's leg set. Webull returns each leg
	/// with the UNDERLYING root in <c>symbol</c> (not the OCC) plus separate <c>strike_price</c>,
	/// <c>option_expire_date</c>, and <c>option_type</c> fields. We reconstruct the OCC-equivalent
	/// key (Side, Root, Expiry, Strike, CallPut) and sort so leg order doesn't matter. Stock legs
	/// (no expiry/strike) fingerprint as Side:Root:STOCK.</summary>
	private static string FingerprintLegs(IEnumerable<WebullOpenApiClient.OrderDetailLeg>? legs)
	{
		if (legs == null) return "";
		return string.Join("|", legs
			.Where(l => !string.IsNullOrEmpty(l.Symbol) && !string.IsNullOrEmpty(l.Side))
			.Select(LegKey)
			.Where(k => k != null)
			.OrderBy(s => s, StringComparer.Ordinal)!);
	}

	private static string? LegKey(WebullOpenApiClient.OrderDetailLeg l)
	{
		var side = l.Side!.ToUpperInvariant();
		var root = l.Symbol!.ToUpperInvariant();
		if (string.IsNullOrEmpty(l.OptionExpireDate) || string.IsNullOrEmpty(l.OptionType) || string.IsNullOrEmpty(l.StrikePrice))
			return $"{side}:{root}:STOCK";
		if (!decimal.TryParse(l.StrikePrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) return null;
		var cp = l.OptionType!.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? "C" : "P";
		return $"{side}:{root}:{l.OptionExpireDate}:{strike.ToString("F2", CultureInfo.InvariantCulture)}:{cp}";
	}

	/// <summary>Canonical fingerprint of a proposal's leg set. The proposal carries OCC symbols
	/// (e.g. <c>SPXW260526C07375000</c>); we parse each to extract (Root, Expiry, Strike, CallPut)
	/// so the fingerprint shape matches what <see cref="FingerprintLegs"/> produces from the broker
	/// representation. Non-OCC symbols fall through to a stock-leg key.</summary>
	private static string FingerprintProposal(IEnumerable<(string Symbol, string Action)> legs) =>
		string.Join("|", legs.Select(l =>
		{
			var side = l.Action.ToUpperInvariant();
			var parsed = ParsingHelpers.ParseOptionSymbol(l.Symbol);
			if (parsed == null) return $"{side}:{l.Symbol.ToUpperInvariant()}:STOCK";
			var cp = parsed.CallPut.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? "C" : "P";
			return $"{side}:{parsed.Root.ToUpperInvariant()}:{parsed.ExpiryDate:yyyy-MM-dd}:{parsed.Strike.ToString("F2", CultureInfo.InvariantCulture)}:{cp}";
		}).OrderBy(s => s, StringComparer.Ordinal));

	// Coalescing state: the watch/scan loop hands BOTH auto-executors the same per-cycle token, so the
	// second one reuses the first's outcome instead of re-pulling. Each refresh is two HTTP round-trips
	// (today's-orders + open-orders); the management+opener pair otherwise doubled the order-endpoint
	// calls per tick and tripped Webull's rate limiter (429 → fail-closed skip). Reference identity, so
	// a fresh `new object()` per tick guarantees inequality across ticks without any clock dependency.
	private object? _lastCycleToken;
	private bool _lastCycleResult;

	/// <summary>Convenience: refresh-or-skip pattern. Wraps <see cref="RefreshAsync"/> in
	/// try/catch, logs the failure, and returns false on any error. Callers use the
	/// return value to decide whether to proceed with live submission attempts.
	/// <para>When <paramref name="cycleToken"/> is supplied and matches the token of the previous call,
	/// the broker round-trip is skipped and that call's outcome is reused — this is how the two
	/// auto-executors share a single pull per tick. A null token (the `wa trade` paths) always refreshes,
	/// so coalescing is opt-in and a forgotten/duplicate token degrades to the prior always-refresh
	/// behavior, never to a stale snapshot.</para></summary>
	public async Task<bool> TryRefreshAsync(CancellationToken cancellation, object? cycleToken = null)
	{
		if (cycleToken != null && ReferenceEquals(cycleToken, _lastCycleToken)) return _lastCycleResult;

		bool ok;
		try
		{
			await RefreshAsync(cancellation);
			ok = true;
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]broker state refresh failed:[/] {Markup.Escape(ex.Message)} — auto-execute skipped this tick (fail-closed).");
			_activeFingerprintCounts = null;
			_activeByFingerprint = null;
			_activeOrderCountByRoot = null;
			ok = false;
		}

		if (cycleToken != null) { _lastCycleToken = cycleToken; _lastCycleResult = ok; }
		return ok;
	}
}

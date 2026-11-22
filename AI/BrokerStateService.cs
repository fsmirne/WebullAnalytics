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
	// Fingerprints of orders still WORKING — active but not fully filled (pending, partially filled, unknown
	// status, or placed locally and not yet echoed by the broker). Close/leg-in dedup reads this, not the
	// active multiset: a FILLED close has already shrunk the broker position, so it must not block the next
	// close of the remainder (scale-out tranches 2/3, an auto take-profit after a manual trim).
	private HashSet<string>? _workingFingerprints;
	// Fingerprints of orders this machine sent with no broker acknowledgement on record (ledger state "sending", not yet reported by
	// the broker): they block re-sends like any working order, and the opener will not complete a split structure around one.
	private HashSet<string>? _unconfirmedFingerprints;
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
	// Fully filled — the order is done and the broker position already reflects it. Anything else active
	// (partial fill, missing status) is still working.
	private static bool IsFilledStatus(string? status) => string.Equals(status, "FILLED", StringComparison.OrdinalIgnoreCase);

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
	public async Task RefreshAsync(CancellationToken cancellation, object? cycleToken = null)
	{
		using var client = new WebullOpenApiClient(_account);

		// Pull both endpoints. Today's-orders catches everything (working + filled); /open is a
		// safety net in case today's-orders is missing carried-over orders for some reason. We
		// union them and dedup by client_order_id (within the orders list).
		// Today's orders come from the tick-shared feed: the position source already pulled them earlier in this tick,
		// and a second order-history call seconds later is what tripped Webull's 2-req/2-s limit (HTTP 429).
		var todayOrders = await TodayOrdersFeed.For(_account).GetAsync(cycleToken, cancellation);
		var openOrders = await client.ListOpenOrdersAsync(cancellation);
		var ledgerEntries = _ledger?.ReadFor(MarketTime.TodayEt());
		var (resolved, voided) = ledgerEntries == null ? ([], []) : await ResolveUnconfirmedAsync(client, ledgerEntries, ReportedClientIds(todayOrders.Concat(openOrders)), cancellation);
		LoadSnapshot(todayOrders, openOrders.Concat(resolved), ledgerEntries?.Where(e => e.ClientOrderId == null || !voided.Contains(e.ClientOrderId)).ToList());
	}

	/// <summary>How long a sent-but-unacknowledged order must stay unknown to the broker before a not-found lookup is taken to mean it was never
	/// placed. Order detail is read seconds after a placement elsewhere (the marketable-close chaser), but an order that does exist and is voided
	/// by a lookup that came too early is a duplicate — so a missing order is believed only after this.</summary>
	internal static readonly TimeSpan UnconfirmedNotFoundGrace = TimeSpan.FromMinutes(2);

	/// <summary>True when an unconfirmed order sent at <paramref name="sentAt"/> has been unknown to the broker long enough, at
	/// <paramref name="now"/>, to be voided. An entry without a send time (never written by this build) is never voided this way.</summary>
	internal static bool PastNotFoundGrace(DateTime? sentAt, DateTime now) => sentAt is DateTime at && now - at >= UnconfirmedNotFoundGrace;

	/// <summary>Looks up each unconfirmed ledger order the broker's lists don't show yet. One Webull returns is passed back to join the
	/// snapshot (broker truth then governs it, including rejected = free to retry). One Webull has no order for (it answers
	/// <see cref="WebullOpenApiException.IsOrderNotFound"/>; an empty detail reads the same) is voided once
	/// <see cref="PastNotFoundGrace"/>. A lookup that fails any other way leaves the order unconfirmed, so it keeps blocking (fail-closed).</summary>
	private async Task<(List<WebullOpenApiClient.OpenOrder> Resolved, HashSet<string> Voided)> ResolveUnconfirmedAsync(WebullOpenApiClient client, IReadOnlyList<LocalOrderLedger.Entry> ledgerEntries, HashSet<string> reportedClientIds, CancellationToken cancellation)
	{
		var resolved = new List<WebullOpenApiClient.OpenOrder>();
		var voided = new HashSet<string>(StringComparer.Ordinal);
		foreach (var e in ledgerEntries)
		{
			if (!e.Unconfirmed || string.IsNullOrEmpty(e.ClientOrderId) || reportedClientIds.Contains(e.ClientOrderId)) continue;
			WebullOpenApiClient.OrderDetail? detail;
			try { detail = await client.GetOrderAsync(e.ClientOrderId, cancellation); }
			catch (WebullOpenApiException ex) when (ex.IsOrderNotFound) { detail = null; }
			catch (Exception ex) when (!cancellation.IsCancellationRequested)
			{
				AnsiConsole.MarkupLine($"[dim]unconfirmed order {Markup.Escape(e.ClientOrderId)}: lookup failed ({Markup.Escape(ex.Message)}) — still held as placed.[/]");
				continue;
			}
			if (detail?.Orders is { Count: > 0 } orders)
			{
				resolved.Add(new WebullOpenApiClient.OpenOrder(null, detail.ComboType, detail.ComboOrderId, orders));
				continue;
			}
			if (!PastNotFoundGrace(e.At, MarketTime.NowEt())) continue;
			AppendLedger(e.Root, e.Open, e.Fingerprint, e.ClientOrderId, LocalOrderLedger.Void);
			voided.Add(e.ClientOrderId);
			AnsiConsole.MarkupLine($"[yellow]unconfirmed order {Markup.Escape(e.ClientOrderId)} is not at the broker {UnconfirmedNotFoundGrace.TotalMinutes:0} min after it was sent — treated as never placed.[/]");
		}
		return (resolved, voided);
	}

	/// <summary>Every client_order_id the broker reported, any status.</summary>
	private static HashSet<string> ReportedClientIds(IEnumerable<WebullOpenApiClient.OpenOrder> combos) =>
		combos.SelectMany(c => c.Orders ?? []).Select(o => o.ClientOrderId).OfType<string>().Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);

	/// <summary>Builds the snapshot from already-fetched orders (what <see cref="RefreshAsync"/> does after its two calls), so it can run without the network.
	/// With a <paramref name="cycleToken"/> the snapshot also stands as that tick's refresh, so <see cref="TryRefreshAsync"/> on the same token reuses it.</summary>
	internal void LoadSnapshot(IEnumerable<WebullOpenApiClient.OpenOrder> todayOrders, IEnumerable<WebullOpenApiClient.OpenOrder> openOrders, IReadOnlyList<LocalOrderLedger.Entry>? ledgerEntries, object? cycleToken = null)
	{
		if (cycleToken != null) { _lastCycleToken = cycleToken; _lastCycleResult = true; }
		var seenClientIds = new HashSet<string>(StringComparer.Ordinal);
		var fingerprints = new Dictionary<string, int>(StringComparer.Ordinal);
		var byFingerprint = new Dictionary<string, List<WebullOpenApiClient.OrderDetailOrder>>(StringComparer.Ordinal);
		var working = new HashSet<string>(StringComparer.Ordinal);
		var unconfirmed = new HashSet<string>(StringComparer.Ordinal);
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
					if (!byFingerprint.TryGetValue(fp, out var list)) byFingerprint[fp] = list = new List<WebullOpenApiClient.OrderDetailOrder>();
					list.Add(order);
					// A resting stop is a guard, not a close: it must neither net against the day's opens (a position with a stop on it is still open)
					// nor block a real close of the same legs (the closer cancels the stop first). Only a filled stop counts as the close it became.
					if (BrokerOrder.IsStop(order.OrderType) && !IsFilledStatus(order.Status)) continue;
					fingerprints[fp] = fingerprints.TryGetValue(fp, out var fpCount) ? fpCount + 1 : 1;
					if (!IsFilledStatus(order.Status)) working.Add(fp);
					var root = RootOf(order.Legs);
					if (root != null && !IsCloseIntent(order.PositionIntent)) rootCount[root] = rootCount.TryGetValue(root, out var c) ? c + 1 : 1;
				}
			}
		}

		Ingest(todayOrders);
		Ingest(openOrders);

		if (ledgerEntries != null)
			MergeLedgerEntries(ledgerEntries, reportedClientIds, fingerprints, working, rootCount, unconfirmed);

		_activeFingerprintCounts = fingerprints;
		_activeByFingerprint = byFingerprint;
		_workingFingerprints = working;
		_unconfirmedFingerprints = unconfirmed;
		_activeOrderCountByRoot = rootCount;
	}

	/// <summary>Folds today's locally-placed orders into the broker snapshot. An entry is skipped once the
	/// broker reports its client_order_id (any status — broker truth, including canceled/rejected = retryable,
	/// governs from then on). Surviving entries count into the fingerprint multiset and the working set (status
	/// unknown — it may have filled, but the broker hasn't said so yet, so fail closed), and opening entries
	/// count toward the per-root daily cap. Entries never acknowledged by the broker are also collected into
	/// <paramref name="unconfirmed"/>.</summary>
	internal static void MergeLedgerEntries(IReadOnlyList<LocalOrderLedger.Entry> entries, HashSet<string> reportedClientIds, Dictionary<string, int> fingerprints, HashSet<string> working, Dictionary<string, int> rootCount, HashSet<string>? unconfirmed = null)
	{
		foreach (var e in entries)
		{
			if (!string.IsNullOrEmpty(e.ClientOrderId) && reportedClientIds.Contains(e.ClientOrderId)) continue;
			fingerprints[e.Fingerprint] = fingerprints.TryGetValue(e.Fingerprint, out var c) ? c + 1 : 1;
			working.Add(e.Fingerprint);
			if (e.Unconfirmed) unconfirmed?.Add(e.Fingerprint);
			if (e.Open) rootCount[e.Root] = rootCount.TryGetValue(e.Root, out var rc) ? rc + 1 : 1;
		}
	}

	/// <summary>An order about to be sent, as <see cref="RecordSending"/> recorded it — what <see cref="RecordPlaced"/> and
	/// <see cref="RecordVoid"/> need to settle it.</summary>
	internal sealed record PendingPlacement(string Fingerprint, string Root, bool IsOpen, string ClientOrderId, bool WasWorking);

	/// <summary>Records an order this process is about to send, BEFORE the send, so the snapshot and the persisted ledger (cross-restart,
	/// cross-process) see it whatever the send's outcome — instead of waiting out the broker's history lag. Patches the in-memory snapshot
	/// in place for the rest of this tick. Opening orders (<paramref name="isOpen"/>) count toward the daily cap; closes participate in leg-set
	/// dedup only. Settle it with <see cref="RecordPlaced"/> or <see cref="RecordVoid"/>; left unsettled (no answer came back) it stays
	/// unconfirmed and blocks like a placed order.</summary>
	public PendingPlacement RecordSending(string ticker, IEnumerable<(string Symbol, string Action)> legs, string clientOrderId, bool isOpen)
	{
		var fp = FingerprintProposal(legs);
		var pending = new PendingPlacement(fp, ticker.ToUpperInvariant(), isOpen, clientOrderId, _workingFingerprints?.Contains(fp) ?? false);
		AppendLedger(pending.Root, isOpen, pending.Fingerprint, clientOrderId, LocalOrderLedger.Sending);
		if (_activeFingerprintCounts != null)
			_activeFingerprintCounts[pending.Fingerprint] = _activeFingerprintCounts.GetValueOrDefault(pending.Fingerprint) + 1;
		_workingFingerprints?.Add(pending.Fingerprint);
		_unconfirmedFingerprints?.Add(pending.Fingerprint);
		if (isOpen && _activeOrderCountByRoot != null)
			_activeOrderCountByRoot[pending.Root] = _activeOrderCountByRoot.GetValueOrDefault(pending.Root) + 1;
		return pending;
	}

	/// <summary>The broker acknowledged <paramref name="pending"/>. Webull echoes the client_order_id it was sent; should it ever answer with
	/// another, the order is re-keyed to the broker's id so the broker reporting it releases the ledger entry.</summary>
	public void RecordPlaced(PendingPlacement pending, string? brokerClientOrderId)
	{
		if (!string.IsNullOrEmpty(brokerClientOrderId) && brokerClientOrderId != pending.ClientOrderId)
		{
			AppendLedger(pending.Root, pending.IsOpen, pending.Fingerprint, pending.ClientOrderId, LocalOrderLedger.Void);
			AppendLedger(pending.Root, pending.IsOpen, pending.Fingerprint, brokerClientOrderId, LocalOrderLedger.Placed);
		}
		else AppendLedger(pending.Root, pending.IsOpen, pending.Fingerprint, pending.ClientOrderId, LocalOrderLedger.Placed);
		_unconfirmedFingerprints?.Remove(pending.Fingerprint);
	}

	/// <summary><paramref name="pending"/> definitely never became an order: undoes <see cref="RecordSending"/>, so the same leg set may be
	/// tried again and the daily cap is not spent on it.</summary>
	public void RecordVoid(PendingPlacement pending)
	{
		AppendLedger(pending.Root, pending.IsOpen, pending.Fingerprint, pending.ClientOrderId, LocalOrderLedger.Void);
		if (_activeFingerprintCounts != null && _activeFingerprintCounts.TryGetValue(pending.Fingerprint, out var c))
		{
			if (c <= 1) _activeFingerprintCounts.Remove(pending.Fingerprint);
			else _activeFingerprintCounts[pending.Fingerprint] = c - 1;
		}
		if (!pending.WasWorking) _workingFingerprints?.Remove(pending.Fingerprint);
		_unconfirmedFingerprints?.Remove(pending.Fingerprint);
		if (pending.IsOpen && _activeOrderCountByRoot != null && _activeOrderCountByRoot.TryGetValue(pending.Root, out var rc))
		{
			if (rc <= 1) _activeOrderCountByRoot.Remove(pending.Root);
			else _activeOrderCountByRoot[pending.Root] = rc - 1;
		}
	}

	/// <summary>True when the leg set matches an order sent with no broker acknowledgement on record. The opener holds a split structure while
	/// one half is in this state: completing it around a half that may never have been placed would leave the other half open on its own.</summary>
	public bool HasUnconfirmedMatching(IEnumerable<(string Symbol, string Action)> proposalLegs) =>
		_unconfirmedFingerprints != null && _unconfirmedFingerprints.Contains(FingerprintProposal(proposalLegs));

	private void AppendLedger(string root, bool isOpen, string fingerprint, string clientOrderId, string state)
	{
		try { _ledger?.Append(root, isOpen, fingerprint, clientOrderId, MarketTime.NowEt(), state); }
		catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]local order ledger append failed:[/] {Markup.Escape(ex.Message)} — dedup holds in-memory for this run only."); }
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

	/// <summary>Returns true when the proposal's leg set matches a WORKING order — pending, partially filled,
	/// or placed by this machine and not yet reported by the broker. Close and leg-in dedup: never stack a
	/// second order on one still resting. A fully FILLED order does not match — it already changed the broker
	/// position the caller sized from, so a further close of the remainder is legitimate. Returns false when
	/// <see cref="IsReady"/> is false; callers should have short-circuited beforehand.</summary>
	public bool HasPendingMatching(IEnumerable<(string Symbol, string Action)> proposalLegs) =>
		_workingFingerprints != null && _workingFingerprints.Contains(FingerprintProposal(proposalLegs));

	/// <summary>Opener-side dedup: blocks only while today's matching OPEN orders outnumber the matching
	/// CLOSES (a close is the same legs with sides inverted — a distinct fingerprint). A structure opened
	/// and then closed today nets to zero and may be deliberately re-opened (live 2026-06-11: the morning's
	/// DoubleDiagonal, closed before noon, blocked the afternoon's `scan --submit-override` re-entry); a
	/// just-filled un-closed open stays net-positive and keeps blocking. ONLY for opening proposals — the
	/// management executor's close dedup uses the working set (<see cref="HasPendingMatching"/>): netting a
	/// close against its same-day open would report 0 while a close order rests, enabling a double-close.</summary>
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
	internal static string Fingerprint(IEnumerable<(string Symbol, string Action)> legs) => FingerprintProposal(legs);

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

	/// <summary>The exception message of the most recent failed <see cref="TryRefreshAsync"/>, null after a success — lets a fail-closed caller record WHY it skipped.</summary>
	public string? LastRefreshError { get; private set; }

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
			await RefreshAsync(cancellation, cycleToken);
			ok = true;
			LastRefreshError = null;
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]broker state refresh failed:[/] {Markup.Escape(ex.Message)} — auto-execute skipped this tick (fail-closed).");
			_activeFingerprintCounts = null;
			_activeByFingerprint = null;
			_workingFingerprints = null;
			_unconfirmedFingerprints = null;
			_activeOrderCountByRoot = null;
			LastRefreshError = ex.Message;
			ok = false;
		}

		if (cycleToken != null) { _lastCycleToken = cycleToken; _lastCycleResult = ok; }
		return ok;
	}
}

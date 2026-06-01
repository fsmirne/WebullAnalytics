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
	// Union of pending + today's filled/partial orders, keyed by leg-set fingerprint. Canceled and
	// rejected orders are excluded — they represent attempted-but-not-resting state and shouldn't
	// gate fresh submissions of the same shape.
	private HashSet<string>? _activeLegSetFingerprints;
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

	public BrokerStateService(TradeAccount account) { _account = account; }

	/// <summary>True once <see cref="RefreshAsync"/> has succeeded at least once. When false,
	/// callers should skip live submission this tick.</summary>
	public bool IsReady => _activeLegSetFingerprints != null;

	/// <summary>Count of distinct ACTIVE orders today (filled or pending, excluding canceled/rejected) for
	/// <paramref name="ticker"/>. The daily cap is scoped per ticker, so concurrent single-ticker watch/scan
	/// processes on the same account enforce independent caps. Counts broker ORDERS: a split structure
	/// (diagonal/calendar vertical, double calendar/diagonal) places two orders and counts as two. Holds
	/// across pending → filled transitions and process restarts (the broker remembers; re-queried each tick).</summary>
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
		var fingerprints = new HashSet<string>(StringComparer.Ordinal);
		var byFingerprint = new Dictionary<string, List<WebullOpenApiClient.OrderDetailOrder>>(StringComparer.Ordinal);
		var rootCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		void Ingest(IEnumerable<WebullOpenApiClient.OpenOrder> combos)
		{
			foreach (var combo in combos)
			{
				if (combo.Orders == null) continue;
				foreach (var order in combo.Orders)
				{
					if (!IsActiveStatus(order.Status)) continue;
					if (!string.IsNullOrEmpty(order.ClientOrderId) && !seenClientIds.Add(order.ClientOrderId)) continue;
					var fp = FingerprintLegs(order.Legs);
					if (string.IsNullOrEmpty(fp)) continue;
					fingerprints.Add(fp);
					if (!byFingerprint.TryGetValue(fp, out var list)) byFingerprint[fp] = list = new List<WebullOpenApiClient.OrderDetailOrder>();
					list.Add(order);
					var root = RootOf(order.Legs);
					if (root != null) rootCount[root] = rootCount.TryGetValue(root, out var c) ? c + 1 : 1;
				}
			}
		}

		Ingest(todayOrders);
		Ingest(openOrders);

		_activeLegSetFingerprints = fingerprints;
		_activeByFingerprint = byFingerprint;
		_activeOrderCountByRoot = rootCount;
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
		if (_activeLegSetFingerprints == null) return false;
		var fp = FingerprintProposal(proposalLegs);
		return _activeLegSetFingerprints.Contains(fp);
	}

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

	/// <summary>Convenience: refresh-or-skip pattern. Wraps <see cref="RefreshAsync"/> in
	/// try/catch, logs the failure, and returns false on any error. Callers use the
	/// return value to decide whether to proceed with live submission attempts.</summary>
	public async Task<bool> TryRefreshAsync(CancellationToken cancellation)
	{
		try
		{
			await RefreshAsync(cancellation);
			return true;
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]broker state refresh failed:[/] {Markup.Escape(ex.Message)} — auto-execute skipped this tick (fail-closed).");
			_activeLegSetFingerprints = null;
			_activeByFingerprint = null;
			_activeOrderCountByRoot = null;
			return false;
		}
	}
}

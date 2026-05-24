using Spectre.Console;
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
	private HashSet<string>? _pendingLegSetFingerprints;

	public BrokerStateService(TradeAccount account) { _account = account; }

	/// <summary>True once <see cref="RefreshAsync"/> has succeeded at least once. When false,
	/// callers should skip live submission this tick.</summary>
	public bool IsReady => _pendingLegSetFingerprints != null;

	/// <summary>Pull pending orders from Webull. Throws on API/network failure — caller
	/// catches and decides whether to skip the tick. Successive calls overwrite the
	/// previous snapshot atomically.</summary>
	public async Task RefreshAsync(CancellationToken cancellation)
	{
		using var client = new WebullOpenApiClient(_account);
		var openOrders = await client.ListOpenOrdersAsync(cancellation);
		var fingerprints = new HashSet<string>(StringComparer.Ordinal);
		foreach (var combo in openOrders)
		{
			if (combo.Orders == null) continue;
			foreach (var order in combo.Orders)
			{
				var fp = FingerprintLegs(order.Legs);
				if (!string.IsNullOrEmpty(fp)) fingerprints.Add(fp);
			}
		}
		_pendingLegSetFingerprints = fingerprints;
	}

	/// <summary>Returns true when the proposal's leg set matches a pending order at the broker.
	/// Returns false when <see cref="IsReady"/> is false — callers should have already
	/// short-circuited in that case rather than relying on this return.</summary>
	public bool HasPendingMatching(IEnumerable<(string Symbol, string Action)> proposalLegs)
	{
		if (_pendingLegSetFingerprints == null) return false;
		var fp = FingerprintProposal(proposalLegs);
		return _pendingLegSetFingerprints.Contains(fp);
	}

	/// <summary>Canonical fingerprint of an order's leg set. Sorts by side+symbol so leg order
	/// doesn't matter (Webull may return legs in a different order than we submitted).</summary>
	private static string FingerprintLegs(IEnumerable<WebullOpenApiClient.OrderDetailLeg>? legs)
	{
		if (legs == null) return "";
		return string.Join("|", legs
			.Where(l => !string.IsNullOrEmpty(l.Symbol) && !string.IsNullOrEmpty(l.Side))
			.Select(l => $"{l.Side!.ToUpperInvariant()}:{l.Symbol!.ToUpperInvariant()}")
			.OrderBy(s => s, StringComparer.Ordinal));
	}

	private static string FingerprintProposal(IEnumerable<(string Symbol, string Action)> legs) =>
		string.Join("|", legs.Select(l => $"{l.Action.ToUpperInvariant()}:{l.Symbol.ToUpperInvariant()}")
			.OrderBy(s => s, StringComparer.Ordinal));

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
			_pendingLegSetFingerprints = null;
			return false;
		}
	}
}

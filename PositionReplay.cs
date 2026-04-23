namespace WebullAnalytics;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Linear cash-flow replay that produces PositionRows directly from a trade stream.
/// Replaces StrategyGrouper's FIFO lot allocation + 5-branch adjusted-basis computation.
/// See docs/superpowers/specs/2026-04-23-position-replay-rewrite-design.md for rules.
/// </summary>
internal static class PositionReplay
{
	/// <summary>An active position being tracked during replay.</summary>
	internal sealed class Lineage
	{
		public int Id;
		public string Underlying = "";
		public Dictionary<string, (Side Side, int Qty)> OpenLegs = new(StringComparer.Ordinal);
		public int Multiplier;
		public decimal RunningCash;
		public int UnitQty;
		public decimal FirstEntryCash;
		public int FirstEntryQty;
		public DateTime FirstEntryTimestamp;
		public List<NetDebitTrade> TradeHistory = new();
		public Dictionary<string, int> StockShareCount = new(StringComparer.Ordinal); // share count for stock matchKeys; option legs absent
	}

	/// <summary>One state-machine input — either a strategy order (multiple trades sharing ParentStrategySeq) or a standalone trade.</summary>
	internal sealed class Event
	{
		public DateTime Timestamp;
		public int? ParentStrategySeq;
		public List<Trade> Trades = new();
		public bool IsStrategyOrder => ParentStrategySeq.HasValue;
	}

	/// <summary>
	/// Entry point. Same return shape as PositionTracker.BuildPositionRows so callers are unchanged.
	/// </summary>
	public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
		Execute(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
	{
		var eventsPerUnderlying = BuildEventsPerUnderlying(allTrades);

		var allLineages = new List<Lineage>();
		int lineageIdCounter = 0;
		foreach (var (underlying, events) in eventsPerUnderlying)
		{
			var active = new List<Lineage>();
			foreach (var evt in events)
				ApplyEvent(active, evt, underlying, ref lineageIdCounter);
			allLineages.AddRange(active);
		}

		return EmitRows(allLineages);
	}

	/// <summary>Groups trades into Events (strategy orders share ParentStrategySeq; standalones are each their own event),
	/// keyed by underlying, sorted chronologically within each underlying.</summary>
	private static Dictionary<string, List<Event>> BuildEventsPerUnderlying(List<Trade> allTrades)
	{
		var byUnderlying = new Dictionary<string, List<Event>>(StringComparer.Ordinal);

		// Strategy orders: group option-leg trades by ParentStrategySeq. Skip the OptionStrategy parent trade row itself —
		// the parent's cash is derived from the legs. Also groups same-timestamp stock+option trades (covered calls etc.)
		// when they share a ParentStrategySeq.
		var legsByParentSeq = allTrades
			.Where(t => t.ParentStrategySeq.HasValue && t.Asset != Asset.OptionStrategy)
			.GroupBy(t => t.ParentStrategySeq!.Value);
		foreach (var group in legsByParentSeq)
		{
			var trades = group.OrderBy(t => t.Seq).ToList();
			var underlying = ExtractUnderlying(trades[0]);
			var evt = new Event { Timestamp = trades.Min(t => t.Timestamp), ParentStrategySeq = group.Key, Trades = trades };
			if (!byUnderlying.TryGetValue(underlying, out var list)) { list = new List<Event>(); byUnderlying[underlying] = list; }
			list.Add(evt);
		}

		// Standalones: trades without ParentStrategySeq, not strategy parents.
		foreach (var t in allTrades.Where(t => !t.ParentStrategySeq.HasValue && t.Asset != Asset.OptionStrategy))
		{
			var underlying = ExtractUnderlying(t);
			var evt = new Event { Timestamp = t.Timestamp, ParentStrategySeq = null, Trades = new List<Trade> { t } };
			if (!byUnderlying.TryGetValue(underlying, out var list)) { list = new List<Event>(); byUnderlying[underlying] = list; }
			list.Add(evt);
		}

		foreach (var (_, list) in byUnderlying)
			list.Sort((a, b) => a.Timestamp != b.Timestamp ? a.Timestamp.CompareTo(b.Timestamp) : a.Trades[0].Seq.CompareTo(b.Trades[0].Seq));

		return byUnderlying;
	}

	/// <summary>Extracts the underlying ticker from a trade (stock uses MatchKey; option uses the root of the OCC symbol).</summary>
	private static string ExtractUnderlying(Trade t)
	{
		if (t.Asset == Asset.Stock) return t.MatchKey;
		var parsed = MatchKeys.ParseOption(t.MatchKey);
		return parsed?.parsed.Root ?? t.MatchKey;
	}

	/// <summary>Applies one event to the active-lineage list.</summary>
	private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
	{
		if (evt.IsStrategyOrder) { ApplyStrategyOrderEvent(active, evt, underlying, ref lineageIdCounter); return; }
		ApplyStandaloneEvent(active, evt, underlying, ref lineageIdCounter);
	}

	private static void ApplyStandaloneEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
	{
		var t = evt.Trades[0];

		// Rule 1: reduce existing open leg (opposite direction).
		foreach (var lin in active)
		{
			if (!lin.OpenLegs.TryGetValue(t.MatchKey, out var existing)) continue;
			if (existing.Side != t.Side)
			{
				ApplyEventToLineage(lin, evt, isNewLineage: false);
				return;
			}
		}

		// Rule 2 handled in Task 8.

		// Rule 3: new-leg trade. Search orphans in the same bucket (underlying + call/put) with UnitQty == trade.Qty.
		var bucket = GetLegBucket(t);
		var orphansInBucket = active
			.Where(lin => lin.OpenLegs.Count == 1 && lin.UnitQty == t.Qty && LineageBucket(lin) == bucket)
			.ToList();

		if (orphansInBucket.Count == 1)
		{
			ApplyEventToLineage(orphansInBucket[0], evt, isNewLineage: false);
			return;
		}

		// Zero or multiple orphans: spawn new standalone lineage.
		var newLineage = new Lineage
		{
			Id = ++lineageIdCounter,
			Underlying = underlying,
			Multiplier = (int)t.Multiplier,
			FirstEntryTimestamp = evt.Timestamp
		};
		active.Add(newLineage);
		ApplyEventToLineage(newLineage, evt, isNewLineage: true);
	}

	/// <summary>Bucket key: stock vs per-call-put. Two standalone legs in different buckets cannot match as orphan.</summary>
	private static string GetLegBucket(Trade t)
	{
		if (t.Asset == Asset.Stock) return "stock";
		var parsed = MatchKeys.ParseOption(t.MatchKey);
		return parsed?.parsed.CallPut == "C" ? "call" : "put";
	}

	/// <summary>A lineage's bucket is the bucket of its first (and in single-leg lineages, only) open leg.</summary>
	private static string LineageBucket(Lineage lin)
	{
		if (lin.OpenLegs.Count == 0) return "stock"; // deactivated; bucket irrelevant
		var firstKey = lin.OpenLegs.Keys.First();
		if (firstKey.StartsWith("option:", StringComparison.Ordinal))
		{
			var parsed = MatchKeys.ParseOption(firstKey);
			return parsed?.parsed.CallPut == "C" ? "call" : "put";
		}
		return "stock";
	}

	private static void ApplyStrategyOrderEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
	{
		// Determine which active lineages this event touches (any event leg matches an open leg).
		var touchedLineages = new HashSet<Lineage>();
		foreach (var t in evt.Trades)
		{
			foreach (var lin in active)
			{
				if (lin.OpenLegs.ContainsKey(t.MatchKey))
					touchedLineages.Add(lin);
			}
		}

		Lineage target;
		if (touchedLineages.Count == 0)
		{
			// Zero touched → new lineage with all event legs.
			target = new Lineage
			{
				Id = ++lineageIdCounter,
				Underlying = underlying,
				Multiplier = (int)evt.Trades[0].Multiplier,
				FirstEntryTimestamp = evt.Timestamp
			};
			active.Add(target);
		}
		else if (touchedLineages.Count == 1)
		{
			target = touchedLineages.First();
		}
		else
		{
			// Multiple touched: oldest by FirstEntryTimestamp wins (deterministic tiebreaker). Log for audit.
			target = touchedLineages.OrderBy(l => l.FirstEntryTimestamp).First();
			Console.Error.WriteLine($"[PositionReplay] Warning: event at {evt.Timestamp} touches {touchedLineages.Count} lineages on {underlying}; assigned to oldest (id={target.Id}).");
		}

		ApplyEventToLineage(target, evt, isNewLineage: touchedLineages.Count == 0);
	}

	/// <summary>Updates a lineage's open legs and running cash for one event. For a new lineage, also sets FirstEntry*.</summary>
	private static void ApplyEventToLineage(Lineage lin, Event evt, bool isNewLineage)
	{
		// Compute event's total cash impact: Σ over legs of (side_sign × qty × price × multiplier), where side_sign = +1 for Buy, −1 for Sell.
		// This "positive = cash paid out" convention matches RunningCash semantics.
		decimal eventCash = 0m;
		int eventQty = 0;
		foreach (var t in evt.Trades)
		{
			var signedCash = (t.Side == Side.Buy ? 1m : -1m) * t.Qty * t.Price * t.Multiplier;
			eventCash += signedCash;
			eventQty = Math.Max(eventQty, t.Qty); // for strategy orders, all legs share qty
			lin.TradeHistory.Add(new NetDebitTrade(t.Timestamp, t.Instrument, t.Side, t.Qty, t.Price, signedCash));
		}

		lin.RunningCash += eventCash;

		if (isNewLineage)
		{
			lin.FirstEntryCash = eventCash;
			lin.FirstEntryQty = eventQty;
		}

		// Apply each leg: match existing open leg (reduce/add) or add as new leg.
		foreach (var t in evt.Trades)
		{
			var signedQty = t.Side == Side.Buy ? t.Qty : -t.Qty;
			if (lin.OpenLegs.TryGetValue(t.MatchKey, out var existing))
			{
				var newSigned = (existing.Side == Side.Buy ? existing.Qty : -existing.Qty) + signedQty;
				if (newSigned == 0)
					lin.OpenLegs.Remove(t.MatchKey);
				else
					lin.OpenLegs[t.MatchKey] = (newSigned > 0 ? Side.Buy : Side.Sell, Math.Abs(newSigned));
			}
			else
			{
				lin.OpenLegs[t.MatchKey] = (t.Side, t.Qty);
			}
		}

		// UnitQty = min of leg qtys across open legs (balanced after strategy orders in the common case).
		lin.UnitQty = lin.OpenLegs.Values.Count > 0 ? lin.OpenLegs.Values.Min(v => v.Qty) : 0;
	}

	/// <summary>Emits PositionRow output from finalized lineages. Filled out in Task 10.</summary>
	private static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
		EmitRows(List<Lineage> lineages)
	{
		return (new List<PositionRow>(), new Dictionary<int, StrategyAdjustment>(), new Dictionary<string, List<NetDebitTrade>>());
	}
}

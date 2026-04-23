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

	/// <summary>Applies one event to the active-lineage list. Rules are added in subsequent tasks; for now this is a no-op.</summary>
	private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
	{
		// Rules added in Tasks 5–8.
	}

	/// <summary>Emits PositionRow output from finalized lineages. Filled out in Task 10.</summary>
	private static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
		EmitRows(List<Lineage> lineages)
	{
		return (new List<PositionRow>(), new Dictionary<int, StrategyAdjustment>(), new Dictionary<string, List<NetDebitTrade>>());
	}
}

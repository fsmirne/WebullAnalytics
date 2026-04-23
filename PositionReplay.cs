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
		// Not yet implemented — returns empty while scaffolding is built out.
		return (new List<PositionRow>(), new Dictionary<int, StrategyAdjustment>(), new Dictionary<string, List<NetDebitTrade>>());
	}
}

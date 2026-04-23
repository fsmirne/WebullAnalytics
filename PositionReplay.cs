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
				ApplyEventToLineage(lin, evt, isNewLineage: false, active, ref lineageIdCounter);
				return;
			}
		}

		// Rule 2: standalone add (same direction as an existing open leg).
		foreach (var lin in active)
		{
			if (!lin.OpenLegs.TryGetValue(t.MatchKey, out var existing)) continue;
			if (existing.Side == t.Side)
			{
				if (lin.OpenLegs.Count == 1)
				{
					// Single-leg target: grow qty.
					ApplyEventToLineage(lin, evt, isNewLineage: false, active, ref lineageIdCounter);
				}
				else
				{
					// Multi-leg target: adding would break balance. Spawn a new standalone lineage for the add.
					var spawn = new Lineage
					{
						Id = ++lineageIdCounter,
						Underlying = underlying,
						Multiplier = (int)t.Multiplier,
						FirstEntryTimestamp = evt.Timestamp
					};
					active.Add(spawn);
					ApplyEventToLineage(spawn, evt, isNewLineage: true, active, ref lineageIdCounter);
				}
				return;
			}
		}

		// Rule 3: new-leg trade. Search orphans in the same bucket (underlying + call/put) with UnitQty == trade.Qty.
		var bucket = GetLegBucket(t);
		var orphansInBucket = active
			.Where(lin => lin.OpenLegs.Count == 1 && lin.UnitQty == t.Qty && LineageBucket(lin) == bucket)
			.ToList();

		if (orphansInBucket.Count == 1)
		{
			ApplyEventToLineage(orphansInBucket[0], evt, isNewLineage: false, active, ref lineageIdCounter);
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
		ApplyEventToLineage(newLineage, evt, isNewLineage: true, active, ref lineageIdCounter);
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

		ApplyEventToLineage(target, evt, isNewLineage: touchedLineages.Count == 0, active, ref lineageIdCounter);
	}

	/// <summary>Updates a lineage's open legs and running cash for one event. For a new lineage, also sets FirstEntry*.</summary>
	private static void ApplyEventToLineage(Lineage lin, Event evt, bool isNewLineage, List<Lineage> active, ref int lineageIdCounter)
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

		// Post-event invariant: every multi-leg lineage must be balanced (all open legs share one qty).
		// Any imbalance is split off immediately — keeping the lineage balanced at the common-qty minimum
		// and spawning a new standalone lineage for the excess, with proportional cash allocation.
		RebalanceLineage(lin, evt, active, ref lineageIdCounter);
	}

	/// <summary>
	/// If `lin` has any open leg whose qty exceeds the minimum leg qty, split the excess into a new
	/// standalone lineage. Proportionally allocates cash: the split lineage gets (excess/origUnit) × cash.
	/// Original lineage's RunningCash and FirstEntryCash are scaled down by (min/origUnit).
	/// </summary>
	private static void RebalanceLineage(Lineage lin, Event causingEvent, List<Lineage> active, ref int lineageIdCounter)
	{
		if (lin.OpenLegs.Count < 2) return;
		var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
		var maxQty = lin.OpenLegs.Values.Max(v => v.Qty);
		if (minQty == maxQty) return; // balanced

		// For each leg whose qty > minQty, split off the excess as a new standalone lineage.
		var imbalancedKeys = lin.OpenLegs.Where(kv => kv.Value.Qty > minQty).Select(kv => kv.Key).ToList();
		var origUnit = maxQty;

		foreach (var key in imbalancedKeys)
		{
			var (side, qty) = lin.OpenLegs[key];
			var excess = qty - minQty;

			// Proportional cash: spawn gets (excess / origUnit) of original cash values.
			var spawnRatio = (decimal)excess / origUnit;
			var spawn = new Lineage
			{
				Id = ++lineageIdCounter,
				Underlying = lin.Underlying,
				Multiplier = lin.Multiplier,
				FirstEntryTimestamp = lin.FirstEntryTimestamp,
				RunningCash = lin.RunningCash * spawnRatio,
				FirstEntryCash = lin.FirstEntryCash * spawnRatio,
				FirstEntryQty = excess,
				UnitQty = excess
			};
			spawn.OpenLegs[key] = (side, excess);
			// Copy a portion of trade history as a single synthetic split marker; downstream AdjustmentReportBuilder
			// reads this list for the "how we got here" display.
			spawn.TradeHistory.Add(new NetDebitTrade(causingEvent.Timestamp, $"[split from lineage {lin.Id}]", Side.Buy, excess, 0m, spawn.RunningCash));
			active.Add(spawn);

			// Original lineage keeps the minQty on this leg; cash scales by complementary ratio.
			lin.OpenLegs[key] = (side, minQty);
		}

		var keepRatio = (decimal)minQty / origUnit;
		lin.RunningCash *= keepRatio;
		lin.FirstEntryCash *= keepRatio;
		lin.FirstEntryQty = minQty;
		lin.UnitQty = minQty;
	}

	private static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
		EmitRows(List<Lineage> lineages)
	{
		var rows = new List<PositionRow>();
		var adjustments = new Dictionary<int, StrategyAdjustment>();
		var singleLegStandalones = new Dictionary<string, List<NetDebitTrade>>();

		foreach (var lin in lineages)
		{
			if (lin.OpenLegs.Count == 0) continue;

			var parentAdj = lin.UnitQty > 0 ? lin.RunningCash / (lin.UnitQty * (decimal)lin.Multiplier) : 0m;
			var parentInit = lin.FirstEntryQty > 0 ? lin.FirstEntryCash / (lin.FirstEntryQty * (decimal)lin.Multiplier) : 0m;

			// Single-leg lineage: emit one PositionRow, no parent.
			if (lin.OpenLegs.Count == 1)
			{
				var (matchKey, leg) = lin.OpenLegs.First();
				var (asset, optionKind, expiry, instrument) = ResolveLegMetadata(matchKey, lin);
				rows.Add(new PositionRow(
					Instrument: instrument,
					Asset: asset,
					OptionKind: optionKind,
					Side: leg.Side,
					Qty: leg.Qty,
					AvgPrice: Math.Abs(parentAdj),
					Expiry: expiry,
					IsStrategyLeg: false,
					InitialAvgPrice: Math.Abs(parentInit),
					AdjustedAvgPrice: Math.Abs(parentAdj),
					MatchKey: matchKey
				));

				if (lin.TradeHistory.Count > 1)
					singleLegStandalones[matchKey] = lin.TradeHistory;
				continue;
			}

			// Multi-leg lineage: emit parent + one leg row per open leg.
			var strategyKind = ClassifyLineage(lin);
			var parentInstrument = BuildParentInstrument(lin);
			var longestExpiry = ResolveLongestExpiry(lin);
			var parentSide = lin.RunningCash >= 0m ? Side.Buy : Side.Sell;

			rows.Add(new PositionRow(
				Instrument: parentInstrument,
				Asset: Asset.OptionStrategy,
				OptionKind: strategyKind,
				Side: parentSide,
				Qty: lin.UnitQty,
				AvgPrice: Math.Abs(parentAdj),
				Expiry: longestExpiry,
				IsStrategyLeg: false,
				InitialAvgPrice: Math.Abs(parentInit),
				AdjustedAvgPrice: Math.Abs(parentAdj)
			));

			// Per-leg rows: InitialAvgPrice = leg's own entry price (from trade history filtered by matchKey);
			// AdjustedAvgPrice = init + apportioned delta so signed sum equals parent's adj.
			var legEntryPrices = ComputeLegEntryPrices(lin);
			var legInitSum = 0m;
			foreach (var (mk, leg) in lin.OpenLegs)
			{
				var entryPrice = legEntryPrices.GetValueOrDefault(mk, 0m);
				legInitSum += (leg.Side == Side.Buy ? 1m : -1m) * entryPrice;
			}
			var perLegAdjDelta = Math.Abs(parentAdj) * (parentSide == Side.Buy ? 1m : -1m) - legInitSum;

			// Allocate the entire delta to a single "target leg" so per-leg signed sum equals parent adj.
			// Convention: prefer the first Buy leg (matches legacy ReconcileLegPricesToParent); for
			// credit-only structures (e.g., short strangles) fall back to the first Sell leg.
			var longLegKey = lin.OpenLegs.FirstOrDefault(kv => kv.Value.Side == Side.Buy).Key
				?? lin.OpenLegs.First().Key;

			foreach (var (mk, leg) in lin.OpenLegs.OrderByDescending(kv => kv.Key))
			{
				var (asset, optionKind, expiry, instrument) = ResolveLegMetadata(mk, lin);
				var initPrice = legEntryPrices.GetValueOrDefault(mk, 0m);
				var adjPrice = (mk == longLegKey) ? initPrice + perLegAdjDelta : initPrice;
				rows.Add(new PositionRow(
					Instrument: instrument,
					Asset: asset,
					OptionKind: optionKind,
					Side: leg.Side,
					Qty: leg.Qty,
					AvgPrice: initPrice,
					Expiry: expiry,
					IsStrategyLeg: true,
					InitialAvgPrice: initPrice,
					AdjustedAvgPrice: adjPrice,
					MatchKey: mk
				));
			}

			adjustments[lin.Id] = new StrategyAdjustment(lin.TradeHistory, lin.RunningCash, null, lin.FirstEntryCash);
		}

		return (rows, adjustments, singleLegStandalones);
	}

	/// <summary>Classifies the lineage's open-leg shape into a strategy kind label (Diagonal, Calendar, etc.).</summary>
	private static string ClassifyLineage(Lineage lin)
	{
		var parsedLegs = lin.OpenLegs.Keys
			.Select(mk => MatchKeys.ParseOption(mk)?.parsed)
			.Where(p => p != null)
			.ToList();
		if (parsedLegs.Count == 0) return "Stock";
		var distinctExpiries = parsedLegs.Select(p => p!.ExpiryDate).Distinct().Count();
		var distinctStrikes = parsedLegs.Select(p => p!.Strike).Distinct().Count();
		var distinctCallPut = parsedLegs.Select(p => p!.CallPut).Distinct().Count();
		return ParsingHelpers.ClassifyStrategyKind(parsedLegs.Count, distinctExpiries, distinctStrikes, distinctCallPut);
	}

	private static DateTime ResolveLongestExpiry(Lineage lin)
	{
		return lin.OpenLegs.Keys
			.Select(mk => MatchKeys.ParseOption(mk)?.parsed.ExpiryDate)
			.Where(d => d.HasValue)
			.Select(d => d!.Value)
			.DefaultIfEmpty(DateTime.MinValue)
			.Max();
	}

	private static string BuildParentInstrument(Lineage lin)
	{
		var longestExpiry = ResolveLongestExpiry(lin);
		return $"{lin.Underlying} {Formatters.FormatOptionDate(longestExpiry)}";
	}

	private static (Asset asset, string optionKind, DateTime? expiry, string instrument) ResolveLegMetadata(string matchKey, Lineage lin)
	{
		if (matchKey.StartsWith("option:", StringComparison.Ordinal))
		{
			var parsed = MatchKeys.ParseOption(matchKey);
			if (parsed != null)
			{
				var p = parsed.Value.parsed;
				return (Asset.Option, ParsingHelpers.CallPutDisplayName(p.CallPut), p.ExpiryDate, Formatters.FormatOptionDisplay(p.Root, p.ExpiryDate, p.Strike));
			}
		}
		return (Asset.Stock, "-", null, lin.Underlying);
	}

	private static Dictionary<string, decimal> ComputeLegEntryPrices(Lineage lin)
	{
		// For each open leg, compute its weighted-average entry price from the lineage's trade history.
		// Entry = trades whose matchKey is this leg's matchKey AND whose direction matches the leg's current side
		// (this excludes reducing / roll-close trades on the same matchKey that already left via Rule 1).
		var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
		foreach (var (mk, leg) in lin.OpenLegs)
		{
			var relevant = lin.TradeHistory.Where(h => h.Instrument != $"[split from lineage {lin.Id}]" && SameMatchKey(h, mk, leg.Side)).ToList();
			if (relevant.Count == 0) { result[mk] = 0m; continue; }
			var totalQty = relevant.Sum(h => h.Qty);
			var weighted = relevant.Sum(h => h.Price * h.Qty) / Math.Max(totalQty, 1);
			result[mk] = weighted;
		}
		return result;
	}

	private static bool SameMatchKey(NetDebitTrade t, string mk, Side openSide)
	{
		// NetDebitTrade doesn't carry matchKey directly; infer from Instrument. For option legs the instrument
		// includes ticker + expiry + strike; match against the lineage's open-leg matchKey.
		var parsed = MatchKeys.ParseOption(mk);
		if (parsed == null) return false;
		var expected = Formatters.FormatOptionDisplay(parsed.Value.parsed.Root, parsed.Value.parsed.ExpiryDate, parsed.Value.parsed.Strike);
		return t.Instrument == expected && t.Side == openSide;
	}
}

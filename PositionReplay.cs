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
		var evaluationDate = EvaluationDate.Today;
		foreach (var (underlying, events) in eventsPerUnderlying)
		{
			var active = new List<Lineage>();
			foreach (var evt in events)
				ApplyEvent(active, evt, underlying, ref lineageIdCounter);
			ApplyExpiries(active, evaluationDate);
			SettleImbalances(active, ref lineageIdCounter);
			AssertInvariants(active, $"end of replay for {underlying}", enforceBalance: true);
			allLineages.AddRange(active);
		}

		return EmitRows(allLineages);
	}

	/// <summary>
	/// Ports AdjustForExpiredStrategyLegs semantics: any open leg whose expiry is past the evaluation date
	/// gets a synthetic terminal event. OTM → silent removal (no cash). ITM → synthetic assignment with
	/// intrinsic cash impact matching the legacy formula at StrategyGrouper.cs:652–743.
	///
	/// Exact ITM formula: the legacy code treats unknown-spot expiries as OTM. We inherit that limitation —
	/// we don't have intraday spot history for past expiries, so we can't compute intrinsic cash impact
	/// reliably. Future work: if we have end-of-day spot data, fork on it here.
	/// </summary>
	private static void ApplyExpiries(List<Lineage> active, DateTime evaluationDate)
	{
		foreach (var lin in active.ToList())
		{
			var expiredLegs = lin.OpenLegs
				.Where(kv => {
					var parsed = MatchKeys.ParseOption(kv.Key);
					return parsed.HasValue && parsed.Value.parsed.ExpiryDate.Date < evaluationDate.Date;
				})
				.Select(kv => kv.Key)
				.ToList();
			foreach (var mk in expiredLegs)
			{
				// Legacy behavior: without spot-at-expiry data, treat as OTM and silently remove.
				lin.OpenLegs.Remove(mk);
				lin.TradeHistory.Add(new NetDebitTrade(evaluationDate, $"[expired OTM: {mk}]", Side.Sell, 0, 0m, 0m));
			}

			if (lin.OpenLegs.Count == 0) continue;

			// Rebalance if expiry removed a leg asymmetrically.
			if (lin.OpenLegs.Count > 0 && lin.OpenLegs.Values.Select(v => v.Qty).Distinct().Count() > 1)
			{
				var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
				lin.UnitQty = minQty;
				// Note: expiry-induced imbalance is atypical because expiries usually affect the short leg
				// (earlier expiry) of a diagonal/calendar. Per Option-A we don't proportionally split here —
				// we just reduce UnitQty to the surviving shared qty. The lineage becomes effectively single-leg.
			}
		}

		active.RemoveAll(lin => lin.OpenLegs.Count == 0);
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

	/// <summary>Called at the end of every event's state transition and at end of replay.
	/// Throws with diagnostic state dump if any invariant is violated — those indicate state-machine bugs,
	/// not valid runtime state.</summary>
	private static void AssertInvariants(IEnumerable<Lineage> active, string context, bool enforceBalance = false)
	{
		foreach (var lin in active)
		{
			if (lin.OpenLegs.Count == 0) continue;

			// Inv1: every open leg has positive qty.
			foreach (var (mk, leg) in lin.OpenLegs)
				if (leg.Qty <= 0)
					throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} has open leg {mk} with non-positive qty {leg.Qty} (context: {context})");

			// Inv2: multi-leg lineages are balanced. Only enforced at end-of-replay (after SettleImbalances);
			// mid-replay imbalance is expected for partial-fill strategy orders.
			if (enforceBalance && lin.OpenLegs.Count > 1)
			{
				var qtys = lin.OpenLegs.Values.Select(v => v.Qty).Distinct().ToList();
				if (qtys.Count > 1)
					throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} is imbalanced: legs have qtys [{string.Join(",", qtys)}] (context: {context})");
			}

			// Inv3: UnitQty > 0 for active lineages.
			if (lin.UnitQty <= 0)
				throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} has non-positive UnitQty {lin.UnitQty} (context: {context})");

			// Inv4: RunningCash is finite (guard against future refactoring that introduces double arithmetic).
			var cashAsDouble = (double)lin.RunningCash;
			if (double.IsNaN(cashAsDouble) || double.IsInfinity(cashAsDouble))
				throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} has non-finite RunningCash {lin.RunningCash} (context: {context})");
		}
	}

	/// <summary>Applies one event to the active-lineage list.</summary>
	private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
	{
		if (evt.IsStrategyOrder) ApplyStrategyOrderEvent(active, evt, underlying, ref lineageIdCounter);
		else ApplyStandaloneEvent(active, evt, underlying, ref lineageIdCounter);

		// Deactivate any lineage whose open legs are all zero.
		active.RemoveAll(lin => lin.OpenLegs.Count == 0);

		AssertInvariants(active, $"after event at {evt.Timestamp}");
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

		// Rule 2: standalone add (same direction as an existing open leg) — always grow that leg.
		// Any resulting imbalance is deferred to end-of-replay settlement via SettleImbalances.
		foreach (var lin in active)
		{
			if (!lin.OpenLegs.TryGetValue(t.MatchKey, out var existing)) continue;
			if (existing.Side == t.Side)
			{
				ApplyEventToLineage(lin, evt, isNewLineage: false, active, ref lineageIdCounter);
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
			// Multiple touched: merge them all into the oldest. This handles the case where earlier
			// state-machine routing produced overlapping lineages (e.g., cross-expiry same-strike
			// sequences) that now need to be reconciled when an event spans their shared leg.
			target = touchedLineages.OrderBy(l => l.FirstEntryTimestamp).First();
			foreach (var donor in touchedLineages.Where(l => l != target).ToList())
			{
				MergeLineageInto(target, donor);
				active.Remove(donor);
			}
		}

		ApplyEventToLineage(target, evt, isNewLineage: touchedLineages.Count == 0, active, ref lineageIdCounter);
	}

	/// <summary>
	/// Merges `donor` into `target`. Sums OpenLegs by matchKey (handling opposite-side cancellation),
	/// adds RunningCash + FirstEntryCash (target's first-entry history is preserved; donor's is absorbed).
	/// Appends donor's TradeHistory to target's. Donor StockShareCount sums into target's per matchKey.
	/// Recomputes target.UnitQty = min of leg qtys after merge.
	/// Caller is responsible for removing `donor` from the active list.
	/// </summary>
	private static void MergeLineageInto(Lineage target, Lineage donor)
	{
		foreach (var (mk, donorLeg) in donor.OpenLegs)
		{
			if (target.OpenLegs.TryGetValue(mk, out var existing))
			{
				var signedExisting = existing.Side == Side.Buy ? existing.Qty : -existing.Qty;
				var signedDonor = donorLeg.Side == Side.Buy ? donorLeg.Qty : -donorLeg.Qty;
				var newSigned = signedExisting + signedDonor;
				if (newSigned == 0)
					target.OpenLegs.Remove(mk);
				else
					target.OpenLegs[mk] = (newSigned > 0 ? Side.Buy : Side.Sell, Math.Abs(newSigned));
			}
			else
			{
				target.OpenLegs[mk] = donorLeg;
			}
		}

		foreach (var (mk, shares) in donor.StockShareCount)
			target.StockShareCount[mk] = target.StockShareCount.GetValueOrDefault(mk) + shares;

		target.RunningCash += donor.RunningCash;
		target.FirstEntryCash += donor.FirstEntryCash;
		target.TradeHistory.AddRange(donor.TradeHistory);
		target.UnitQty = target.OpenLegs.Values.Count > 0 ? target.OpenLegs.Values.Min(v => v.Qty) : 0;
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
			// For stock legs, the multiplier difference matters: 100 shares = 1 option-equivalent qty.
			// Internally we track the option-equivalent qty in OpenLegs (so matching/balance work uniformly),
			// and the actual share count in StockShareCount for downstream display.
			int qtyInLineageUnits;
			if (t.Asset == Asset.Stock)
			{
				qtyInLineageUnits = t.Qty / 100; // assume 100-share lots align with option contract count
				lin.StockShareCount[t.MatchKey] = lin.StockShareCount.GetValueOrDefault(t.MatchKey) + (t.Side == Side.Buy ? t.Qty : -t.Qty);
			}
			else
			{
				qtyInLineageUnits = t.Qty;
			}
			var signedQty = t.Side == Side.Buy ? qtyInLineageUnits : -qtyInLineageUnits;

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
				lin.OpenLegs[t.MatchKey] = (t.Side, qtyInLineageUnits);
			}
		}

		// UnitQty = min of leg qtys across open legs (balanced after strategy orders in the common case).
		lin.UnitQty = lin.OpenLegs.Values.Count > 0 ? lin.OpenLegs.Values.Min(v => v.Qty) : 0;

		// Imbalance (if any) is deferred to end-of-replay settlement via SettleImbalances —
		// partial-fill strategy orders temporarily imbalance lineages mid-replay but usually
		// self-resolve before the replay ends.
	}

	/// <summary>
	/// If `lin` has any open leg whose qty exceeds the minimum leg qty, split the excess into a new
	/// standalone lineage. Proportionally allocates cash: the split lineage gets (excess/origUnit) × cash.
	/// Original lineage's RunningCash and FirstEntryCash are scaled down by (min/origUnit).
	/// Called once per lineage at end of per-underlying replay (after all events, after ApplyExpiries).
	/// </summary>
	private static void SettleImbalance(Lineage lin, List<Lineage> active, ref int lineageIdCounter)
	{
		if (lin.OpenLegs.Count < 2) return;
		var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
		var maxQty = lin.OpenLegs.Values.Max(v => v.Qty);
		if (minQty == maxQty) return; // balanced

		var imbalancedKeys = lin.OpenLegs.Where(kv => kv.Value.Qty > minQty).Select(kv => kv.Key).ToList();
		var origUnit = maxQty;

		// Split-marker timestamp: use the lineage's most-recent trade timestamp (no causing Event at settlement).
		var markerTimestamp = lin.TradeHistory.Count > 0 ? lin.TradeHistory[^1].Timestamp : lin.FirstEntryTimestamp;

		foreach (var key in imbalancedKeys)
		{
			var (side, qty) = lin.OpenLegs[key];
			var excess = qty - minQty;
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
			spawn.TradeHistory.Add(new NetDebitTrade(markerTimestamp, $"[split from lineage {lin.Id} at end of replay]", Side.Buy, excess, 0m, spawn.RunningCash));
			active.Add(spawn);
			lin.OpenLegs[key] = (side, minQty);
		}

		var keepRatio = (decimal)minQty / origUnit;
		lin.RunningCash *= keepRatio;
		lin.FirstEntryCash *= keepRatio;
		lin.FirstEntryQty = minQty;
		lin.UnitQty = minQty;
	}

	/// <summary>
	/// End-of-replay settlement: walks active lineages once, calling SettleImbalance on each.
	/// Any imbalance that survived the event loop (e.g., a deliberate partial close that wasn't
	/// rebalanced by subsequent trades) becomes a standalone orphan lineage at this point.
	/// Partial-fill strategy orders, by contrast, self-resolve during the event loop and produce
	/// no imbalance at settlement.
	/// </summary>
	private static void SettleImbalances(List<Lineage> active, ref int lineageIdCounter)
	{
		// Snapshot the current list; spawned lineages append to `active` but shouldn't be re-examined
		// (they're single-leg by construction and have nothing to settle).
		var snapshot = active.ToList();
		foreach (var lin in snapshot)
			SettleImbalance(lin, active, ref lineageIdCounter);
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
				var displayQty = asset == Asset.Stock && lin.StockShareCount.TryGetValue(matchKey, out var shares) ? Math.Abs(shares) : leg.Qty;
				rows.Add(new PositionRow(
					Instrument: instrument,
					Asset: asset,
					OptionKind: optionKind,
					Side: leg.Side,
					Qty: displayQty,
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

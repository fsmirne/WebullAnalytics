namespace WebullAnalytics;

/// <summary>
/// Groups option positions into strategies and builds strategy summary rows.
/// Extracted from PositionTracker to isolate the complex grouping logic.
/// </summary>
internal static class StrategyGrouper
{
	/// <summary>
	/// Groups option positions into strategies using trade relationships.
	/// Legs that share a ParentStrategySeq in their lots were traded together as part of the same strategy.
	/// </summary>
	internal static (List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped, Dictionary<List<(string matchKey, PositionRow row, Trade? trade)>, HashSet<string>> foreignKeysByGroup, HashSet<List<(string matchKey, PositionRow row, Trade? trade)>> brandNewGroups) GroupIntoStrategies(List<(string matchKey, PositionRow row, Trade? trade)> allPositions, Dictionary<string, List<Lot>> positions)
	{
		var grouped = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();
		var foreignKeysByGroup = new Dictionary<List<(string matchKey, PositionRow row, Trade? trade)>, HashSet<string>>(ReferenceEqualityComparer.Instance);
		var brandNewGroups = new HashSet<List<(string matchKey, PositionRow row, Trade? trade)>>(ReferenceEqualityComparer.Instance);
		var processed = new HashSet<string>();

		// Build reverse index: parentSeq → set of option matchKeys whose lots reference it
		var parentToKeys = new Dictionary<int, HashSet<string>>();
		foreach (var p in allPositions.Where(p => p.trade?.Asset == Asset.Option))
			foreach (var lot in positions.GetValueOrDefault(p.matchKey, []).Where(l => l.ParentStrategySeq.HasValue))
			{
				if (!parentToKeys.TryGetValue(lot.ParentStrategySeq!.Value, out var set))
					parentToKeys[lot.ParentStrategySeq!.Value] = set = [];
				set.Add(p.matchKey);
			}

		// Build strategy groups from parentSeqs that link ≥2 positions
		var strategyGroups = new List<HashSet<string>>();
		foreach (var (_, keys) in parentToKeys.Where(kvp => kvp.Value.Count >= 2))
		{
			var existing = strategyGroups.FirstOrDefault(g => g.Overlaps(keys));
			if (existing != null)
				existing.UnionWith(keys);
			else
				strategyGroups.Add(new HashSet<string>(keys));
		}

		// Second merge pass: collapse groups that now overlap
		for (int i = 0; i < strategyGroups.Count; i++)
			for (int j = i + 1; j < strategyGroups.Count; j++)
				if (strategyGroups[i].Overlaps(strategyGroups[j]))
				{
					strategyGroups[i].UnionWith(strategyGroups[j]);
					strategyGroups.RemoveAt(j);
					j--;
				}

		// Convert hashsets to position groups — restrict qty to lots matching the group's parentSeqs.
		// This correctly partitions lots when a matchKey's lots belong to different strategies
		// (e.g., 100 lots in a diagonal via parentSeq B, 299 lots orphaned from an expired leg via parentSeq A).
		var positionLookup = allPositions.Where(p => p.trade?.Asset == Asset.Option).ToDictionary(p => p.matchKey);
		var allocatedParentSeqs = new Dictionary<string, HashSet<int>>();
		var balanceExcess = new Dictionary<string, (string matchKey, PositionRow row, Trade? trade)>();

		foreach (var keySet in strategyGroups)
		{
			var groupParentSeqs = parentToKeys.Where(kvp => kvp.Value.Count >= 2 && kvp.Value.Overlaps(keySet)).Select(kvp => kvp.Key).ToHashSet();

			var group = new List<(string matchKey, PositionRow row, Trade? trade)>();
			foreach (var k in keySet)
			{
				var entry = positionLookup[k];
				var lots = positions.GetValueOrDefault(k, []);
				var matchingLots = lots.Where(l => l.ParentStrategySeq.HasValue && groupParentSeqs.Contains(l.ParentStrategySeq.Value)).ToList();

				if (matchingLots.Count > 0 && matchingLots.Count < lots.Count)
				{
					var qty = matchingLots.Sum(l => l.Qty);
					var avg = matchingLots.Sum(l => l.Price * l.Qty) / qty;
					group.Add((k, entry.row with { Qty = qty, AvgPrice = avg }, entry.trade));
				}
				else
				{
					group.Add(entry);
				}

				if (!allocatedParentSeqs.TryGetValue(k, out var allocated))
					allocatedParentSeqs[k] = allocated = [];
				allocated.UnionWith(groupParentSeqs);
			}

			// Balance leg quantities: when one leg was partially closed via standalone trades,
			// its lots were consumed but the paired leg's lots retain the original parentSeq,
			// creating uneven legs. Cap each leg to the minimum qty across the group.
			if (group.Count >= 2)
			{
				var minQty = group.Min(g => g.row.Qty);
				for (int gi = 0; gi < group.Count; gi++)
				{
					var leg = group[gi];
					if (leg.row.Qty <= minQty) continue;

					var lots = positions.GetValueOrDefault(leg.matchKey, []);
					var matchingLots = lots.Where(l => l.ParentStrategySeq.HasValue && groupParentSeqs.Contains(l.ParentStrategySeq.Value)).ToList();
					// Assign excess FIRST (FIFO), then remaining to kept. This ensures the excess
					// (spun-off position) gets the main batch's homogeneous price, while smaller
					// tail batches stay in the kept (original strategy) group.
					var excessTarget = leg.row.Qty - minQty;
					var keptQty = 0; var keptValue = 0m; var exQty = 0; var exValue = 0m;
					foreach (var lot in matchingLots)
					{
						var toExcess = Math.Min(lot.Qty, excessTarget - exQty);
						if (toExcess > 0) { exQty += toExcess; exValue += lot.Price * toExcess; }
						var toKept = lot.Qty - toExcess;
						if (toKept > 0) { keptQty += toKept; keptValue += lot.Price * toKept; }
					}

					if (keptQty > 0)
						group[gi] = (leg.matchKey, leg.row with { Qty = keptQty, AvgPrice = keptValue / keptQty }, leg.trade);

					if (exQty > 0)
					{
						var exAvg = exValue / exQty;
						if (balanceExcess.TryGetValue(leg.matchKey, out var prev))
						{
							var cq = prev.row.Qty + exQty; var ca = (prev.row.AvgPrice * prev.row.Qty + exAvg * exQty) / cq;
							balanceExcess[leg.matchKey] = (leg.matchKey, prev.row with { Qty = cq, AvgPrice = ca }, prev.trade);
						}
						else
							balanceExcess[leg.matchKey] = (leg.matchKey, positionLookup[leg.matchKey].row with { Qty = exQty, AvgPrice = exAvg }, leg.trade);
					}
				}
			}

			grouped.Add(group);
			foreach (var k in keySet) processed.Add(k);
		}

		// Compute remainders: lots not allocated to any strategy group (orphaned parentSeqs, standalone lots).
		var remaindersByKey = new Dictionary<string, (string matchKey, PositionRow row, Trade? trade)>();
		foreach (var (key, usedSeqs) in allocatedParentSeqs)
		{
			var entry = positionLookup[key];
			var lots = positions.GetValueOrDefault(key, []);
			var remainingLots = lots.Where(l => !l.ParentStrategySeq.HasValue || !usedSeqs.Contains(l.ParentStrategySeq.Value)).ToList();
			if (remainingLots.Count > 0)
			{
				var qty = remainingLots.Sum(l => l.Qty);
				var avg = remainingLots.Sum(l => l.Price * l.Qty) / qty;
				remaindersByKey[key] = (key, entry.row with { Qty = qty, AvgPrice = avg }, entry.trade);
			}
		}

		// Merge excess from leg balancing into remainders for fallback grouping.
		foreach (var (key, entry) in balanceExcess)
		{
			if (remaindersByKey.TryGetValue(key, out var existing))
			{
				var cq = existing.row.Qty + entry.row.Qty; var ca = (existing.row.AvgPrice * existing.row.Qty + entry.row.AvgPrice * entry.row.Qty) / cq;
				remaindersByKey[key] = (key, existing.row with { Qty = cq, AvgPrice = ca }, existing.trade);
			}
			else
				remaindersByKey[key] = entry;
		}

		// Fallback grouping: group ungrouped options that form calendars/diagonals/verticals.
		// Remainders (lots not allocated to primary groups) flow here for balanced grouping.
		var ungroupedOptions = allPositions.Where(p => p.trade?.Asset == Asset.Option && !processed.Contains(p.matchKey))
			.Concat(remaindersByKey.Values)
			.ToList();

		var fallbackGrouped = new HashSet<string>();

		var byRootCallPut = ungroupedOptions
			.Select(p => (entry: p, parsed: MatchKeys.TryGetOptionSymbol(p.matchKey, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
			.Where(x => x.parsed != null)
			.GroupBy(x => (x.parsed!.Root, x.parsed.CallPut))
			.Where(g => g.Count() >= 2);

		foreach (var group in byRootCallPut)
		{
			var entries = group.Select(x => x.entry).ToList();
			foreach (var e in entries) fallbackGrouped.Add(e.matchKey);

			var buys = entries.Where(e => e.row.Side == Side.Buy).ToList();
			var sells = entries.Where(e => e.row.Side == Side.Sell).ToList();

			// Single-side group (rare — e.g., two shorts at different strikes): preserve legacy
			// "balance to minQty" behavior since there's no buy↔sell pairing to do.
			if (buys.Count == 0 || sells.Count == 0)
			{
				BalanceToMinQty(entries, positions, grouped);
				continue;
			}

			// Greedy pair buys with sells: match exact-qty first to form 2-leg strategies
			// (vertical/calendar/diagonal). Remaining qty from each leg becomes standalone.
			// Tracks consumed qty per matchKey so each pair draws FIFO-ordered lots.
			var partitionKeys = entries.Select(e => e.matchKey).ToHashSet();
			var remaining = entries.ToDictionary(e => e.matchKey, e => e.row.Qty);
			var consumed = entries.ToDictionary(e => e.matchKey, _ => 0);
			var createdPairs = new List<List<(string, PositionRow, Trade?)>>();

			while (buys.Any(b => remaining[b.matchKey] > 0) && sells.Any(s => remaining[s.matchKey] > 0))
			{
				var buy = buys.Where(b => remaining[b.matchKey] > 0).OrderByDescending(b => remaining[b.matchKey]).First();
				var buyQty = remaining[buy.matchKey];
				var availableSells = sells.Where(s => remaining[s.matchKey] > 0).ToList();
				var exactMatchIdx = availableSells.FindIndex(s => remaining[s.matchKey] == buyQty);
				var sell = exactMatchIdx >= 0 ? availableSells[exactMatchIdx] : availableSells.OrderByDescending(s => remaining[s.matchKey]).First();
				var pairQty = Math.Min(buyQty, remaining[sell.matchKey]);

				var pair = new List<(string, PositionRow, Trade?)>
				{
					SliceEntry(buy, positions, consumed[buy.matchKey], pairQty),
					SliceEntry(sell, positions, consumed[sell.matchKey], pairQty),
				};
				grouped.Add(pair);
				createdPairs.Add(pair);

				// A pair is "brand new" if every allocated lot on both legs has no parentStrategySeq
				// (pure synthetic / standalone entry with no roll history). Such groups need no replay —
				// their adjusted price equals the blended entry price from allocated lots.
				if (AllocationHasNoParentSeq(positions.GetValueOrDefault(buy.matchKey, []), consumed[buy.matchKey], pairQty)
					&& AllocationHasNoParentSeq(positions.GetValueOrDefault(sell.matchKey, []), consumed[sell.matchKey], pairQty))
					brandNewGroups.Add(pair);

				consumed[buy.matchKey] += pairQty;
				consumed[sell.matchKey] += pairQty;
				remaining[buy.matchKey] -= pairQty;
				remaining[sell.matchKey] -= pairQty;
			}

			// Record foreign matchKeys per pair: matchKeys from sibling pairs in the same partition.
			// Used by ReplayTimeline to exclude trades that belong to another group's allocation,
			// preventing synthetic trades in one pair from polluting another pair's adjusted price.
			foreach (var pair in createdPairs)
			{
				var pairKeys = pair.Select(p => p.Item1).ToHashSet();
				var foreign = partitionKeys.Where(k => !pairKeys.Contains(k)).ToHashSet();
				if (foreign.Count > 0)
					foreignKeysByGroup[pair] = foreign;
			}

			// Remaining qty (unmatched side) becomes standalone
			foreach (var e in entries)
			{
				if (remaining[e.matchKey] > 0)
					grouped.Add(new List<(string, PositionRow, Trade?)> { SliceEntry(e, positions, consumed[e.matchKey], remaining[e.matchKey]) });
			}
		}

		// Add remaining unprocessed options as standalone
		grouped.AddRange(ungroupedOptions.Where(p => !fallbackGrouped.Contains(p.matchKey)).Select(p => new List<(string, PositionRow, Trade?)> { p }));

		// Add non-option positions
		grouped.AddRange(allPositions.Where(p => p.trade?.Asset != Asset.Option).Select(p => new List<(string, PositionRow, Trade?)> { p }));

		// Sort by asset type, then instrument
		grouped.Sort((a, b) =>
		{
			var cmp = a[0].row.Asset.CompareTo(b[0].row.Asset);
			return cmp != 0 ? cmp : string.Compare(a[0].row.Instrument, b[0].row.Instrument, StringComparison.Ordinal);
		});

		return (grouped, foreignKeysByGroup, brandNewGroups);
	}

	/// <summary>
	/// Returns an entry representing a FIFO-ordered slice of the source entry's lots: skip the first
	/// <paramref name="offset"/> contracts, then take <paramref name="qty"/>. AvgPrice is computed
	/// from the sliced lots' prices.
	/// </summary>
	private static (string matchKey, PositionRow row, Trade? trade) SliceEntry((string matchKey, PositionRow row, Trade? trade) entry, Dictionary<string, List<Lot>> positions, int offset, int qty)
	{
		if (qty == entry.row.Qty && offset == 0) return entry;

		var lots = positions.GetValueOrDefault(entry.matchKey, []);
		var skip = offset; var remaining = qty; var total = 0m; var taken = 0;
		foreach (var lot in lots)
		{
			var available = lot.Qty;
			if (skip > 0) { var s = Math.Min(skip, available); skip -= s; available -= s; }
			if (available == 0 || remaining == 0) continue;
			var take = Math.Min(available, remaining);
			total += lot.Price * take; taken += take; remaining -= take;
			if (remaining == 0) break;
		}
		var avg = taken > 0 ? total / taken : entry.row.AvgPrice;
		return (entry.matchKey, entry.row with { Qty = qty, AvgPrice = avg }, entry.trade);
	}

	/// <summary>
	/// Returns true if every lot in the FIFO window [offset, offset+qty) has no ParentStrategySeq.
	/// Used to detect brand-new fallback pairs whose allocated lots came from standalone/synthetic
	/// trades only (no rolling history worth replaying).
	/// </summary>
	private static bool AllocationHasNoParentSeq(List<Lot> lots, int offset, int qty)
	{
		var skip = offset; var remaining = qty;
		foreach (var lot in lots)
		{
			var available = lot.Qty;
			if (skip > 0) { var s = Math.Min(skip, available); skip -= s; available -= s; }
			if (available == 0 || remaining == 0) continue;
			if (lot.ParentStrategySeq != null) return false;
			remaining -= Math.Min(available, remaining);
			if (remaining == 0) break;
		}
		return true;
	}

	/// <summary>
	/// Legacy fallback balancing used for single-side groups: caps every entry to the minimum qty
	/// and emits the excess as standalone groups.
	/// </summary>
	private static void BalanceToMinQty(List<(string matchKey, PositionRow row, Trade? trade)> entries, Dictionary<string, List<Lot>> positions, List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped)
	{
		var minQty = entries.Min(e => e.row.Qty);
		var balanced = new List<(string matchKey, PositionRow row, Trade? trade)>();
		foreach (var e in entries)
		{
			if (e.row.Qty <= minQty)
				balanced.Add(e);
			else
			{
				balanced.Add(SliceEntry(e, positions, 0, minQty));
				grouped.Add(new List<(string, PositionRow, Trade?)> { SliceEntry(e, positions, minQty, e.row.Qty - minQty) });
			}
		}
		grouped.Add(balanced);
	}

	/// <summary>
	/// Builds the final position rows, creating strategy summary rows for multi-leg positions.
	/// Returns the rows and a dictionary of pre-computed timeline replays keyed by summary row index.
	/// </summary>
	internal static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments) BuildFinalPositionRows(List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped, List<Trade> allTrades, Dictionary<string, List<Lot>> positions, Dictionary<List<(string matchKey, PositionRow row, Trade? trade)>, HashSet<string>>? foreignKeysByGroup = null, HashSet<List<(string matchKey, PositionRow row, Trade? trade)>>? brandNewGroups = null)
	{
		var tradeBySeq = allTrades.Where(t => t.Asset == Asset.OptionStrategy).ToDictionary(t => t.Seq);

		// Identify pure strategy groups (all lots from strategy parents whose legs match the group).
		// Pure groups don't need the timeline replay — their adjusted price equals their initial price.
		// Non-pure groups exclude pure siblings' parentStrategySeqs from the replay to avoid double-counting.
		var pureParentSeqs = new HashSet<int>();
		var isPure = new bool[grouped.Count];
		var pureSeqsByGroup = new HashSet<int>?[grouped.Count];
		for (int i = 0; i < grouped.Count; i++)
		{
			if (grouped[i].Count <= 1) continue;
			var seqs = GetPureStrategyParentSeqs(grouped[i], positions, allTrades);
			if (seqs != null)
			{
				isPure[i] = true;
				pureSeqsByGroup[i] = seqs;
				pureParentSeqs.UnionWith(seqs);
			}
		}

		var rows = new List<PositionRow>();
		var adjustments = new Dictionary<int, StrategyAdjustment>();

		for (int i = 0; i < grouped.Count; i++)
		{
			var group = grouped[i];
			if (group.Count > 1)
			{
				var summaryIndex = rows.Count;
				var foreign = foreignKeysByGroup != null && foreignKeysByGroup.TryGetValue(group, out var fk) ? fk : null;
				var isBrandNew = brandNewGroups != null && brandNewGroups.Contains(group);
				var (strategyRows, adjustment) = BuildStrategyRows(group, allTrades, isBrandNew ? null : (isPure[i] ? null : pureParentSeqs), isPure[i] ? pureSeqsByGroup[i] : null, foreign, isBrandNew);
				rows.AddRange(strategyRows);
				if (adjustment != null)
					adjustments[summaryIndex] = adjustment;
			}
			else
				rows.Add(AdjustForExpiredStrategyLegs(group[0], positions, allTrades, tradeBySeq));
		}

		return (rows, adjustments);
	}

	/// <summary>
	/// Returns the set of parentStrategySeqs if the group is a "pure" strategy: all lots originate
	/// from strategy parent trades whose legs match the group's keys, and the combined lots cover
	/// the group qty for every leg. Supports multiple batches (e.g., 450x + 5x fills).
	/// </summary>
	private static HashSet<int>? GetPureStrategyParentSeqs(List<(string matchKey, PositionRow row, Trade? trade)> group, Dictionary<string, List<Lot>> positions, List<Trade> allTrades)
	{
		var candidates = new HashSet<int>();
		foreach (var leg in group)
			foreach (var lot in positions.GetValueOrDefault(leg.matchKey, []))
				if (lot.ParentStrategySeq.HasValue)
					candidates.Add(lot.ParentStrategySeq.Value);

		var groupKeys = group.Select(g => g.matchKey).ToHashSet();
		var validSeqs = new HashSet<int>();
		foreach (var seq in candidates)
		{
			var parentLegKeys = allTrades.Where(t => t.ParentStrategySeq == seq && t.Asset == Asset.Option).Select(t => t.MatchKey).ToHashSet();
			if (parentLegKeys.IsSubsetOf(groupKeys))
				validSeqs.Add(seq);
		}

		if (validSeqs.Count == 0) return null;
		if (!group.All(leg => positions.GetValueOrDefault(leg.matchKey, []).Where(l => l.ParentStrategySeq.HasValue && validSeqs.Contains(l.ParentStrategySeq.Value)).Sum(l => l.Qty) >= leg.row.Qty))
			return null;

		return validSeqs;
	}

	/// <summary>
	/// For a single-leg option that was part of a strategy whose other legs have expired/closed,
	/// computes an adjusted price reflecting the credits/debits from those expired legs
	/// and any standalone roll trades that modified the position after the strategy entry.
	/// </summary>
	private static PositionRow AdjustForExpiredStrategyLegs((string matchKey, PositionRow row, Trade? trade) entry, Dictionary<string, List<Lot>> positions, List<Trade> allTrades, Dictionary<int, Trade> tradeBySeq)
	{
		var (matchKey, row, trade) = entry;
		if (trade?.Asset != Asset.Option) return row;

		var lots = positions.GetValueOrDefault(matchKey, []);
		var strategyLots = lots.Where(l => l.ParentStrategySeq.HasValue).ToList();
		if (strategyLots.Count == 0) return row;

		// Collect all parentSeqs where the other legs have fully expired/closed
		var expiredParentSeqs = new HashSet<int>();
		var allOtherLegKeys = new HashSet<string>();
		var latestEntryTime = DateTime.MinValue;
		foreach (var seqGroup in strategyLots.GroupBy(l => l.ParentStrategySeq!.Value))
		{
			if (!tradeBySeq.TryGetValue(seqGroup.Key, out var parentTrade)) continue;
			var otherLegs = allTrades.Where(t => t.ParentStrategySeq == parentTrade.Seq && t.MatchKey != matchKey).ToList();
			if (otherLegs.Count == 0) continue;
			if (otherLegs.Any(leg => positions.ContainsKey(leg.MatchKey) && positions[leg.MatchKey].Sum(l => l.Qty) > 0)) continue;
			expiredParentSeqs.Add(seqGroup.Key);
			allOtherLegKeys.UnionWith(otherLegs.Select(l => l.MatchKey));
			if (parentTrade.Timestamp > latestEntryTime) latestEntryTime = parentTrade.Timestamp;
		}
		if (expiredParentSeqs.Count == 0) return row;

		// Basic adjustment: credit from the expired short legs of each parent strategy
		var totalAdjustment = 0m;
		var adjustedQty = 0;
		foreach (var lot in strategyLots.Where(l => expiredParentSeqs.Contains(l.ParentStrategySeq!.Value)))
		{
			var parentTrade = tradeBySeq[lot.ParentStrategySeq!.Value];
			totalAdjustment += (lot.Price - parentTrade.Price) * lot.Qty;
			adjustedQty += lot.Qty;
		}

		// Standalone roll credit: find trades that rolled expired legs to new strikes.
		// Computed ONCE across all expired parentSeqs to avoid double-counting when
		// multiple parent batches (e.g., 450x + 5x fills) share the same expired legs.
		var parsed = MatchKeys.TryGetOptionSymbol(matchKey, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null;
		if (parsed != null && allOtherLegKeys.Count > 0)
		{
			var parentLegStrikes = allOtherLegKeys.Select(mk => MatchKeys.TryGetOptionSymbol(mk, out var s) ? ParsingHelpers.ParseOptionSymbol(s) : null).Where(p => p != null).Select(p => p!.Strike).Distinct().Concat(new[] { parsed.Strike }).ToHashSet();
			var occSuffixes = parentLegStrikes.Select(s => $"{parsed.CallPut}{(long)(s * 1000m):D8}").ToHashSet();
			var optionKeyPrefix = $"{MatchKeys.OptionPrefix}{parsed.Root}";

			var standaloneTrades = allTrades.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && !t.ParentStrategySeq.HasValue && t.Timestamp > latestEntryTime && t.MatchKey != matchKey && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(s => t.MatchKey.EndsWith(s, StringComparison.Ordinal))).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq).ToList();
			if (standaloneTrades.Count > 0)
			{
				// The close qty of the expired other legs determines how many contracts were rolled.
				// Limit opening trades at new strikes to the close qty to exclude unrelated trades.
				var closeQty = standaloneTrades.Where(t => allOtherLegKeys.Contains(t.MatchKey) && t.Side == Side.Buy).Sum(t => t.Qty);
				if (closeQty > 0)
				{
					var sellQtyUsed = 0;
					var totalCredit = 0m;
					foreach (var st in standaloneTrades)
					{
						if (allOtherLegKeys.Contains(st.MatchKey))
							totalCredit += (st.Side == Side.Sell ? 1m : -1m) * st.Qty * st.Price * st.Multiplier;
						else
						{
							var available = closeQty - sellQtyUsed;
							if (available <= 0) continue;
							var qty = Math.Min(st.Qty, available);
							totalCredit += (st.Side == Side.Sell ? 1m : -1m) * qty * st.Price * st.Multiplier;
							sellQtyUsed += qty;
						}
					}
					totalAdjustment += totalCredit / Trade.OptionMultiplier;
				}
			}
		}

		if (adjustedQty == 0) return row;
		var adjustedPrice = row.AvgPrice - totalAdjustment / row.Qty;
		return row with { InitialAvgPrice = row.AvgPrice, AdjustedAvgPrice = adjustedPrice };
	}

	/// <summary>
	/// Builds rows for a multi-leg strategy (calendar, vertical, diagonal, etc.).
	/// Returns the position rows and, for non-pure strategies, the timeline replay data for the adjustment report.
	/// </summary>
	/// <param name="excludedParentSeqs">
	/// Null = pure strategy (no replay needed, adj = init).
	/// Non-null = exclude trades belonging to these parentStrategySeqs from the timeline replay.
	/// </param>
	/// <param name="pureSeqs">
	/// For pure strategies with multiple entry batches, the set of parent trade seqs.
	/// Used to compute init price from strategy-level prices instead of FIFO-distorted leg prices.
	/// </param>
	private static (List<PositionRow> rows, StrategyAdjustment? adjustment) BuildStrategyRows(List<(string matchKey, PositionRow row, Trade? trade)> group, List<Trade> allTrades, HashSet<int>? excludedParentSeqs, HashSet<int>? pureSeqs = null, HashSet<string>? foreignMatchKeys = null, bool isBrandNewFallback = false)
	{
		var rows = new List<PositionRow>();

		var parsedLegs = group.Select(g => (leg: g, parsed: MatchKeys.TryGetOptionSymbol(g.matchKey, out var symbol) ? ParsingHelpers.ParseOptionSymbol(symbol) : null)).Where(x => x.parsed != null).ToList();

		if (!parsedLegs.Any())
		{
			rows.AddRange(group.Select(g => g.row));
			return (rows, null);
		}

		var firstParsed = parsedLegs[0].parsed!;
		var distinctExpiries = parsedLegs.Select(x => x.parsed!.ExpiryDate).Distinct().Count();
		var distinctStrikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().Count();
		var distinctCallPut = parsedLegs.Select(x => x.parsed!.CallPut).Distinct().Count();

		var strategyKind = ParsingHelpers.ClassifyStrategyKind(parsedLegs.Count, distinctExpiries, distinctStrikes, distinctCallPut);
		var qty = group[0].row.Qty;

		// For pure groups, compute adj from parent strategy trade prices (immune to lot contamination).
		// For multi-batch, init = first batch entry; single-batch, init = same as adj.
		var netInitial = group.Sum(leg => leg.row.Side == Side.Buy ? leg.row.AvgPrice : -leg.row.AvgPrice);
		decimal? pureInitOverride = null;
		if (pureSeqs != null)
		{
			var parentTrades = allTrades.Where(t => pureSeqs.Contains(t.Seq) && t.Asset == Asset.OptionStrategy).OrderBy(t => t.Timestamp).ToList();
			if (parentTrades.Count > 0)
			{
				var totalQty = parentTrades.Sum(p => p.Qty);
				netInitial = parentTrades.Sum(p => (p.Side == Side.Buy ? p.Price : -p.Price) * p.Qty) / totalQty;
				if (pureSeqs.Count > 1)
					pureInitOverride = parentTrades[0].Side == Side.Buy ? parentTrades[0].Price : -parentTrades[0].Price;
			}
		}

		// Pure strategies: adj = blended avg, init = first batch entry.
		// Non-pure strategies: replay the timeline to compute the adjusted price.
		StrategyAdjustment? adjustment = null;
		var netAdjusted = netInitial;
		var strategyInitial = pureInitOverride ?? netInitial;
		if (excludedParentSeqs != null)
		{
			var legStrikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().OrderBy(s => s).ToList();
			var qtyLimits = group.ToDictionary(g => g.matchKey, g => g.row.Qty);
			// Foreign matchKeys (sibling-group legs) get limit=0 so their trades are excluded from
			// this group's replay, preventing cross-contamination between fallback-paired groups.
			if (foreignMatchKeys != null)
				foreach (var fk in foreignMatchKeys)
					qtyLimits.TryAdd(fk, 0);
			var replay = ReplayTimeline(allTrades, firstParsed.Root, legStrikes, firstParsed.CallPut, excludedParentSeqs, qtyLimits);

			// Detect incomplete replays: if the replay is missing key trades (e.g., entry
			// trades excluded as belonging to a pure sibling group), the debit/credit direction
			// will disagree with the leg-based pricing. Fall back to leg-based pricing.
			var replayNetAdjusted = replay.TotalNetDebit / (qty * 100m);
			// qtyLimits scopes the replay to the group's allocation, so a flat time isn't
			// required to validate completeness. Only reject on sign mismatch (incomplete data).
			var replayUsable = !(netInitial != 0 && replayNetAdjusted != 0 && Math.Sign(replayNetAdjusted) != Math.Sign(netInitial));
			if (replayUsable)
			{
				adjustment = replay;
				netAdjusted = replayNetAdjusted;

				// Derive Init from the first batch of trades after the last flat point.
				// This preserves the original entry price as a fixed reference, even after
				// adding more contracts at different prices.
				if (adjustment.Trades.Count > 0)
				{
					var firstTs = adjustment.Trades[0].Timestamp;
					var firstBatchDebit = 0m;
					var firstBatchQty = 0;
					foreach (var t in adjustment.Trades)
					{
						if (t.Timestamp != firstTs) break;
						firstBatchDebit -= t.CashImpact;
						firstBatchQty = Math.Max(firstBatchQty, t.Qty);
					}
					if (firstBatchQty > 0)
					{
						strategyInitial = firstBatchDebit / (firstBatchQty * 100m);
						adjustment = adjustment with { InitNetDebit = firstBatchDebit };
					}
				}
			}
			else if (excludedParentSeqs.Count > 0)
			{
				// Replay is incomplete — this group inherited lots from a pure sibling (e.g.,
				// a calendar formed by rolling a diagonal's short leg). Compute adj as the
				// parent entry price minus the per-contract roll credit from standalone trades.
				var inheritedKeys = allTrades.Where(t => t.Asset == Asset.Option && t.ParentStrategySeq.HasValue && excludedParentSeqs.Contains(t.ParentStrategySeq.Value)).Select(t => t.MatchKey).ToHashSet();
				if (group.Any(leg => inheritedKeys.Contains(leg.matchKey)))
				{
					var parentTrades = allTrades.Where(t => excludedParentSeqs.Contains(t.Seq) && t.Asset == Asset.OptionStrategy).OrderBy(t => t.Timestamp).ToList();
					if (parentTrades.Count > 0)
					{
						var entryQty = parentTrades.Sum(p => p.Qty);
						var entryPrice = parentTrades.Sum(p => (p.Side == Side.Buy ? p.Price : -p.Price) * p.Qty) / entryQty;

						// Expand to parent leg strikes (e.g., include original $23.5 short that was rolled)
						var parentLegStrikes = allTrades.Where(t => t.Asset == Asset.Option && t.ParentStrategySeq.HasValue && excludedParentSeqs.Contains(t.ParentStrategySeq.Value)).Select(t => MatchKeys.TryGetOptionSymbol(t.MatchKey, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null).Where(p => p != null).Select(p => p!.Strike).Distinct().ToHashSet();
						var occSuffixes = parentLegStrikes.Select(s => $"{firstParsed.CallPut}{(long)(s * 1000m):D8}").ToHashSet();
						var optionKeyPrefix = $"{MatchKeys.OptionPrefix}{firstParsed.Root}";
						var lastEntryTime = parentTrades.Max(t => t.Timestamp);

						var standaloneTrades = allTrades.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && !t.ParentStrategySeq.HasValue && t.Timestamp > lastEntryTime && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(s => t.MatchKey.EndsWith(s, StringComparison.Ordinal))).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq).ToList();

						// Apply qtyLimits to standalone trades: exclude trades that push any
						// matchKey beyond the group's allocation (e.g., synthetic excess).
						if (standaloneTrades.Count > 0)
						{
							var saQtyTrack = new Dictionary<string, int>();
							standaloneTrades = standaloneTrades.Where(t =>
							{
								if (!qtyLimits.TryGetValue(t.MatchKey, out var saLimit)) return true;
								var saDelta = t.Side == Side.Buy ? t.Qty : -t.Qty;
								var saNew = saQtyTrack.GetValueOrDefault(t.MatchKey) + saDelta;
								if (Math.Abs(saNew) > saLimit) return false;
								saQtyTrack[t.MatchKey] = saNew;
								return true;
							}).ToList();
						}

						// Build adjustment with parent entry + standalone roll trades
						var adjTrades = new List<NetDebitTrade>();
						decimal adjDebit = 0m;
						foreach (var pt in parentTrades)
						{
							var ptCash = (pt.Side == Side.Buy ? -1m : 1m) * pt.Qty * pt.Price * pt.Multiplier;
							adjDebit -= ptCash;
							adjTrades.Add(new NetDebitTrade(pt.Timestamp, pt.Instrument, pt.Side, pt.Qty, pt.Price, ptCash));
						}
						foreach (var st in standaloneTrades)
						{
							var stCash = (st.Side == Side.Buy ? -1m : 1m) * st.Qty * st.Price * st.Multiplier;
							adjDebit -= stCash;
							adjTrades.Add(new NetDebitTrade(st.Timestamp, st.Instrument, st.Side, st.Qty, st.Price, stCash));
						}

						if (standaloneTrades.Count > 0)
						{
							var totalCredit = standaloneTrades.Sum(t => (t.Side == Side.Sell ? 1m : -1m) * t.Qty * t.Price * t.Multiplier);
							var creditQty = standaloneTrades.Max(t => t.Qty);
							netAdjusted = entryPrice - totalCredit / (creditQty * standaloneTrades[0].Multiplier);
						}

						var initDebitInherited = parentTrades.Sum(p => (p.Side == Side.Buy ? 1m : -1m) * p.Qty * p.Price * p.Multiplier);
						adjustment = new StrategyAdjustment(adjTrades, adjDebit, null, initDebitInherited);
					}
				}
			}
		}

		// For strategies without replay data (pure or unusable replay), compute trades directly.
		// Pure strategies filter by pureSeqs to exclude trades from other groups sharing the same matchKey.
		// Brand-new fallback pairs filter to only synthetic trades (no parentSeq) since their allocated
		// lots came from standalone entries — historical trades with parentSeq belong to closed strategies.
		if (adjustment == null)
		{
			var legMatchKeys = group.Select(g => g.matchKey).ToHashSet();
			var filterByPureSeqs = pureSeqs != null && pureSeqs.Count > 0;
			var ownTrades = allTrades.Where(t => legMatchKeys.Contains(t.MatchKey) && t.Side is Side.Buy or Side.Sell && t.Asset == Asset.Option && (!filterByPureSeqs || (t.ParentStrategySeq.HasValue && pureSeqs!.Contains(t.ParentStrategySeq.Value))) && (!isBrandNewFallback || !t.ParentStrategySeq.HasValue)).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq).Select(t => new NetDebitTrade(t.Timestamp, t.Instrument, t.Side, t.Qty, t.Price, (t.Side == Side.Buy ? -1m : 1m) * t.Qty * t.Price * t.Multiplier)).ToList();
			if (ownTrades.Count >= 2)
			{
				var totalDebit = ownTrades.Sum(t => -t.CashImpact);
				var firstTs = ownTrades[0].Timestamp;
				// Brand-new fallback: all ownTrades ARE the entry, so initDebit equals totalDebit
				// (no later rolling/adjustment trades). Otherwise, initDebit is just the first batch.
				var initDebit = isBrandNewFallback ? totalDebit : ownTrades.TakeWhile(t => t.Timestamp == firstTs).Sum(t => -t.CashImpact);
				adjustment = new StrategyAdjustment(ownTrades, totalDebit, null, initDebit);
			}
		}

		var longLegAdjustment = netInitial - netAdjusted;
		var side = netAdjusted >= 0 ? Side.Buy : Side.Sell;
		var longestExpiry = group.Max(g => g.row.Expiry) ?? DateTime.MinValue;

		var displayPrice = side == Side.Sell ? -netAdjusted : netAdjusted;
		var displayInitial = side == Side.Sell ? -strategyInitial : strategyInitial;
		rows.Add(new PositionRow(Instrument: $"{firstParsed.Root} {Formatters.FormatOptionDate(longestExpiry)}", Asset: Asset.OptionStrategy, OptionKind: strategyKind, Side: side, Qty: qty, AvgPrice: displayPrice, Expiry: longestExpiry, IsStrategyLeg: false, InitialAvgPrice: displayInitial, AdjustedAvgPrice: displayPrice));

		foreach (var leg in group.OrderByDescending(g => g.row.Expiry).ThenByDescending(g => parsedLegs.FirstOrDefault(p => p.leg.matchKey == g.matchKey).parsed?.Strike ?? 0))
		{
			var initialPrice = leg.row.AvgPrice;
			var adjustedPrice = leg.row.Side == Side.Buy ? initialPrice - longLegAdjustment : initialPrice;
			rows.Add(leg.row with { IsStrategyLeg = true, InitialAvgPrice = initialPrice, AdjustedAvgPrice = adjustedPrice });
		}

		return (rows, adjustment);
	}

	/// <summary>
	/// Replays the trade timeline for a set of strikes, computing the total net debit and producing
	/// a trade-by-trade breakdown. Expands strikes transitively through strategy parent relationships
	/// and finds the last point where all related positions were flat.
	/// </summary>
	private static StrategyAdjustment ReplayTimeline(List<Trade> allTrades, string root, List<decimal> strikes, string callPut, HashSet<int>? excludedParentSeqs = null, Dictionary<string, int>? qtyLimits = null)
	{
		var optionKeyPrefix = $"{MatchKeys.OptionPrefix}{root}";

		// Filter trades, excluding those belonging to other strategy groups
		var optionBuySellTrades = allTrades.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && (excludedParentSeqs == null || excludedParentSeqs.Count == 0 || !t.ParentStrategySeq.HasValue || !excludedParentSeqs.Contains(t.ParentStrategySeq.Value))).ToList();

		// Expand strikes transitively by following strategy parent relationships
		var expandedStrikes = new HashSet<decimal>(strikes);
		bool changed = true;
		while (changed)
		{
			changed = false;
			var suffixes = expandedStrikes.Select(s => $"{callPut}{(long)(s * 1000m):D8}").ToHashSet();
			foreach (var trade in optionBuySellTrades.Where(t => t.ParentStrategySeq.HasValue && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && suffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal))))
				foreach (var sibling in allTrades.Where(t => t.ParentStrategySeq == trade.ParentStrategySeq && t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal)))
				{
					if (!MatchKeys.TryGetOptionSymbol(sibling.MatchKey, out var sym)) continue;
					var parsed = ParsingHelpers.ParseOptionSymbol(sym);
					if (parsed == null || parsed.CallPut != callPut || expandedStrikes.Contains(parsed.Strike)) continue;

					var priorQty = optionBuySellTrades.Where(t => t.MatchKey == sibling.MatchKey && t.Timestamp < sibling.Timestamp).Sum(t => t.Side == Side.Buy ? t.Qty : -t.Qty);
					if (priorQty == 0)
					{
						expandedStrikes.Add(parsed.Strike);
						changed = true;
					}
				}
		}

		var occSuffixes = expandedStrikes.Select(s => $"{callPut}{(long)(s * 1000m):D8}").ToHashSet();
		var today = EvaluationDate.Today;

		var legEvents = optionBuySellTrades
			.Where(t => t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal)))
			.Select(t => (t.Timestamp, t.Seq, t.MatchKey, t.Side, t.Qty, t.Price, t.Multiplier, t.Instrument, IsExpiry: false))
			.ToList();

		// Add expiration events for expired contracts (needed to find flat time)
		var expiredContracts = legEvents.Select(e => e.MatchKey).Distinct().Select(mk => (matchKey: mk, parsed: MatchKeys.TryGetOptionSymbol(mk, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null)).Where(x => x.parsed != null && x.parsed.ExpiryDate.Date < today).ToList();
		foreach (var (matchKey, parsed) in expiredContracts)
			legEvents.Add((parsed!.ExpiryDate.Date + PositionTracker.ExpirationTime, int.MaxValue, matchKey, Side.Expire, 0, 0m, 0m, Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike) + " " + ParsingHelpers.CallPutDisplayName(parsed.CallPut), IsExpiry: true));

		legEvents.Sort((a, b) => { var cmp = a.Timestamp.CompareTo(b.Timestamp); return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq); });

		// Walk events chronologically to find the last point where all positions were flat.
		// This uses the FULL unfiltered event list — qtyLimits must not interfere here.
		var positions = new Dictionary<string, int>();
		DateTime lastFlatTime = DateTime.MinValue;
		int lastFlatSeq = int.MinValue;

		for (int i = 0; i < legEvents.Count;)
		{
			var batchTime = legEvents[i].Timestamp;
			var batchEndSeq = legEvents[i].Seq;
			while (i < legEvents.Count && legEvents[i].Timestamp == batchTime)
			{
				var e = legEvents[i];
				if (e.IsExpiry)
					positions[e.MatchKey] = 0;
				else
					positions[e.MatchKey] = positions.GetValueOrDefault(e.MatchKey) + (e.Side == Side.Buy ? e.Qty : -e.Qty);
				batchEndSeq = e.Seq;
				i++;
			}

			if (positions.Values.All(v => v == 0))
			{
				lastFlatTime = batchTime;
				lastFlatSeq = batchEndSeq;
			}
		}

		// Sum cash flows since the last flat point, applying qtyLimits to cap each leg's
		// contribution to the group's allocated qty. Trades exceeding the limit are partially
		// included (pro-rated) so that e.g. a 450-contract buy only contributes 300 to a
		// 300-contract strategy. This prevents trades belonging to other groups from polluting
		// this strategy's adjusted price.
		var trades = new List<NetDebitTrade>();
		decimal totalDebit = 0m;
		var qtyTracking = qtyLimits != null ? new Dictionary<string, int>() : null;

		foreach (var e in legEvents.Where(e => !e.IsExpiry && (e.Timestamp > lastFlatTime || (e.Timestamp == lastFlatTime && e.Seq > lastFlatSeq))))
		{
			var qty = e.Qty;
			if (qtyTracking != null && qtyLimits!.TryGetValue(e.MatchKey, out var limit))
			{
				var currentPos = qtyTracking.GetValueOrDefault(e.MatchKey);
				var delta = e.Side == Side.Buy ? qty : -qty;
				var newPos = currentPos + delta;
				if (Math.Abs(newPos) > limit)
				{
					var allowedPos = delta > 0 ? limit : -limit;
					qty = Math.Abs(allowedPos - currentPos);
					newPos = allowedPos;
				}
				if (qty <= 0) continue;
				qtyTracking[e.MatchKey] = newPos;
			}

			var cashImpact = (e.Side == Side.Buy ? -1m : 1m) * qty * e.Price * e.Multiplier;
			totalDebit -= cashImpact;
			trades.Add(new NetDebitTrade(e.Timestamp, e.Instrument, e.Side, qty, e.Price, cashImpact));
		}

		return new StrategyAdjustment(trades, totalDebit, lastFlatTime == DateTime.MinValue ? null : lastFlatTime);
	}
}

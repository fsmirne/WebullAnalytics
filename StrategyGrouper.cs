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
	internal static List<List<(string matchKey, PositionRow row, Trade? trade)>> GroupIntoStrategies(List<(string matchKey, PositionRow row, Trade? trade)> allPositions, Dictionary<string, List<Lot>> positions)
	{
		var grouped = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();
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

		// Merge remainders into existing strategy groups when ALL legs have remainders.
		// This handles lots entered via standalone trades (no ParentStrategySeq) or linked to
		// expired legs — they belong with the strategy group rather than forming a separate one.
		// When only SOME legs have remainders, merging would create unbalanced legs, so those
		// remainders go to fallback grouping instead (e.g., 299 orphaned longs pair with 299 shorts).
		foreach (var group in grouped)
		{
			if (!group.All(leg => remaindersByKey.ContainsKey(leg.matchKey))) continue;
			for (int li = 0; li < group.Count; li++)
			{
				var leg = group[li];
				var rem = remaindersByKey[leg.matchKey];
				var combinedQty = leg.row.Qty + rem.row.Qty;
				var combinedAvg = (leg.row.AvgPrice * leg.row.Qty + rem.row.AvgPrice * rem.row.Qty) / combinedQty;
				group[li] = (leg.matchKey, leg.row with { Qty = combinedQty, AvgPrice = combinedAvg }, leg.trade);
				remaindersByKey.Remove(leg.matchKey);
			}
		}

		// Fallback grouping: group ungrouped options that form calendars/diagonals/verticals
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
			grouped.Add(entries);
			foreach (var e in entries) fallbackGrouped.Add(e.matchKey);
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

		return grouped;
	}

	/// <summary>
	/// Builds the final position rows, creating strategy summary rows for multi-leg positions.
	/// Returns the rows and a dictionary of pre-computed timeline replays keyed by summary row index.
	/// </summary>
	internal static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments) BuildFinalPositionRows(List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped, List<Trade> allTrades, Dictionary<string, List<Lot>> positions)
	{
		var tradeBySeq = allTrades.Where(t => t.Asset == Asset.OptionStrategy).ToDictionary(t => t.Seq);

		// Identify pure strategy groups (all lots from one parent, all parent legs present).
		// Pure groups don't need the timeline replay — their adjusted price equals their initial price.
		// Non-pure groups exclude pure siblings' parentStrategySeqs from the replay to avoid double-counting.
		var pureParentSeqs = new HashSet<int>();
		var isPure = new bool[grouped.Count];
		for (int i = 0; i < grouped.Count; i++)
		{
			if (grouped[i].Count <= 1) continue;
			var parentSeq = GetPureStrategyParentSeq(grouped[i], positions, allTrades);
			if (parentSeq.HasValue)
			{
				isPure[i] = true;
				pureParentSeqs.Add(parentSeq.Value);
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
				var (strategyRows, adjustment) = BuildStrategyRows(group, allTrades, isPure[i] ? null : pureParentSeqs);
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
	/// Returns the parentStrategySeq if the group is a "pure" strategy: all lots originate from
	/// a single parent trade and all of that parent's legs are present in the group.
	/// </summary>
	private static int? GetPureStrategyParentSeq(List<(string matchKey, PositionRow row, Trade? trade)> group, Dictionary<string, List<Lot>> positions, List<Trade> allTrades)
	{
		var candidates = new HashSet<int>();
		foreach (var leg in group)
			foreach (var lot in positions.GetValueOrDefault(leg.matchKey, []))
				if (lot.ParentStrategySeq.HasValue)
					candidates.Add(lot.ParentStrategySeq.Value);

		var groupKeys = group.Select(g => g.matchKey).ToHashSet();
		foreach (var seq in candidates)
		{
			if (!group.All(leg => positions.GetValueOrDefault(leg.matchKey, []).Where(l => l.ParentStrategySeq == seq).Sum(l => l.Qty) >= leg.row.Qty))
				continue;

			var parentLegKeys = allTrades.Where(t => t.ParentStrategySeq == seq && t.Asset == Asset.Option).Select(t => t.MatchKey).ToHashSet();
			if (parentLegKeys.IsSubsetOf(groupKeys))
				return seq;
		}

		return null;
	}

	/// <summary>
	/// For a single-leg option that was part of a strategy whose other legs have expired/closed,
	/// computes an adjusted price reflecting the credits/debits from those expired legs.
	/// </summary>
	private static PositionRow AdjustForExpiredStrategyLegs((string matchKey, PositionRow row, Trade? trade) entry, Dictionary<string, List<Lot>> positions, List<Trade> allTrades, Dictionary<int, Trade> tradeBySeq)
	{
		var (matchKey, row, trade) = entry;
		if (trade?.Asset != Asset.Option) return row;

		var lots = positions.GetValueOrDefault(matchKey, []);
		var strategyLots = lots.Where(l => l.ParentStrategySeq.HasValue).ToList();
		if (strategyLots.Count == 0) return row;

		var totalAdjustment = 0m;
		var adjustedQty = 0;
		foreach (var lot in strategyLots)
		{
			if (!tradeBySeq.TryGetValue(lot.ParentStrategySeq!.Value, out var parentTrade)) continue;
			var otherLegs = allTrades.Where(t => t.ParentStrategySeq == parentTrade.Seq && t.MatchKey != matchKey).ToList();
			if (otherLegs.Count == 0) continue;
			if (otherLegs.Any(leg => positions.ContainsKey(leg.MatchKey) && positions[leg.MatchKey].Sum(l => l.Qty) > 0)) continue;

			totalAdjustment += (lot.Price - parentTrade.Price) * lot.Qty;
			adjustedQty += lot.Qty;
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
	private static (List<PositionRow> rows, StrategyAdjustment? adjustment) BuildStrategyRows(List<(string matchKey, PositionRow row, Trade? trade)> group, List<Trade> allTrades, HashSet<int>? excludedParentSeqs)
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

		var netInitial = group.Sum(leg => leg.row.Side == Side.Buy ? leg.row.AvgPrice : -leg.row.AvgPrice);

		// Pure strategies: adj = init, no replay needed.
		// Non-pure strategies: replay the timeline to compute the adjusted price.
		StrategyAdjustment? adjustment = null;
		var netAdjusted = netInitial;
		var strategyInitial = netInitial;
		if (excludedParentSeqs != null)
		{
			var legStrikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().OrderBy(s => s).ToList();
			adjustment = ReplayTimeline(allTrades, firstParsed.Root, legStrikes, firstParsed.CallPut, excludedParentSeqs);
			netAdjusted = adjustment.TotalNetDebit / (qty * 100m);

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
					strategyInitial = firstBatchDebit / (firstBatchQty * 100m);
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
	private static StrategyAdjustment ReplayTimeline(List<Trade> allTrades, string root, List<decimal> strikes, string callPut, HashSet<int>? excludedParentSeqs = null)
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
		var today = DateTime.Today;

		var legEvents = optionBuySellTrades
			.Where(t => t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal)))
			.Select(t => (t.Timestamp, t.Seq, t.MatchKey, t.Side, t.Qty, t.Price, t.Multiplier, t.Instrument, IsExpiry: false))
			.ToList();

		// Add expiration events for expired contracts (needed to find flat time)
		var expiredContracts = legEvents.Select(e => e.MatchKey).Distinct().Select(mk => (matchKey: mk, parsed: MatchKeys.TryGetOptionSymbol(mk, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null)).Where(x => x.parsed != null && x.parsed.ExpiryDate.Date < today).ToList();
		foreach (var (matchKey, parsed) in expiredContracts)
			legEvents.Add((parsed!.ExpiryDate.Date + PositionTracker.ExpirationTime, int.MaxValue, matchKey, Side.Expire, 0, 0m, 0m, Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike) + " " + ParsingHelpers.CallPutDisplayName(parsed.CallPut), IsExpiry: true));

		legEvents.Sort((a, b) => { var cmp = a.Timestamp.CompareTo(b.Timestamp); return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq); });

		// Walk events chronologically to find the last point where all positions were flat
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

		// Sum cash flows and collect trade details since the last flat point
		var trades = new List<NetDebitTrade>();
		decimal totalDebit = 0m;

		foreach (var e in legEvents.Where(e => !e.IsExpiry && (e.Timestamp > lastFlatTime || (e.Timestamp == lastFlatTime && e.Seq > lastFlatSeq))))
		{
			var cashImpact = (e.Side == Side.Buy ? -1m : 1m) * e.Qty * e.Price * e.Multiplier;
			totalDebit -= cashImpact;
			trades.Add(new NetDebitTrade(e.Timestamp, e.Instrument, e.Side, e.Qty, e.Price, cashImpact));
		}

		return new StrategyAdjustment(trades, totalDebit, lastFlatTime == DateTime.MinValue ? null : lastFlatTime);
	}
}

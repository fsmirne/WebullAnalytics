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

		// Split positions with mixed lots: some strategy-linked, some standalone.
		var standaloneSplits = new List<(string matchKey, PositionRow row, Trade? trade)>();
		var workingPositions = new List<(string matchKey, PositionRow row, Trade? trade)>();
		foreach (var p in allPositions)
		{
			if (p.trade?.Asset != Asset.Option) { workingPositions.Add(p); continue; }

			var lots = positions.GetValueOrDefault(p.matchKey, []);
			var strategyLots = lots.Where(l => l.ParentStrategySeq.HasValue).ToList();
			var standaloneLots = lots.Where(l => !l.ParentStrategySeq.HasValue).ToList();

			if (strategyLots.Count > 0 && standaloneLots.Count > 0)
			{
				var strategyQty = strategyLots.Sum(l => l.Qty);
				var strategyAvg = strategyLots.Sum(l => l.Price * l.Qty) / strategyQty;
				workingPositions.Add((p.matchKey, p.row with { Qty = strategyQty, AvgPrice = strategyAvg }, p.trade));

				var standaloneQty = standaloneLots.Sum(l => l.Qty);
				var standaloneAvg = standaloneLots.Sum(l => l.Price * l.Qty) / standaloneQty;
				standaloneSplits.Add((p.matchKey, p.row with { Qty = standaloneQty, AvgPrice = standaloneAvg }, p.trade));
			}
			else
			{
				workingPositions.Add(p);
			}
		}

		// Build reverse index: parentSeq → set of option matchKeys whose lots reference it
		var parentToKeys = new Dictionary<int, HashSet<string>>();
		foreach (var p in workingPositions.Where(p => p.trade?.Asset == Asset.Option))
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

		// Convert hashsets to position groups
		var positionLookup = workingPositions.Where(p => p.trade?.Asset == Asset.Option).ToDictionary(p => p.matchKey);
		foreach (var keySet in strategyGroups)
		{
			var group = keySet.Select(k => positionLookup[k]).ToList();
			grouped.Add(group);
			foreach (var k in keySet) processed.Add(k);
		}

		// Recombine standalone splits with their strategy-linked counterparts
		var recombinedKeys = new HashSet<string>();
		foreach (var split in standaloneSplits)
		{
			if (!processed.Contains(split.matchKey))
			{
				recombinedKeys.Add(split.matchKey);
			}
			else
			{
				var targetGroup = grouped.FirstOrDefault(g => g.Any(p => p.matchKey == split.matchKey));
				if (targetGroup != null)
				{
					var legIndex = targetGroup.FindIndex(p => p.matchKey == split.matchKey);
					var original = allPositions.First(p => p.matchKey == split.matchKey);
					targetGroup[legIndex] = original;
				}
				else
				{
					grouped.Add(new List<(string, PositionRow, Trade?)> { split });
				}
			}
		}

		// Fallback grouping: group ungrouped options that form calendars/diagonals/verticals
		var ungroupedOptions = workingPositions.Where(p => p.trade?.Asset == Asset.Option && !processed.Contains(p.matchKey) && !recombinedKeys.Contains(p.matchKey))
			.Concat(allPositions.Where(p => recombinedKeys.Contains(p.matchKey)))
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
		grouped.AddRange(workingPositions.Where(p => p.trade?.Asset != Asset.Option).Select(p => new List<(string, PositionRow, Trade?)> { p }));

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
	/// </summary>
	internal static List<PositionRow> BuildFinalPositionRows(List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped, List<Trade> allTrades, Dictionary<string, List<Lot>> positions)
	{
		var tradeBySeq = allTrades.Where(t => t.Asset == Asset.OptionStrategy).ToDictionary(t => t.Seq);
		var rows = new List<PositionRow>();

		foreach (var group in grouped)
		{
			if (group.Count > 1)
				rows.AddRange(BuildStrategyRows(group, allTrades));
			else
				rows.Add(AdjustForExpiredStrategyLegs(group[0], positions, allTrades, tradeBySeq));
		}

		return rows;
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
	/// </summary>
	private static List<PositionRow> BuildStrategyRows(List<(string matchKey, PositionRow row, Trade? trade)> group, List<Trade> allTrades)
	{
		var rows = new List<PositionRow>();

		var parsedLegs = group.Select(g => (leg: g, parsed: MatchKeys.TryGetOptionSymbol(g.matchKey, out var symbol) ? ParsingHelpers.ParseOptionSymbol(symbol) : null)).Where(x => x.parsed != null).ToList();

		if (!parsedLegs.Any())
		{
			rows.AddRange(group.Select(g => g.row));
			return rows;
		}

		var firstParsed = parsedLegs[0].parsed!;
		var distinctExpiries = parsedLegs.Select(x => x.parsed!.ExpiryDate).Distinct().Count();
		var distinctStrikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().Count();
		var distinctCallPut = parsedLegs.Select(x => x.parsed!.CallPut).Distinct().Count();

		var strategyKind = ParsingHelpers.ClassifyStrategyKind(parsedLegs.Count, distinctExpiries, distinctStrikes, distinctCallPut);
		var qty = group[0].row.Qty;

		var netInitial = group.Sum(leg => leg.row.Side == Side.Buy ? leg.row.AvgPrice : -leg.row.AvgPrice);

		var netAdjusted = netInitial;
		var legStrikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().OrderBy(s => s).ToList();
		var (totalNetDebit, _) = CalculateTotalNetDebit(allTrades, firstParsed.Root, legStrikes, firstParsed.CallPut);
		netAdjusted = totalNetDebit / (qty * 100m);

		var longLegAdjustment = netInitial - netAdjusted;
		var side = netAdjusted >= 0 ? Side.Buy : Side.Sell;
		var longestExpiry = group.Max(g => g.row.Expiry) ?? DateTime.MinValue;

		var displayPrice = side == Side.Sell ? -netAdjusted : netAdjusted;
		var displayInitial = side == Side.Sell ? -netInitial : netInitial;
		rows.Add(new PositionRow(Instrument: $"{firstParsed.Root} {Formatters.FormatOptionDate(longestExpiry)}", Asset: Asset.OptionStrategy, OptionKind: strategyKind, Side: side, Qty: qty, AvgPrice: displayPrice, Expiry: longestExpiry, IsStrategyLeg: false, InitialAvgPrice: displayInitial, AdjustedAvgPrice: displayPrice));

		foreach (var leg in group.OrderByDescending(g => g.row.Expiry).ThenByDescending(g => parsedLegs.FirstOrDefault(p => p.leg.matchKey == g.matchKey).parsed?.Strike ?? 0))
		{
			var initialPrice = leg.row.AvgPrice;
			var adjustedPrice = leg.row.Side == Side.Buy ? initialPrice - longLegAdjustment : initialPrice;
			rows.Add(leg.row with { IsStrategyLeg = true, InitialAvgPrice = initialPrice, AdjustedAvgPrice = adjustedPrice });
		}

		return rows;
	}

	/// <summary>
	/// Computes total net debit from individual leg trades for the current position at given root/strikes/type.
	/// </summary>
	private static (decimal totalDebit, int accountedQty) CalculateTotalNetDebit(List<Trade> allTrades, string root, List<decimal> strikes, string callPut)
	{
		var optionKeyPrefix = $"{MatchKeys.OptionPrefix}{root}";

		// Expand strikes transitively by following strategy parent relationships
		var expandedStrikes = new HashSet<decimal>(strikes);
		var optionBuySellTrades = allTrades.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell).ToList();
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

		var legEvents = allTrades
			.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal)))
			.Select(t => (t.Timestamp, t.Seq, t.MatchKey, t.Side, t.Qty, t.Price, t.Multiplier, IsExpiry: false))
			.ToList();

		var expiredContracts = legEvents.Select(e => e.MatchKey).Distinct()
			.Select(mk => (matchKey: mk, parsed: MatchKeys.TryGetOptionSymbol(mk, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
			.Where(x => x.parsed != null && x.parsed.ExpiryDate.Date < today)
			.ToList();

		foreach (var (matchKey, parsed) in expiredContracts)
			legEvents.Add((parsed!.ExpiryDate.Date + PositionTracker.ExpirationTime, int.MaxValue, matchKey, Side.Expire, 0, 0m, 0m, IsExpiry: true));

		legEvents.Sort((a, b) => { var cmp = a.Timestamp.CompareTo(b.Timestamp); return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq); });

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

		var totalDebit = legEvents
			.Where(e => !e.IsExpiry && (e.Timestamp > lastFlatTime || (e.Timestamp == lastFlatTime && e.Seq > lastFlatSeq)))
			.Sum(e => (e.Side == Side.Buy ? 1m : -1m) * e.Qty * e.Price * e.Multiplier);

		return (totalDebit, int.MaxValue);
	}
}

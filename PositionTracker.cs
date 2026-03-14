namespace WebullAnalytics;

/// <summary>
/// Tracks option and stock positions, calculates realized P&L, and builds reports.
/// Uses FIFO (First-In-First-Out) accounting for P&L matching and average cost method for position display.
/// </summary>
public static class PositionTracker
{
	private static readonly TimeSpan ExpirationTime = new(23, 59, 59);

	private static bool IsStrategyParent(Trade trade) => trade.Asset == Asset.OptionStrategy;
	private static bool IsStrategyLeg(Trade trade) => trade.ParentStrategySeq.HasValue;

	/// <summary>
	/// Computes the realized P&L report by processing all trades chronologically.
	/// Generates synthetic expiration trades for options that have expired.
	/// </summary>
	/// <param name="trades">All trades loaded from CSV files</param>
	/// <param name="sinceDate">Only include trades on or after this date (DateTime.MinValue for all)</param>
	/// <returns>Report rows, final positions, and total realized P&L</returns>
	public static (List<ReportRow> rows, Dictionary<string, List<Lot>> positions, decimal running) ComputeReport(List<Trade> trades, DateTime sinceDate, decimal initialAmount = 0m, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup = null)
	{
		// Filter trades by date and add synthetic expiration trades
		var allTrades = trades.Where(t => t.Timestamp.Date >= sinceDate.Date).Concat(BuildExpirationTrades(trades)).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq).ToList();

		var positions = new Dictionary<string, List<Lot>>();
		var running = 0m;
		var cash = initialAmount;
		var rows = new List<ReportRow>();
		var skippedParentSeqs = new HashSet<int>();

		foreach (var trade in allTrades)
		{
			var (realized, closedQty) = ProcessTrade(trade, positions, allTrades);

			// Adjust P&L for strategy parent/leg price discrepancy.
			// CSV parent prices reflect actual broker cash flow, but individual leg prices
			// may not sum to the parent price due to rounding. Without this adjustment,
			// leg-level FIFO P&L diverges from actual cash flow.
			if (IsStrategyParent(trade) && trade.Side is Side.Buy or Side.Sell)
			{
				var legs = allTrades.Where(t => t.ParentStrategySeq == trade.Seq).ToList();
				if (legs.Count >= 2)
				{
					var parentCash = (trade.Side == Side.Sell ? 1m : -1m) * trade.Qty * trade.Price * trade.Multiplier;
					var legCash = legs.Sum(leg => (leg.Side == Side.Sell ? 1m : -1m) * leg.Qty * leg.Price * leg.Multiplier);
					realized += parentCash - legCash;
				}
			}

			var fee = LookupFee(trade, allTrades, feeLookup);
			var row = BuildReportRow(trade, realized, closedQty, fee, ref running, ref cash, initialAmount);

			// Track skipped strategy parents so their legs can also be skipped
			if (row == null && IsStrategyParent(trade))
				skippedParentSeqs.Add(trade.Seq);

			// Skip strategy legs whose parent expiration was skipped (no positions to close)
			if (row != null && trade.ParentStrategySeq.HasValue && skippedParentSeqs.Contains(trade.ParentStrategySeq.Value))
				row = null;

			if (row != null)
				rows.Add(row);
		}

		return (rows, positions, running);
	}

	/// <summary>
	/// Looks up the fee for a trade. For strategy parents, sums up fees from all child legs.
	/// </summary>
	private static decimal LookupFee(Trade trade, List<Trade> allTrades, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup)
	{
		if (feeLookup == null) return 0m;

		// Strategy parent: sum fees from child legs
		if (trade.Asset == Asset.OptionStrategy)
			return allTrades.Where(t => t.ParentStrategySeq == trade.Seq).Select(leg => (leg.Timestamp, leg.Side, leg.Qty)).Distinct().Sum(key => feeLookup.GetValueOrDefault(key, 0m));

		// Strategy leg or standalone: direct lookup
		return feeLookup.GetValueOrDefault((trade.Timestamp, trade.Side, trade.Qty), 0m);
	}

	/// <summary>
	/// Processes a single trade, updating positions in-place.
	/// Returns realized P&L and quantity closed.
	/// </summary>
	private static (decimal realized, int closedQty) ProcessTrade(Trade trade, Dictionary<string, List<Lot>> positions, List<Trade> allTrades)
	{
		if (!IsStrategyParent(trade))
			return ApplyTrade(positions, trade);

		// Strategy parent expiration: expire each leg and sum P&L
		if (trade.Side == Side.Expire)
		{
			var legs = allTrades.Where(t => t.ParentStrategySeq == trade.Seq).ToList();

			if (legs.Count >= 2)
			{
				// Expire legs and compute combined P&L
				var realized = 0m;
				var maxLegClosed = 0;
				foreach (var leg in legs)
				{
					var legLots = positions.GetValueOrDefault(leg.MatchKey, []);
					var (_, legRealized, legClosed) = ApplyExpiration(legLots, leg.Multiplier);
					realized += legRealized;
					maxLegClosed = Math.Max(maxLegClosed, legClosed);
					positions.Remove(leg.MatchKey);
				}

				// Clean up strategy parent lots (P&L already captured via legs)
				positions.Remove(trade.MatchKey);
				return (realized, maxLegClosed);
			}

			// No linked legs: just clean up parent lots, legs will be expired individually
			positions.Remove(trade.MatchKey);
			return (0m, 0);
		}

		// Strategy parent: compute P&L from leg-level FIFO matching.
		// Parent-level FIFO is unreliable because legs can be modified by intermediate strategy trades,
		// causing parent open/close prices to diverge from actual leg P&L.
		var pnl = 0m;
		var closedQty = 0;
		foreach (var leg in allTrades.Where(t => t.ParentStrategySeq == trade.Seq))
		{
			var legLots = positions.GetValueOrDefault(leg.MatchKey, []);
			var (_, legPnl, legClosed) = ApplyToLots(legLots, leg.Side, leg.Qty, leg.Price, leg.Multiplier);
			pnl += legPnl;
			closedQty = Math.Max(closedQty, legClosed);
		}
		return (pnl, closedQty);
	}

	/// <summary>
	/// Builds a report row for the given trade. Returns null if the row should be skipped.
	/// Strategy legs are shown but don't affect running P&L or cash.
	/// </summary>
	private static ReportRow? BuildReportRow(Trade trade, decimal realized, int closedQty, decimal fee, ref decimal running, ref decimal cash, decimal initialAmount)
	{
		var optionKind = string.IsNullOrEmpty(trade.OptionKind) ? "-" : trade.OptionKind;
		var isLeg = IsStrategyLeg(trade);

		// Strategy legs: show in report but don't affect running P&L or cash
		if (isLeg)
		{
			return new ReportRow(trade.Timestamp, trade.Instrument, trade.Asset, optionKind, trade.Side, trade.Qty, trade.Price, ClosedQty: 0m, Realized: 0m, running, Cash: cash, Total: initialAmount + running, IsStrategyLeg: true, Fees: fee);
		}

		// Skip expirations that didn't close any positions
		if (trade.Side == Side.Expire && closedQty == 0)
			return null;

		// Update cash: buys spend cash, sells receive cash, expires don't move cash
		if (trade.Side == Side.Buy)
			cash -= trade.Qty * trade.Price * trade.Multiplier;
		else if (trade.Side == Side.Sell)
			cash += trade.Qty * trade.Price * trade.Multiplier;

		// Subtract fees from realized P&L and cash
		realized -= fee;
		cash -= fee;

		// Regular trade or strategy parent: update running P&L
		running += realized;
		var displayQty = trade.Side == Side.Expire ? closedQty : trade.Qty;

		return new ReportRow(trade.Timestamp, trade.Instrument, trade.Asset, optionKind, trade.Side, displayQty, trade.Price, closedQty, realized, running, Cash: cash, Total: initialAmount + running, IsStrategyLeg: false, Fees: fee);
	}

	/// <summary>
	/// Creates synthetic expiration trades for all options that have expired (before today).
	/// These trades close out any remaining positions at $0.
	/// Preserves strategy parent/leg relationships so expired spreads display as strategies.
	/// </summary>
	private static List<Trade> BuildExpirationTrades(List<Trade> trades)
	{
		if (!trades.Any())
			return [];

		var maxSeq = trades.Max(t => t.Seq);
		var today = DateTime.Today;
		var seq = maxSeq + 1;

		// Get one trade per unique match key that has expired
		var uniqueExpired = trades.Where(t => t.Expiry.HasValue && t.Expiry.Value.Date < today).GroupBy(t => t.MatchKey).Select(g => g.First()).ToList();

		// Build mapping: leg match key → parent match key (from original strategy trades)
		var legToParentKey = new Dictionary<string, string>();
		foreach (var trade in trades.Where(t => t.ParentStrategySeq.HasValue && t.Asset == Asset.Option))
		{
			var parent = trades.FirstOrDefault(t => t.Seq == trade.ParentStrategySeq!.Value);
			if (parent != null)
				legToParentKey.TryAdd(trade.MatchKey, parent.MatchKey);
		}

		var result = new List<Trade>();

		// First pass: create strategy parent expirations and record their seq numbers
		var parentInfo = new Dictionary<string, (int seq, DateTime? expiry)>();
		foreach (var trade in uniqueExpired.Where(t => t.Asset == Asset.OptionStrategy))
		{
			var expSeq = seq++;
			parentInfo[trade.MatchKey] = (expSeq, trade.Expiry);
			result.Add(new Trade(Seq: expSeq, Timestamp: trade.Expiry!.Value.Date + ExpirationTime, trade.Instrument, trade.MatchKey, trade.Asset, trade.OptionKind, Side: Side.Expire, Qty: 0, Price: 0m, trade.Multiplier, trade.Expiry));
		}

		// Second pass: create option expirations, linking legs to their parent when both expire on the same date
		foreach (var trade in uniqueExpired.Where(t => t.Asset == Asset.Option))
		{
			int? parentStrategySeq = null;
			if (legToParentKey.TryGetValue(trade.MatchKey, out var parentMatchKey) && parentInfo.TryGetValue(parentMatchKey, out var info) && trade.Expiry?.Date == info.expiry?.Date)
				parentStrategySeq = info.seq;

			result.Add(new Trade(Seq: seq++, Timestamp: trade.Expiry!.Value.Date + ExpirationTime, trade.Instrument, trade.MatchKey, trade.Asset, trade.OptionKind, Side: Side.Expire, Qty: 0, Price: 0m, trade.Multiplier, trade.Expiry, ParentStrategySeq: parentStrategySeq));
		}

		// Unlink parents with fewer than 2 linked legs (e.g., calendar with only one leg expiring)
		var legsPerParent = result.Where(t => t.ParentStrategySeq.HasValue).GroupBy(t => t.ParentStrategySeq!.Value).ToDictionary(g => g.Key, g => g.Count());
		result = result.Select(t =>
		{
			if (t.ParentStrategySeq.HasValue && legsPerParent.GetValueOrDefault(t.ParentStrategySeq.Value) < 2)
				return t with { ParentStrategySeq = null };
			return t;
		}).ToList();

		return result;
	}

	/// <summary>
	/// Applies a trade to the current positions using FIFO accounting, mutating positions in-place.
	/// Returns realized P&L and quantity closed.
	/// </summary>
	private static (decimal realized, int closedQty) ApplyTrade(Dictionary<string, List<Lot>> positions, Trade trade)
	{
		var lots = positions.GetValueOrDefault(trade.MatchKey, []);

		var (updatedLots, realized, closedQty) = trade.Side == Side.Expire ? ApplyExpiration(lots, trade.Multiplier) : ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier, trade.ParentStrategySeq);

		if (updatedLots.Count > 0)
			positions[trade.MatchKey] = updatedLots;
		else
			positions.Remove(trade.MatchKey);

		return (realized, closedQty);
	}

	/// <summary>
	/// Closes all lots at expiration (price = $0).
	/// Long positions lose their full value; short positions gain their full value.
	/// </summary>
	private static (List<Lot>, decimal realized, int closedQty) ApplyExpiration(List<Lot> lots, decimal multiplier)
	{
		if (lots.Count == 0)
			return (new List<Lot>(), 0m, 0);

		// Long position expires worthless (negative P&L), short position keeps premium (positive P&L)
		var realized = lots.Sum(lot => lot.Side == Side.Buy ? -lot.Price * lot.Qty * multiplier : lot.Price * lot.Qty * multiplier);

		var closedQty = lots.Sum(lot => lot.Qty);

		return (new List<Lot>(), realized, closedQty);
	}

	/// <summary>
	/// Applies a buy/sell trade to existing lots using FIFO matching.
	/// Opposite-side lots are closed first, then remaining quantity opens new position.
	/// </summary>
	private static (List<Lot>, decimal realized, int closedQty) ApplyToLots(List<Lot> lots, Side tradeSide, int tradeQty, decimal tradePrice, decimal multiplier, int? parentStrategySeq = null)
	{
		var remaining = tradeQty;
		var realized = 0m;
		var closedQty = 0;
		var updated = new List<Lot>();

		foreach (var lot in lots)
		{
			// Match against opposite-side lots
			if (remaining > 0 && lot.Side != tradeSide)
			{
				var matchQty = Math.Min(remaining, lot.Qty);

				// P&L = (exit price - entry price) * qty * multiplier
				// For buys closing shorts: lot.Price - tradePrice (we sold high, bought low)
				// For sells closing longs: tradePrice - lot.Price (we bought low, sold high)
				var pnlPerContract = tradeSide == Side.Buy ? lot.Price - tradePrice : tradePrice - lot.Price;

				realized += pnlPerContract * matchQty * multiplier;
				closedQty += matchQty;
				remaining -= matchQty;

				// Keep leftover quantity in the lot
				var leftover = lot.Qty - matchQty;
				if (leftover > 0)
					updated.Add(lot with { Qty = leftover });
			}
			else
			{
				updated.Add(lot);
			}
		}

		// Add remaining quantity as new lot
		if (remaining > 0)
			updated.Add(new Lot(tradeSide, remaining, tradePrice, parentStrategySeq));

		return (updated, realized, closedQty);
	}

	/// <summary>
	/// Builds position rows for display, grouping options into strategies using trade relationships.
	/// </summary>
	public static List<PositionRow> BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
	{
		var avgCosts = ComputeAverageCosts(allTrades);
		var allPositions = BuildRawPositionRows(positions, tradeIndex, avgCosts);
		var grouped = GroupIntoStrategies(allPositions, positions);
		return BuildFinalPositionRows(grouped, allTrades);
	}

	/// <summary>
	/// Computes average cost per position using the average cost method (matching Webull's display).
	/// Opening trades update the weighted average; closing trades leave it unchanged.
	/// </summary>
	private static Dictionary<string, decimal> ComputeAverageCosts(List<Trade> allTrades)
	{
		var state = new Dictionary<string, (int qty, decimal avg)>();

		foreach (var trade in allTrades.Where(t => t.Side is Side.Buy or Side.Sell && t.Asset != Asset.OptionStrategy).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq))
		{
			var key = trade.MatchKey;
			var (qty, avg) = state.GetValueOrDefault(key);

			var tradeSign = trade.Side == Side.Buy ? 1 : -1;
			var newQty = qty + tradeSign * trade.Qty;
			var isOpening = qty == 0 || (qty > 0 && trade.Side == Side.Buy) || (qty < 0 && trade.Side == Side.Sell);

			if (isOpening)
				avg = (Math.Abs(qty) * avg + trade.Qty * trade.Price) / Math.Abs(newQty);
			else if (newQty == 0)
				avg = 0m;
			else if ((qty > 0 && newQty < 0) || (qty < 0 && newQty > 0))
				avg = trade.Price;
			// else: partial close, avg stays the same

			state[key] = (newQty, avg);
		}

		return state.Where(kvp => kvp.Value.qty != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.avg);
	}

	/// <summary>
	/// Converts raw position data (lots) into position rows with calculated averages.
	/// Uses average cost method for display prices (matching broker display).
	/// </summary>
	private static List<(string matchKey, PositionRow row, Trade? trade)> BuildRawPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, Dictionary<string, decimal> avgCosts)
	{
		var result = new List<(string matchKey, PositionRow row, Trade? trade)>();

		foreach (var (matchKey, lots) in positions)
		{
			if (!lots.Any() || lots.Sum(l => l.Qty) <= 0)
				continue;

			var trade = tradeIndex.GetValueOrDefault(matchKey);

			// Skip strategy parents - show legs only
			if (trade?.Asset == Asset.OptionStrategy)
				continue;

			var totalQty = lots.Sum(l => l.Qty);
			var avgPrice = avgCosts.GetValueOrDefault(matchKey, lots.Sum(l => l.Price * l.Qty) / totalQty);

			var row = new PositionRow(Instrument: trade?.Instrument ?? matchKey, Asset: trade?.Asset ?? Asset.Stock, OptionKind: !string.IsNullOrEmpty(trade?.OptionKind) ? trade.OptionKind : "-", Side: lots[0].Side, Qty: totalQty, AvgPrice: avgPrice, Expiry: trade?.Expiry, IsStrategyLeg: false, MatchKey: matchKey);

			result.Add((matchKey, row, trade));
		}

		return result;
	}

	/// <summary>
	/// Groups option positions into strategies using trade relationships.
	/// Legs that share a ParentStrategySeq in their lots were traded together as part of the same strategy.
	/// </summary>
	private static List<List<(string matchKey, PositionRow row, Trade? trade)>> GroupIntoStrategies(List<(string matchKey, PositionRow row, Trade? trade)> allPositions, Dictionary<string, List<Lot>> positions)
	{
		var grouped = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();
		var processed = new HashSet<string>();

		// Split positions with mixed lots: some strategy-linked (ParentStrategySeq set), some standalone.
		// The strategy-linked portion stays in allPositions for grouping; the standalone portion is held separately.
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

		// Build strategy groups from parentSeqs that link ≥2 positions, merging overlapping groups
		var strategyGroups = new List<HashSet<string>>();
		foreach (var (_, keys) in parentToKeys.Where(kvp => kvp.Value.Count >= 2))
		{
			// Find an existing group that overlaps with this set
			var existing = strategyGroups.FirstOrDefault(g => g.Overlaps(keys));
			if (existing != null)
				existing.UnionWith(keys);
			else
				strategyGroups.Add(new HashSet<string>(keys));
		}

		// Second merge pass: collapse groups that now overlap after the first pass
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

		// Add standalone splits from positions that were partially strategy-linked
		grouped.AddRange(standaloneSplits.Select(p => new List<(string, PositionRow, Trade?)> { p }));

		// Add unprocessed options as standalone
		grouped.AddRange(workingPositions.Where(p => p.trade?.Asset == Asset.Option && !processed.Contains(p.matchKey)).Select(p => new List<(string, PositionRow, Trade?)> { p }));

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
	/// Builds the final position rows, creating strategy summary rows for multi-leg positions
	/// and calculating adjusted prices based on credits from rolled legs.
	/// </summary>
	private static List<PositionRow> BuildFinalPositionRows(List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped, List<Trade> allTrades)
	{
		var rows = new List<PositionRow>();

		foreach (var group in grouped)
		{
			if (group.Count > 1)
			{
				var strategyRows = BuildStrategyRows(group, allTrades);
				rows.AddRange(strategyRows);
			}
			else
			{
				rows.Add(group[0].row);
			}
		}

		return rows;
	}

	/// <summary>
	/// Builds rows for a multi-leg strategy (calendar, vertical, or diagonal spread).
	/// Creates a summary row plus individual leg rows. For calendars/diagonals, the adjusted
	/// price is the exact break-even computed from total strategy-level cash flows.
	/// </summary>
	private static List<PositionRow> BuildStrategyRows(List<(string matchKey, PositionRow row, Trade? trade)> group, List<Trade> allTrades)
	{
		var rows = new List<PositionRow>();

		// Parse all legs to determine strategy type
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

		// Calculate net initial price from average cost of each leg
		var netInitial = group.Sum(leg => leg.row.Side == Side.Buy ? leg.row.AvgPrice : -leg.row.AvgPrice);

		// For calendars/diagonals, compute adjusted price from total cash flows (exact break-even).
		// For other strategies, adjusted = initial (no roll activity to account for).
		var netAdjusted = netInitial;
		if (strategyKind is "Calendar" or "Diagonal")
		{
			var legStrikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().OrderBy(s => s).ToList();
			var totalNetDebit = CalculateTotalNetDebit(allTrades, firstParsed.Root, legStrikes, firstParsed.CallPut);
			netAdjusted = totalNetDebit / (qty * 100m);
		}

		// The per-leg adjustment = difference between initial and adjusted at the strategy level.
		// Applied entirely to the long leg; short leg stays at its average cost.
		var longLegAdjustment = netInitial - netAdjusted;

		// Determine side from net price: positive = debit (Buy), negative = credit (Sell)
		var side = netInitial >= 0 ? Side.Buy : Side.Sell;
		var longestExpiry = group.Max(g => g.row.Expiry) ?? DateTime.MinValue;

		// Strategy summary row
		rows.Add(new PositionRow(Instrument: $"{firstParsed.Root} {Formatters.FormatOptionDate(longestExpiry)}", Asset: Asset.OptionStrategy, OptionKind: strategyKind, Side: side, Qty: qty, AvgPrice: Math.Abs(netAdjusted), Expiry: longestExpiry, IsStrategyLeg: false, InitialAvgPrice: Math.Abs(netInitial), AdjustedAvgPrice: Math.Abs(netAdjusted)));

		// Add leg rows with adjusted prices (sorted by expiry descending, then strike descending)
		foreach (var leg in group.OrderByDescending(g => g.row.Expiry).ThenByDescending(g => parsedLegs.FirstOrDefault(p => p.leg.matchKey == g.matchKey).parsed?.Strike ?? 0))
		{
			var initialPrice = leg.row.AvgPrice;
			var adjustedPrice = leg.row.Side == Side.Buy ? initialPrice - longLegAdjustment : initialPrice;

			rows.Add(leg.row with { IsStrategyLeg = true, InitialAvgPrice = initialPrice, AdjustedAvgPrice = adjustedPrice });
		}

		return rows;
	}

	/// <summary>
	/// Computes total net debit (cash spent minus cash received) from strategy parent trades
	/// for the CURRENT calendar/diagonal position at a given underlying/strikes/type.
	/// Tracks per-contract positions at the strike level to find when the position was last flat,
	/// then only includes strategy trades after that point. This prevents closed historical positions
	/// from contaminating the break-even of a newly opened position.
	/// </summary>
	private static decimal CalculateTotalNetDebit(List<Trade> allTrades, string root, List<decimal> strikes, string callPut)
	{
		var legsKeySuffix = ":" + string.Join(",", strikes.Select(s => $"{callPut}{Formatters.FormatQty(s)}"));
		var rootSegment = $":{root}:";

		// Build OCC suffixes to match all options at these root/strikes/callPut across any expiry
		var occSuffixes = strikes.Select(s => $"{callPut}{(long)(s * 1000m):D8}").ToHashSet();
		var optionKeyPrefix = $"{MatchKeys.OptionPrefix}{root}";
		var today = DateTime.Today;

		// Collect all option leg trades at these strikes, plus synthetic expiry events
		var legEvents = allTrades
			.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal)))
			.Select(t => (t.Timestamp, t.Seq, t.MatchKey, t.Side, t.Qty, IsExpiry: false))
			.ToList();

		// Generate synthetic expiry-close events for contracts whose expiry has passed
		var expiredContracts = legEvents.Select(e => e.MatchKey).Distinct()
			.Select(mk => (matchKey: mk, parsed: MatchKeys.TryGetOptionSymbol(mk, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
			.Where(x => x.parsed != null && x.parsed.ExpiryDate.Date < today)
			.ToList();

		foreach (var (matchKey, parsed) in expiredContracts)
			legEvents.Add((parsed!.ExpiryDate.Date + ExpirationTime, int.MaxValue, matchKey, Side.Expire, 0, IsExpiry: true));

		legEvents.Sort((a, b) => { var cmp = a.Timestamp.CompareTo(b.Timestamp); return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq); });

		// Track per-contract net positions and find the last time ALL were simultaneously zero
		var positions = new Dictionary<string, int>();
		DateTime lastFlatTime = DateTime.MinValue;
		int lastFlatSeq = int.MinValue;

		foreach (var e in legEvents)
		{
			if (e.IsExpiry)
				positions[e.MatchKey] = 0;
			else
				positions[e.MatchKey] = positions.GetValueOrDefault(e.MatchKey) + (e.Side == Side.Buy ? e.Qty : -e.Qty);

			if (positions.Values.All(v => v == 0))
			{
				lastFlatTime = e.Timestamp;
				lastFlatSeq = e.Seq;
			}
		}

		// Only include strategy parent trades after the last flat point
		return allTrades
			.Where(t => t.Asset == Asset.OptionStrategy && t.Side is Side.Buy or Side.Sell && t.MatchKey.Contains(rootSegment) && t.MatchKey.EndsWith(legsKeySuffix))
			.Where(t => t.Timestamp > lastFlatTime || (t.Timestamp == lastFlatTime && t.Seq > lastFlatSeq))
			.Sum(t => (t.Side == Side.Buy ? 1m : -1m) * t.Qty * t.Price * t.Multiplier);
	}

	/// <summary>
	/// Builds an index mapping match keys to their first trade (for metadata lookup).
	/// </summary>
	public static Dictionary<string, Trade> BuildTradeIndex(IEnumerable<Trade> trades) => trades.GroupBy(t => t.MatchKey).ToDictionary(g => g.Key, g => g.First());
}

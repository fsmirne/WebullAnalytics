namespace WebullAnalytics;

/// <summary>
/// Tracks option and stock positions, calculates realized P&L, and builds reports.
/// Uses FIFO (First-In-First-Out) accounting for matching lots.
/// </summary>
public static class PositionTracker
{
	private static readonly TimeSpan ExpirationTime = new(23, 59, 59);

	private static bool IsStrategyParent(Trade trade) => trade.Asset == Asset.OptionStrategy;
	private static bool IsStrategyLeg(Trade trade) => trade.ParentStrategySeq.HasValue;

	/// <summary>
	/// Loads trades from a single CSV file.
	/// </summary>
	public static List<Trade> LoadTradesFromFile(string filePath)
	{
		var (trades, _) = CsvParser.ParseTradeCsv(filePath, 0);
		return trades;
	}

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
			return allTrades.Where(t => t.ParentStrategySeq == trade.Seq).Sum(leg => feeLookup.GetValueOrDefault((leg.Timestamp, leg.Side, leg.Qty), 0m));

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

		// Strategy parent: calculate P&L and track position
		var lots = positions.GetValueOrDefault(trade.MatchKey, new List<Lot>());
		var (updatedLots, realized2, closedQty) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);

		// For SELL strategies with no direct P&L, calculate from legs (closing a spread)
		// For BUY strategies (opening/rolling), don't count leg P&L - cost is in the spread price
		if (realized2 == 0m && closedQty == 0 && trade.Side == Side.Sell)
		{
			realized2 = allTrades
				.Where(t => t.ParentStrategySeq == trade.Seq)
				.Sum(leg =>
				{
					var legLots = positions.GetValueOrDefault(leg.MatchKey, []);
					var (_, legPnl, _) = ApplyToLots(legLots, leg.Side, leg.Qty, leg.Price, leg.Multiplier);
					return legPnl;
				});
		}

		if (updatedLots.Count > 0)
			positions[trade.MatchKey] = updatedLots;
		else
			positions.Remove(trade.MatchKey);

		return (realized2, closedQty);
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
			return new List<Trade>();

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

		var (updatedLots, realized, closedQty) = trade.Side == Side.Expire ? ApplyExpiration(lots, trade.Multiplier) : ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);

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
	private static (List<Lot>, decimal realized, int closedQty) ApplyToLots(List<Lot> lots, Side tradeSide, int tradeQty, decimal tradePrice, decimal multiplier)
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
			updated.Add(new Lot(tradeSide, remaining, tradePrice));

		return (updated, realized, closedQty);
	}

	/// <summary>
	/// Builds position rows for display, grouping options into calendar spreads where applicable.
	/// </summary>
	public static List<PositionRow> BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
	{
		var allPositions = BuildRawPositionRows(positions, tradeIndex);
		var grouped = GroupIntoStrategies(allPositions);
		return BuildFinalPositionRows(grouped, allTrades);
	}

	/// <summary>
	/// Converts raw position data (lots) into position rows with calculated averages.
	/// </summary>
	private static List<(string matchKey, PositionRow row, Trade? trade)> BuildRawPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex)
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
			var avgPrice = lots.Sum(l => l.Price * l.Qty) / totalQty;

			var row = new PositionRow(Instrument: trade?.Instrument ?? matchKey, Asset: trade?.Asset ?? Asset.Stock, OptionKind: !string.IsNullOrEmpty(trade?.OptionKind) ? trade.OptionKind : "-", Side: lots[0].Side, Qty: totalQty, AvgPrice: avgPrice, Expiry: trade?.Expiry, IsStrategyLeg: false);

			result.Add((matchKey, row, trade));
		}

		return result;
	}

	/// <summary>
	/// Groups option positions into calendar spreads by matching long/short legs
	/// with the same underlying, strike, and call/put type but different expirations.
	/// Handles partial rolls by creating separate calendars for different quantities.
	/// </summary>
	private static List<List<(string matchKey, PositionRow row, Trade? trade)>> GroupIntoStrategies(List<(string matchKey, PositionRow row, Trade? trade)> allPositions)
	{
		var grouped = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();
		var processed = new HashSet<string>();

		// Parse and group options by underlying/strike/type
		var optionsByKey = allPositions
			.Where(p => p.trade?.Asset == Asset.Option)
			.Select(p => (pos: p, parsed: MatchKeys.TryGetOptionSymbol(p.matchKey, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
			.Where(x => x.parsed != null)
			.GroupBy(x => $"{x.parsed!.Root}|{x.parsed.Strike}|{x.parsed.CallPut}")
			.ToDictionary(g => g.Key, g => g.ToList());

		foreach (var (_, legs) in optionsByKey)
		{
			var longs = legs.Where(l => l.pos.row.Side == Side.Buy).OrderByDescending(l => l.parsed!.ExpiryDate).ToList();
			var shorts = legs.Where(l => l.pos.row.Side == Side.Sell).OrderBy(l => l.parsed!.ExpiryDate).ToList();
			grouped.AddRange(MatchSpreadLegs(longs, shorts, processed, addLongRemainders: true));
		}

		// Second pass: match remaining unprocessed options as vertical spreads (same root + expiry + callput, different strikes)
		var verticalCandidates = allPositions
			.Where(p => p.trade?.Asset == Asset.Option && !processed.Contains(p.matchKey))
			.Select(p => (pos: p, parsed: MatchKeys.TryGetOptionSymbol(p.matchKey, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
			.Where(x => x.parsed != null)
			.GroupBy(x => $"{x.parsed!.Root}|{x.parsed.ExpiryDate:yyyyMMdd}|{x.parsed.CallPut}")
			.ToDictionary(g => g.Key, g => g.ToList());

		foreach (var (_, legs) in verticalCandidates)
		{
			var longs = legs.Where(l => l.pos.row.Side == Side.Buy).OrderBy(l => l.parsed!.Strike).ToList();
			var shorts = legs.Where(l => l.pos.row.Side == Side.Sell).OrderBy(l => l.parsed!.Strike).ToList();
			grouped.AddRange(MatchSpreadLegs(longs, shorts, processed));
		}

		// Add unprocessed options as standalone
		grouped.AddRange(allPositions
			.Where(p => p.trade?.Asset == Asset.Option && !processed.Contains(p.matchKey))
			.Select(p =>
			{
				processed.Add(p.matchKey);
				return new List<(string, PositionRow, Trade?)> { p };
			}));

		// Add non-option positions
		grouped.AddRange(allPositions
			.Where(p => p.trade?.Asset != Asset.Option)
			.Select(p => new List<(string, PositionRow, Trade?)> { p }));

		// Sort by asset type, then instrument
		grouped.Sort((a, b) =>
		{
			var cmp = a[0].row.Asset.CompareTo(b[0].row.Asset);
			return cmp != 0 ? cmp : string.Compare(a[0].row.Instrument, b[0].row.Instrument, StringComparison.Ordinal);
		});

		return grouped;
	}

	/// <summary>
	/// Matches pre-sorted long and short option legs into spread groups by quantity.
	/// Used for both calendar spreads (sorted by expiry) and vertical spreads (sorted by strike).
	/// When addLongRemainders is true, unmatched long quantities are added as standalone groups.
	/// </summary>
	private static List<List<(string matchKey, PositionRow row, Trade? trade)>> MatchSpreadLegs(List<((string matchKey, PositionRow row, Trade? trade) pos, OptionParsed? parsed)> sortedLongs, List<((string matchKey, PositionRow row, Trade? trade) pos, OptionParsed? parsed)> sortedShorts, HashSet<string> processed, bool addLongRemainders = false)
	{
		var result = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();

		if (!sortedLongs.Any() || !sortedShorts.Any())
			return result;

		var longRemaining = sortedLongs.ToDictionary(l => l.pos.matchKey, l => l.pos.row.Qty);
		var shortRemaining = sortedShorts.ToDictionary(l => l.pos.matchKey, l => l.pos.row.Qty);

		foreach (var shortLeg in sortedShorts)
		{
			var shortQty = shortRemaining[shortLeg.pos.matchKey];
			if (shortQty <= 0 || processed.Contains(shortLeg.pos.matchKey))
				continue;

			foreach (var longLeg in sortedLongs)
			{
				var longQty = longRemaining[longLeg.pos.matchKey];
				if (longQty <= 0)
					continue;

				var matchedQty = Math.Min(longQty, shortQty);

				result.Add(new List<(string, PositionRow, Trade?)>
				{
					(longLeg.pos.matchKey, longLeg.pos.row with { Qty = matchedQty }, longLeg.pos.trade),
					(shortLeg.pos.matchKey, shortLeg.pos.row with { Qty = matchedQty }, shortLeg.pos.trade)
				});

				longRemaining[longLeg.pos.matchKey] -= matchedQty;
				shortRemaining[shortLeg.pos.matchKey] -= matchedQty;
				shortQty -= matchedQty;

				if (shortQty <= 0)
				{
					processed.Add(shortLeg.pos.matchKey);
					break;
				}
			}
		}

		// Mark fully consumed legs as processed
		foreach (var leg in sortedLongs.Where(l => longRemaining[l.pos.matchKey] <= 0))
			processed.Add(leg.pos.matchKey);

		if (addLongRemainders)
		{
			foreach (var longLeg in sortedLongs)
			{
				var remaining = longRemaining[longLeg.pos.matchKey];
				if (remaining > 0 && !processed.Contains(longLeg.pos.matchKey))
				{
					processed.Add(longLeg.pos.matchKey);
					result.Add(new List<(string, PositionRow, Trade?)> { (longLeg.pos.matchKey, longLeg.pos.row with { Qty = remaining }, longLeg.pos.trade) });
				}
			}
		}

		return result;
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
	/// Creates a summary row plus individual leg rows with adjusted prices.
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

		var strategyKind = (distinctExpiries > 1, distinctStrikes > 1) switch
		{
			(true, false) => "Calendar",
			(false, true) => "Vertical",
			(true, true) => "Diagonal",
			_ => "Strategy"
		};

		var qty = group[0].row.Qty;

		// Calculate credits from previously closed short legs (calendars and diagonals roll short legs for premium)
		var closedCredits = strategyKind is "Calendar" or "Diagonal" ? CalculateClosedLegsCredits(allTrades, firstParsed.Root, firstParsed.Strike, firstParsed.CallPut) : 0m;

		// Calculate net prices (long - short)
		var (netInitial, netAdjusted) = CalculateNetPrices(group, closedCredits);

		// Determine side from net price: positive = debit (Buy), negative = credit (Sell)
		var side = netInitial >= 0 ? Side.Buy : Side.Sell;
		var longestExpiry = group.Max(g => g.row.Expiry) ?? DateTime.MinValue;

		// Strategy summary row
		rows.Add(new PositionRow(Instrument: $"{firstParsed.Root} {Formatters.FormatOptionDate(longestExpiry)}", Asset: Asset.OptionStrategy, OptionKind: strategyKind, Side: side, Qty: qty, AvgPrice: Math.Abs(netAdjusted), Expiry: longestExpiry, IsStrategyLeg: false, InitialAvgPrice: Math.Abs(netInitial), AdjustedAvgPrice: Math.Abs(netAdjusted)));

		// Add leg rows with adjusted prices (sorted by expiry descending, then strike descending)
		foreach (var leg in group.OrderByDescending(g => g.row.Expiry).ThenByDescending(g => parsedLegs.FirstOrDefault(p => p.leg.matchKey == g.matchKey).parsed?.Strike ?? 0))
		{
			var initialPrice = leg.row.AvgPrice;
			var adjustedPrice = initialPrice;

			// Reduce long leg cost basis by credits from closed short legs
			if (leg.row.Side == Side.Buy && closedCredits > 0)
				adjustedPrice = AdjustPriceForCredits(initialPrice, closedCredits, leg.row.Qty);

			rows.Add(leg.row with { IsStrategyLeg = true, InitialAvgPrice = initialPrice, AdjustedAvgPrice = adjustedPrice });
		}

		return rows;
	}

	/// <summary>
	/// Calculates net initial and adjusted prices for a calendar spread.
	/// Net price = sum of long prices - sum of short prices.
	/// </summary>
	private static (decimal initial, decimal adjusted) CalculateNetPrices(List<(string matchKey, PositionRow row, Trade? trade)> group, decimal closedCredits)
	{
		var netInitial = 0m;
		var netAdjusted = 0m;

		foreach (var leg in group)
		{
			var initial = leg.row.AvgPrice;
			var adjusted = initial;

			if (leg.row.Side == Side.Buy && closedCredits > 0)
				adjusted = AdjustPriceForCredits(initial, closedCredits, leg.row.Qty);

			if (leg.row.Side == Side.Buy)
			{
				netInitial += initial;
				netAdjusted += adjusted;
			}
			else
			{
				netInitial -= initial;
				netAdjusted -= adjusted;
			}
		}

		return (netInitial, netAdjusted);
	}

	/// <summary>
	/// Adjusts a price by subtracting credits earned per share from rolled short legs.
	/// </summary>
	private static decimal AdjustPriceForCredits(decimal price, decimal credits, decimal qty) => price - credits / (qty * 100m);

	/// <summary>
	/// Calculates total credits from closed short legs for a given underlying/strike/type.
	/// Used to adjust the cost basis of long legs in calendar rolls.
	/// Tracks short-side P&L per cycle (a cycle ends when lots hit zero at an expiry).
	/// Only positive short cycles and partial short closes count as credits.
	/// </summary>
	private static decimal CalculateClosedLegsCredits(List<Trade> allTrades, string root, decimal strike, string callPut)
	{
		// Find all strategy leg trades matching this underlying/strike/type
		var matchingLegs = allTrades
			.Where(t => t.Asset == Asset.Option && t.ParentStrategySeq.HasValue)
			.Select(t => (trade: t, parsed: MatchKeys.TryGetOptionSymbol(t.MatchKey, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
			.Where(x => x.parsed != null && x.parsed.Root == root && x.parsed.Strike == strike && x.parsed.CallPut == callPut)
			.OrderBy(x => x.trade.Timestamp)
			.ToList();

		// Track lots and short-side P&L per cycle per expiration date
		var lotsByExpiry = new Dictionary<DateTime, List<Lot>>();
		var shortPnlByExpiry = new Dictionary<DateTime, decimal>();
		var totalCredits = 0m;

		foreach (var (trade, parsed) in matchingLegs)
		{
			var expiry = parsed!.ExpiryDate;

			if (!lotsByExpiry.ContainsKey(expiry))
			{
				lotsByExpiry[expiry] = new List<Lot>();
				shortPnlByExpiry[expiry] = 0m;
			}

			var lots = lotsByExpiry[expiry];
			var (updatedLots, realized, _) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);

			// Only track realized P&L from Buy trades (closing short positions)
			if (trade.Side == Side.Buy && realized != 0)
				shortPnlByExpiry[expiry] += realized;

			lotsByExpiry[expiry] = updatedLots;

			// Cycle complete - position fully closed at this expiry
			if (!updatedLots.Any())
			{
				var cyclePnl = shortPnlByExpiry[expiry];
				if (cyclePnl > 0)
					totalCredits += cyclePnl;
				shortPnlByExpiry[expiry] = 0m; // Reset for potential next cycle
			}
		}

		// Add credits from partially closed short positions (still have open lots)
		foreach (var (_, pnl) in shortPnlByExpiry)
		{
			if (pnl > 0)
				totalCredits += pnl;
		}

		return totalCredits;
	}

	/// <summary>
	/// Builds an index mapping match keys to their first trade (for metadata lookup).
	/// </summary>
	public static Dictionary<string, Trade> BuildTradeIndex(IEnumerable<Trade> trades) => trades.GroupBy(t => t.MatchKey).ToDictionary(g => g.Key, g => g.First());
}

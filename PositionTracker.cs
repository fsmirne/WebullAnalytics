namespace WebullAnalytics;

/// <summary>
/// Tracks option and stock positions, calculates realized P&L, and builds reports.
/// Uses FIFO (First-In-First-Out) accounting for P&L matching and average cost method for position display.
/// </summary>
public static class PositionTracker
{
	internal static readonly TimeSpan ExpirationTime = new(23, 59, 59);

	private static bool IsStrategyParent(Trade trade) => trade.Asset == Asset.OptionStrategy;
	private static bool IsStrategyLeg(Trade trade) => trade.ParentStrategySeq.HasValue;

	/// <summary>
	/// Computes the realized P&L report by processing all trades chronologically.
	/// Generates synthetic expiration trades for options that have expired.
	/// </summary>
	public static (List<ReportRow> rows, Dictionary<string, List<Lot>> positions, decimal running) ComputeReport(List<Trade> trades, DateTime sinceDate, decimal initialAmount = 0m, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup = null)
	{
		var allTrades = trades.Where(t => t.Timestamp.Date >= sinceDate.Date).Concat(BuildExpirationTrades(trades)).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq).ToList();

		var positions = new Dictionary<string, List<Lot>>();
		var running = 0m;
		var cash = initialAmount;
		var rows = new List<ReportRow>();
		var skippedParentSeqs = new HashSet<int>();

		foreach (var trade in allTrades)
		{
			var (realized, closedQty) = ProcessTrade(trade, positions, allTrades);

			if (IsStrategyParent(trade) && trade.Side is Side.Buy or Side.Sell)
			{
				var legs = Trade.GetLegs(allTrades, trade.Seq);
				if (legs.Count >= 2)
				{
					var parentCash = (trade.Side == Side.Sell ? 1m : -1m) * trade.Qty * trade.Price * trade.Multiplier;
					var legCash = legs.Sum(leg => (leg.Side == Side.Sell ? 1m : -1m) * leg.Qty * leg.Price * leg.Multiplier);
					realized += parentCash - legCash;
				}
			}

			var fee = LookupFee(trade, allTrades, feeLookup);
			var row = BuildReportRow(trade, realized, closedQty, fee, ref running, ref cash, initialAmount);

			if (row == null && IsStrategyParent(trade))
				skippedParentSeqs.Add(trade.Seq);

			if (row != null && trade.ParentStrategySeq.HasValue && skippedParentSeqs.Contains(trade.ParentStrategySeq.Value))
				row = null;

			if (row != null)
				rows.Add(row);
		}

		return (rows, positions, running);
	}

	/// <summary>Looks up the fee for a trade. For strategy parents, sums up fees from all child legs.</summary>
	private static decimal LookupFee(Trade trade, List<Trade> allTrades, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup)
	{
		if (feeLookup == null) return 0m;

		if (trade.Asset == Asset.OptionStrategy)
			return Trade.GetLegs(allTrades, trade.Seq).Select(leg => (leg.Timestamp, leg.Side, leg.Qty)).Distinct().Sum(key => feeLookup.GetValueOrDefault(key, 0m));

		return feeLookup.GetValueOrDefault((trade.Timestamp, trade.Side, trade.Qty), 0m);
	}

	/// <summary>Processes a single trade, updating positions in-place.</summary>
	private static (decimal realized, int closedQty) ProcessTrade(Trade trade, Dictionary<string, List<Lot>> positions, List<Trade> allTrades)
	{
		if (!IsStrategyParent(trade))
			return ApplyTrade(positions, trade);

		// Strategy parent expiration: expire each leg and sum P&L
		if (trade.Side == Side.Expire)
		{
			var legs = Trade.GetLegs(allTrades, trade.Seq);

			if (legs.Count >= 2)
			{
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

				positions.Remove(trade.MatchKey);
				return (realized, maxLegClosed);
			}

			positions.Remove(trade.MatchKey);
			return (0m, 0);
		}

		// Strategy parent buy/sell: compute P&L from leg-level FIFO matching
		var pnl = 0m;
		var closedQty = 0;
		foreach (var leg in Trade.GetLegs(allTrades, trade.Seq))
		{
			var legLots = positions.GetValueOrDefault(leg.MatchKey, []);
			var (_, legPnl, legClosed) = ApplyToLots(legLots, leg.Side, leg.Qty, leg.Price, leg.Multiplier);
			pnl += legPnl;
			closedQty = Math.Max(closedQty, legClosed);
		}
		return (pnl, closedQty);
	}

	/// <summary>Builds a report row for the given trade. Returns null if the row should be skipped.</summary>
	private static ReportRow? BuildReportRow(Trade trade, decimal realized, int closedQty, decimal fee, ref decimal running, ref decimal cash, decimal initialAmount)
	{
		var optionKind = string.IsNullOrEmpty(trade.OptionKind) ? "-" : trade.OptionKind;
		var isLeg = IsStrategyLeg(trade);

		if (isLeg)
			return new ReportRow(trade.Timestamp, trade.Instrument, trade.Asset, optionKind, trade.Side, trade.Qty, trade.Price, ClosedQty: 0m, Realized: 0m, running, Cash: cash, Total: initialAmount + running, IsStrategyLeg: true, Fees: fee);

		if (trade.Side == Side.Expire && closedQty == 0)
			return null;

		if (trade.Side == Side.Buy)
			cash -= trade.Qty * trade.Price * trade.Multiplier;
		else if (trade.Side == Side.Sell)
			cash += trade.Qty * trade.Price * trade.Multiplier;

		realized -= fee;
		cash -= fee;
		running += realized;
		var displayQty = trade.Side == Side.Expire ? closedQty : trade.Qty;

		return new ReportRow(trade.Timestamp, trade.Instrument, trade.Asset, optionKind, trade.Side, displayQty, trade.Price, closedQty, realized, running, Cash: cash, Total: initialAmount + running, IsStrategyLeg: false, Fees: fee);
	}

	/// <summary>Creates synthetic expiration trades for all options that have expired.</summary>
	private static List<Trade> BuildExpirationTrades(List<Trade> trades)
	{
		if (!trades.Any())
			return [];

		var maxSeq = trades.Max(t => t.Seq);
		var today = DateTime.Today;
		var seq = maxSeq + 1;

		var uniqueExpired = trades.Where(t => t.Expiry.HasValue && t.Expiry.Value.Date < today).GroupBy(t => t.MatchKey).Select(g => g.First()).ToList();

		// Build mapping: leg match key → parent match key
		var legToParentKey = new Dictionary<string, string>();
		foreach (var trade in trades.Where(t => t.ParentStrategySeq.HasValue && t.Asset == Asset.Option))
		{
			var parent = trades.FirstOrDefault(t => t.Seq == trade.ParentStrategySeq!.Value);
			if (parent != null)
				legToParentKey.TryAdd(trade.MatchKey, parent.MatchKey);
		}

		var result = new List<Trade>();

		// First pass: strategy parent expirations
		var parentInfo = new Dictionary<string, (int seq, DateTime? expiry)>();
		foreach (var trade in uniqueExpired.Where(t => t.Asset == Asset.OptionStrategy))
		{
			var expSeq = seq++;
			parentInfo[trade.MatchKey] = (expSeq, trade.Expiry);
			result.Add(new Trade(Seq: expSeq, Timestamp: trade.Expiry!.Value.Date + ExpirationTime, trade.Instrument, trade.MatchKey, trade.Asset, trade.OptionKind, Side: Side.Expire, Qty: 0, Price: 0m, trade.Multiplier, trade.Expiry));
		}

		// Second pass: option expirations
		foreach (var trade in uniqueExpired.Where(t => t.Asset == Asset.Option))
		{
			int? parentStrategySeq = null;
			if (legToParentKey.TryGetValue(trade.MatchKey, out var parentMatchKey) && parentInfo.TryGetValue(parentMatchKey, out var info) && trade.Expiry?.Date == info.expiry?.Date)
				parentStrategySeq = info.seq;

			result.Add(new Trade(Seq: seq++, Timestamp: trade.Expiry!.Value.Date + ExpirationTime, trade.Instrument, trade.MatchKey, trade.Asset, trade.OptionKind, Side: Side.Expire, Qty: 0, Price: 0m, trade.Multiplier, trade.Expiry, ParentStrategySeq: parentStrategySeq));
		}

		// Unlink parents with fewer than 2 linked legs
		var legsPerParent = result.Where(t => t.ParentStrategySeq.HasValue).GroupBy(t => t.ParentStrategySeq!.Value).ToDictionary(g => g.Key, g => g.Count());
		result = result.Select(t =>
		{
			if (t.ParentStrategySeq.HasValue && legsPerParent.GetValueOrDefault(t.ParentStrategySeq.Value) < 2)
				return t with { ParentStrategySeq = null };
			return t;
		}).ToList();

		return result;
	}

	/// <summary>Applies a trade to the current positions using FIFO accounting.</summary>
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

	/// <summary>Closes all lots at expiration (price = $0).</summary>
	private static (List<Lot>, decimal realized, int closedQty) ApplyExpiration(List<Lot> lots, decimal multiplier)
	{
		if (lots.Count == 0)
			return (new List<Lot>(), 0m, 0);

		var realized = lots.Sum(lot => lot.Side == Side.Buy ? -lot.Price * lot.Qty * multiplier : lot.Price * lot.Qty * multiplier);
		var closedQty = lots.Sum(lot => lot.Qty);
		return (new List<Lot>(), realized, closedQty);
	}

	/// <summary>Applies a buy/sell trade to existing lots using FIFO matching.</summary>
	private static (List<Lot>, decimal realized, int closedQty) ApplyToLots(List<Lot> lots, Side tradeSide, int tradeQty, decimal tradePrice, decimal multiplier, int? parentStrategySeq = null)
	{
		var remaining = tradeQty;
		var realized = 0m;
		var closedQty = 0;
		var updated = new List<Lot>();

		foreach (var lot in lots)
		{
			if (remaining > 0 && lot.Side != tradeSide)
			{
				var matchQty = Math.Min(remaining, lot.Qty);
				var pnlPerContract = tradeSide == Side.Buy ? lot.Price - tradePrice : tradePrice - lot.Price;
				realized += pnlPerContract * matchQty * multiplier;
				closedQty += matchQty;
				remaining -= matchQty;

				var leftover = lot.Qty - matchQty;
				if (leftover > 0)
					updated.Add(lot with { Qty = leftover });
			}
			else
			{
				updated.Add(lot);
			}
		}

		if (remaining > 0)
			updated.Add(new Lot(tradeSide, remaining, tradePrice, parentStrategySeq));

		return (updated, realized, closedQty);
	}

	/// <summary>Builds position rows for display, grouping options into strategies.</summary>
	public static List<PositionRow> BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
	{
		var avgCosts = ComputeAverageCosts(allTrades);
		var allPositions = BuildRawPositionRows(positions, tradeIndex, avgCosts);
		var grouped = StrategyGrouper.GroupIntoStrategies(allPositions, positions);
		return StrategyGrouper.BuildFinalPositionRows(grouped, allTrades, positions);
	}

	/// <summary>Computes average cost per position using the average cost method.</summary>
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

			state[key] = (newQty, avg);
		}

		return state.Where(kvp => kvp.Value.qty != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.avg);
	}

	/// <summary>Converts raw position data (lots) into position rows with calculated averages.</summary>
	private static List<(string matchKey, PositionRow row, Trade? trade)> BuildRawPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, Dictionary<string, decimal> avgCosts)
	{
		var result = new List<(string matchKey, PositionRow row, Trade? trade)>();

		foreach (var (matchKey, lots) in positions)
		{
			if (!lots.Any() || lots.Sum(l => l.Qty) <= 0)
				continue;

			var trade = tradeIndex.GetValueOrDefault(matchKey);

			if (trade?.Asset == Asset.OptionStrategy)
				continue;

			var totalQty = lots.Sum(l => l.Qty);
			var avgPrice = avgCosts.GetValueOrDefault(matchKey, lots.Sum(l => l.Price * l.Qty) / totalQty);

			var row = new PositionRow(Instrument: trade?.Instrument ?? matchKey, Asset: trade?.Asset ?? Asset.Stock, OptionKind: !string.IsNullOrEmpty(trade?.OptionKind) ? trade.OptionKind : "-", Side: lots[0].Side, Qty: totalQty, AvgPrice: avgPrice, Expiry: trade?.Expiry, IsStrategyLeg: false, MatchKey: matchKey);

			result.Add((matchKey, row, trade));
		}

		return result;
	}

	/// <summary>Builds an index mapping match keys to their first trade.</summary>
	public static Dictionary<string, Trade> BuildTradeIndex(IEnumerable<Trade> trades) => trades.GroupBy(t => t.MatchKey).ToDictionary(g => g.Key, g => g.First());
}

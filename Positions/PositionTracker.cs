using WebullAnalytics.Report;

namespace WebullAnalytics.Positions;

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
	///
	/// <paramref name="underlyingCloseLookup"/> resolves the underlying's settle/close price for a
	/// (ticker, expiryDate) pair so ITM expirations can be settled at intrinsic value. When null,
	/// or when the lookup returns null, all expirations are treated as worthless (the legacy
	/// behavior, which over-credits realized P&L and under-debits cash for ITM expirations).
	/// </summary>
	public static (List<ReportRow> rows, Dictionary<string, List<Lot>> positions, decimal running) ComputeReport(List<Trade> trades, decimal initialAmount = 0m, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup = null, Func<string, DateTime, decimal?>? underlyingCloseLookup = null)
	{
		var allTrades = trades.Concat(BuildExpirationTrades(trades)).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq).ToList();

		var positions = new Dictionary<string, List<Lot>>();
		var running = 0m;
		var cash = initialAmount;
		var rows = new List<ReportRow>();
		var skippedParentSeqs = new HashSet<int>();

		foreach (var trade in allTrades)
		{
			var (realized, cashFlow, closedQty) = ProcessTrade(trade, positions, allTrades, underlyingCloseLookup);

			if (IsStrategyParent(trade) && trade.Side is Side.Buy or Side.Sell)
			{
				var legs = Trade.GetLegs(allTrades, trade.Seq);
				if (legs.Count >= 2)
				{
					var parentCash = (trade.Side == Side.Sell ? 1m : -1m) * Math.Round(trade.Qty * trade.Price, 2) * trade.Multiplier;
					var legCash = legs.Sum(leg => (leg.Side == Side.Sell ? 1m : -1m) * Math.Round(leg.Qty * leg.Price, 2) * leg.Multiplier);
					realized += parentCash - legCash;
				}
			}

			var fee = LookupFee(trade, allTrades, feeLookup);
			var row = BuildReportRow(trade, realized, cashFlow, closedQty, fee, ref running, ref cash, initialAmount);

			if (row == null && IsStrategyParent(trade))
				skippedParentSeqs.Add(trade.Seq);

			if (row != null && trade.ParentStrategySeq.HasValue && skippedParentSeqs.Contains(trade.ParentStrategySeq.Value))
				row = null;

			if (row != null)
				rows.Add(row);
		}

		return (rows, positions, running);
	}

	/// <summary>Intrinsic value per share at expiry: max(0, S-K) for calls, max(0, K-S) for puts.
	/// Returns 0 if the option metadata can't be parsed or the lookup returns no close.</summary>
	private static decimal ComputeIntrinsicPerShare(Trade trade, Func<string, DateTime, decimal?>? underlyingCloseLookup)
	{
		if (underlyingCloseLookup == null || trade.Expiry == null || trade.Asset != Asset.Option) return 0m;
		var parsed = MatchKeys.ParseOption(trade.MatchKey);
		if (parsed == null) return 0m;
		var close = underlyingCloseLookup(parsed.Value.parsed.Root, trade.Expiry.Value.Date);
		if (close == null) return 0m;
		return parsed.Value.parsed.CallPut == "C"
			? Math.Max(0m, close.Value - parsed.Value.parsed.Strike)
			: Math.Max(0m, parsed.Value.parsed.Strike - close.Value);
	}

	/// <summary>Looks up the fee for a trade. For strategy parents, sums up fees from all child legs.</summary>
	private static decimal LookupFee(Trade trade, List<Trade> allTrades, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup)
	{
		if (feeLookup == null) return 0m;

		if (trade.Asset == Asset.OptionStrategy)
			return Trade.GetLegs(allTrades, trade.Seq).Select(leg => (leg.Timestamp, leg.Side, leg.Qty)).Distinct().Sum(key => feeLookup.GetValueOrDefault(key, 0m));

		return feeLookup.GetValueOrDefault((trade.Timestamp, trade.Side, trade.Qty), 0m);
	}

	/// <summary>
	/// Processes a single trade. Non-parent trades mutate positions via FIFO. Strategy-parent trades
	/// fold leg-level P&L; for expiry, they also eagerly consume leg positions (so the legs' own
	/// expiry trades become no-ops); for buy/sell, leg trades (processed separately) do the mutation.
	/// </summary>
	private static (decimal realized, decimal cashFlow, int closedQty) ProcessTrade(Trade trade, Dictionary<string, List<Lot>> positions, List<Trade> allTrades, Func<string, DateTime, decimal?>? underlyingCloseLookup)
	{
		if (!IsStrategyParent(trade))
			return ApplyTrade(positions, trade, underlyingCloseLookup);

		var legs = Trade.GetLegs(allTrades, trade.Seq);
		var isExpiry = trade.Side == Side.Expire;

		if (isExpiry && legs.Count < 2)
		{
			positions.Remove(trade.MatchKey);
			return (0m, 0m, 0);
		}

		var realized = 0m;
		var cashFlow = 0m;
		var maxClosed = 0;
		foreach (var leg in legs)
		{
			var legLots = positions.GetValueOrDefault(leg.MatchKey, []);
			decimal legRealized;
			decimal legCashFlow;
			int legClosed;
			if (isExpiry)
			{
				var intrinsic = ComputeIntrinsicPerShare(leg, underlyingCloseLookup);
				(_, legRealized, legCashFlow, legClosed) = ApplyExpiration(legLots, leg.Multiplier, intrinsic);
			}
			else
			{
				(_, legRealized, legClosed) = ApplyToLots(legLots, leg.Side, leg.Qty, leg.Price, leg.Multiplier);
				legCashFlow = 0m;
			}
			realized += legRealized;
			cashFlow += legCashFlow;
			maxClosed = Math.Max(maxClosed, legClosed);
			if (isExpiry) positions.Remove(leg.MatchKey);
		}
		if (isExpiry) positions.Remove(trade.MatchKey);
		return (realized, cashFlow, maxClosed);
	}

	/// <summary>Builds a report row for the given trade. Returns null if the row should be skipped.</summary>
	private static ReportRow? BuildReportRow(Trade trade, decimal realized, decimal cashFlow, int closedQty, decimal fee, ref decimal running, ref decimal cash, decimal initialAmount)
	{
		var optionKind = string.IsNullOrEmpty(trade.OptionKind) ? "-" : trade.OptionKind;
		var isLeg = IsStrategyLeg(trade);

		if (isLeg)
			return new ReportRow(trade.Timestamp, trade.Instrument, trade.Asset, optionKind, trade.Side, trade.Qty, trade.Price, ClosedQty: 0m, Realized: 0m, running, Cash: cash, Total: initialAmount + running, IsStrategyLeg: true, Fees: fee);

		if (trade.Side == Side.Expire && closedQty == 0)
			return null;

		var tradeValue = Math.Round(trade.Qty * trade.Price, 2) * trade.Multiplier;
		if (trade.Side == Side.Buy)
			cash -= tradeValue;
		else if (trade.Side == Side.Sell)
			cash += tradeValue;
		else if (trade.Side == Side.Expire)
			cash += cashFlow;

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
		var today = EvaluationDate.Today;
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
	private static (decimal realized, decimal cashFlow, int closedQty) ApplyTrade(Dictionary<string, List<Lot>> positions, Trade trade, Func<string, DateTime, decimal?>? underlyingCloseLookup)
	{
		var lots = positions.GetValueOrDefault(trade.MatchKey, []);

		List<Lot> updatedLots;
		decimal realized;
		decimal cashFlow;
		int closedQty;
		if (trade.Side == Side.Expire)
		{
			var intrinsic = ComputeIntrinsicPerShare(trade, underlyingCloseLookup);
			(updatedLots, realized, cashFlow, closedQty) = ApplyExpiration(lots, trade.Multiplier, intrinsic);
		}
		else
		{
			(updatedLots, realized, closedQty) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier, trade.ParentStrategySeq);
			cashFlow = 0m;
		}

		if (updatedLots.Count > 0)
			positions[trade.MatchKey] = updatedLots;
		else
			positions.Remove(trade.MatchKey);

		return (realized, cashFlow, closedQty);
	}

	/// <summary>Closes all lots at expiration. ITM legs settle at intrinsic value (cash settled):
	/// long lots receive intrinsic, short lots pay it. OTM legs (intrinsic = 0) settle worthless.</summary>
	private static (List<Lot>, decimal realized, decimal cashFlow, int closedQty) ApplyExpiration(List<Lot> lots, decimal multiplier, decimal intrinsicPerShare)
	{
		if (lots.Count == 0)
			return (new List<Lot>(), 0m, 0m, 0);

		var realized = 0m;
		var cashFlow = 0m;
		var closedQty = 0;
		foreach (var lot in lots)
		{
			var notional = intrinsicPerShare * lot.Qty * multiplier;
			if (lot.Side == Side.Buy)
			{
				realized += (intrinsicPerShare - lot.Price) * lot.Qty * multiplier;
				cashFlow += notional;
			}
			else
			{
				realized += (lot.Price - intrinsicPerShare) * lot.Qty * multiplier;
				cashFlow -= notional;
			}
			closedQty += lot.Qty;
		}
		return (new List<Lot>(), realized, cashFlow, closedQty);
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

	/// <summary>Builds position rows for display, using the PositionReplay linear-cash-flow state machine.</summary>
	public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones) BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
	{
		return PositionReplay.Execute(positions, tradeIndex, allTrades);
	}

	/// <summary>
	/// Advances running (qty, avg) state for one trade. Handles opening (add), closing (FIFO-equivalent
	/// average method), and position flips (new avg = trade price).
	/// </summary>
	internal static (int qty, decimal avg) StepAverageCost((int qty, decimal avg) state, Side side, int tradeQty, decimal tradePrice)
	{
		var (qty, avg) = state;
		var tradeSign = side == Side.Buy ? 1 : -1;
		var newQty = qty + tradeSign * tradeQty;
		var isOpening = qty == 0 || (qty > 0 && side == Side.Buy) || (qty < 0 && side == Side.Sell);

		if (isOpening)
			avg = (Math.Abs(qty) * avg + tradeQty * tradePrice) / Math.Abs(newQty);
		else if (newQty == 0)
			avg = 0m;
		else if ((qty > 0 && newQty < 0) || (qty < 0 && newQty > 0))
			avg = tradePrice;

		return (newQty, avg);
	}

	/// <summary>Builds an index mapping match keys to their first trade.</summary>
	public static Dictionary<string, Trade> BuildTradeIndex(IEnumerable<Trade> trades) => trades.GroupBy(t => t.MatchKey).ToDictionary(g => g.Key, g => g.First());
}

using WebullAnalytics.AI;

namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Rebuilds OpenPosition snapshots from orders.jsonl at any historical timestamp by delegating
/// to PositionTracker.ComputeReport and BuildPositionRows, then grouping the resulting
/// strategy-parent + leg rows into OpenPosition records.
/// </summary>
internal sealed class ReplayPositionSource : IPositionSource
{
	private readonly List<Trade> _allTrades;
	private readonly Dictionary<(DateTime timestamp, Side side, int qty), decimal>? _feeLookup;

	public ReplayPositionSource(List<Trade> allTrades, Dictionary<(DateTime timestamp, Side side, int qty), decimal>? feeLookup)
	{
		_allTrades = allTrades;
		_feeLookup = feeLookup;
	}

	public Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(
		DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		var slice = _allTrades.Where(t => t.Timestamp <= asOf).ToList();
		if (slice.Count == 0) return Task.FromResult<IReadOnlyDictionary<string, OpenPosition>>(new Dictionary<string, OpenPosition>());

		var (_, positionsLots, _) = PositionTracker.ComputeReport(slice, initialAmount: 0m, feeLookup: _feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(slice);
		var (rows, _, _) = PositionTracker.BuildPositionRows(positionsLots, tradeIndex, slice);

		var result = new Dictionary<string, OpenPosition>();
		var parentCounter = 0;

		// Walk rows: parent (IsStrategyLeg=false, Asset=OptionStrategy) starts a group;
		// subsequent IsStrategyLeg=true rows belong to it until the next parent row.
		PositionRow? currentParent = null;
		var currentLegs = new List<PositionRow>();

		void Flush()
		{
			if (currentParent == null) { currentLegs.Clear(); return; }
			if (currentLegs.Count == 0) { currentParent = null; return; }

			// Ticker derivation: parse first leg's MatchKey.
			string? ticker = null;
			foreach (var leg in currentLegs)
			{
				if (leg.MatchKey == null) continue;
				var occ = leg.MatchKey.StartsWith("option:") ? leg.MatchKey[7..] : leg.MatchKey;
				var parsed = ParsingHelpers.ParseOptionSymbol(occ);
				if (parsed != null) { ticker = parsed.Root; break; }
			}
			if (ticker == null || !tickers.Contains(ticker))
			{
				currentParent = null; currentLegs.Clear(); return;
			}

			// Build PositionLeg[].
			var legObjs = new List<PositionLeg>();
			foreach (var leg in currentLegs)
			{
				if (leg.MatchKey == null) continue;
				var occ = leg.MatchKey.StartsWith("option:") ? leg.MatchKey[7..] : leg.MatchKey;
				var parsed = ParsingHelpers.ParseOptionSymbol(occ);
				if (parsed == null) continue;
				legObjs.Add(new PositionLeg(
					Symbol: occ,
					Side: leg.Side,
					Strike: parsed.Strike,
					Expiry: parsed.ExpiryDate,
					CallPut: parsed.CallPut,
					Qty: leg.Qty
				));
			}
			if (legObjs.Count == 0) { currentParent = null; currentLegs.Clear(); return; }

			// Key: prefer short-leg identity.
			var shortLeg = legObjs.FirstOrDefault(l => l.Side == Side.Sell);
			var key = shortLeg != null && shortLeg.Expiry.HasValue
				? $"{ticker}_{currentParent.OptionKind}_{shortLeg.Strike:F2}_{shortLeg.Expiry.Value:yyyyMMdd}"
				: $"{ticker}_{currentParent.OptionKind}_{parentCounter++}";

			var initialDebit = currentParent.InitialAvgPrice ?? currentParent.AvgPrice;
			var adjustedDebit = currentParent.AdjustedAvgPrice ?? currentParent.AvgPrice;

			result[key] = new OpenPosition(
				Key: key,
				Ticker: ticker,
				StrategyKind: currentParent.OptionKind,
				Legs: legObjs,
				InitialNetDebit: initialDebit,
				AdjustedNetDebit: adjustedDebit,
				Quantity: currentParent.Qty
			);

			currentParent = null;
			currentLegs.Clear();
		}

		foreach (var row in rows)
		{
			if (!row.IsStrategyLeg)
			{
				Flush();
				if (row.Asset == Asset.OptionStrategy)
					currentParent = row;
				// Single-leg positions not in strategies are skipped for phase-1 replay
				// (rules target calendars/diagonals primarily).
			}
			else
			{
				if (currentParent != null) currentLegs.Add(row);
			}
		}
		Flush();

		return Task.FromResult<IReadOnlyDictionary<string, OpenPosition>>(result);
	}

	public Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation)
	{
		var slice = _allTrades.Where(t => t.Timestamp <= asOf).ToList();
		if (slice.Count == 0) return Task.FromResult((0m, 0m));
		var (_, _, running) = PositionTracker.ComputeReport(slice, initialAmount: 0m, feeLookup: _feeLookup);
		// Phase-1 approximation: cash = running total; accountValue = running total.
		return Task.FromResult((running, running));
	}
}

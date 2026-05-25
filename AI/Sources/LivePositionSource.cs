using System.Globalization;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Trading;

namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Live position source backed by the Webull OpenAPI.
///
/// The broker response already groups multi-leg option strategies: each holding with a non-null
/// option_strategy value (e.g., "CALENDAR", "DIAGONAL", "VERTICAL") has a legs[] array, and the
/// parent record carries the aggregate quantity and net debit (cost_price) per contract.
///
/// For CALENDAR/DIAGONAL call-side positions — the structures the rules target — we map:
///   - Quantity ← parent.quantity
///   - Short leg ← the leg with the earliest expiry
///   - Long leg  ← the leg with the later expiry
///
/// Cost-basis enrichment: the broker's cost_price is the current open-leg basis; it does NOT
/// reflect roll history (e.g., a long-leg roll for a credit reduces the user's break-even but the
/// broker reports the new leg's standalone basis). To give rule evaluators the same adjusted basis
/// that `wa report` shows, we run PositionReplay over local trade history and look up the matching
/// strategy by sorted leg-set, taking InitialAvgPrice / AdjustedAvgPrice from the replay row.
/// When no replay match is found (positions opened outside the tracked trade history), we fall
/// back to the broker's CostPrice.
///
/// Positions whose option_strategy is null (single-leg) or whose strategy is not in the supported
/// set (currently CALENDAR and DIAGONAL) are skipped.
/// </summary>
internal sealed class LivePositionSource : IPositionSource
{
	// Webull's option_strategy values we know how to interpret. Anything else is silently skipped.
	private static readonly HashSet<string> SupportedStrategies =
		new(StringComparer.OrdinalIgnoreCase) { "SINGLE", "VERTICAL", "CALENDAR", "DIAGONAL" };

	private readonly TradeAccount _account;
	private readonly IReadOnlyList<Trade>? _trades;
	private readonly Dictionary<(DateTime, Side, int), decimal>? _feeLookup;

	public LivePositionSource(TradeAccount account, IReadOnlyList<Trade>? trades = null, Dictionary<(DateTime, Side, int), decimal>? feeLookup = null)
	{
		_account = account;
		_trades = trades;
		_feeLookup = feeLookup;
	}

	public async Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(
		DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		using var client = new WebullOpenApiClient(_account);

		List<WebullOpenApiClient.AccountHolding> holdings;
		try
		{
			holdings = await client.FetchAccountPositionsAsync(cancellation);
		}
		catch (WebullOpenApiException ex) when (ex.HttpStatus == 404)
		{
			Console.Error.WriteLine($"Error: /openapi/assets/positions returned 404 on {_account.BaseUrl}. Is this account configured correctly?");
			return new Dictionary<string, OpenPosition>();
		}

		// Pull today's orders so we can stamp OpenedAt on positions whose fill landed today. We
		// don't fail the position-source if this call errors — the position's still tradable, just
		// OpenedAt stays null and any rule that gates on it falls back to its missing-OpenedAt path.
		List<WebullOpenApiClient.OpenOrder> todaysOrders = new();
		try { todaysOrders = await client.ListTodayOrdersAsync(cancellation); }
		catch { /* OpenedAt enrichment is best-effort */ }

		var costBasisLookup = BuildCostBasisLookup(_trades, _feeLookup);

		var result = new Dictionary<string, OpenPosition>();
		foreach (var h in holdings)
		{
			if (string.IsNullOrEmpty(h.Symbol)) continue;
			if (!tickers.Contains(h.Symbol)) continue;
			if (string.IsNullOrEmpty(h.OptionStrategy) || !SupportedStrategies.Contains(h.OptionStrategy)) continue;
			if (h.Legs == null || h.Legs.Count < 1) continue;

			if (!int.TryParse(h.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0) continue;
			if (!decimal.TryParse(h.CostPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var brokerNetDebit)) brokerNetDebit = 0m;

			var parsedLegs = new List<ParsedLeg>();
			foreach (var leg in h.Legs)
			{
				var p = ParseLeg(leg);
				if (p == null) { parsedLegs.Clear(); break; }
				parsedLegs.Add(p);
			}
			if (parsedLegs.Count == 0) continue;

			// Build PositionLeg list. For multi-leg positions we infer side from the cost — Webull
			// doesn't expose per-leg side directly on the holdings response, but for SINGLE (always
			// long the leg the user paid for) and for the long-side verticals/calendars/diagonals
			// the AI rules care about, side can be inferred from leg ordering (earlier-expiry/
			// closer-to-spot is typically the short). For multi-leg single-expiry verticals we use
			// strike ordering; for multi-expiry we use expiry ordering. This matches the prior
			// short-vs-long inference used by the calendar/diagonal path.
			var positionLegs = BuildPositionLegs(h.OptionStrategy!, parsedLegs, qty);
			if (positionLegs == null) continue;

			var strategyKind = MapWebullStrategyToAiKind(h.OptionStrategy!, positionLegs);

			var legSetKey = BuildLegSetKey(positionLegs.Select(l => l.Symbol));
			var (initialDebit, adjustedDebit) = costBasisLookup.TryGetValue(legSetKey, out var basis)
				? basis
				: (brokerNetDebit, brokerNetDebit);

			// Position key needs to be stable even for single-leg (no "short leg" in that case).
			// For multi-leg, use the short leg's strike+expiry (existing convention); for single-leg,
			// use the single leg's strike+expiry.
			var keyLeg = positionLegs.FirstOrDefault(l => l.Side == Side.Sell) ?? positionLegs[0];
			var key = $"{h.Symbol}_{strategyKind}_{keyLeg.Strike:F2}_{keyLeg.Expiry:yyyyMMdd}";

			// OpenedAt: try to find a filled order in today's-orders that matches this position's leg
			// set. If found, the position was opened (or transitioned into its current shape) today.
			DateTime? openedAt = FindOpenedAtFromTodaysOrders(positionLegs, todaysOrders);

			result[key] = new OpenPosition(
				Key: key,
				Ticker: h.Symbol,
				StrategyKind: strategyKind,
				Legs: positionLegs,
				InitialNetDebit: initialDebit,
				AdjustedNetDebit: adjustedDebit,
				Quantity: qty,
				OpenedAt: openedAt,
				MaxLossPerShare: PositionRiskEstimator.MaxLossPerShare(initialDebit, positionLegs)
			);
		}

		return result;
	}

	/// <summary>Builds the per-leg list from Webull's parsed legs. Webull's holdings response doesn't
	/// expose per-leg side, so for multi-leg structures we infer it. For SINGLE the leg is always
	/// long. For multi-leg structures we apply the existing convention: earlier-expiry leg is the
	/// short (calendars/diagonals); same-expiry vertical → the leg closer to spot is the short.
	/// Returns null if the structure can't be classified.</summary>
	private static List<PositionLeg>? BuildPositionLegs(string optionStrategy, List<ParsedLeg> parsed, int qty)
	{
		if (parsed.Count == 1)
		{
			var l = parsed[0];
			return new List<PositionLeg> { new(l.OccSymbol, Side.Buy, l.Strike, l.Expiry, l.CallPut, qty) };
		}
		if (parsed.Count == 2)
		{
			parsed.Sort((a, b) => a.Expiry.CompareTo(b.Expiry));
			var first = parsed[0];
			var second = parsed[1];
			bool sameExpiry = first.Expiry == second.Expiry;
			if (sameExpiry)
			{
				// Vertical: short leg is the one closer to ATM (lower strike for puts, higher strike for calls).
				// Webull's "VERTICAL" doesn't say whether it's debit or credit; we infer from strike order.
				// For a LongCall debit spread: long the lower strike, short the higher strike. So lower strike = long.
				// For a ShortCall credit spread: short the lower strike, long the higher strike. So lower strike = short.
				// Without knowing debit vs credit, default to debit (long lower / short higher) — matches our
				// bot's primary use case (LegInShortRule converts LongCall to LongCallVertical).
				var lower = first.Strike < second.Strike ? first : second;
				var higher = first.Strike < second.Strike ? second : first;
				return new List<PositionLeg>
				{
					new(lower.OccSymbol, Side.Buy, lower.Strike, lower.Expiry, lower.CallPut, qty),
					new(higher.OccSymbol, Side.Sell, higher.Strike, higher.Expiry, higher.CallPut, qty),
				};
			}
			// Calendar / diagonal: shorter expiry is the short (existing convention).
			return new List<PositionLeg>
			{
				new(first.OccSymbol, Side.Sell, first.Strike, first.Expiry, first.CallPut, qty),
				new(second.OccSymbol, Side.Buy, second.Strike, second.Expiry, second.CallPut, qty),
			};
		}
		// 3+ legs: not yet supported in live mode. The bot doesn't open these structures, so this is
		// only relevant for manually-placed exotic positions which we can ignore.
		return null;
	}

	/// <summary>Maps Webull's option_strategy + inferred leg sides to an OpenStructureKind name
	/// string the AI rules recognize.</summary>
	private static string MapWebullStrategyToAiKind(string webullStrategy, IReadOnlyList<PositionLeg> legs)
	{
		switch (webullStrategy.ToUpperInvariant())
		{
			case "SINGLE":
				return legs[0].CallPut == "C" ? "LongCall" : "LongPut";
			case "VERTICAL":
				// 2 legs same expiry; we set lower=long, higher=short by convention above (debit spread).
				// If both legs are puts → LongPutVertical (or ShortPutVertical for credit); if both calls →
				// LongCallVertical. Heuristic for debit vs credit: positive net debit → long vertical.
				// Without that signal here we default to the long variant; rules that distinguish credit
				// vs debit can compute from per-leg quotes.
				return legs[0].CallPut == "C" ? "LongCallVertical" : "LongPutVertical";
			case "CALENDAR": return "LongCalendar";
			case "DIAGONAL": return "LongDiagonal";
			default: return webullStrategy; // pass through for any not-yet-mapped strategy
		}
	}

	/// <summary>Finds the place_time of a filled order whose leg set matches the position's. Returns
	/// null if no match. Used to stamp OpenedAt for positions that landed today.</summary>
	private static DateTime? FindOpenedAtFromTodaysOrders(IReadOnlyList<PositionLeg> positionLegs, List<WebullOpenApiClient.OpenOrder> todaysOrders)
	{
		if (todaysOrders.Count == 0) return null;
		var posFp = FingerprintLegs(positionLegs);
		foreach (var combo in todaysOrders)
		{
			if (combo.Orders == null) continue;
			foreach (var order in combo.Orders)
			{
				if (!string.Equals(order.Status, "FILLED", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(order.Status, "PARTIALLY_FILLED", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(order.Status, "PARTIAL_FILLED", StringComparison.OrdinalIgnoreCase))
					continue;
				if (FingerprintWebullLegs(order.Legs) != posFp) continue;
				if (!string.IsNullOrEmpty(order.FilledTimeAt) && DateTime.TryParse(order.FilledTimeAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ft))
					return ft;
				if (!string.IsNullOrEmpty(order.PlaceTimeAt) && DateTime.TryParse(order.PlaceTimeAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pt))
					return pt;
			}
		}
		return null;
	}

	/// <summary>Canonical leg-set fingerprint for matching positions to filled orders. Mirrors the
	/// shape used in <see cref="BrokerStateService"/> (Side:Root:Expiry:Strike:CP).</summary>
	private static string FingerprintLegs(IReadOnlyList<PositionLeg> legs) =>
		string.Join("|", legs
			.Select(l => $"{(l.Side == Side.Buy ? "BUY" : "SELL")}:{ExtractRoot(l.Symbol)}:{l.Expiry:yyyy-MM-dd}:{l.Strike.ToString("F2", CultureInfo.InvariantCulture)}:{l.CallPut}")
			.OrderBy(s => s, StringComparer.Ordinal));

	private static string FingerprintWebullLegs(IEnumerable<WebullOpenApiClient.OrderDetailLeg>? legs)
	{
		if (legs == null) return "";
		return string.Join("|", legs
			.Where(l => !string.IsNullOrEmpty(l.Symbol) && !string.IsNullOrEmpty(l.Side) && !string.IsNullOrEmpty(l.OptionExpireDate) && !string.IsNullOrEmpty(l.OptionType) && !string.IsNullOrEmpty(l.StrikePrice))
			.Select(l =>
			{
				decimal.TryParse(l.StrikePrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var strike);
				var cp = l.OptionType!.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? "C" : "P";
				return $"{l.Side!.ToUpperInvariant()}:{l.Symbol!.ToUpperInvariant()}:{l.OptionExpireDate}:{strike.ToString("F2", CultureInfo.InvariantCulture)}:{cp}";
			})
			.OrderBy(s => s, StringComparer.Ordinal));
	}

	private static string ExtractRoot(string occSymbol)
	{
		var parsed = ParsingHelpers.ParseOptionSymbol(occSymbol);
		return parsed?.Root ?? occSymbol;
	}

	public async Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation)
	{
		using var client = new WebullOpenApiClient(_account);
		try
		{
			var balance = await client.FetchAccountBalanceAsync(cancellation);
			// Deployable capital: prefer option_buying_power / buying_power (what the broker actually
			// authorizes for new trades). Raw total_cash_balance can be negative on a margin account
			// (margin debit), which would zero out every cash-cap check even though BP is positive.
			var availableFunds = balance.TryGetAvailableFunds() ?? 0m;
			var totalCash = ParseDecimal(balance.TotalCashBalance);
			var unrealized = ParseDecimal(balance.TotalUnrealizedProfitLoss);
			// Equity estimate. total_cash_balance + unrealized approximates NLV when cash already
			// reflects proceeds from short positions; on margin accounts with debit balances it can go
			// negative. Floor at availableFunds so the risk-budget cap (% of accountValue) never
			// inverts and blocks trades that are otherwise fundable.
			var accountValue = Math.Max(availableFunds, totalCash + unrealized);
			return (availableFunds, accountValue);
		}
		catch (WebullOpenApiException ex) when (ex.HttpStatus == 404)
		{
			return (0m, 0m);
		}
	}

	/// <summary>Runs PositionReplay over the supplied trade history and indexes each open strategy by
	/// the sorted leg-set of its current legs. Returns an empty map when no trades are supplied so the
	/// caller falls back to the broker's reported cost basis.</summary>
	internal static Dictionary<string, (decimal initialDebit, decimal adjustedDebit)> BuildCostBasisLookup(
		IReadOnlyList<Trade>? trades, Dictionary<(DateTime, Side, int), decimal>? feeLookup)
	{
		var map = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal);
		if (trades == null || trades.Count == 0) return map;

		var (_, lots, _) = PositionTracker.ComputeReport(trades.ToList(), initialAmount: 0m, feeLookup: feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades.ToList());
		var (rows, _, _) = PositionTracker.BuildPositionRows(lots, tradeIndex, trades.ToList());

		PositionRow? currentParent = null;
		var currentLegs = new List<PositionRow>();

		void Flush()
		{
			if (currentParent != null && currentLegs.Count >= 2 && currentParent.Qty > 0)
			{
				var occs = new List<string>();
				foreach (var leg in currentLegs)
				{
					if (leg.MatchKey == null) continue;
					var occ = leg.MatchKey.StartsWith("option:", StringComparison.Ordinal) ? leg.MatchKey[7..] : leg.MatchKey;
					occs.Add(occ);
				}
				if (occs.Count >= 2)
				{
					var key = BuildLegSetKey(occs);
					var initial = currentParent.InitialAvgPrice ?? currentParent.AvgPrice;
					var adjusted = currentParent.AdjustedAvgPrice ?? currentParent.AvgPrice;
					map[key] = (Math.Abs(initial), Math.Abs(adjusted));
				}
			}
			currentParent = null;
			currentLegs.Clear();
		}

		foreach (var row in rows)
		{
			if (!row.IsStrategyLeg)
			{
				Flush();
				if (row.Asset == Asset.OptionStrategy) currentParent = row;
			}
			else if (currentParent != null)
			{
				currentLegs.Add(row);
			}
		}
		Flush();

		return map;
	}

	private static string BuildLegSetKey(IEnumerable<string> occSymbols)
	{
		var sorted = occSymbols
			.Select(s => s.Trim().ToUpperInvariant())
			.OrderBy(s => s, StringComparer.Ordinal);
		return string.Join("|", sorted);
	}

	private static ParsedLeg? ParseLeg(WebullOpenApiClient.HoldingLeg leg)
	{
		if (string.IsNullOrEmpty(leg.Symbol) || string.IsNullOrEmpty(leg.OptionType)
			|| string.IsNullOrEmpty(leg.OptionExpireDate) || string.IsNullOrEmpty(leg.OptionExercisePrice))
			return null;

		var callPut = leg.OptionType.ToUpperInvariant() switch
		{
			"CALL" => "C",
			"PUT" => "P",
			_ => null
		};
		if (callPut == null) return null;

		if (!DateTime.TryParseExact(leg.OptionExpireDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
			return null;

		if (!decimal.TryParse(leg.OptionExercisePrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var strike))
			return null;

		var occ = MatchKeys.OccSymbol(leg.Symbol, expiry, strike, callPut);
		return new ParsedLeg(occ, leg.Symbol, callPut, strike, expiry);
	}

	private static decimal ParseDecimal(string? s) =>
		decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

	private sealed record ParsedLeg(string OccSymbol, string Root, string CallPut, decimal Strike, DateTime Expiry);
}

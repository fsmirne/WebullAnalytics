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
	private static readonly HashSet<string> SupportedStrategies =
		new(StringComparer.OrdinalIgnoreCase) { "CALENDAR", "DIAGONAL" };

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

		var costBasisLookup = BuildCostBasisLookup(_trades, _feeLookup);

		var result = new Dictionary<string, OpenPosition>();
		foreach (var h in holdings)
		{
			if (string.IsNullOrEmpty(h.Symbol)) continue;
			if (!tickers.Contains(h.Symbol)) continue;
			if (string.IsNullOrEmpty(h.OptionStrategy) || !SupportedStrategies.Contains(h.OptionStrategy)) continue;
			if (h.Legs == null || h.Legs.Count < 2) continue;

			if (!int.TryParse(h.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0) continue;
			if (!decimal.TryParse(h.CostPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var brokerNetDebit)) brokerNetDebit = 0m;

			var parsedLegs = new List<ParsedLeg>();
			foreach (var leg in h.Legs)
			{
				var p = ParseLeg(leg);
				if (p == null) { parsedLegs.Clear(); break; }
				parsedLegs.Add(p);
			}
			if (parsedLegs.Count < 2) continue;

			parsedLegs.Sort((a, b) => a.Expiry.CompareTo(b.Expiry));
			var shortLeg = parsedLegs[0];
			var longLeg = parsedLegs[^1];

			var positionLegs = new List<PositionLeg>
			{
				new(shortLeg.OccSymbol, Side.Sell, shortLeg.Strike, shortLeg.Expiry, shortLeg.CallPut, qty),
				new(longLeg.OccSymbol, Side.Buy, longLeg.Strike, longLeg.Expiry, longLeg.CallPut, qty)
			};

			var legSetKey = BuildLegSetKey(positionLegs.Select(l => l.Symbol));
			var (initialDebit, adjustedDebit) = costBasisLookup.TryGetValue(legSetKey, out var basis)
				? basis
				: (brokerNetDebit, brokerNetDebit);

			var key = $"{h.Symbol}_{h.OptionStrategy}_{shortLeg.Strike:F2}_{shortLeg.Expiry:yyyyMMdd}";

			result[key] = new OpenPosition(
				Key: key,
				Ticker: h.Symbol,
				StrategyKind: h.OptionStrategy!,
				Legs: positionLegs,
				InitialNetDebit: initialDebit,
				AdjustedNetDebit: adjustedDebit,
				Quantity: qty,
				MaxLossPerShare: PositionRiskEstimator.MaxLossPerShare(initialDebit, positionLegs)
			);
		}

		return result;
	}

	public async Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation)
	{
		using var client = new WebullOpenApiClient(_account);
		try
		{
			var balance = await client.FetchAccountBalanceAsync(cancellation);
			var cash = ParseDecimal(balance.TotalCashBalance);
			var unrealized = ParseDecimal(balance.TotalUnrealizedProfitLoss);
			var accountValue = cash + unrealized;
			return (cash, accountValue);
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

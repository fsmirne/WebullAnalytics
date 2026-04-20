using System.Globalization;

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
///   - InitialNetDebit / AdjustedNetDebit ← parent.cost_price (per-contract)
///   - Short leg ← the leg with the earliest expiry
///   - Long leg  ← the leg with the later expiry
///
/// Positions whose option_strategy is null (single-leg) or whose strategy is not in the supported
/// set (currently CALENDAR and DIAGONAL) are skipped. Iron condors, verticals, and other structures
/// are out of scope for phase 1 and will produce no proposals.
///
/// Phase-1 caveat: AdjustedNetDebit equals InitialNetDebit because the broker API reports current
/// cost basis, not roll-history-adjusted break-even. Integrating local trade-log adjustments is a
/// follow-up.
/// </summary>
internal sealed class LivePositionSource : IPositionSource
{
	private static readonly HashSet<string> SupportedStrategies =
		new(StringComparer.OrdinalIgnoreCase) { "CALENDAR", "DIAGONAL" };

	private readonly TradeAccount _account;

	public LivePositionSource(TradeAccount account) { _account = account; }

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

		var result = new Dictionary<string, OpenPosition>();

		foreach (var h in holdings)
		{
			if (string.IsNullOrEmpty(h.Symbol)) continue;
			if (!tickers.Contains(h.Symbol)) continue;
			if (string.IsNullOrEmpty(h.OptionStrategy) || !SupportedStrategies.Contains(h.OptionStrategy)) continue;
			if (h.Legs == null || h.Legs.Count < 2) continue;

			if (!int.TryParse(h.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0) continue;
			if (!decimal.TryParse(h.CostPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var netDebit)) netDebit = 0m;

			// Parse every leg.
			var parsedLegs = new List<ParsedLeg>();
			foreach (var leg in h.Legs)
			{
				var p = ParseLeg(leg);
				if (p == null) { parsedLegs.Clear(); break; }
				parsedLegs.Add(p);
			}
			if (parsedLegs.Count < 2) continue;

			// Short = earliest expiry; Long = latest expiry. For CALENDAR/DIAGONAL call-side this
			// matches the standard structure.
			parsedLegs.Sort((a, b) => a.Expiry.CompareTo(b.Expiry));
			var shortLeg = parsedLegs[0];
			var longLeg = parsedLegs[^1];

			// Build position legs.
			var positionLegs = new List<PositionLeg>
			{
				new(shortLeg.OccSymbol, Side.Sell, shortLeg.Strike, shortLeg.Expiry, shortLeg.CallPut, qty),
				new(longLeg.OccSymbol, Side.Buy, longLeg.Strike, longLeg.Expiry, longLeg.CallPut, qty)
			};

			var kind = h.OptionStrategy!;
			var key = $"{h.Symbol}_{kind}_{shortLeg.Strike:F2}_{shortLeg.Expiry:yyyyMMdd}";

			result[key] = new OpenPosition(
				Key: key,
				Ticker: h.Symbol,
				StrategyKind: kind,
				Legs: positionLegs,
				InitialNetDebit: netDebit,
				AdjustedNetDebit: netDebit,
				Quantity: qty
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

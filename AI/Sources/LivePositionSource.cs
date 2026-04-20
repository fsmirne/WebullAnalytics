using System.Globalization;

namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Live position source backed by the Webull OpenAPI.
///
/// Grouping strategy (phase 1, calendar/diagonal focus):
///   1. Fetch all holdings for the configured account.
///   2. Filter to OPTION holdings whose OCC symbol parses to a root in the configured ticker set.
///   3. Within each ticker, pair short legs (qty &lt; 0) with long legs (qty &gt; 0) of the same callPut
///      whose expiry is later than the short leg's. Same strike = Calendar, different strike = Diagonal.
///   4. Unmatched legs (naked shorts, standalone longs, iron condors, butterflies, etc.) are skipped.
/// This covers the structures the rules target; more exotic structures are deferred.
///
/// Cost basis note: phase-1 InitialNetDebit comes from the broker's unit_cost at the time of query.
/// AdjustedNetDebit (break-even including roll history) requires cross-referencing the local trade log;
/// for now we set it equal to InitialNetDebit. A follow-up can wire in PositionTracker's adjusted basis.
/// </summary>
internal sealed class LivePositionSource : IPositionSource
{
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

		// Parse each option holding and filter by ticker.
		var parsed = new List<ParsedOptionHolding>();
		foreach (var h in holdings)
		{
			if (h.InstrumentType is not "OPTION") continue;
			if (string.IsNullOrEmpty(h.Symbol)) continue;
			var p = ParsingHelpers.ParseOptionSymbol(h.Symbol);
			if (p == null) continue;
			if (!tickers.Contains(p.Root)) continue;
			if (!decimal.TryParse(h.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty == 0m) continue;
			if (!decimal.TryParse(h.CostPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var unitCost)) unitCost = 0m;
			parsed.Add(new ParsedOptionHolding(h.Symbol, p.Root, p.CallPut, p.Strike, p.ExpiryDate, qty, unitCost));
		}

		var result = new Dictionary<string, OpenPosition>();
		var byTickerAndType = parsed.GroupBy(x => (x.Root, x.CallPut));

		foreach (var grp in byTickerAndType)
		{
			var (root, callPut) = grp.Key;
			var shorts = grp.Where(x => x.Qty < 0).OrderBy(x => x.Expiry).ToList();
			var longs = grp.Where(x => x.Qty > 0).OrderBy(x => x.Expiry).ToList();
			var longsRemaining = new List<ParsedOptionHolding>(longs);

			foreach (var shortLeg in shorts)
			{
				// Find a long leg with the same call/put whose expiry is strictly after the short's.
				// Prefer same strike (calendar) over different strike (diagonal).
				var match = longsRemaining.FirstOrDefault(l => l.Expiry > shortLeg.Expiry && l.Strike == shortLeg.Strike)
					?? longsRemaining.FirstOrDefault(l => l.Expiry > shortLeg.Expiry);
				if (match == null) continue;

				var kind = shortLeg.Strike == match.Strike ? "Calendar" : "Diagonal";
				var absShortQty = (int)Math.Abs(shortLeg.Qty);
				var absLongQty = (int)Math.Abs(match.Qty);
				var qty = Math.Min(absShortQty, absLongQty);
				if (qty == 0) continue;

				// NetDebit per contract = long cost - short credit. Short unit_cost is reported as
				// a positive magnitude by Webull (per their convention); we subtract because selling
				// a short generated credit.
				var shortCredit = Math.Abs(shortLeg.UnitCost);
				var longCost = Math.Abs(match.UnitCost);
				var netDebit = longCost - shortCredit;

				var key = $"{root}_{kind}_{shortLeg.Strike:F2}_{shortLeg.Expiry:yyyyMMdd}";

				result[key] = new OpenPosition(
					Key: key,
					Ticker: root,
					StrategyKind: kind,
					Legs: new[]
					{
						new PositionLeg(shortLeg.Symbol, Side.Sell, shortLeg.Strike, shortLeg.Expiry, callPut, qty),
						new PositionLeg(match.Symbol, Side.Buy, match.Strike, match.Expiry, callPut, qty)
					},
					InitialNetDebit: netDebit,
					AdjustedNetDebit: netDebit,
					Quantity: qty
				);

				longsRemaining.Remove(match);
			}
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

	private static decimal ParseDecimal(string? s) =>
		decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

	private sealed record ParsedOptionHolding(
		string Symbol, string Root, string CallPut, decimal Strike, DateTime Expiry, decimal Qty, decimal UnitCost);
}

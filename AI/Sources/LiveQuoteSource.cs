using System.Text.Json;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Sources;

/// <summary>Live option-chain quote source. Backed by Webull because Yahoo's chain endpoint omits the
/// historical-vol and 5-day IV fields the scorer relies on and exhibits throttling / gaps that the AI
/// pipeline can't tolerate. Requires <c>api-config.json</c> populated by <c>wa sniff</c>.</summary>
internal sealed class LiveQuoteSource : IQuoteSource
{
	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation, QuoteOverrides overrides = default)
	{
		// Build minimal PositionRow stubs for each option symbol so the fetcher can be reused.
		var rows = optionSymbols.Select(sym => new PositionRow(
			Instrument: sym,
			Asset: Asset.Option,
			OptionKind: "Call",          // placeholder; the fetcher parses the OCC symbol itself
			Side: Side.Buy,
			Qty: 1,
			AvgPrice: 0m,
			Expiry: null,
			MatchKey: MatchKeys.Option(sym)
		)).ToList();

		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(configPath)) throw new InvalidOperationException("api-config.json not found. Run 'sniff' first.");
		var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath))
			?? throw new InvalidOperationException("api-config.json is empty.");
		if (config.Webull.Headers.Count == 0) throw new InvalidOperationException("api-config.json has no headers. Run 'sniff' first.");

		var (options, spots) = await WebullOptionsClient.FetchOptionQuotesAsync(config, rows, cancellation);

		// Back-solve IV from the NBBO mid for every two-sided book — the SAME basis the backtest's
		// QuotesQuoteSource uses (OptionMath.ImpliedVol on the mid). Live previously trusted the vendor's
		// reported ImpliedVolatility field, which the 2026-06-15 cross-vendor audit showed diverges from the
		// mid-implied IV by 10–50 vol points at 0DTE (a vendor IV convention, not a Webull-specific bug). That
		// made the live 0DTE scorer — and EM / PoP / the exit projectors, which all read q.ImpliedVolatility —
		// select different strikes/structures than the NBBO-back-solved backtest the 0DTE settings were tuned
		// on. Re-basing here (the single seam both the opener and WatchLoop fetch through) makes live IV ≡
		// backtest IV. Vendor IV is kept as the fallback when the book is one-sided (no mid to solve from).
		// Dividend adjustment is intentionally omitted: at 0DTE (T≈hours, no in-window ex-div) it is a no-op;
		// at longer DTE the residual is sub-vol-point and the calendar/diagonal long leg already prices on the
		// dividend-aware MarketImpliedIv path.
		var rebased = new Dictionary<string, OptionContractQuote>(options.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, q) in options)
			rebased[sym] = BackSolveIvFromMid(sym, q, spots, asOf);

		// Filter spots to the tickers requested.
		var filteredSpots = spots.Where(kv => tickers.Contains(kv.Key))
			.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

		return new QuoteSnapshot(rebased, filteredSpots);
	}

	/// <summary>Returns <paramref name="q"/> with ImpliedVolatility re-solved from the NBBO mid (matching the
	/// backtest), or unchanged when the book is one-sided / inputs are degenerate (keeps the vendor IV).</summary>
	private static OptionContractQuote BackSolveIvFromMid(string sym, OptionContractQuote q, IReadOnlyDictionary<string, decimal> spots, DateTime asOf)
	{
		if (q.Bid is not (> 0m) || q.Ask is not (> 0m)) return q;        // one-sided / missing book → no mid to solve from
		var p = ParsingHelpers.ParseOptionSymbol(sym);
		if (p is null || p.CallPut is not ("C" or "P")) return q;
		if (!spots.TryGetValue(p.Root, out var spot) || spot <= 0m) return q;
		var t = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, p.ExpiryDate);
		if (t <= 0) return q;
		var mid = (q.Bid.Value + q.Ask.Value) / 2m;
		if (mid <= 0m) return q;
		var iv = OptionMath.ImpliedVol(spot, p.Strike, t, OptionMath.RiskFreeRate, mid, p.CallPut);
		return q with { ImpliedVolatility = iv, VendorImpliedVolatility = q.VendorImpliedVolatility ?? q.ImpliedVolatility };
	}
}

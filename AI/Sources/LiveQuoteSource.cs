using System.Text.Json;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Sources;

/// <summary>Live option-chain vendor backing <see cref="LiveQuoteSource"/>. Webull is the default (sniffed
/// session). Schwab pulls the chains API's real NBBO — a cross-check for when Webull's API quotes diverge
/// from its own platform.</summary>
internal enum QuoteVendor { Webull, Schwab }

/// <summary>Live option-chain quote source. Webull is the default backing vendor (Yahoo's chain endpoint omits
/// the historical-vol and 5-day IV fields the scorer relies on and exhibits throttling / gaps the AI pipeline
/// can't tolerate); Schwab is selectable via <c>--source schwab</c> to cross-check Webull's feed. Either vendor
/// returns the full chain for the requested expiry window, so the opener can enumerate the strike ladder.
/// Webull requires <c>api-config.json</c> populated by <c>wa sniff</c>; Schwab requires <c>wa schwab login</c>.</summary>
internal sealed class LiveQuoteSource : IQuoteSource
{
	private readonly QuoteVendor _vendor;

	public LiveQuoteSource(QuoteVendor vendor = QuoteVendor.Webull) => _vendor = vendor;

	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation, QuoteOverrides overrides = default)
	{
		var (options, spots) = _vendor == QuoteVendor.Schwab
			? await FetchSchwabAsync(asOf, optionSymbols, tickers, cancellation)
			: await FetchWebullAsync(optionSymbols, cancellation);

		// Back-solve IV from the NBBO mid for every two-sided book — the SAME basis the backtest's
		// QuotesQuoteSource uses (OptionMath.ImpliedVol on the mid). Live previously trusted the vendor's
		// reported ImpliedVolatility field, which the 2026-06-15 cross-vendor audit showed diverges from the
		// mid-implied IV by 10–50 vol points at 0DTE (a vendor IV convention, not a Webull-specific bug). That
		// made the live 0DTE scorer — and EM / PoP / the exit projectors, which all read q.ImpliedVolatility —
		// select different strikes/structures than the NBBO-back-solved backtest the 0DTE settings were tuned
		// on. Re-basing here (the single seam both the opener and WatchLoop fetch through) makes live IV ≡
		// backtest IV, and crucially makes the Webull and Schwab vendors comparable (both scored off the mid,
		// not each vendor's own model IV). Vendor IV is kept as the fallback when the book is one-sided.
		var rebased = new Dictionary<string, OptionContractQuote>(options.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, q) in options)
			rebased[sym] = BackSolveIvFromMid(sym, q, spots, asOf);

		// Filter spots to the tickers requested.
		var filteredSpots = spots.Where(kv => tickers.Contains(kv.Key))
			.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

		return new QuoteSnapshot(rebased, filteredSpots);
	}

	/// <summary>Webull strategy/list chain + queryBatch fallback. Returns the full chain for each requested
	/// root plus the underlying spot, regardless of which symbols were asked for (one placeholder per root
	/// suffices to trigger the chain fetch).</summary>
	private static async Task<(IReadOnlyDictionary<string, OptionContractQuote> Options, IReadOnlyDictionary<string, decimal> Spots)> FetchWebullAsync(
		IReadOnlySet<string> optionSymbols, CancellationToken cancellation)
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

		var config = LoadApiConfig();
		if (config.Webull.Headers.Count == 0) throw new InvalidOperationException("api-config.json has no headers. Run 'sniff' first.");

		return await WebullOptionsClient.FetchOptionQuotesAsync(config, rows, cancellation);
	}

	/// <summary>Schwab chains-API source. Fetches one full strike ladder (<c>range=ALL</c>) per underlying root
	/// over the [min,max] expiry window the requested symbols span — the same full-chain shape the Webull path
	/// returns, so the opener's strike enumeration is unaffected. Roots requested only for a spot (no option
	/// legs) fetch the current day's chain purely to surface the underlying price. Requires <c>wa schwab login</c>.</summary>
	private static async Task<(IReadOnlyDictionary<string, OptionContractQuote> Options, IReadOnlyDictionary<string, decimal> Spots)> FetchSchwabAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		var config = LoadApiConfig();
		if (config.Schwab is null || string.IsNullOrEmpty(config.Schwab.RefreshToken))
			throw new InvalidOperationException("--source schwab needs Schwab credentials in api-config.json. Run 'wa schwab login' first.");

		var token = await SchwabAuthClient.GetAccessTokenAsync(config.Schwab, configPath, cancellation);

		// Group the requested expiries by root so each underlying is fetched once over exactly the window it
		// needs. Roots that appear only in `tickers` (spot lookup, no legs) get today's chain for the spot.
		var today = DateOnly.FromDateTime(asOf);
		var windows = new Dictionary<string, (DateOnly Min, DateOnly Max)>(StringComparer.OrdinalIgnoreCase);
		foreach (var sym in optionSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p is null) continue;
			var exp = DateOnly.FromDateTime(p.ExpiryDate);
			windows[p.Root] = windows.TryGetValue(p.Root, out var w)
				? (exp < w.Min ? exp : w.Min, exp > w.Max ? exp : w.Max)
				: (exp, exp);
		}
		foreach (var t in tickers)
			if (!windows.ContainsKey(t)) windows[t] = (today, today);

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var spots = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var (root, window) in windows)
		{
			var (spot, quotes) = await SchwabOptionsClient.FetchChainAsync(token, root, window.Min, window.Max, cancellation, strikeCount: 0);
			if (spot is > 0m) spots[root] = spot.Value;
			foreach (var q in quotes) options[q.ContractSymbol] = q;
		}
		return (options, spots);
	}

	private static ApiConfig LoadApiConfig()
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(configPath)) throw new InvalidOperationException("api-config.json not found. Run 'sniff' first.");
		return JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath))
			?? throw new InvalidOperationException("api-config.json is empty.");
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

using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Sources;

/// <summary>Live option-chain quote source. Backed by Webull because Yahoo's chain endpoint omits the
/// historical-vol and 5-day IV fields the scorer relies on and exhibits throttling / gaps that the AI
/// pipeline can't tolerate. Requires <c>api-config.json</c> populated by <c>wa sniff</c>.</summary>
internal sealed class LiveQuoteSource : IQuoteSource
{
	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation)
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
		if (config.Headers.Count == 0) throw new InvalidOperationException("api-config.json has no headers. Run 'sniff' first.");

		var (options, spots) = await WebullOptionsClient.FetchOptionQuotesAsync(config, rows, cancellation);

		// Filter spots to the tickers requested.
		var filteredSpots = spots.Where(kv => tickers.Contains(kv.Key))
			.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

		return new QuoteSnapshot(options, filteredSpots);
	}
}

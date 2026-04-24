using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Sources;

internal sealed class LiveQuoteSource : IQuoteSource
{
	private readonly string _provider; // "webull" | "yahoo"

	public LiveQuoteSource(string provider)
	{
		_provider = provider is "webull" or "yahoo"
			? provider
			: throw new ArgumentException($"Unknown quote provider: {provider}");
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation)
	{
		// Build minimal PositionRow stubs for each option symbol so existing fetchers can be reused.
		var rows = optionSymbols.Select(sym => new PositionRow(
			Instrument: sym,
			Asset: Asset.Option,
			OptionKind: "Call",          // placeholder; fetchers parse the OCC symbol themselves
			Side: Side.Buy,
			Qty: 1,
			AvgPrice: 0m,
			Expiry: null,
			MatchKey: MatchKeys.Option(sym)
		)).ToList();

		IReadOnlyDictionary<string, OptionContractQuote> options;
		IReadOnlyDictionary<string, decimal> spots;

		if (_provider == "webull")
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath)) throw new InvalidOperationException("api-config.json not found. Run 'sniff' first.");
			var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath))
				?? throw new InvalidOperationException("api-config.json is empty.");
			if (config.Headers.Count == 0) throw new InvalidOperationException("api-config.json has no headers. Run 'sniff' first.");

			var (quotes, underlyings) = await WebullOptionsClient.FetchOptionQuotesAsync(config, rows, cancellation);
			options = quotes;
			spots = underlyings;
		}
		else
		{
			var (quotes, underlyings) = await YahooOptionsClient.FetchOptionQuotesAsync(rows, cancellation);
			options = quotes;
			spots = underlyings;
		}

		// Filter spots to the tickers requested.
		var filteredSpots = spots.Where(kv => tickers.Contains(kv.Key))
			.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

		return new QuoteSnapshot(options, filteredSpots);
	}
}

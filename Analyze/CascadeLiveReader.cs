using Spectre.Console;
using System.Text.Json;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>One live cascade reading with the chain it was read from.</summary>
/// <param name="Spot">The spot the reading was scored at.</param>
/// <param name="Quotes">The fetched chain (every expiry within the horizon), keyed by OCC symbol.</param>
internal sealed record CascadeLiveRead(CascadeReading Reading, int ExpiryCount, decimal Spot, IReadOnlyDictionary<string, OptionContractQuote> Quotes);

/// <summary>
/// The live read behind `wa analyze cascade` and the cascade opener of `wa ai watch`: fetch the chain from the quote vendor, load the
/// session's minute bars, and score the regime at this instant. Every read appends its chain to data/iv (the volume-pace factor's
/// input) and its reading to data/cascade (<see cref="CascadeLog"/>).
/// </summary>
internal static class CascadeLiveReader
{
	/// <param name="VendorName">"schwab" or "webull" — which option-chain client to read.</param>
	/// <param name="StrikeRangePct">Strike window as ± percent of spot.</param>
	/// <param name="SpotOverride">Scores the reading at this spot instead of the vendor's.</param>
	internal sealed record Request(string Ticker, string VendorName, int Dte, int StrikeRangePct, int MaxStrikes, int TriggerScore, decimal? SpotOverride = null);

	/// <summary>The reading, or the reason there is none as console markup.</summary>
	internal static async Task<(CascadeLiveRead? Read, string? Error)> ReadAsync(Request request, DateTime nowEt, CancellationToken cancellation)
	{
		var ticker = request.Ticker.ToUpperInvariant();
		var (quotes, vendorSpot, apiConfig, fetchError) = await FetchChainAsync(request, ticker, nowEt, cancellation);
		if (quotes == null) return (null, fetchError);

		var spot = request.SpotOverride ?? vendorSpot;
		if (spot is not > 0m) return (null, $"[red]No spot price available for {Markup.Escape(ticker)}. Pass --spot {Markup.Escape(ticker)}:PRICE to override.[/]");

		// Minute bars have one source (Webull's chart endpoint) whatever the quote vendor: the vendor only picks between the Webull and Schwab option-chain clients.
		var intradayCache = apiConfig != null ? new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig)) : null;
		// A live read includes the forming minute.
		var rthBars = await CascadeScreener.LoadSessionRthBarsAsync(ticker, nowEt, intradayCache, includeCurrentBar: true, cancellation);

		// Always append a data/iv capture — the volume-pace factor is the difference of consecutive captures.
		var band = request.StrikeRangePct / 100m;
		var inWindow = quotes.Values.Where(q =>
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
			if (parsed == null || !ParsingHelpers.RootsMatchForAggregation(parsed.Root, ticker, parsed.ExpiryDate)) return false;
			var exp = parsed.ExpiryDate.Date;
			if (exp < nowEt.Date || exp > nowEt.Date.AddDays(request.Dte)) return false;
			return Math.Abs(parsed.Strike - spot.Value) / spot.Value <= band;
		});
		IvDumpStore.Append(ticker, request.VendorName, nowEt, spot.Value, inWindow);

		if (AnalyzeGexCommand.IsOiBlackout(quotes, ticker, nowEt.Date))
			return (null, $"[yellow]The vendor returned no open interest for any unexpired {Markup.Escape(ticker)} contract (overnight OI blackout — OI is zeroed until the next morning's OCC update), and the terrain is built from OI. Re-run during market hours, or use --date with a captured data/oi snapshot.[/]");

		var read = await CascadeScreener.ReadChainAsync(ticker, spot.Value, nowEt, quotes, request.Dte, band, request.MaxStrikes, request.TriggerScore, rthBars, intradayCache, new HistoricalPriceCache(), new SmileIndexCache(offline: false), request.VendorName, includeCurrentBar: true, cancellation);
		if (read == null) return (null, $"[yellow]No strikes match within ±{request.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]");

		var (reading, expiryCount) = read.Value;
		CascadeLog.Append(ticker, request.VendorName, nowEt, nowEt, request.SpotOverride.HasValue, reading);
		return (new CascadeLiveRead(reading, expiryCount, spot.Value, quotes), null);
	}

	/// <summary>The vendor's chain out to the horizon and its spot; a null chain carries the reason as console markup.</summary>
	private static async Task<(Dictionary<string, OptionContractQuote>? Quotes, decimal? Spot, ApiConfig? ApiConfig, string? Error)> FetchChainAsync(Request request, string ticker, DateTime nowEt, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(configPath)) return (null, null, null, "[red]Error: api-config.json not found. Run 'sniff' first (or pass a past --date with a data/oi snapshot).[/]");
		var apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));

		if (request.VendorName == "schwab")
		{
			if (apiConfig?.Schwab == null) return (null, null, apiConfig, "[red]Error: schwab vendor needs Schwab credentials in api-config.json. Run 'wa schwab login' first.[/]");
			IReadOnlyList<OptionContractQuote> schwabQuotes;
			decimal? schwabSpot;
			try
			{
				var token = await SchwabAuthClient.GetAccessTokenAsync(apiConfig.Schwab, configPath, cancellation);
				var fromExpiry = DateOnly.FromDateTime(nowEt.Date);
				(schwabSpot, schwabQuotes) = await SchwabOptionsClient.FetchChainAsync(token, ticker, fromExpiry, fromExpiry.AddDays(request.Dte), cancellation, strikeCount: 0);
			}
			catch (SchwabAuthException ex)
			{
				return (null, null, apiConfig, $"[red]Schwab auth failed: {Markup.Escape(ex.Message)} Re-run 'wa schwab login'.[/]");
			}
			var fetched = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			foreach (var q in schwabQuotes)
				fetched[q.ContractSymbol] = q;
			if (fetched.Count == 0) return (null, null, apiConfig, $"[red]No option chain data returned for {Markup.Escape(ticker)} from Schwab.[/]");
			var normalized = schwabSpot is > 0m
				? LiveQuoteSource.NormalizeIvs(fetched, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = schwabSpot.Value })
				: fetched;
			return (normalized, schwabSpot, apiConfig, null);
		}

		if (apiConfig == null || apiConfig.Webull.Headers.Count == 0) return (null, null, apiConfig, "[red]Error: api-config.json is empty or missing headers. Run 'sniff' first.[/]");
		var (initialQuotes, fetchedSpot, derivativeIds) = await WebullOptionsClient.FetchChainAsync(apiConfig, ticker, cancellation);
		if (initialQuotes.Count == 0) return (null, null, apiConfig, $"[red]No option chain data returned for {Markup.Escape(ticker)}.[/]");
		var quotes = new Dictionary<string, OptionContractQuote>(initialQuotes, StringComparer.OrdinalIgnoreCase);
		var spot = request.SpotOverride ?? fetchedSpot;
		if (spot is > 0m)
		{
			var refreshed = await AnalyzeGexCommand.RefreshInWindowContractsAsync(apiConfig, quotes, derivativeIds, ticker, spot.Value, nowEt, expiryFilter: null, request.StrikeRangePct / 100m, request.Dte, request.MaxStrikes, cancellation);
			if (refreshed > 0) Log.Debug($"Refreshed {refreshed} in-window contract(s) via queryBatch.");
		}
		return (quotes, fetchedSpot, apiConfig, null);
	}
}

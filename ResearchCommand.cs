using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics;

class ResearchSettings : ReportSettings
{
	[Description("Hypothetical trades to include. Format: SYMBOL:PRICExQTY where PRICE is a number (positive=buy, negative=sell) or -/+BID/MID/ASK for market prices. QTY is optional (default 1). Comma-separated for multiple. Example: GME260501C00023000:-MIDx300")]
	[CommandOption("--trades")]
	public string? Trades { get; set; }

	internal static readonly HashSet<string> MarketPriceKeywords = new(StringComparer.OrdinalIgnoreCase) { "BID", "MID", "ASK" };

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Trades != null)
		{
			foreach (var entry in Trades.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var colonIdx = entry.IndexOf(':');
				if (colonIdx < 1)
					return ValidationResult.Error($"--trades: invalid entry '{entry}'. Expected format: SYMBOL:PRICExQTY");

				var symbol = entry[..colonIdx];
				var priceQty = entry[(colonIdx + 1)..];

				if (ParsingHelpers.ParseOptionSymbol(symbol) == null)
					return ValidationResult.Error($"--trades: invalid OCC symbol '{symbol}'");

				var xIdx = priceQty.IndexOf('x');
				var priceStr = xIdx >= 0 ? priceQty[..xIdx] : priceQty;
				var qtyStr = xIdx >= 0 ? priceQty[(xIdx + 1)..] : null;

				// Strip sign to get the keyword or number
				var unsigned = priceStr.TrimStart('-', '+');
				if (!MarketPriceKeywords.Contains(unsigned))
				{
					if (!decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price == 0)
						return ValidationResult.Error($"--trades: invalid price '{priceStr}' in entry '{entry}'");
				}

				if (qtyStr != null && (!int.TryParse(qtyStr, out var qty) || qty <= 0))
					return ValidationResult.Error($"--trades: invalid quantity '{qtyStr}' in entry '{entry}'");
			}
		}

		return ValidationResult.Success();
	}
}

class ResearchCommand : AsyncCommand<ResearchSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ResearchSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		var (trades, feeLookup, err) = ReportCommand.LoadTrades(settings);
		if (err != 0) return err;

		if (!string.IsNullOrEmpty(settings.Trades))
		{
			// Resolve market price keywords (BID/MID/ASK) by fetching quotes
			IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
			if (NeedsMarketPrices(settings.Trades))
			{
				quotes = await FetchQuotesForSymbols(settings, settings.Trades, cancellation);
				if (quotes == null) return 1;
			}

			var maxSeq = trades.Count > 0 ? trades.Max(t => t.Seq) + 1 : 0;
			var baseTime = trades.Count > 0 ? trades.Max(t => t.Timestamp) : DateTime.Now;
			trades.AddRange(ParseSyntheticTrades(settings.Trades, maxSeq, baseTime, quotes));
		}

		return await ReportCommand.RunReportPipeline(settings, trades, feeLookup, cancellation);
	}

	private static bool NeedsMarketPrices(string tradesSpec) =>
		tradesSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(entry =>
			{
				var priceQty = entry[(entry.IndexOf(':') + 1)..];
				var priceStr = priceQty.Contains('x') ? priceQty[..priceQty.IndexOf('x')] : priceQty;
				return ResearchSettings.MarketPriceKeywords.Contains(priceStr.TrimStart('-', '+'));
			});

	private static async Task<IReadOnlyDictionary<string, OptionContractQuote>?> FetchQuotesForSymbols(ResearchSettings settings, string tradesSpec, CancellationToken cancellation)
	{
		var symbols = tradesSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e[..e.IndexOf(':')]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var minimalRows = symbols.Select(s => new PositionRow(Instrument: s, Asset: Asset.Option, OptionKind: "Call", Side: Side.Buy, Qty: 1, AvgPrice: 0m, Expiry: null, MatchKey: MatchKeys.Option(s))).ToList();

		var apiSource = settings.Api?.ToLowerInvariant();
		if (apiSource == null)
		{
			Console.WriteLine("Error: --api (yahoo or webull) is required when using BID/MID/ASK price keywords");
			return null;
		}

		try
		{
			if (apiSource == "webull")
			{
				var configPath = Program.ResolvePath(Program.ApiConfigPath);
				if (!File.Exists(configPath)) { Console.WriteLine("Error: api-config.json not found. Run 'sniff' first."); return null; }
				var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
				if (config == null || config.Headers.Count == 0) { Console.WriteLine("Error: api-config.json has no headers. Run 'sniff' first."); return null; }
				Console.WriteLine("Webull: fetching quotes for hypothetical trades...");
				var (quotes, _) = await WebullOptionsClient.FetchOptionQuotesAsync(config, minimalRows, cancellation);
				return quotes;
			}
			else
			{
				Console.WriteLine("Yahoo Finance: fetching quotes for hypothetical trades...");
				var (quotes, _) = await YahooOptionsClient.FetchOptionQuotesAsync(minimalRows, cancellation);
				return quotes;
			}
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Error: Failed to fetch quotes: {ex.Message}");
			return null;
		}
	}

	private static decimal ResolvePrice(string priceStr, string symbol, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var isSell = priceStr.StartsWith('-');
		var unsigned = priceStr.TrimStart('-', '+');

		if (!ResearchSettings.MarketPriceKeywords.Contains(unsigned))
			return decimal.Parse(priceStr, CultureInfo.InvariantCulture);

		if (quotes == null || !quotes.TryGetValue(symbol, out var quote))
			throw new InvalidOperationException($"No quote found for '{symbol}'");

		var price = unsigned.ToUpperInvariant() switch
		{
			"BID" => quote.Bid ?? throw new InvalidOperationException($"No bid price for '{symbol}'"),
			"ASK" => quote.Ask ?? throw new InvalidOperationException($"No ask price for '{symbol}'"),
			"MID" => (quote.Bid ?? 0m) + (quote.Ask ?? 0m) == 0m ? throw new InvalidOperationException($"No bid/ask for '{symbol}'") : ((quote.Bid ?? 0m) + (quote.Ask ?? 0m)) / 2m,
			_ => throw new InvalidOperationException($"Unknown price keyword '{unsigned}'")
		};

		return isSell ? -price : price;
	}

	internal static List<Trade> ParseSyntheticTrades(string tradesSpec, int startSeq, DateTime baseTimestamp, IReadOnlyDictionary<string, OptionContractQuote>? quotes = null)
	{
		var result = new List<Trade>();
		var seq = startSeq;

		foreach (var entry in tradesSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var colonIdx = entry.IndexOf(':');
			var symbol = entry[..colonIdx];
			var priceQty = entry[(colonIdx + 1)..];

			var xIdx = priceQty.IndexOf('x');
			var priceStr = xIdx >= 0 ? priceQty[..xIdx] : priceQty;
			var qty = xIdx >= 0 ? int.Parse(priceQty[(xIdx + 1)..]) : 1;

			var signedPrice = ResolvePrice(priceStr, symbol, quotes);
			var parsed = ParsingHelpers.ParseOptionSymbol(symbol)!;
			var side = signedPrice > 0 ? Side.Buy : Side.Sell;
			var price = Math.Abs(signedPrice);
			var timestamp = baseTimestamp.AddSeconds(seq - startSeq + 1);

			result.Add(new Trade(Seq: seq++, Timestamp: timestamp, Instrument: Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike), MatchKey: MatchKeys.Option(symbol), Asset: Asset.Option, OptionKind: ParsingHelpers.CallPutDisplayName(parsed.CallPut), Side: side, Qty: qty, Price: price, Multiplier: Trade.OptionMultiplier, Expiry: parsed.ExpiryDate));
		}

		return result;
	}
}

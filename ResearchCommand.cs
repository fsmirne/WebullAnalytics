using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics;

class ResearchSettings : ReportSettings
{
	[Description("Hypothetical trades to include. Format: SYMBOL:PRICExQTY where PRICE is a number (positive=sell/credit, negative=buy/debit) or -/+BID/MID/ASK for market prices. QTY is optional (default 1). Comma-separated for multiple. Example: GME260501C00023000:-MIDx300")]
	[CommandOption("--trades")]
	public string? Trades { get; set; }

	[Description("Analyze a roll: shows credit/debit at various underlying prices. Format: OLD_SYMBOL>NEW_SYMBOLxQTY. Example: GME260410C00023000>GME260417C00023000x300")]
	[CommandOption("--roll")]
	public string? Roll { get; set; }

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

		if (Roll != null)
		{
			var gtIdx = Roll.IndexOf('>');
			if (gtIdx < 1)
				return ValidationResult.Error("--roll: expected format OLD_SYMBOL>NEW_SYMBOLxQTY");
			var remaining = Roll[(gtIdx + 1)..];
			var xIdx = remaining.IndexOf('x');
			var oldSym = Roll[..gtIdx];
			var newSym = xIdx >= 0 ? remaining[..xIdx] : remaining;
			var qtyStr = xIdx >= 0 ? remaining[(xIdx + 1)..] : null;

			if (ParsingHelpers.ParseOptionSymbol(oldSym) == null)
				return ValidationResult.Error($"--roll: invalid OCC symbol '{oldSym}'");
			if (ParsingHelpers.ParseOptionSymbol(newSym) == null)
				return ValidationResult.Error($"--roll: invalid OCC symbol '{newSym}'");
			if (qtyStr != null && (!int.TryParse(qtyStr, out var rqty) || rqty <= 0))
				return ValidationResult.Error($"--roll: invalid quantity '{qtyStr}'");
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

		if (!string.IsNullOrEmpty(settings.Roll))
		{
			return await AnalyzeRoll(settings, cancellation);
		}

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

	private static async Task<int> AnalyzeRoll(ResearchSettings settings, CancellationToken cancellation)
	{
		var gtIdx = settings.Roll!.IndexOf('>');
		var remaining = settings.Roll[(gtIdx + 1)..];
		var xIdx = remaining.IndexOf('x');
		var oldSymbol = settings.Roll[..gtIdx];
		var newSymbol = xIdx >= 0 ? remaining[..xIdx] : remaining;
		var qty = xIdx >= 0 ? int.Parse(remaining[(xIdx + 1)..]) : 1;

		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSymbol)!;
		var newParsed = ParsingHelpers.ParseOptionSymbol(newSymbol)!;

		// Fetch quotes for both legs to get IV and current prices
		var allSymbols = new[] { oldSymbol, newSymbol };
		var minimalRows = allSymbols.Select(s => new PositionRow(Instrument: s, Asset: Asset.Option, OptionKind: "Call", Side: Side.Buy, Qty: 1, AvgPrice: 0m, Expiry: null, MatchKey: MatchKeys.Option(s))).ToList();

		var apiSource = settings.Api?.ToLowerInvariant();
		if (apiSource == null) { Console.WriteLine("Error: --api (yahoo or webull) is required for --roll analysis"); return 1; }

		IReadOnlyDictionary<string, OptionContractQuote> quotes;
		IReadOnlyDictionary<string, decimal> underlyingPrices;
		try
		{
			if (apiSource == "webull")
			{
				var configPath = Program.ResolvePath(Program.ApiConfigPath);
				if (!File.Exists(configPath)) { Console.WriteLine("Error: api-config.json not found."); return 1; }
				var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
				if (config == null || config.Headers.Count == 0) { Console.WriteLine("Error: api-config.json has no headers."); return 1; }
				Console.WriteLine("Webull: fetching option chain data for roll analysis...");
				(quotes, underlyingPrices) = await WebullOptionsClient.FetchOptionQuotesAsync(config, minimalRows, cancellation);
			}
			else
			{
				Console.WriteLine("Yahoo Finance: fetching option chain data for roll analysis...");
				(quotes, underlyingPrices) = await YahooOptionsClient.FetchOptionQuotesAsync(minimalRows, cancellation);
			}

			var riskFreeRate = await YahooOptionsClient.FetchRiskFreeRateAsync(cancellation);
			if (riskFreeRate.HasValue)
			{
				OptionMath.RiskFreeRate = riskFreeRate.Value;
				Console.WriteLine($"Risk-free rate (13-week T-bill): {riskFreeRate.Value:P2}");
			}
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Error: Failed to fetch option data: {ex.Message}");
			return 1;
		}

		// Resolve IVs (--iv overrides take priority)
		var ivOverrides = settings.IvOverrides != null ? ReportCommand.ParseIvOverrides(settings.IvOverrides) : null;
		decimal? oldIv = ivOverrides != null && ivOverrides.TryGetValue(oldSymbol, out var oiv) ? oiv : quotes.TryGetValue(oldSymbol, out var oq) && oq.ImpliedVolatility > 0 ? oq.ImpliedVolatility : null;
		decimal? newIv = ivOverrides != null && ivOverrides.TryGetValue(newSymbol, out var niv) ? niv : quotes.TryGetValue(newSymbol, out var nq) && nq.ImpliedVolatility > 0 ? nq.ImpliedVolatility : null;

		if (!oldIv.HasValue || !newIv.HasValue)
		{
			Console.WriteLine($"Error: Could not determine IV for {(!oldIv.HasValue ? oldSymbol : newSymbol)}. Use --iv to override.");
			return 1;
		}

		var spot = underlyingPrices.TryGetValue(oldParsed.Root, out var sp) ? sp : 0m;
		if (settings.CurrentUnderlyingPrice != null)
		{
			var overrides = ReportCommand.ParseUnderlyingPriceOverrides(settings.CurrentUnderlyingPrice);
			if (overrides.TryGetValue(oldParsed.Root, out var ovr)) spot = ovr;
		}
		if (spot == 0) { Console.WriteLine($"Error: Could not determine underlying price for {oldParsed.Root}"); return 1; }

		// Current market prices
		var oldBid = quotes.TryGetValue(oldSymbol, out var obq) ? obq.Bid : null;
		var oldAsk = quotes.TryGetValue(oldSymbol, out var oaq) ? oaq.Ask : null;
		var newBid = quotes.TryGetValue(newSymbol, out var nbq) ? nbq.Bid : null;
		var newAsk = quotes.TryGetValue(newSymbol, out var naq) ? naq.Ask : null;

		// Build price grid: roll credit at various underlying prices
		var strike = oldParsed.Strike;
		// Use fine-grained steps for roll credit analysis (10x finer than break-even grid)
		var step = OptionMath.GetPriceStep(strike) / settings.Range / 5m;
		var padding = step * 10;
		// Center on current price if available, otherwise on strike
		var center = Math.Abs(spot - strike) < padding ? spot : strike;
		var minPrice = Math.Min(strike, center) - padding;
		var maxPrice = Math.Max(strike, center) + padding;

		var today = DateTime.Today;
		var oldExpiry = oldParsed.ExpiryDate;
		var newExpiry = newParsed.ExpiryDate;
		var rfr = OptionMath.RiskFreeRate;

		// Header
		Console.WriteLine();
		Console.WriteLine($"Roll Analysis: {Formatters.FormatOptionDisplay(oldParsed.Root, oldParsed.ExpiryDate, oldParsed.Strike)} {ParsingHelpers.CallPutDisplayName(oldParsed.CallPut)} -> {Formatters.FormatOptionDisplay(newParsed.Root, newParsed.ExpiryDate, newParsed.Strike)} {ParsingHelpers.CallPutDisplayName(newParsed.CallPut)}  ({qty}x)");
		Console.WriteLine($"Current: {oldParsed.Root} @ ${spot}  |  Close {oldSymbol}: Bid ${oldBid?.ToString("N2") ?? "?"} / Ask ${oldAsk?.ToString("N2") ?? "?"}  |  Open {newSymbol}: Bid ${newBid?.ToString("N2") ?? "?"} / Ask ${newAsk?.ToString("N2") ?? "?"}");
		Console.WriteLine($"IV: Close leg {oldIv.Value:P1} | Open leg {newIv.Value:P1}");

		// Compute current market roll credit
		var currentCredit = (newBid ?? 0m) - (oldAsk ?? 0m);
		Console.WriteLine($"Current roll credit (natural): ${currentCredit:N4}/contract = ${currentCredit * qty * 100m:N2} total");
		Console.WriteLine();

		// Compute max columns from terminal width. Cell format: "0.13/0.37/0.24" or "120.39/135.50/15.11"
		var sampleWidth = Math.Max(strike, newParsed.Strike).ToString("N2").Length;
		var cellWidth = sampleWidth * 3 + 5; // 3 values + 2 slashes + 2 padding + 1 sign on net
		const int fixedOverhead = 11; // borders + price column
		var terminalWidth = settings.Simplified ? TerminalHelper.SimplifiedMinWidth : TerminalHelper.DetailedMinWidth;
		try { terminalWidth = Math.Max(terminalWidth, Console.WindowWidth); } catch { /* use default */ }
		var maxCols = Math.Max(3, (terminalWidth - fixedOverhead) / cellWidth);

		// Build time columns: hourly on expiry day for <=1 DTE, daily otherwise
		var oldDays = (int)(oldExpiry.Date - today).TotalDays;
		var evalTimes = new List<DateTime>();
		var isIntraday = oldDays <= 1;
		if (isIntraday)
		{
			// Hourly on the expiry day from market open to 4 PM (options stop trading)
			var optionsClose = new TimeSpan(16, 0, 0);
			var expiryDay = oldExpiry.Date;
			var allHours = new List<DateTime>();
			for (var h = OptionMath.MarketOpen; h < optionsClose; h += TimeSpan.FromHours(1))
				allHours.Add(expiryDay + h);
			allHours.Add(expiryDay + optionsClose);

			if (allHours.Count <= maxCols)
				evalTimes.AddRange(allHours);
			else
			{
				var hourStep = Math.Max(1, (allHours.Count - 1) / (maxCols - 1));
				for (var i = 0; i < allHours.Count - 1; i += hourStep)
					evalTimes.Add(allHours[i]);
				if (evalTimes[^1] != allHours[^1])
					evalTimes.Add(allHours[^1]);
			}
		}
		else
		{
			var dayStep = Math.Max(1, oldDays / (maxCols - 1));
			for (var d = 0; d < oldDays; d += dayStep)
				evalTimes.Add(today.AddDays(d) + OptionMath.MarketOpen);
			var expiryOpen = oldExpiry.Date + OptionMath.MarketOpen;
			if (evalTimes[^1] != expiryOpen)
				evalTimes.Add(expiryOpen);
		}

		// Build 2D grid: prices × times
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn(new TableColumn("[bold]Price[/]").RightAligned().NoWrap());
		foreach (var t in evalTimes)
			table.AddColumn(new TableColumn($"[bold]{(isIntraday ? t.ToString("h tt") : t.ToString("dd MMM"))}[/]").RightAligned().NoWrap());

		var prices = new SortedSet<decimal>();
		for (var p = minPrice; p <= maxPrice; p += step) prices.Add(p);
		// Always include the strike and current price
		prices.Add(strike);
		if (spot >= minPrice && spot <= maxPrice) prices.Add(Math.Round(spot, 2));

		// Find the exact optimal price via fine search and insert it
		var searchStep = step / 10m;
		var bestPrice = strike;
		var bestCredit = decimal.MinValue;
		for (var p = minPrice; p <= maxPrice; p += searchStep)
		{
			var credit = evalTimes.Max(t => OptionMath.BlackScholes(p, newParsed.Strike, (newExpiry.Date + OptionMath.MarketClose - t).TotalDays / 365.0, rfr, newIv.Value, newParsed.CallPut) - OptionMath.BlackScholes(p, oldParsed.Strike, (oldExpiry.Date + OptionMath.MarketClose - t).TotalDays / 365.0, rfr, oldIv.Value, oldParsed.CallPut));
			if (credit > bestCredit) { bestCredit = credit; bestPrice = Math.Round(p, 2); }
		}
		prices.Add(bestPrice);

		var priceList = prices.Reverse().ToList();

		// Precompute all cells: old value, new value, credit
		var oldGrid = new decimal[priceList.Count, evalTimes.Count];
		var newGrid = new decimal[priceList.Count, evalTimes.Count];
		var creditGrid = new decimal[priceList.Count, evalTimes.Count];
		var maxCredit = decimal.MinValue;
		var maxCreditPrice = 0m;
		var maxCreditDate = today;
		for (int pi = 0; pi < priceList.Count; pi++)
			for (int di = 0; di < evalTimes.Count; di++)
			{
				var oldDte = (oldExpiry.Date + OptionMath.MarketClose - evalTimes[di]).TotalDays / 365.0;
				var newDte = (newExpiry.Date + OptionMath.MarketClose - evalTimes[di]).TotalDays / 365.0;
				oldGrid[pi, di] = OptionMath.BlackScholes(priceList[pi], oldParsed.Strike, oldDte, rfr, oldIv.Value, oldParsed.CallPut);
				newGrid[pi, di] = OptionMath.BlackScholes(priceList[pi], newParsed.Strike, newDte, rfr, newIv.Value, newParsed.CallPut);
				creditGrid[pi, di] = newGrid[pi, di] - oldGrid[pi, di];
				if (creditGrid[pi, di] > maxCredit) { maxCredit = creditGrid[pi, di]; maxCreditPrice = priceList[pi]; maxCreditDate = evalTimes[di]; }
			}

		for (int pi = 0; pi < priceList.Count; pi++)
		{
			var isCurrent = priceList[pi] == Math.Round(spot, 2);
			var isMaxRow = Enumerable.Range(0, evalTimes.Count).Any(di => creditGrid[pi, di] == maxCredit);
			var priceStr = $"${priceList[pi]:N2}";
			if (isMaxRow && isCurrent) priceStr = $"[bold green]{priceStr}[/]";
			else if (isMaxRow) priceStr = $"[green]{priceStr}[/]";
			else if (isCurrent) priceStr = $"[bold]{priceStr}[/]";

			var cells = new List<string> { priceStr };
			for (int di = 0; di < evalTimes.Count; di++)
			{
				var c = creditGrid[pi, di];
				var isGlobalMax = c == maxCredit;
				var creditColor = c >= 0 ? "green" : "red";
				var creditSign = Math.Round(c, 2) >= 0 ? "+" : "";
				var creditStr = isGlobalMax ? $"[bold underline {creditColor}]{creditSign}{c:N2}[/]" : $"[{creditColor}]{creditSign}{c:N2}[/]";
				cells.Add($"[grey]{oldGrid[pi, di]:N2}[/]/[grey]{newGrid[pi, di]:N2}[/]/{creditStr}");
			}
			table.AddRow(cells.ToArray());
		}

		AnsiConsole.Write(table);
		var maxDateLabel = isIntraday ? $"at {maxCreditDate:h:mm tt}" : $"on {maxCreditDate:dd MMM}";
		Console.WriteLine($"  * = max credit (${maxCredit:N4} @ ${maxCreditPrice:N2} {maxDateLabel})    > = current price");
		Console.WriteLine($"  Each cell: Close / Open / Net per contract. Total for {qty}x: max ${maxCredit * qty * 100m:N2}");
		Console.WriteLine();

		return 0;
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
		var isBuy = priceStr.StartsWith('-');
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

		return isBuy ? -price : price;
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
			var side = signedPrice > 0 ? Side.Sell : Side.Buy;
			var price = Math.Abs(signedPrice);
			var timestamp = baseTimestamp.AddSeconds(seq - startSeq + 1);

			result.Add(new Trade(Seq: seq++, Timestamp: timestamp, Instrument: Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike), MatchKey: MatchKeys.Option(symbol), Asset: Asset.Option, OptionKind: ParsingHelpers.CallPutDisplayName(parsed.CallPut), Side: side, Qty: qty, Price: price, Multiplier: Trade.OptionMultiplier, Expiry: parsed.ExpiryDate));
		}

		return result;
	}
}

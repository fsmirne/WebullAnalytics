using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics;

// Shared base for both subcommands.
internal abstract class AnalyzeSubcommandSettings : ReportSettings
{
	[CommandOption("--date")]
	[Description("Override 'today' for evaluation. Simulates running on a different date (e.g., after short leg expiration). Format: YYYY-MM-DD")]
	public string? Date { get; set; }

	internal DateTime? EvaluationDateOverride => Date != null ? DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--date: expected format YYYY-MM-DD, got '{Date}'");

		return ValidationResult.Success();
	}
}

// ─── `analyze trade` ──────────────────────────────────────────────────────────

internal sealed class AnalyzeTradeSettings : AnalyzeSubcommandSettings
{
	[CommandArgument(0, "<spec>")]
	[Description("Hypothetical trades. Format: ACTION:SYMBOL:QTY@PRICE where ACTION is buy|sell, SYMBOL is an OCC option symbol, and PRICE is a decimal or BID|MID|ASK (keywords require --api). Comma-separated for multiple. Example: buy:GME260501C00023000:300@MID")]
	public string Spec { get; set; } = "";

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		List<ParsedLeg> legs;
		try { legs = TradeLegParser.Parse(Spec); }
		catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }

		foreach (var leg in legs)
		{
			if (leg.Option == null)
				return ValidationResult.Error($"<spec>: '{leg.Symbol}' is not an OCC option symbol (analyze trade supports option legs only)");
			if (leg.Price == null && leg.PriceKeyword == null)
				return ValidationResult.Error($"<spec>: leg '{leg.Symbol}' is missing @PRICE (a decimal or BID|MID|ASK is required)");
		}

		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeTradeCommand : AsyncCommand<AnalyzeTradeSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AnalyzeTradeSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		var (trades, feeLookup, err) = ReportCommand.LoadTrades(settings);
		if (err != 0) return err;

		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
		if (AnalyzeCommon.NeedsMarketPrices(settings.Spec))
		{
			quotes = await AnalyzeCommon.FetchQuotesForSymbols(settings, settings.Spec, cancellation);
			if (quotes == null) return 1;
		}

		var maxSeq = trades.Count > 0 ? trades.Max(t => t.Seq) + 1 : 0;
		var baseTime = settings.Until != null ? settings.UntilDate.AddHours(18) : trades.Count > 0 ? trades.Max(t => t.Timestamp) : DateTime.Now;
		trades.AddRange(AnalyzeCommon.ParseSyntheticTrades(settings.Spec, maxSeq, baseTime, quotes));

		return await ReportCommand.RunReportPipeline(settings, trades, feeLookup, cancellation);
	}
}

// ─── `analyze roll` ───────────────────────────────────────────────────────────

internal sealed class AnalyzeRollSettings : AnalyzeSubcommandSettings
{
	[CommandArgument(0, "<spec>")]
	[Description("Roll spec. Format: OLD_SYMBOL>NEW_SYMBOL:QTY. Example: GME260410C00023000>GME260417C00023000:300")]
	public string Spec { get; set; } = "";

	[CommandOption("--side")]
	[Description("Position side. 'short' computes close-short-on-old / open-short-on-new (credit = new_bid - old_ask). 'long' computes close-long-on-old / open-long-on-new (credit = old_bid - new_ask). Default: short.")]
	public string? Side { get; set; }

	[CommandOption("--pair")]
	[Description("Static paired leg for spread margin calculation. Format: SYMBOL:QTY where SYMBOL is an equity ticker or OCC option symbol. Only meaningful with --side short. Example: --pair GME260515C00025000:499 or --pair GME:500")]
	public string? Pair { get; set; }

	[CommandOption("--cash")]
	[Description("Available cash for funding the roll. Format: dollar amount (e.g. 23015 or 23015.50). Prints a funding-check block against the BP delta. Only meaningful with --side short.")]
	public string? Cash { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		var gtIdx = Spec.IndexOf('>');
		if (gtIdx < 1)
			return ValidationResult.Error("<spec>: expected format OLD_SYMBOL>NEW_SYMBOL:QTY");
		var remaining = Spec[(gtIdx + 1)..];
		var colonIdx = remaining.IndexOf(':');
		var oldSym = Spec[..gtIdx];
		var newSym = colonIdx >= 0 ? remaining[..colonIdx] : remaining;
		var qtyStr = colonIdx >= 0 ? remaining[(colonIdx + 1)..] : null;

		if (ParsingHelpers.ParseOptionSymbol(oldSym) == null)
			return ValidationResult.Error($"<spec>: invalid OCC symbol '{oldSym}'");
		if (ParsingHelpers.ParseOptionSymbol(newSym) == null)
			return ValidationResult.Error($"<spec>: invalid OCC symbol '{newSym}'");
		if (qtyStr != null && (!int.TryParse(qtyStr, out var rqty) || rqty <= 0))
			return ValidationResult.Error($"<spec>: invalid quantity '{qtyStr}'");

		if (Side != null && !string.Equals(Side, "long", StringComparison.OrdinalIgnoreCase) && !string.Equals(Side, "short", StringComparison.OrdinalIgnoreCase))
			return ValidationResult.Error($"--side: must be 'long' or 'short', got '{Side}'");

		if (Pair != null)
		{
			var isLongSide = string.Equals(Side, "long", StringComparison.OrdinalIgnoreCase);
			if (isLongSide)
				return ValidationResult.Error("--pair is only meaningful with --side short (the default). Long-side rolls don't affect Reg-T margin.");

			var parts = Pair.Split(':');
			if (parts.Length != 2)
				return ValidationResult.Error($"--pair: expected SYMBOL:QTY, got '{Pair}'");
			if (string.IsNullOrWhiteSpace(parts[0]))
				return ValidationResult.Error($"--pair: SYMBOL is empty");
			if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lqty) || lqty <= 0)
				return ValidationResult.Error($"--pair: QTY must be a positive integer, got '{parts[1]}'");

			// SYMBOL must be either a valid OCC option or an equity ticker. If it parses as OCC, also verify the root matches the rolled leg's root.
			var longOpt = ParsingHelpers.ParseOptionSymbol(parts[0]);
			if (longOpt != null)
			{
				// Cross-check root against the rolled leg.
				var specGtIdx = Spec.IndexOf('>');
				if (specGtIdx > 0)
				{
					var oldSymSpec = Spec[..specGtIdx];
					var oldOpt = ParsingHelpers.ParseOptionSymbol(oldSymSpec);
					if (oldOpt != null && !string.Equals(longOpt.Root, oldOpt.Root, StringComparison.OrdinalIgnoreCase))
						return ValidationResult.Error($"--pair: option root '{longOpt.Root}' does not match rolled leg root '{oldOpt.Root}'");
				}
			}
			// else: equity ticker, no additional validation (we don't validate ticker strings against any registry).
		}

		if (Cash != null)
		{
			if (!decimal.TryParse(Cash, NumberStyles.Any, CultureInfo.InvariantCulture, out var c) || c < 0m)
				return ValidationResult.Error($"--cash: must be a non-negative decimal, got '{Cash}'");

			var isLongSide = string.Equals(Side, "long", StringComparison.OrdinalIgnoreCase);
			if (isLongSide)
				return ValidationResult.Error("--cash is only meaningful with --side short (the default). Long-side rolls don't affect Reg-T margin.");
		}

		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeRollCommand : AsyncCommand<AnalyzeRollSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AnalyzeRollSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		return await AnalyzeCommon.RunRollAnalysis(settings, cancellation);
	}
}

// ─── Shared helpers (ported from old AnalyzeCommand) ─────────────────────────

internal static class AnalyzeCommon
{
	internal static bool NeedsMarketPrices(string tradesSpec) =>
		TradeLegParser.Parse(tradesSpec).Any(leg => leg.PriceKeyword != null);

	internal static async Task<IReadOnlyDictionary<string, OptionContractQuote>?> FetchQuotesForSymbols(AnalyzeSubcommandSettings settings, string tradesSpec, CancellationToken cancellation)
	{
		var symbols = TradeLegParser.Parse(tradesSpec).Select(leg => leg.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

	internal static decimal ResolvePrice(ParsedLeg leg, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (leg.Price.HasValue) return leg.Price.Value;
		if (leg.PriceKeyword == null) throw new InvalidOperationException($"Leg '{leg.Symbol}' has no price");
		if (quotes == null || !quotes.TryGetValue(leg.Symbol, out var quote))
			throw new InvalidOperationException($"No quote found for '{leg.Symbol}'");
		return leg.PriceKeyword switch
		{
			"BID" => quote.Bid ?? throw new InvalidOperationException($"No bid price for '{leg.Symbol}'"),
			"ASK" => quote.Ask ?? throw new InvalidOperationException($"No ask price for '{leg.Symbol}'"),
			"MID" => (quote.Bid ?? 0m) + (quote.Ask ?? 0m) == 0m ? throw new InvalidOperationException($"No bid/ask for '{leg.Symbol}'") : ((quote.Bid ?? 0m) + (quote.Ask ?? 0m)) / 2m,
			_ => throw new InvalidOperationException($"Unknown price keyword '{leg.PriceKeyword}'")
		};
	}

	internal static List<Trade> ParseSyntheticTrades(string tradesSpec, int startSeq, DateTime baseTimestamp, IReadOnlyDictionary<string, OptionContractQuote>? quotes = null)
	{
		var result = new List<Trade>();
		var seq = startSeq;
		var legs = TradeLegParser.Parse(tradesSpec);

		foreach (var leg in legs)
		{
			var parsed = leg.Option!;
			var price = ResolvePrice(leg, quotes);
			var side = leg.Action == LegAction.Buy ? Side.Buy : Side.Sell;
			var timestamp = baseTimestamp.AddSeconds(seq - startSeq + 1);

			result.Add(new Trade(Seq: seq++, Timestamp: timestamp, Instrument: Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike), MatchKey: MatchKeys.Option(leg.Symbol), Asset: Asset.Option, OptionKind: ParsingHelpers.CallPutDisplayName(parsed.CallPut), Side: side, Qty: leg.Quantity, Price: price, Multiplier: Trade.OptionMultiplier, Expiry: parsed.ExpiryDate));
		}

		return result;
	}

	internal static async Task<int> RunRollAnalysis(AnalyzeRollSettings settings, CancellationToken cancellation)
	{
		var gtIdx = settings.Spec.IndexOf('>');
		var remaining = settings.Spec[(gtIdx + 1)..];
		var colonIdx = remaining.IndexOf(':');
		var oldSymbol = settings.Spec[..gtIdx];
		var newSymbol = colonIdx >= 0 ? remaining[..colonIdx] : remaining;
		var qty = colonIdx >= 0 ? int.Parse(remaining[(colonIdx + 1)..]) : 1;

		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSymbol)!;
		var newParsed = ParsingHelpers.ParseOptionSymbol(newSymbol)!;

		// Parse optional --pair leg for spread margin.
		OptionParsed? longOpt = null;
		string? longStockTicker = null;
		int longQty = 0;
		string? pairOccSymbol = null;
		if (!string.IsNullOrEmpty(settings.Pair))
		{
			var parts = settings.Pair.Split(':');
			longQty = int.Parse(parts[1], CultureInfo.InvariantCulture);
			longOpt = ParsingHelpers.ParseOptionSymbol(parts[0]);
			if (longOpt == null) longStockTicker = parts[0];
			else pairOccSymbol = parts[0];
		}

		// Fetch quotes for both legs to get IV and current prices
		var allSymbols = pairOccSymbol != null
			? new[] { oldSymbol, newSymbol, pairOccSymbol }
			: new[] { oldSymbol, newSymbol };
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

		decimal pairMid = 0m;
		if (pairOccSymbol != null && quotes.TryGetValue(pairOccSymbol, out var pq))
		{
			if (pq.Bid.HasValue && pq.Ask.HasValue) pairMid = (pq.Bid.Value + pq.Ask.Value) / 2m;
		}

		// Build price grid: roll credit at various underlying prices
		var strike = oldParsed.Strike;
		// Use fine-grained steps for roll credit analysis (10x finer than break-even grid)
		var step = OptionMath.GetPriceStep(strike) / settings.Range / 5m;
		var padding = step * 10;
		// Center on current price if available, otherwise on strike
		var center = Math.Abs(spot - strike) < padding ? spot : strike;
		var minPrice = Math.Min(strike, center) - padding;
		var maxPrice = Math.Max(strike, center) + padding;

		var today = EvaluationDate.Today;
		var oldExpiry = oldParsed.ExpiryDate;
		var newExpiry = newParsed.ExpiryDate;
		var rfr = OptionMath.RiskFreeRate;

		// Header
		Console.WriteLine();
		Console.WriteLine($"Roll Analysis: {Formatters.FormatOptionDisplay(oldParsed.Root, oldParsed.ExpiryDate, oldParsed.Strike)} {ParsingHelpers.CallPutDisplayName(oldParsed.CallPut)} -> {Formatters.FormatOptionDisplay(newParsed.Root, newParsed.ExpiryDate, newParsed.Strike)} {ParsingHelpers.CallPutDisplayName(newParsed.CallPut)}  ({qty}x)");
		Console.WriteLine($"Current: {oldParsed.Root} @ ${spot}  |  Close {oldSymbol}: Bid ${oldBid?.ToString("N2") ?? "?"} / Ask ${oldAsk?.ToString("N2") ?? "?"}  |  Open {newSymbol}: Bid ${newBid?.ToString("N2") ?? "?"} / Ask ${newAsk?.ToString("N2") ?? "?"}");
		Console.WriteLine($"IV: Close leg {oldIv.Value:P1} | Open leg {newIv.Value:P1}");

		var isLong = string.Equals(settings.Side, "long", StringComparison.OrdinalIgnoreCase);
		var sideLabel = isLong ? "long" : "short";

		// Compute current market roll net: short-roll = new_bid - old_ask; long-roll = old_bid - new_ask.
		var currentCredit = isLong ? (oldBid ?? 0m) - (newAsk ?? 0m) : (newBid ?? 0m) - (oldAsk ?? 0m);
		Console.WriteLine($"Current roll net (natural, {sideLabel}): ${currentCredit:N4}/contract = ${currentCredit * qty * 100m:N2} total");

		if (!isLong)
		{
			var oldMarketMid = (oldBid.HasValue && oldAsk.HasValue) ? (oldBid.Value + oldAsk.Value) / 2m : 0m;
			var newMarketMid = (newBid.HasValue && newAsk.HasValue) ? (newBid.Value + newAsk.Value) / 2m : 0m;

			var oldCov = ComputeLegMargin(oldParsed, qty, spot, oldMarketMid, longOpt, longStockTicker, longQty, pairMid, isExisting: true);
			var newCov = ComputeLegMargin(newParsed, qty, spot, newMarketMid, longOpt, longStockTicker, longQty, pairMid, isExisting: false);

			var header = settings.Pair != null
				? $"Margin analysis (Reg-T estimate, at spot ${spot:N2}, with pair {Markup.Escape(settings.Pair)}):"
				: $"Margin analysis (Reg-T estimate, at spot ${spot:N2}):";
			Console.WriteLine(header);
			Console.WriteLine($"  Current requirement:  {oldCov.StatusLabel} = ${oldCov.Total:N2} total");
			Console.WriteLine($"  New requirement:      {newCov.StatusLabel} = ${newCov.Total:N2} total");
			var deltaMargin = newCov.Total - oldCov.Total;
			var deltaSign = deltaMargin >= 0 ? "+" : "-";
			Console.WriteLine($"  BP delta:             {deltaSign}${Math.Abs(deltaMargin):N2} total");

			if (!string.IsNullOrEmpty(settings.Cash))
			{
				var cash = decimal.Parse(settings.Cash, CultureInfo.InvariantCulture);
				var rollNetTotal = currentCredit * qty * 100m; // positive = credit; negative = debit
				var available = cash + rollNetTotal;
				var net = available - deltaMargin;
				var rollLabel = rollNetTotal >= 0m
					? $"${cash:N2} cash + ${rollNetTotal:N2} roll credit = ${available:N2}"
					: $"${cash:N2} cash - ${Math.Abs(rollNetTotal):N2} roll debit = ${available:N2}";
				var netSign = net >= 0m ? "+" : "-";
				var netLabel = net >= 0m ? "sufficient" : "shortfall — needs additional funds";
				Console.WriteLine();
				Console.WriteLine($"Funding check (--cash ${cash:N2}):");
				Console.WriteLine($"  Available:  {rollLabel}");
				Console.WriteLine($"  Required:   ${deltaMargin:N2} (BP delta)");
				Console.WriteLine($"  Net:        {netSign}${Math.Abs(net):N2} ({netLabel})");
			}
		}

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
		// Always include the strike, current price, and notable prices
		prices.Add(strike);
		if (spot >= minPrice && spot <= maxPrice) prices.Add(Math.Round(spot, 2));
		if (settings.NotablePrices != null)
			foreach (var pair in ReportCommand.ParseNotablePrices(settings.NotablePrices))
				if (pair.Key.Equals(oldParsed.Root, StringComparison.OrdinalIgnoreCase))
					foreach (var np in pair.Value)
						if (np >= minPrice && np <= maxPrice) prices.Add(np);

		// Find the exact optimal price via fine search and insert it
		var searchStep = step / 10m;
		var bestPrice = strike;
		var bestCredit = decimal.MinValue;
		for (var p = minPrice; p <= maxPrice; p += searchStep)
		{
			var credit = evalTimes.Max(t =>
			{
				var newVal = OptionMath.BlackScholes(p, newParsed.Strike, (newExpiry.Date + OptionMath.MarketClose - t).TotalDays / 365.0, rfr, newIv.Value, newParsed.CallPut);
				var oldVal = OptionMath.BlackScholes(p, oldParsed.Strike, (oldExpiry.Date + OptionMath.MarketClose - t).TotalDays / 365.0, rfr, oldIv.Value, oldParsed.CallPut);
				return isLong ? oldVal - newVal : newVal - oldVal;
			});
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
				creditGrid[pi, di] = isLong ? oldGrid[pi, di] - newGrid[pi, di] : newGrid[pi, di] - oldGrid[pi, di];
				if (creditGrid[pi, di] > maxCredit) { maxCredit = creditGrid[pi, di]; maxCreditPrice = priceList[pi]; maxCreditDate = evalTimes[di]; }
			}

		// Right-pad old, new, and credit text to uniform widths with figure spaces so the '|' separators align vertically.
		int oldWidth = 0, newWidth = 0, creditWidth = 0;
		for (int pi = 0; pi < priceList.Count; pi++)
			for (int di = 0; di < evalTimes.Count; di++)
			{
				oldWidth = Math.Max(oldWidth, oldGrid[pi, di].ToString("N2").Length);
				newWidth = Math.Max(newWidth, newGrid[pi, di].ToString("N2").Length);
				var c = creditGrid[pi, di];
				var sign = Math.Round(c, 2) >= 0 ? "+" : "";
				creditWidth = Math.Max(creditWidth, $"{sign}{c:N2}".Length);
			}
		const char pad = '\u2007';

		for (int pi = 0; pi < priceList.Count; pi++)
		{
			var isCurrent = priceList[pi] == Math.Round(spot, 2);
			var isMaxRow = Enumerable.Range(0, evalTimes.Count).Any(di => creditGrid[pi, di] == maxCredit);
			var priceStr = $"${priceList[pi]:N2}";
			if (isCurrent) priceStr = $"[bold yellow]{priceStr}[/]";
			else if (isMaxRow) priceStr = $"[green]{priceStr}[/]";

			var cells = new List<string> { priceStr };
			for (int di = 0; di < evalTimes.Count; di++)
			{
				var c = creditGrid[pi, di];
				var isGlobalMax = c == maxCredit;
				var creditColor = c >= 0 ? "green" : "red";
				var creditSign = Math.Round(c, 2) >= 0 ? "+" : "";
				var creditText = $"{creditSign}{c:N2}".PadLeft(creditWidth, pad);
				string creditStr;
				if (isCurrent) creditStr = $"[bold yellow]{creditText}[/]";
				else if (isGlobalMax) creditStr = $"[bold underline {creditColor}]{creditText}[/]";
				else creditStr = $"[{creditColor}]{creditText}[/]";
				var oldText = oldGrid[pi, di].ToString("N2").PadLeft(oldWidth, pad);
				var newText = newGrid[pi, di].ToString("N2").PadLeft(newWidth, pad);
				cells.Add($"[grey]{oldText}[/]|[grey]{newText}[/]|{creditStr}");
			}
			table.AddRow(cells.ToArray());
		}

		AnsiConsole.Write(table);
		var maxDateLabel = isIntraday ? $"at {maxCreditDate:h:mm tt}" : $"on {maxCreditDate:dd MMM}";
		AnsiConsole.MarkupLine($"  [bold underline green]max net ({sideLabel})[/] (${maxCredit:N4} @ ${maxCreditPrice:N2} {maxDateLabel})    [bold yellow]current price[/]    [green]price with max net[/]");
		Console.WriteLine($"  Each cell: Close|Open|Net per contract. Total for {qty}x: max ${maxCredit * qty * 100m:N2}");
		Console.WriteLine();

		return 0;
	}

	/// <summary>
	/// Reg-T naked short option margin estimate (per contract, = per 100-share unit).
	/// Formula: max(0.20 * spot * 100 - OTM_amount * 100, 0.10 * strike * 100) + premium * 100.
	/// For calls, OTM_amount = max(strike - spot, 0).
	/// For puts,  OTM_amount = max(spot - strike, 0).
	/// premium is the per-share option value used to anchor the collateral (pass the market mid if available, else 0 for a conservative lower bound).
	/// </summary>
	internal static decimal EstimateNakedShortMargin(decimal spot, decimal strike, string callPut, decimal premium)
	{
		var otm = callPut == "C" ? Math.Max(strike - spot, 0m) : Math.Max(spot - strike, 0m);
		var primary = 0.20m * spot * 100m - otm * 100m;
		var floor = 0.10m * strike * 100m;
		return Math.Max(primary, floor) + premium * 100m;
	}

	/// <summary>
	/// Holds a per-leg combined margin result: status label for display plus the total margin in dollars.
	/// </summary>
	internal sealed record LegMargin(string StatusLabel, decimal Total);

	/// <summary>
	/// Computes combined margin for a single short leg paired with an optional static long leg.
	/// Unified formula when long_expiry ≥ short_expiry and same call/put type:
	///   margin = max(strike_loss, 0) × 100 + max((long_mid - short_mid) × 100, 0)
	/// where strike_loss = long_strike - short_strike for calls, short_strike - long_strike for puts.
	/// The first term is 0 for standard covered (calendar or vertical) positions and positive for
	/// inverted-strike diagonals. The second term is the net debit paid to hold the spread.
	///
	/// Cases still treated as naked: no pair, wrong ticker, wrong call/put type, long expires before
	/// short, or long stock paired with a short put (long stock covers only short calls).
	/// </summary>
	internal static LegMargin ComputeLegMargin(OptionParsed shortLeg, int shortQty, decimal spot, decimal shortPremium, OptionParsed? longOpt, string? longStockTicker, int longQty, decimal longPremium, bool isExisting)
	{
		var naked = EstimateNakedShortMargin(spot, shortLeg.Strike, shortLeg.CallPut, shortPremium);

		// No pair → naked on all contracts.
		if (longOpt == null && string.IsNullOrEmpty(longStockTicker))
			return new LegMargin($"naked  ${naked:N2}/contract × {shortQty}", naked * shortQty);

		// Long stock.
		if (longStockTicker != null)
		{
			if (!string.Equals(longStockTicker, shortLeg.Root, StringComparison.OrdinalIgnoreCase))
				return new LegMargin($"no cover (stock ticker '{longStockTicker}' ≠ '{shortLeg.Root}')  ${naked:N2}/contract × {shortQty}", naked * shortQty);
			if (shortLeg.CallPut != "C")
				return new LegMargin($"no cover (long stock does not cover short puts)  ${naked:N2}/contract × {shortQty}", naked * shortQty);
			var coverable = Math.Min(shortQty, longQty / 100);
			var uncovered = shortQty - coverable;
			var total = uncovered * naked;
			var label = uncovered == 0
				? $"covered by stock (long {longQty} shares)  $0.00/contract × {shortQty}"
				: $"partial cover ({coverable} covered by stock, {uncovered} naked @ ${naked:N2})";
			return new LegMargin(label, total);
		}

		// Long option — longOpt is guaranteed non-null here.
		var lo = longOpt!;
		if (shortLeg.CallPut != lo.CallPut)
			return new LegMargin($"no cover (long {(lo.CallPut == "C" ? "call" : "put")} does not cover short {(shortLeg.CallPut == "C" ? "call" : "put")})  ${naked:N2}/contract × {shortQty}", naked * shortQty);

		if (lo.ExpiryDate < shortLeg.ExpiryDate)
			return new LegMargin($"no cover (long expires {lo.ExpiryDate:yyyy-MM-dd} < short expires {shortLeg.ExpiryDate:yyyy-MM-dd})  ${naked:N2}/contract × {shortQty}", naked * shortQty);

		// Unified spread-margin formula: strike_loss × 100 + debit × 100.
		var strikeLoss = shortLeg.CallPut == "C"
			? Math.Max(lo.Strike - shortLeg.Strike, 0m)
			: Math.Max(shortLeg.Strike - lo.Strike, 0m);
		var debit = Math.Max(longPremium - shortPremium, 0m);
		var coveredPer = strikeLoss * 100m + (isExisting ? 0m : debit * 100m);

		var coverableOpt = Math.Min(shortQty, longQty);
		var uncoveredOpt = shortQty - coverableOpt;
		var totalOpt = coverableOpt * coveredPer + uncoveredOpt * naked;

		// Label explains the structure: strike_loss = 0 → vertical/calendar; positive → inverted diagonal.
		var structureLabel = strikeLoss == 0m
			? (lo.Strike == shortLeg.Strike ? "calendar" : "covered vertical")
			: $"inverted diagonal (strike loss ${strikeLoss * 100m:N2})";
		var costBreakdown = isExisting
			? $"${strikeLoss * 100m:N2} strike (debit sunk) = ${coveredPer:N2}/contract"
			: $"${strikeLoss * 100m:N2} strike + ${debit * 100m:N2} debit = ${coveredPer:N2}/contract";
		var labelOpt = uncoveredOpt == 0
			? $"{structureLabel}  {costBreakdown} × {shortQty}"
			: $"partial cover ({structureLabel}: {coverableOpt} @ ${coveredPer:N2}, {uncoveredOpt} naked @ ${naked:N2})";
		return new LegMargin(labelOpt, totalOpt);
	}
}

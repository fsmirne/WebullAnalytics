using Spectre.Console;
using System.Globalization;
using WebullAnalytics.Pricing;
using WebullAnalytics.Positions;
using WebullAnalytics.Report;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>One `;` group of an `analyze trade --compare` spec, evaluated as if it were the only trade: its price columns come from the
/// same break-even analysis as its panel, its POP/EV from the opener scorer exactly as `analyze risk` scores the group's legs.</summary>
internal sealed record TradeComparisonRow(
	string Ticker,
	string Structure,
	int Qty,
	decimal NetPerContract,
	decimal? NaturalPerContract,
	decimal? Margin,
	decimal? MaxProfit,
	decimal? MaxProfitPrice,
	decimal? MaxLoss,
	IReadOnlyList<decimal> BreakEvens,
	decimal? ProbabilityOfProfit,
	decimal? ExpectedValue);

/// <summary>`analyze trade --compare`: a table ranking each `;` group side by side. Each group is evaluated alone — its row never depends on the
/// other groups — while the panels above it keep treating the spec as one book.</summary>
internal static class TradeComparison
{
	/// <summary>Builds one row per group that leaves an open position. <paramref name="specQuotes"/> are the quotes the spec's BID/MID/ASK prices
	/// were resolved from (the same snapshot as the panels' cost basis; null when every leg carries a decimal price); the chain in
	/// <paramref name="opts"/> supplies the scorer. <paramref name="scoreAtCostBasis"/> mirrors analyze risk: a fill scores at what was paid.</summary>
	internal static async Task<List<TradeComparisonRow>> BuildAsync(string tradesSpec, AnalysisOptions opts, IReadOnlyDictionary<string, OptionContractQuote>? specQuotes, GridSettings settings, DateTime baseTime, Func<string, DateTime, decimal?> closeLookup, int terminalWidth, bool scoreAtCostBasis, CancellationToken cancellation)
	{
		var priceQuotes = specQuotes ?? opts.OptionQuotes;
		var groups = tradesSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(g => (Spec: g, Legs: TradeLegParser.Parse(g))).Where(g => g.Legs.Count > 0).ToList();
		var scoring = await LoadScoringAsync(groups.Select(g => g.Legs[0].Option!.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), opts, settings, cancellation);

		var rows = new List<TradeComparisonRow>();
		foreach (var (groupSpec, legs) in groups)
		{
			var trades = AnalyzeCommon.ParseSyntheticTrades(groupSpec, 0, baseTime, specQuotes);
			var (_, positions, _) = PositionTracker.ComputeReport(trades, 0m, null, closeLookup);
			var (positionRows, _, _) = PositionTracker.BuildPositionRows(positions, PositionTracker.BuildTradeIndex(trades), trades);
			if (positionRows.Count == 0) continue;

			var results = BreakEvenAnalyzer.Analyze(positionRows, opts, settings.Range, terminalWidth, settings.DisplayMode.ToLowerInvariant(), settings.ShowLegs, gridTableHasBorder: true);
			var result = results.Count == 1 ? results[0] : CombinedBreakEvenAnalyzer.Analyze(positionRows, opts, settings.Range, terminalWidth, settings.DisplayMode.ToLowerInvariant(), settings.ShowLegs, gridTableHasBorder: true, individualResults: results).FirstOrDefault();
			if (result == null) continue;

			var ticker = legs[0].Option!.Root;
			var qty = legs[0].Quantity;
			var net = legs.Sum(l => Sign(l) * AnalyzeCommon.ResolvePrice(l, priceQuotes) * l.Quantity) / qty;
			var (pop, ev) = Score(legs, priceQuotes, scoring.GetValueOrDefault(ticker), scoreAtCostBasis);
			rows.Add(new TradeComparisonRow(ticker, Describe(legs), qty, net, Natural(legs, priceQuotes), result.Margin, result.MaxProfit, result.MaxProfitPrice, result.MaxLoss, result.BreakEvens, pop, ev));
		}
		return rows;
	}

	/// <summary>The scorer's view of one ticker: its spot, the quotes it scores on (re-based to the mid surface like analyze risk), the
	/// instants it is anchored on and the per-ticker inputs.</summary>
	private sealed record TickerScoring(decimal Spot, IReadOnlyDictionary<string, OptionContractQuote> Quotes, DateTime AsOf, DateTime QuoteAsOf, AnalyzeRiskCommand.OpenerScoringInputs Inputs, bool UseMarketImpliedIv);

	/// <summary>Anchors exactly as analyze risk: live = scored as of now on quotes struck at the observation instant, the forming minute bar
	/// included; a past --date = both at that instant, the forming bar excluded. A --spot override makes the mids stale, so the back-solve is
	/// skipped and broker IV used. A ticker without a spot or chain gets no scoring (its POP/EV show blank).</summary>
	private static async Task<Dictionary<string, TickerScoring>> LoadScoringAsync(IReadOnlyList<string> tickers, AnalysisOptions opts, GridSettings settings, CancellationToken cancellation)
	{
		var scoring = new Dictionary<string, TickerScoring>(StringComparer.OrdinalIgnoreCase);
		if (opts.OptionQuotes == null || opts.UnderlyingPrices == null) return scoring;

		var past = EvaluationDate.IsPast;
		var asOf = past ? EvaluationDate.Instant!.Value : DateTime.Now;
		var quoteAsOf = past ? EvaluationDate.Instant!.Value : OptionMath.ObservationInstant();
		var useMarketImpliedIv = string.IsNullOrEmpty(settings.Spot);
		var quotes = (settings.Calibrated || opts.MarksAsOfEvalDate) && useMarketImpliedIv ? AnalyzeCommon.RecalibrateQuotesToMid(opts.OptionQuotes, opts.UnderlyingPrices, dividends: null, quoteAsOf) : opts.OptionQuotes;
		var intradayCache = AnalyzeRiskCommand.CreateIntradayCache();

		foreach (var ticker in tickers)
		{
			decimal spot = 0m;
			var hasSpot = opts.UnderlyingPriceOverrides != null && opts.UnderlyingPriceOverrides.TryGetValue(ticker, out spot) || ParsingHelpers.TryResolveForRoot(opts.UnderlyingPrices, ticker, out spot);
			if (!hasSpot || spot <= 0m) continue;
			var inputs = await AnalyzeRiskCommand.LoadOpenerScoringInputsAsync(ticker, asOf, includeCurrentBar: !past, intradayCache, cancellation);
			scoring[ticker] = new TickerScoring(spot, quotes, asOf, quoteAsOf, inputs, useMarketImpliedIv);
		}
		return scoring;
	}

	/// <summary>POP (fraction) and EV in dollars for the whole group, from the same probe analyze risk builds. Null when the ticker has no
	/// scoring, a leg has no quote, the legs differ in quantity (analyze risk refuses those), or the scorer can't score the structure.</summary>
	private static (decimal? Pop, decimal? Ev) Score(IReadOnlyList<ParsedLeg> legs, IReadOnlyDictionary<string, OptionContractQuote>? priceQuotes, TickerScoring? scoring, bool scoreAtCostBasis)
	{
		if (scoring == null || legs.Select(l => l.Quantity).Distinct().Count() != 1 || legs.Any(l => !scoring.Quotes.ContainsKey(l.Symbol))) return (null, null);
		var positionLegs = legs.Select(l => new AnalyzePositionCommand.PositionSnapshot(l.Symbol, l.Action, l.Quantity, AnalyzeCommon.ResolvePrice(l, priceQuotes), l.Option!)).ToList();
		var diagLegs = AnalyzeRiskCommand.BuildDiagnosticLegs(positionLegs, scoring.Quotes, scoring.Spot, scoring.AsOf, AnalyzeRiskSettings.DefaultIvPct);
		var score = AnalyzeRiskCommand.BuildProbe(diagLegs, scoring.Spot, scoring.AsOf, scoring.QuoteAsOf, scoring.Quotes, scoring.Inputs, AnalyzeRiskSettings.DefaultIvPct, scoreAtCostBasis, scoring.UseMarketImpliedIv).OpenerScore;
		return (score?.ProbabilityOfProfit, score?.ExpectedValuePerContract * legs[0].Quantity);
	}

	/// <summary>+1 for a leg that pays (buy), −1 for one that collects (sell): a positive net is a debit.</summary>
	private static decimal Sign(ParsedLeg leg) => leg.Action == LegAction.Buy ? 1m : -1m;

	/// <summary>Net per contract crossing the spread: buys at the ask, sells at the bid. Null when a leg lacks that side of the book.</summary>
	private static decimal? Natural(IReadOnlyList<ParsedLeg> legs, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (quotes == null) return null;
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (!quotes.TryGetValue(leg.Symbol, out var q)) return null;
			var price = leg.Action == LegAction.Buy ? q.Ask : q.Bid;
			if (price is not decimal p) return null;
			total += Sign(leg) * p * leg.Quantity;
		}
		return total / legs[0].Quantity;
	}

	/// <summary>Legs as "−760P 17 Nov +759P 27 Nov" (− sold, + bought), with a per-leg ratio only when quantities differ.</summary>
	private static string Describe(IReadOnlyList<ParsedLeg> legs)
	{
		var ratio = legs.Select(l => l.Quantity).Distinct().Count() > 1;
		return string.Join(" ", legs.Select(l => $"{(l.Action == LegAction.Buy ? "+" : "−")}{(ratio ? $"{l.Quantity}x" : "")}{Formatters.FormatQty(l.Option!.Strike)}{l.Option.CallPut} {l.Option.ExpiryDate.ToString("dd MMM", CultureInfo.InvariantCulture)}"));
	}

	internal static void Render(IAnsiConsole console, IReadOnlyList<TradeComparisonRow> rows, AnalysisOptions opts, bool ascii)
	{
		if (rows.Count == 0) return;
		var tickers = rows.Select(r => r.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var spotText = tickers.Count == 1 && SpotFor(tickers[0], opts) is decimal spot ? $", spot ${spot:N2}" : "";
		var table = new Table()
			.Border(ascii ? TableBorder.Ascii : TableBorder.Rounded)
			.BorderColor(Color.Cyan1)
			.Title($"[bold cyan]{Markup.Escape(string.Join("/", tickers))} Compare[/] [grey]— {rows.Count} structures{spotText}[/]");
		var ratios = rows.Any(r => r.Qty != rows[0].Qty);
		table.AddColumn("Structure");
		if (ratios) table.AddColumn(new TableColumn("Qty").RightAligned());
		foreach (var header in new[] { "Net", "Natural", "Margin", "Max profit", "Max loss", "Profit/margin", "Break-evens", "POP", "EV" })
			table.AddColumn(new TableColumn(header).RightAligned());

		foreach (var r in rows)
		{
			var cells = new List<string> { Markup.Escape(tickers.Count > 1 ? $"{r.Ticker} {r.Structure}" : r.Structure) };
			if (ratios) cells.Add(r.Qty.ToString(CultureInfo.InvariantCulture));
			cells.Add(SignedPrice(r.NetPerContract));
			cells.Add(r.NaturalPerContract is decimal n ? SignedPrice(n) : "—");
			cells.Add(r.Margin is decimal m ? Dollars(m) : "—");
			cells.Add(r.MaxProfit is decimal mp ? $"{Dollars(mp)}{(r.MaxProfitPrice is decimal at ? $" @ {at:0.##}" : "")}" : "Unlimited");
			cells.Add(r.MaxLoss is decimal ml ? PnlMarkup(-ml) : "Unlimited"); // BreakEvenResult.MaxLoss is a magnitude, as its panel prints it
			cells.Add(r.MaxProfit is decimal p && r.Margin is decimal mg && mg > 0m ? (p / mg).ToString("0.00", CultureInfo.InvariantCulture) : "—");
			cells.Add(r.BreakEvens.Count > 0 ? string.Join(" / ", r.BreakEvens.Select(b => b.ToString("0.00", CultureInfo.InvariantCulture))) : "—");
			cells.Add(r.ProbabilityOfProfit is decimal pop ? $"{pop * 100m:0.0}%" : "—");
			cells.Add(r.ExpectedValue is decimal ev ? PnlMarkup(ev) : "—");
			table.AddRow(cells.ToArray());
		}

		console.Write(table);
		console.MarkupLine("[dim]  Net / Natural: per contract (negative = credit); Net at the spec's prices, Natural buys at the ask and sells at the bid.[/]");
		console.MarkupLine("[dim]  Margin, Max profit/loss, Break-evens: each structure alone, at its first expiry, over its grid's price range (as its panel).[/]");
		console.MarkupLine("[dim]  POP / EV: the opener scorer (as `analyze risk` on that structure), horizon its first expiry; EV for the whole quantity; — = not scored (e.g. a credit diagonal).[/]");
		console.WriteLine();
	}

	private static decimal? SpotFor(string ticker, AnalysisOptions opts) =>
		opts.UnderlyingPriceOverrides != null && opts.UnderlyingPriceOverrides.TryGetValue(ticker, out var o) ? o
		: opts.UnderlyingPrices != null && ParsingHelpers.TryResolveForRoot(opts.UnderlyingPrices, ticker, out var s) ? s
		: null;

	private static string SignedPrice(decimal value) => value.ToString("0.00#", CultureInfo.InvariantCulture);

	private static string Dollars(decimal value) => value < 0m ? $"-${Math.Abs(value):N0}" : $"${value:N0}";

	private static string PnlMarkup(decimal value) => value < 0m ? $"[red]{Dollars(value)}[/]" : $"[green]{Dollars(value)}[/]";
}

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;

namespace WebullAnalytics;

class ResearchSettings : ReportSettings
{
	[Description("Hypothetical trades to include. Format: SYMBOL:PRICExQTY (positive=buy, negative=sell, qty optional, default 1). Comma-separated for multiple. Example: GME260501C00023000:0.50x300,GME260410C00023500:-0.30x155")]
	[CommandOption("--trades")]
	public string? Trades { get; set; }

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

				if (!decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price == 0)
					return ValidationResult.Error($"--trades: invalid price '{priceStr}' in entry '{entry}'");

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
			var maxSeq = trades.Count > 0 ? trades.Max(t => t.Seq) + 1 : 0;
			var baseTime = trades.Count > 0 ? trades.Max(t => t.Timestamp) : DateTime.Now;
			trades.AddRange(ParseSyntheticTrades(settings.Trades, maxSeq, baseTime));
		}

		return await ReportCommand.RunReportPipeline(settings, trades, feeLookup, cancellation);
	}

	internal static List<Trade> ParseSyntheticTrades(string tradesSpec, int startSeq, DateTime baseTimestamp)
	{
		var result = new List<Trade>();
		var seq = startSeq;

		foreach (var entry in tradesSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var colonIdx = entry.IndexOf(':');
			var symbol = entry[..colonIdx];
			var priceQty = entry[(colonIdx + 1)..];

			var xIdx = priceQty.IndexOf('x');
			var signedPrice = decimal.Parse(xIdx >= 0 ? priceQty[..xIdx] : priceQty, CultureInfo.InvariantCulture);
			var qty = xIdx >= 0 ? int.Parse(priceQty[(xIdx + 1)..]) : 1;

			var parsed = ParsingHelpers.ParseOptionSymbol(symbol)!;
			var side = signedPrice > 0 ? Side.Buy : Side.Sell;
			var price = Math.Abs(signedPrice);
			var timestamp = baseTimestamp.AddSeconds(seq - startSeq + 1);

			result.Add(new Trade(Seq: seq++, Timestamp: timestamp, Instrument: Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike), MatchKey: MatchKeys.Option(symbol), Asset: Asset.Option, OptionKind: ParsingHelpers.CallPutDisplayName(parsed.CallPut), Side: side, Qty: qty, Price: price, Multiplier: Trade.OptionMultiplier, Expiry: parsed.ExpiryDate));
		}

		return result;
	}
}

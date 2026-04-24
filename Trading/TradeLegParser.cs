using System.Globalization;

namespace WebullAnalytics.Trading;

/// <summary>
/// Represents a single leg after parsing the --trades syntax (ACTION:SYMBOL:QTY[@PRICE]).
/// Price is populated only for analyze-style inputs; trade command rejects any leg with a price.
/// </summary>
internal sealed record ParsedLeg(
	LegAction Action,
	string Symbol,
	int Quantity,
	OptionParsed? Option,
	decimal? Price,
	string? PriceKeyword);

internal enum LegAction { Buy, Sell }

internal static class TradeLegParser
{
	/// <summary>
	/// Parses a comma-separated list of legs.
	/// Each leg: ACTION:SYMBOL:QTY[@PRICE]
	///   ACTION = buy|sell (case-insensitive)
	///   SYMBOL = equity ticker or OCC option symbol
	///   QTY    = unsigned positive integer
	///   @PRICE = optional; decimal or one of BID|MID|ASK (case-insensitive)
	/// Throws FormatException with a readable message on any malformed input.
	/// </summary>
	internal static List<ParsedLeg> Parse(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
			throw new FormatException("Leg list is empty.");

		var results = new List<ParsedLeg>();
		var legs = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		for (int i = 0; i < legs.Length; i++)
		{
			try { results.Add(ParseLeg(legs[i])); }
			catch (FormatException ex) { throw new FormatException($"Leg {i + 1} ('{legs[i]}'): {ex.Message}"); }
		}
		return results;
	}

	private static ParsedLeg ParseLeg(string leg)
	{
		// Split off optional @PRICE first.
		string main; string? priceToken;
		var atIdx = leg.IndexOf('@');
		if (atIdx >= 0) { main = leg[..atIdx]; priceToken = leg[(atIdx + 1)..].Trim(); }
		else { main = leg; priceToken = null; }

		var parts = main.Split(':', StringSplitOptions.TrimEntries);
		if (parts.Length != 3)
			throw new FormatException("expected ACTION:SYMBOL:QTY");

		LegAction action = parts[0].ToLowerInvariant() switch
		{
			"buy" => LegAction.Buy,
			"sell" => LegAction.Sell,
			_ => throw new FormatException($"ACTION must be 'buy' or 'sell', got '{parts[0]}'")
		};

		var symbol = parts[1];
		if (string.IsNullOrWhiteSpace(symbol)) throw new FormatException("SYMBOL is empty");

		if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
			throw new FormatException($"QTY must be a positive integer, got '{parts[2]}'");

		var option = ParsingHelpers.ParseOptionSymbol(symbol); // null for equity

		decimal? price = null;
		string? keyword = null;
		if (priceToken != null)
		{
			var upper = priceToken.ToUpperInvariant();
			if (upper is "BID" or "MID" or "ASK") keyword = upper;
			else if (decimal.TryParse(priceToken, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) price = p;
			else throw new FormatException($"@PRICE must be decimal or BID|MID|ASK, got '{priceToken}'");
		}

		return new ParsedLeg(action, symbol, qty, option, price, keyword);
	}
}

using System.ComponentModel.DataAnnotations;

namespace WebullAnalytics;

public enum Side
{
	Buy,
	Sell,
	Expire
}

public enum Asset
{
	Stock,
	Option,
	[Display(Name = "Option Strategy")]
	OptionStrategy
}

// CsvHelper doesn't reliably populate positional records (primary-ctor records) via ClassMap.
// Use a POCO with settable properties instead.
public class RawTrade
{
	public required string Name { get; set; }
	public required string Symbol { get; set; }
	public Side Side { get; set; }
	public required string Status { get; set; }
	public int Filled { get; set; }
	public int Quantity { get; set; }
	public decimal? Price { get; set; }
	public decimal? AveragePrice { get; set; }
	public required string TimeInForce { get; set; }
	public DateTime PlacedTime { get; set; }
	public DateTime? FilledTime { get; set; }
}


/// <summary>
/// Represents a single trade (buy/sell/expire) of a stock, option, or strategy.
/// </summary>
/// <param name="Seq">Sequence number for ordering trades with the same timestamp</param>
/// <param name="Timestamp">When the trade was executed</param>
/// <param name="Instrument">Human-readable description (e.g., "GME 13 Feb 2026 $25")</param>
/// <param name="MatchKey">Unique key for matching opposite trades (e.g., "option:GME260213C00025000")</param>
/// <param name="Asset">Asset type: Stock, Option, or OptionStrategy</param>
/// <param name="OptionKind">For options: "Call"/"Put"; for strategies: "Calendar"/"Spread"/etc.</param>
/// <param name="Side">"Buy", "Sell", or "Expire"</param>
/// <param name="Qty">Number of shares or contracts</param>
/// <param name="Price">Price per share/contract</param>
/// <param name="Multiplier">Contract multiplier (100 for options, 1 for stocks)</param>
/// <param name="Expiry">Option expiration date, if applicable</param>
/// <param name="ParentStrategySeq">For strategy legs, the Seq of the parent strategy trade</param>
public record Trade
(
	int Seq,
	DateTime Timestamp,
	string Instrument,
	string MatchKey,
	Asset Asset,
	string OptionKind,
	Side Side,
	int Qty,
	decimal Price,
	decimal Multiplier,
	DateTime? Expiry = null,
	int? ParentStrategySeq = null
);

// CsvHelper doesn't reliably populate positional records (primary-ctor records) via ClassMap.
// Use a POCO with settable properties instead.
public class Fee
{
	public required string Symbol { get; set; }
	public DateTime DateTime { get; set; }
	public Side Side { get; set; }
	public int Quantity { get; set; }
	public decimal AveragePrice { get; set; }
	public decimal Amount { get; set; }
	public decimal Fees { get; set; }
}

/// <summary>
/// Represents a position lot for FIFO accounting.
/// Multiple lots can exist for the same position if acquired at different prices.
/// </summary>
public record Lot(
	Side Side,
	int Qty,
	decimal Price
);

/// <summary>
/// A row in the realized P&L report showing a single transaction.
/// </summary>
public record ReportRow(
	DateTime Timestamp,
	string Instrument,
	Asset Asset,
	string OptionKind,
	Side Side,
	int Qty,
	decimal Price,
	decimal ClosedQty,
	decimal Realized,
	decimal Running,
	decimal Cash,
	decimal Total,
	bool IsStrategyLeg = false,
	decimal Fees = 0m
);

/// <summary>
/// A row in the open positions table.
/// </summary>
/// <param name="InitialAvgPrice">Original average price before roll adjustments</param>
/// <param name="AdjustedAvgPrice">Price after applying credits from rolled short legs</param>
public record PositionRow(
	string Instrument,
	Asset Asset,
	string OptionKind,
	Side Side,
	int Qty,
	decimal AvgPrice,
	DateTime? Expiry,
	bool IsStrategyLeg = false,
	decimal? InitialAvgPrice = null,
	decimal? AdjustedAvgPrice = null
);

/// <summary>
/// Parsed components of an OCC option symbol.
/// </summary>
/// <param name="Root">Underlying symbol (e.g., "GME")</param>
/// <param name="ExpiryDate">Option expiration date</param>
/// <param name="CallPut">"C" for call, "P" for put</param>
/// <param name="Strike">Strike price</param>
public record OptionParsed(
	string Root,
	DateTime ExpiryDate,
	string CallPut,
	decimal Strike
);

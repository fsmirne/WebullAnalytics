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
	decimal? AdjustedAvgPrice = null,
	string? MatchKey = null
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

public record PricePnL(decimal UnderlyingPrice, decimal PnL);

/// <summary>
/// Break-even and P&L analysis for a single position or strategy at expiration.
/// </summary>
/// <param name="Title">e.g., "GME Long Call $25" or "GME Vertical Call $25/$30"</param>
/// <param name="Details">e.g., "2x @ $2.50 adj, Exp 13 Feb 2026"</param>
/// <param name="BreakEvens">Break-even prices (empty if can't be determined; may have 1 or 2 values)</param>
/// <param name="MaxProfit">null = unlimited</param>
/// <param name="MaxLoss">null = unlimited (stored as positive value, displayed with "-")</param>
/// <param name="DaysToExpiry">null for stocks; negative if expired</param>
/// <param name="Note">e.g., "Long leg (exp 15 May 2026) retains time value"</param>
/// <param name="Legs">Individual leg descriptions for strategies</param>
public record BreakEvenResult(
	string Title,
	string Details,
	int Qty,
	List<decimal> BreakEvens,
	decimal? MaxProfit,
	decimal? MaxLoss,
	int? DaysToExpiry,
	List<PricePnL> PriceLadder,
	string? Note,
	List<string>? Legs = null,
	List<PricePnL>? ChartData = null
);

using System;

namespace WebullAnalytics;

public record Trade(
    int Seq,
    DateTime Timestamp,
    string Instrument,
    string MatchKey,
    string Asset,
    string OptionKind,
    string Side,
    decimal Qty,
    decimal Price,
    decimal Multiplier,
    DateTime? Expiry = null,
    int? ParentStrategySeq = null
);

public record Lot(
    string Side,
    decimal Qty,
    decimal Price
);

public record ReportRow(
    DateTime Timestamp,
    string Instrument,
    string Asset,
    string OptionKind,
    string Side,
    decimal Qty,
    decimal Price,
    decimal ClosedQty,
    decimal Realized,
    decimal Running,
    bool IsStrategyLeg = false
);

public record PositionRow(
    string Instrument,
    string Asset,
    string OptionKind,
    string Side,
    decimal Qty,
    decimal AvgPrice,
    DateTime? Expiry,
    bool IsStrategyLeg = false,
    decimal? InitialAvgPrice = null,  // Original price before adjustments
    decimal? AdjustedAvgPrice = null  // Price after applying credits from rolls
);

public record OptionParsed(
    string Root,
    DateTime ExpiryDate,
    string CallPut,
    decimal Strike
);

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
    DateTime? Expiry = null
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
    decimal Running
);

public record PositionRow(
    string Instrument,
    string Asset,
    string OptionKind,
    string Side,
    decimal Qty,
    decimal AvgPrice,
    DateTime? Expiry
);

public record OptionParsed(
    string Root,
    DateTime ExpiryDate,
    string CallPut,
    decimal Strike
);

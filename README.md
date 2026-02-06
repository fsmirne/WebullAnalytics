# WebullAnalytics

A C# command-line tool for analyzing trading performance from Webull CSV order exports. This tool generates comprehensive realized P&L reports with support for stocks, options, and complex multi-leg option strategies.

## Features

- **FIFO Lot-Based Position Tracking**: Accurately tracks positions using First-In-First-Out lot accounting
- **Option Strategy Support**: Recognizes and properly handles multi-leg strategies including:
  - Calendar Spreads
  - Diagonals
  - Butterflies
  - Iron Condors
  - Straddles/Strangles
  - Vertical Spreads
- **Calendar Roll Tracking**: Intelligently groups rolled positions and tracks adjusted cost basis
- **Multiple Output Modes**:
  - Console: Color-coded tables with detailed transaction history
  - Excel: Formatted workbook with charts and analytics
  - Text: Plain text file for sharing or archiving
- **Transaction History**: Complete trade-by-trade P&L calculation
- **Open Position Analysis**: Shows both initial and adjusted average prices for rolled positions
- **Daily P&L Tracking**: Visual chart showing cumulative P&L over time

## Prerequisites

- .NET 10.0 SDK or later
- Windows, macOS, or Linux

## Installation

1. Clone or download this repository
2. Navigate to the `WebullAnalytics` directory
3. Build the project:
   ```bash
   dotnet build
   ```

## Usage

### Basic Usage

Run the tool with default settings (console output, all trades):

```bash
dotnet run
```

### Command-Line Options

```
Options:
  --data-dir <path>    Directory containing CSV order exports (default: data)
  --since <date>       Include only trades on or after this date in YYYY-MM-DD format (default: all trades)
  --output <format>    Output format: 'console', 'excel', or 'text' (default: console)
  --excel-path <path>  Path for Excel output file (default: WebullAnalytics_YYYYMMDD.xlsx)
  --text-path <path>   Path for text output file (default: WebullAnalytics_YYYYMMDD.txt)
  --help, -h           Show this help message
```

### Examples

**Console output with custom data directory:**
```bash
dotnet run -- --data-dir "C:\MyTrades\data"
```

**Export to Excel with default filename:**
```bash
dotnet run -- --output excel
```

**Export to Excel with custom path:**
```bash
dotnet run -- --output excel --excel-path "January2026_Report.xlsx"
```

**Filter trades since a specific date:**
```bash
dotnet run -- --since 2026-01-01 --output excel
```

**Export to text file:**
```bash
dotnet run -- --output text --text-path "January2026_Report.txt"
```

## Data Format

Place your Webull CSV order export files in the `data` directory (or specify a custom directory with `--data-dir`). The tool will automatically process all CSV files in the directory.

### Required CSV Columns

The tool expects Webull CSV exports with the following columns:
- Placed Time
- Filled Time
- Name
- Symbol
- Side
- Quantity
- Avg Fill Price

## Output Formats

### Console Output

The console output displays three sections:

1. **Realized P&L by Transaction**: Chronological list of all trades with:
   - Date and time
   - Instrument details
   - Option strategy legs (indented under parent strategy)
   - Side (Buy/Sell)
   - Quantity and price
   - Closed quantity
   - Realized P&L (color-coded: green for profit, red for loss)
   - Running total P&L

2. **Open Positions**: Current positions grouped by instrument showing:
   - Position details (asset, side, quantity)
   - Initial average price (original cost basis)
   - Adjusted average price (cost basis after calendar roll credits)
   - Expiration date
   - Calendar strategies are intelligently grouped with their legs

3. **Final Summary**: Total realized P&L

### Excel Output

The Excel workbook contains three worksheets:

1. **Transactions**: Complete transaction history with:
   - Color-coded P&L columns
   - Formatted currency values
   - All trade details

2. **Open Positions**: Current positions with:
   - Initial and adjusted prices
   - Strategy grouping
   - Expiration tracking

3. **Daily P&L**: Daily summary featuring:
   - Date-by-date P&L breakdown
   - Daily realized gains/losses
   - Cumulative P&L column
   - Line chart visualizing cumulative P&L over time

### Text Output

The text output produces a plain text file containing:
- ASCII-formatted tables matching the console output
- Transaction history with all trade details
- Open positions summary
- Final realized P&L

This format is useful for sharing reports via email, archiving, or importing into other tools.

## Position Tracking Details

### FIFO Lot Accounting

The tool uses First-In-First-Out (FIFO) lot accounting to match closing trades with opening trades. This ensures accurate P&L calculation even with multiple entries and exits in the same instrument.

### Calendar Strategy Recognition

For open positions, the tool intelligently groups option legs into calendar strategies when:
- Multiple legs share the same root symbol, strike price, and call/put type
- The legs have different expiration dates
- The legs have opposite sides (one long, one short)

### Adjusted Cost Basis

For long legs in calendar strategies, the tool tracks:
- **Initial Average Price**: The original cost paid to open the position
- **Adjusted Average Price**: The cost basis after subtracting credits from fully closed short legs (calendar rolls)

This helps traders understand their true cost basis after collecting credits from rolling short legs.

## Strategy Leg Display

Multi-leg option strategies are displayed with their parent strategy and individual legs:

```
2026-01-29  GME Calendar   Option Strategy  Buy   30   2.12   0   +0.00   +0.00
            └─ GME Call    Option           Buy   30   2.64   -     -
            └─ GME Call    Option           Sell  30   0.52   -     -
```

The parent strategy shows the net debit/credit and contributes to P&L calculation, while individual legs are shown for reference and position tracking.

## Dependencies

- **CsvHelper** (33.1.0): CSV parsing
- **Spectre.Console** (0.54.0): Console formatting and tables
- **EPPlus** (7.6.0): Excel file generation

## License

This tool uses EPPlus configured for non-commercial use. For commercial use, you must obtain an appropriate EPPlus license.

## Troubleshooting

**No trades found:**
- Ensure CSV files are in the data directory
- Verify CSV files are Webull export format

**Excel export fails:**
- Ensure the output directory is writable
- Check that no other program has the Excel file open

**Incorrect P&L calculations:**
- If using `--since`, verify the date includes all relevant trades
- Check that all trade CSV files are in the data directory

## Future Enhancements

Potential features for future versions:
- Support for other broker CSV formats
- Unrealized P&L calculation with market data
- Tax reporting (wash sales, short-term vs long-term gains)
- Performance metrics (win rate, average gain/loss, etc.)
- Multiple account support

## Contributing

Contributions are welcome! Please ensure any changes maintain accurate FIFO lot accounting and properly handle multi-leg option strategies.

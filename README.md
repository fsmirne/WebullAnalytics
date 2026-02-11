# WebullAnalytics

A C# command-line tool for analyzing trading performance from Webull CSV order exports. Generates comprehensive realized P&L reports with support for stocks, options, and complex multi-leg option strategies.

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
- **Fee Tracking**: Optional fee CSV import for accurate net P&L after commissions and fees
- **Cash Tracking**: Tracks current cash in hand starting from an optional initial amount
- **Multiple Output Modes**:
  - Console: Color-coded tables with detailed transaction history
  - Excel: Formatted workbook with charts and analytics
  - Text: Plain text file for sharing or archiving
- **Transaction History**: Complete trade-by-trade P&L calculation with fees, cash, and running totals
- **Open Position Analysis**: Shows both initial and adjusted average prices for rolled positions
- **Daily P&L Tracking**: Visual chart showing cumulative P&L over time

## Prerequisites

- .NET 10.0 SDK or later
- Windows, macOS, or Linux

## Installation

### From Source

1. Clone or download this repository
2. Navigate to the `WebullAnalytics` directory
3. Build the project:
   ```bash
   dotnet build
   ```

### Windows Installer

Run `install.bat` to build a self-contained executable and add it to your PATH:

```batch
install.bat
```

By default this installs to `%LOCALAPPDATA%\WebullAnalytics`. You can specify a custom directory:

```batch
install.bat "C:\MyTools"
```

Alternatively, use `build.bat` to just build the executable without installing:

```batch
build.bat
```

The output will be in `bin\Release\net10.0\win-x64\publish\`.

## Usage

### Basic Usage

```bash
WebullAnalytics --data-trades path/to/orders.csv
```

### Command-Line Options

```
Options:
  --data-trades <path>      Path to a CSV order export file (required)
  --data-fees <path>        Path to a CSV file containing fee information per trade
  --since <date>            Include only trades on or after this date (YYYY-MM-DD format)
  --output <format>         Output format: 'console', 'excel', or 'text' (default: console)
  --excel-path <path>       Path for Excel output file (default: WebullAnalytics_YYYYMMDD.xlsx)
  --text-path <path>        Path for text output file (default: WebullAnalytics_YYYYMMDD.txt)
  --initial-amount <amount> Initial portfolio amount in dollars (default: 0)
  --help, -h                Show help message
```

### Examples

**Console output from a trades file:**
```bash
WebullAnalytics --data-trades "C:\MyTrades\Options_Orders.csv"
```

**Include fee data for accurate net P&L:**
```bash
WebullAnalytics --data-trades orders.csv --data-fees fees.csv
```

**Set an initial portfolio amount to track cash:**
```bash
WebullAnalytics --data-trades orders.csv --initial-amount 10000
```

**Export to Excel with default filename:**
```bash
WebullAnalytics --data-trades orders.csv --output excel
```

**Export to Excel with custom path:**
```bash
WebullAnalytics --data-trades orders.csv --output excel --excel-path "January2026_Report.xlsx"
```

**Filter trades since a specific date:**
```bash
WebullAnalytics --data-trades orders.csv --since 2026-01-01 --output excel
```

**Export to text file:**
```bash
WebullAnalytics --data-trades orders.csv --output text --text-path "January2026_Report.txt"
```

**Combine all options:**
```bash
WebullAnalytics --data-trades orders.csv --data-fees fees.csv --initial-amount 10000 --since 2026-01-01 --output excel
```

## Data Format

### Trades CSV

Provide your Webull CSV order export file via the `--data-trades` option. The tool determines whether the file contains stock or option orders based on the filename (files with "Options" in the name are parsed as option orders).

Required columns:
- **Side** - Buy or Sell
- **Filled** - Filled quantity
- **Avg Price** (or **Price**) - Average fill price
- **Filled Time** (or **Placed Time**) - Timestamp of the fill
- **Status** - Must be "Filled" (non-filled orders are skipped)
- **Symbol** - Ticker symbol (for stocks) or OCC option symbol (for options)
- **Name** - Strategy name (for multi-leg option strategies)
- **Placed Time** - Used to associate strategy legs with their parent order

### Fee CSV (Optional)

Provide a separate fee CSV via `--data-fees` to include trading fees and commissions in the P&L report. Expected columns:
- **Time** - Timestamp matching the trade
- **Side** - Buy or Sell
- **Quantity** - Trade quantity
- **Avg Price** - Trade price
- **Fees** - Fee amount for the trade

Fees are matched to trades by timestamp, side and quantity. For multi-leg strategies, fees from individual legs are summed under the parent strategy.

## Output Formats

### Console Output

The console output displays:

1. **Realized P&L by Transaction**: Chronological list of all trades with:
   - Date and time
   - Instrument details
   - Option strategy legs (indented under parent strategy)
   - Side (Buy/Sell/Expire)
   - Quantity and price
   - Fees
   - Closed quantity
   - Realized P&L (color-coded: green for profit, red for loss)
   - Running total P&L
   - Cash balance
   - Total portfolio value

2. **Open Positions**: Current positions grouped by instrument showing:
   - Position details (asset, side, quantity)
   - Initial average price (original cost basis)
   - Adjusted average price (cost basis after calendar roll credits)
   - Expiration date
   - Calendar strategies are intelligently grouped with their legs

3. **Final Summary**: Total fees, final realized P&L, and final portfolio amount

### Excel Output

The Excel workbook contains three worksheets:

1. **Transactions**: Complete transaction history with:
   - Color-coded P&L columns
   - Formatted currency values
   - Fees column
   - Cash and Total columns
   - Summary row with total fees, final P&L, and final amount

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
- Total fees, final realized P&L, and final amount

This format is useful for sharing reports via email, archiving, or importing into other tools.

## Position Tracking Details

### FIFO Lot Accounting

The tool uses First-In-First-Out (FIFO) lot accounting to match closing trades with opening trades. This ensures accurate P&L calculation even with multiple entries and exits in the same instrument.

### Option Expiration

Options that expire within the reporting date range are automatically handled. Long positions expire worthless (loss), and short positions keep the full premium (gain). Synthetic expiration trades are generated at market close on the expiration date.

### Calendar Strategy Recognition

For open positions, the tool intelligently groups option legs into calendar strategies when:
- Multiple legs share the same root symbol, strike price, and call/put type
- The legs have different expiration dates
- The legs have opposite sides (one long, one short)

Partial rolls are handled by splitting quantities into separate calendar groups when leg sizes don't match.

### Adjusted Cost Basis

For long legs in calendar strategies, the tool tracks:
- **Initial Average Price**: The original cost paid to open the position
- **Adjusted Average Price**: The cost basis after subtracting credits from fully closed short legs (calendar rolls)

This helps traders understand their true cost basis after collecting credits from rolling short legs.

### Cash Tracking

When `--initial-amount` is specified, the tool tracks the cash balance throughout the report:
- Buys reduce cash by `quantity * price * multiplier`
- Sells increase cash by `quantity * price * multiplier`
- Fees reduce cash
- The Total column shows `initial amount + running P&L`

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
- **Spectre.Console.Cli** (0.53.1): Command-line argument parsing with validation
- **EPPlus** (7.6.0): Excel file generation

## License

This tool uses EPPlus configured for non-commercial use. For commercial use, you must obtain an appropriate EPPlus license.

## Troubleshooting

**No trades found:**
- Verify the CSV file path passed to `--data-trades` is correct
- Ensure the CSV file is a Webull order export with the required columns
- Check that orders have a "Filled" status

**Fee matching issues:**
- Ensure the fee CSV timestamps, sides, quantities, and prices exactly match the trades CSV
- Fees with no matching trade are silently ignored

**Excel export fails:**
- Ensure the output directory is writable
- Check that no other program has the Excel file open

**Incorrect P&L calculations:**
- If using `--since`, note that only trades on or after this date are included; earlier context is not considered
- For options files, ensure the filename contains "Options" so they are parsed correctly

**Invalid option errors:**
- The tool uses strict parsing; any unrecognized command-line options will produce an error

## Future Enhancements

Potential features for future versions:
- Support for other broker CSV formats
- Unrealized P&L calculation with market data
- Tax reporting (wash sales, short-term vs long-term gains)
- Performance metrics (win rate, average gain/loss, etc.)
- Multiple account support

## Contributing

Contributions are welcome! Please ensure any changes maintain accurate FIFO lot accounting and properly handle multi-leg option strategies.

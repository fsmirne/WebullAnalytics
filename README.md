# WebullAnalytics

A C# command-line tool for analyzing trading performance from Webull order data. Generates comprehensive realized P&L reports with support for stocks, options, and complex multi-leg option strategies.

## Features

- **Webull API Integration**: Fetch order data directly from the Webull API
- **FIFO Lot-Based Position Tracking**: Accurately tracks positions using First-In-First-Out lot accounting
- **Option Strategy Support**: Recognizes and properly handles multi-leg strategies including:
  - Calendar Spreads
  - Diagonals
  - Butterflies
  - Iron Condors
  - Straddles/Strangles
  - Vertical Spreads
- **Calendar Roll Tracking**: Intelligently groups rolled positions and tracks adjusted cost basis
- **Fee Tracking**: Commissions and fees embedded in the order data
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

### Commands

WebullAnalytics has two commands: `report` (generate a P&L report) and `fetch` (download order data from the Webull API).

### Report Command

```bash
# Generate a report using default data/orders.jsonl
WebullAnalytics report

# Fetch fresh data from the API, then generate the report
WebullAnalytics report --fetch

# Use Webull CSV exports as the source of truth (fees from JSONL if available)
WebullAnalytics report --source export

# Filter trades since a specific date
WebullAnalytics report --since 2026-01-01

# Export to Excel
WebullAnalytics report --output excel

# Set an initial portfolio amount to track cash
WebullAnalytics report --initial-amount 10000

# Combine options
WebullAnalytics report --fetch --since 2026-01-01 --output excel --initial-amount 10000
```

#### Report Options

```
Options:
  --source <source>         Data source: 'api' or 'export' (default: api)
  --data-orders <path>      Path to the JSONL orders file (default: data/orders.jsonl)
  --fetch                   Fetch orders from the Webull API before generating the report
  --config <path>           Path to the API config JSON file, used with --fetch (default: data/api-config.json)
  --since <date>            Include only trades on or after this date (YYYY-MM-DD format)
  --output <format>         Output format: 'console', 'excel', or 'text' (default: console)
  --excel-path <path>       Path for Excel output file (default: WebullAnalytics_YYYYMMDD.xlsx)
  --text-path <path>        Path for text output file (default: WebullAnalytics_YYYYMMDD.txt)
  --initial-amount <amount> Initial portfolio amount in dollars (default: 0)
  --help, -h                Show help message
```

### Fetch Command

```bash
# Fetch order data using default paths
WebullAnalytics fetch

# Custom config and output paths
WebullAnalytics fetch --config my-config.json --output my-orders.jsonl
```

#### Fetch Options

```
Options:
  --config <path>   Path to the API config JSON file (default: data/api-config.json)
  --output <path>   Output path for the JSONL orders file (default: data/orders.jsonl)
  --help, -h        Show help message
```

## Data Sources

The `--source` option controls which data source provides the trades:

- **`api`** (default): Uses the JSONL orders file as the primary source. Trades, fees, and commissions all come from the API data. If Webull CSV exports are present in the same directory, their prices are used to correct sub-penny rounding in strategy parent prices.

- **`export`**: Uses Webull CSV export files as the primary source. This gives exact prices matching Webull's accounting. If an `orders.jsonl` file exists in the same directory, its fee data is used to populate the fees column (CSV exports don't include fees).

### JSONL Orders File

A JSONL file where each line is a JSON object containing an `orderList` array for one ticker. This file is produced by the `fetch` command or can be manually exported.

Each order includes the symbol, fill price, fill time, action (buy/sell), quantity, fees, and commission. Orders sharing the same `transactTime` are treated as legs of a single strategy.

### Webull CSV Exports (Optional Price Override)

If Webull CSV export files are present in the same directory as the JSONL file, their prices are automatically used to override the JSONL-computed values. This corrects sub-penny rounding differences in strategy parent prices that the API doesn't preserve.

The recognized CSV files are:
- `Webull_Orders_Records.csv`
- `Webull_Orders_Records_Bonds.csv`
- `Webull_Orders_Records_Options.csv`

No configuration is needed. If the files exist, they're used; if not, the JSONL data is sufficient on its own.

## API Configuration

The `fetch` command and the `--fetch` flag on the report command require an API config file. This file contains your Webull session credentials and account information.

### Setup

1. Copy the example config:
   ```bash
   cp api-config.example.json data/api-config.json
   ```

2. Open your browser, log into [app.webull.com](https://app.webull.com/), and navigate to the P&L section.

3. Open browser DevTools (F12), go to the Network tab, and look for requests to `profitloss/ticker/orderList`. Copy the header values from the request into your config file.

### Config File Format

```json
{
  "secAccountId": "YOUR_ACCOUNT_ID",
  "tickerIds": [123456789, 987654321],
  "startDate": "2026-01-01",
  "endDate": "2026-12-31",
  "limit": 10000,
  "headers": {
    "access_token": "dc_us_tech1.xxxxxxxxx-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "did": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "lzone": "dc_core_r001",
    "osv": "xxxx",
    "ph": "Windows Edge",
    "t_time": "1234567890123",
    "t_token": "xxxxxxxxxxxxx-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "tz": "America/New_York",
    "ver": "6.3.1",
    "x-s": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "x-sv": "xxxxxxxx"
  }
}
```

### Config Fields

| Field | Description |
|---|---|
| `secAccountId` | Your Webull securities account ID (visible in the API request URL) |
| `tickerIds` | Array of Webull ticker IDs to fetch. Each ticker produces one line in the JSONL output. Find these in the `tickerId` query parameter of the API requests in your browser's Network tab. |
| `startDate` | Start of the date range to fetch (YYYY-MM-DD) |
| `endDate` | End of the date range to fetch (YYYY-MM-DD) |
| `limit` | Maximum number of orders to return per ticker (default: 10000) |
| `headers` | Authentication and session headers copied from your browser. These are session-specific and will expire. |

### Session Tokens

The `access_token`, `t_token`, `x-s`, and `x-sv` headers are session tokens that expire. When they expire, the API will return an error. To refresh them, log into Webull in your browser again and copy the updated values from the Network tab.

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

**API fetch fails:**
- Verify your `data/api-config.json` has valid session tokens
- Session tokens expire; log into Webull in your browser and copy fresh values from the Network tab
- The `x-s` header may be request-specific; try copying it from a recent request

**No trades found:**
- Verify the JSONL file exists at the expected path (default: `data/orders.jsonl`)
- Run `WebullAnalytics fetch` to download fresh data
- Check that the `tickerIds` in your config cover all tickers you trade

**Incorrect P&L calculations:**
- If using `--since`, note that only trades on or after this date are included; earlier context is not considered
- Place Webull CSV exports in the same directory as the JSONL file for exact price matching

**Excel export fails:**
- Ensure the output directory is writable
- Check that no other program has the Excel file open

**Invalid option errors:**
- The tool uses strict parsing; any unrecognized command-line options will produce an error

## Contributing

Contributions are welcome! Please ensure any changes maintain accurate FIFO lot accounting and properly handle multi-leg option strategies.

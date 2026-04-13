# WebullAnalytics

A C# command-line tool for analyzing trading performance from Webull order data. Generates comprehensive realized P&L reports with support for stocks, options, and complex multi-leg option strategies.

## Features

- **Webull API Integration**: Fetch order data directly from the Webull API
- **Position Tracking**: FIFO lot accounting for P&L calculations, average cost method for position display (matching Webull)
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
- **Time-Decay Grid**: 2D grid showing option values across dates and underlying prices, visualizing how time decay and price movement affect position value

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

WebullAnalytics has four commands: `report` (generate a P&L report), `research` (hypothetical what-if analysis), `fetch` (download order data from the Webull API), and `sniff` (automatically capture fresh API session headers).

### Report Command

```bash
# Generate a report using default data/orders.jsonl
WebullAnalytics report

# Use Webull CSV exports as the source of truth (fees from JSONL if available)
WebullAnalytics report --source export

# Filter trades since a specific date
WebullAnalytics report --since 2026-01-01

# Filter trades until a specific date
WebullAnalytics report --until 2026-03-31

# Filter trades within a date range
WebullAnalytics report --since 2026-01-01 --until 2026-03-31

# Export to Excel
WebullAnalytics report --output excel

# Set an initial portfolio amount to track cash
WebullAnalytics report --initial-amount 10000

# Combine options
WebullAnalytics report --since 2026-01-01 --output excel --initial-amount 10000

# Fetch option chain data for break-even analysis with time-decay grids (Yahoo Finance)
WebullAnalytics report --api yahoo

# Use Webull option chain data (requires sniffed headers via 'sniff' command)
WebullAnalytics report --api webull

# Override implied volatility for specific option legs (per OCC symbol)
WebullAnalytics report --iv GME260213C00025000:50,GME260516C00025000:45

# Combine API data with manual IV overrides (overrides take priority)
WebullAnalytics report --api yahoo --iv GME260213C00025000:60

# Show P&L instead of contract value in the grid
WebullAnalytics report --api yahoo --display pnl

# Show each leg's contract value alongside the net in every grid cell
WebullAnalytics report --api yahoo --grid verbose

# Increase grid granularity (more rows between strikes, default: 2)
WebullAnalytics report --api yahoo --range 4

# Override the current underlying price (for "what-if" evaluation)
WebullAnalytics report --api yahoo --current-underlying-price GME:24.88,SPY:580.50

# Use Black-Scholes theoretical prices instead of market mid for today's grid column
WebullAnalytics report --api yahoo --theoretical

# Add custom notable prices to break-even reports (e.g., support/resistance levels)
WebullAnalytics report --notable-prices GME:20/25/30

# Show only specific tickers in the report
WebullAnalytics report --tickers GME,SPY
```

#### Report Options

```
Options:
  --source <source>         Data source: 'api' or 'export' (default: api)
  --since <date>            Include only trades on or after this date (YYYY-MM-DD format)
  --until <date>            Include only trades on or before this date (YYYY-MM-DD format)
  --output <format>         Output format: 'console', 'excel', or 'text' (default: console)
  --output-path <path>      Path for output file, used with --output excel or text (default: WebullAnalytics_YYYYMMDD.xlsx/.txt)
  --initial-amount <amount> Initial portfolio amount in dollars (default: 0)
  --view <view>             Report view: 'detailed' or 'simplified' (default: detailed)
  --iv <overrides>          Override implied volatility per leg. Format: SYMBOL:IV% (e.g., GME260213C00025000:50). Comma-separated for multiple.
  --api <source>            Option chain data source for break-even analysis: 'yahoo' or 'webull' (webull requires sniffed headers)
  --range <granularity>     Grid granularity: rows per strike gap in the time-decay grid (default: 2, higher = more rows)
  --display <mode>          Grid display mode: 'value' (contract value, default) or 'pnl' (profit/loss)
  --grid <layout>           Grid cell layout: 'simple' (net only, default) or 'verbose' (per-leg values '1.23|0.45|$0.78')
  --current-underlying-price <prices>  Override underlying price(s). Format: TICKER:PRICE (e.g., GME:24.88,SPY:580.50)
  --theoretical             Use Black-Scholes theoretical price instead of market mid for today's grid column
  --notable-prices <prices> Additional prices to show in break-even reports. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30,SPY:580/590)
  --tickers <list>          Show only these tickers in the report. Comma-separated (e.g., GME,SPY,AAPL)
  --help, -h                Show help message
```

### Research Command

The `research` command runs a hypothetical what-if analysis by injecting synthetic trades into the report pipeline without modifying any data files. It accepts all the same options as `report` plus a `--trades` option.

#### Trade Format

```
--trades "SYMBOL:PRICExQTY,SYMBOL:PRICExQTY,..."
```

- **SYMBOL**: OCC option symbol (e.g., `GME260501C00023000`)
- **PRICE**: Signed value — positive = sell/credit, negative = buy/debit. Can be:
  - A decimal number (e.g., `0.50`, `-0.38`)
  - A market price keyword: `BID`, `MID`, or `ASK` (requires `--api`)
- **QTY**: Optional quantity after `x` (default: 1)

#### Examples

```bash
# What if I roll the calendar short from Apr 10 to Apr 17 for $0.24 credit?
WebullAnalytics research --trades "GME260410C00023000:0.14x300,GME260417C00023000:-0.38x300"

# Same roll but use live market prices (buy at bid, sell at ask)
WebullAnalytics research --trades "GME260410C00023000:BIDx300,GME260417C00023000:-ASKx300"

# Use mid-market prices for both legs
WebullAnalytics research --trades "GME260410C00023000:MIDx300,GME260417C00023000:-MIDx300"

# What if I close 100 contracts of my long call?
WebullAnalytics research --trades "GME260501C00023000:-0.70x100"

# What if I add a protective put?
WebullAnalytics research --trades "GME260501P00022000:0.25x455"

# Simulate running on a future date (e.g., after short leg expiration)
WebullAnalytics research --trades "GME260417C00023000:-0.38x300" --date 2026-04-11

# Combine with report options (output to text, override underlying price)
WebullAnalytics research --trades "GME260410C00023000:0.14x300,GME260417C00023000:-0.38x300" --output text --current-underlying-price GME:23.20
```

When using `BID`, `MID`, or `ASK`, the command fetches live quotes from the configured API source (`--api webull` or `--api yahoo`) before building the hypothetical trades.

The synthetic trades are appended after all real trades and processed through the full report pipeline — FIFO matching, strategy grouping, break-even analysis, and rendering all work normally. The original trade files are never modified.

#### Roll Analysis

The `--roll` option computes the theoretical roll credit at various underlying prices using Black-Scholes, helping you find the optimal moment to roll a short leg.

```bash
# Analyze rolling the $23 short from Apr 10 to Apr 17 (300 contracts)
WebullAnalytics research --roll "GME260410C00023000>GME260417C00023000x300"

# Roll to a different strike
WebullAnalytics research --roll "GME260410C00023000>GME260417C00023500x300"

# Override IV for the analysis
WebullAnalytics research --roll "GME260410C00023000>GME260417C00023000x300" --iv GME260410C00023000:37,GME260417C00023000:31
```

The output is a 2D grid of roll credits across underlying prices (rows) and times (columns). For intraday scenarios (0–1 DTE), columns are hourly from 9:30 AM to 4 PM. For multi-day scenarios, columns are daily, adapting to terminal width. Each cell shows `Close|Open|Net` per contract (leg values in grey, net color-coded green for credit / red for debit). The current-price row is rendered in **bold yellow**, the best-credit cell (globally) in **bold underline green**, and any row whose max credit matches the global best in **green**. Live market credit from bid/ask quotes is shown below the grid.

Notable prices from `--notable-prices` are included as additional rows in the grid.

#### Research Options

All `report` options are available, plus:

```
  --trades <trades>       Hypothetical trades. Format: SYMBOL:PRICExQTY (positive=sell/credit, negative=buy/debit, qty defaults to 1).
                          Price can be a number or BID/MID/ASK (requires --api). Comma-separated for multiple.
  --roll <roll>           Analyze a roll: shows credit/debit at various underlying prices and times using Black-Scholes.
                          Format: OLD_SYMBOL>NEW_SYMBOLxQTY. Requires --api.
  --date <date>           Override 'today' for evaluation (YYYY-MM-DD). Simulates running on a future date — options expiring
                          on or before this date generate synthetic expirations, and all DTE/Black-Scholes calculations use it.
```

### Fetch Command

```bash
# Fetch order data from the Webull API
WebullAnalytics fetch
```

Reads API credentials from `data/api-config.json` and writes orders to `data/orders.jsonl`.

### Sniff Command

```bash
# Automatically capture fresh API session headers
WebullAnalytics sniff
```

Launches Microsoft Edge with remote debugging, navigates to Webull, enters your unlock PIN, and captures the API session headers from the network traffic. The captured headers are written directly into `data/api-config.json`, replacing the existing `headers` object.

**Requirements:**
- Microsoft Edge must be installed
- Edge will be closed if running (prompts for confirmation)
- The `pin` field must be set in `data/api-config.json`

**Configuration** (in `config.json` under `"sniff"`):

| Field | Description |
|---|---|
| `autoCloseEdge` | If `true`, closes Edge without prompting (default: `false`) |

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

The `fetch` command requires an API config file. This file contains your Webull session credentials and account information.

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
  "tickers": ["GME", "SPY"],
  "startDate": "2026-01-01",
  "endDate": "2026-12-31",
  "limit": 10000,
  "pin": "YOUR_6_DIGIT_UNLOCK_CODE",
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
| `tickers` | Array of ticker symbols to fetch (e.g., `["GME", "SPY"]`). Each ticker produces one line in the JSONL output. |
| `startDate` | Start of the date range to fetch (YYYY-MM-DD) |
| `endDate` | End of the date range to fetch (YYYY-MM-DD) |
| `limit` | Maximum number of orders to return per ticker (default: 10000) |
| `pin` | Your 6-digit Webull unlock code. Required by the `sniff` command to automate header capture. Not used by `fetch`. |
| `headers` | Authentication and session headers copied from your browser or captured by `sniff`. These are session-specific and will expire. |

### Session Tokens

The `access_token`, `t_token`, `x-s`, and `x-sv` headers are session tokens that expire. When they expire, the API will return an error. To refresh them, either run `WebullAnalytics sniff` to capture fresh headers automatically, or log into Webull in your browser and copy the updated values from the Network tab manually.

## Application Configuration

An optional `data/config.json` file provides default values for command-line options. CLI arguments always take precedence over config values.

```json
{
  "autoExpandTerminal": false,
  "report": {
    "source": "api",
    "since": null,
    "until": null,
    "output": "console",
    "outputPath": null,
    "initialAmount": 0,
    "view": "detailed",
    "currentUnderlyingPrice": null,
    "iv": null,
    "api": "yahoo",
    "range": 2,
    "display": "value",
    "grid": "simple",
    "theoretical": false,
    "notablePrices": null,
    "tickers": null
  },
  "sniff": {
    "autoCloseEdge": false
  }
}
```

Copy and customize from `config.example.json`. The `report` section maps directly to the report/research command options. The `autoExpandTerminal` flag, when `true`, automatically resizes the terminal to fit wide tables.

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
   - Initial average price (average cost method, matching Webull's display)
   - Adjusted average price (break-even price accounting for all cash flows including rolls)
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

## Time-Decay Grid

When implied volatility is available (via `--api` or `--iv` overrides), the break-even panel for each option position includes a 2D time-decay grid showing how the position value changes across underlying prices (rows) and dates (columns).

- **Date columns**: Evenly-spaced dates from today through expiration, evaluated at market open (9:30 AM). The last two columns show expiration day at market open (with remaining intraday time value via Black-Scholes) and "At Exp" at market close (4:30 PM, intrinsic value only). The number of columns adapts to the terminal width — dates are skipped as needed so the grid never overflows horizontally.
- **Price rows**: Step size is derived from the position's strike spacing (or strike-to-break-even distance for single-strike positions like calendars), so the grid adapts to any stock price. The `--range` parameter controls granularity — it sets how many rows fit per strike gap (default: 2). Break-even prices (marked with `*`) and strike prices are always included, with 2 padding rows beyond the outermost notable price. The current underlying-price row is marked with `>` and rendered in bold yellow.
- **Display modes**: `--display value` (default) shows the contract value per share; `--display pnl` shows total P&L.
- **Cell layout**: `--grid simple` (default) shows one value per cell (the net). `--grid verbose` prefixes each cell with the per-share Black-Scholes contract value of every leg, separated by `|` — e.g. `1.23|0.45|$0.78` for a two-leg spread. Leg order matches the leg descriptions shown above the grid, and a legend below repeats the order (e.g. `LC23|SC23.5|Net`).
- **Cell colors**: Green for profit, red for loss. Legs in verbose mode render in grey.
- **Calendar/diagonal spreads**: The grid ends at the short leg's expiration. The long leg's remaining time value is reflected via Black-Scholes pricing at each date, including the "At Exp" column.

Without implied volatility data, the existing 1D price ladder (Price | Value | P&L at expiration) is shown instead.

## Volatility Analysis

When using the Webull data source (`--api webull`), the break-even leg descriptions include volatility metrics for each option contract:

```
└─ ... | IV 32.3% | HV 43.0% | IV5 40.0%
```

| Metric | Description |
|--------|-------------|
| **IV** | Current implied volatility — the market's expectation of future movement priced into the option |
| **HV** | Historical (realized) volatility — how much the underlying has actually been moving |
| **IV5** | 5-day implied volatility — short-term IV trend |

### Pricing Signal

The IV value is color-coded based on the IV/HV ratio (volatility risk premium):

| IV Value Color | Condition | Meaning |
|----------------|-----------|---------|
| Green | IV/HV < 0.90 | Options are priced below realized movement — favors buying |
| Red | IV/HV > 1.10 | Options are priced above realized movement — favors selling |
| White | 0.90 ≤ IV/HV ≤ 1.10 | Options are fairly priced relative to realized movement |

The IV5 metric provides additional context: if IV5 is rising toward HV, a cheap signal may be closing; if IV5 is falling away from HV, a rich signal may be strengthening.

These metrics are sourced from the Webull option chain API and are not available when using Yahoo Finance (`--api yahoo`).

## Position Tracking Details

### FIFO Lot Accounting

The tool uses First-In-First-Out (FIFO) lot accounting to match closing trades with opening trades for realized P&L calculations. For open position display, the average cost method is used instead — this matches Webull's position cost basis and means the displayed average price doesn't change when you partially close a position.

### Option Expiration

Options that expire within the reporting date range are automatically handled. Long positions expire worthless (loss), and short positions keep the full premium (gain). Synthetic expiration trades are generated at market close on the expiration date.

### Calendar Strategy Recognition

For open positions, the tool intelligently groups option legs into calendar strategies when:
- Multiple legs share the same root symbol, strike price, and call/put type
- The legs have different expiration dates
- The legs have opposite sides (one long, one short)

Partial rolls are handled by splitting quantities into separate calendar groups when leg sizes don't match.

### Adjusted Cost Basis

For strategies with rolled legs, the tool tracks:
- **Initial Average Price**: The average cost of the current position (average cost method, matching Webull)
- **Adjusted Average Price**: The exact break-even price computed from total cash flows across all related strategy trades, including rolls. Selling the position at this price recovers the total net debit invested.

At the per-leg level, the long leg's adjusted price reflects the full rolling history, while the short leg's adjusted price remains at its average cost.

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
- Session tokens expire; run `WebullAnalytics sniff` to capture fresh headers, or log into Webull in your browser and copy fresh values from the Network tab
- The `x-s` header may be request-specific; try copying it from a recent request

**No trades found:**
- Verify the JSONL file exists at the expected path (default: `data/orders.jsonl`)
- Run `WebullAnalytics fetch` to download fresh data
- Check that the `tickers` in your config cover all tickers you trade

**Incorrect P&L calculations:**
- If using `--since` or `--until`, note that only trades within the specified date range are included; earlier or later context is not considered
- Place Webull CSV exports in the same directory as the JSONL file for exact price matching

**Excel export fails:**
- Ensure the output directory is writable
- Check that no other program has the Excel file open

**Invalid option errors:**
- The tool uses strict parsing; any unrecognized command-line options will produce an error

## Contributing

Contributions are welcome! Please ensure any changes maintain accurate FIFO lot accounting for P&L, average cost for position display, and properly handle multi-leg option strategies.

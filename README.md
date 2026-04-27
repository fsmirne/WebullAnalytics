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

### Linux Installer

Run `install.sh` to build a self-contained executable and add it to your PATH:

```bash
./install.sh
```

By default this installs to `~/.local/bin`. You can specify a custom directory:

```bash
./install.sh /opt/webull-analytics
```

## Usage

### Commands

wa has five commands: `report` (generate a P&L report), `analyze` (hypothetical what-if analysis), `fetch` (download order data from the Webull API), `sniff` (automatically capture fresh API session headers), and `trade` (place, cancel, and inspect orders via the Webull OpenAPI).

### Report Command

```bash
# Generate a report using default data/orders.jsonl
wa report

# Use Webull CSV exports as the source of truth (fees from JSONL if available)
wa report --source export

# Filter trades since a specific date
wa report --since 2026-01-01

# Filter trades until a specific date
wa report --until 2026-03-31

# Filter trades within a date range
wa report --since 2026-01-01 --until 2026-03-31

# Export to Excel
wa report --output excel

# Set an initial portfolio amount to track cash
wa report --initial-amount 10000

# Combine options
wa report --since 2026-01-01 --output excel --initial-amount 10000

# Fetch option chain data for break-even analysis with time-decay grids (Yahoo Finance)
wa report --api yahoo

# Use Webull option chain data (requires sniffed headers via 'sniff' command)
wa report --api webull

# Override implied volatility for specific option legs (per OCC symbol)
wa report --iv GME260213C00025000:50,GME260516C00025000:45

# Combine API data with manual IV overrides (overrides take priority)
wa report --api yahoo --iv GME260213C00025000:60

# Show P&L instead of contract value in the grid
wa report --api yahoo --display pnl

# Show each leg's contract value alongside the net in every grid cell
wa report --api yahoo --grid verbose

# Increase grid granularity (more rows between strikes, default: 2)
wa report --api yahoo --range 4

# Override the current underlying price (for "what-if" evaluation)
wa report --api yahoo --current-underlying-price GME:24.88,SPY:580.50

# Use Black-Scholes theoretical prices instead of market mid for today's grid column
wa report --api yahoo --theoretical

# Add custom notable prices to break-even reports (e.g., support/resistance levels)
wa report --notable-prices GME:20/25/30

# Show only specific tickers in the report
wa report --tickers GME,SPY
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

### Analyze Command

The `analyze` command has two subcommands:

- `analyze trade` — inject hypothetical trades into the report pipeline for what-if analysis.
- `analyze roll` — show a 2D grid of theoretical roll credit/debit across underlying prices × times using Black-Scholes.

Both subcommands accept all of the `report` command's options plus `--date` for simulating a future evaluation date.

#### `analyze trade`

Runs a hypothetical what-if analysis by injecting synthetic trades into the report pipeline without modifying any data files.

```
wa analyze trade "<spec>" [--date <YYYY-MM-DD>] [report options]
```

The `<spec>` is a comma-separated list of legs:

```
ACTION:SYMBOL:QTY@PRICE,ACTION:SYMBOL:QTY@PRICE,...
```

- **ACTION**: `buy` or `sell`.
- **SYMBOL**: OCC option symbol (e.g., `GME260501C00023000`). `analyze trade` supports option legs only.
- **QTY**: Positive integer.
- **PRICE**: Required. A decimal (e.g., `0.50`) or a market-price keyword (`BID`, `MID`, `ASK`, case-insensitive). Keywords require `--api`.

Examples:

```bash
# What if I roll the calendar short from Apr 10 to Apr 17 for $0.24 credit?
wa analyze trade "sell:GME260410C00023000:300@0.14,buy:GME260417C00023000:300@0.38"

# Same roll but use live market prices (buy at ask, sell at bid)
wa analyze trade "sell:GME260410C00023000:300@BID,buy:GME260417C00023000:300@ASK" --api yahoo

# Use mid-market prices for both legs
wa analyze trade "sell:GME260410C00023000:300@MID,buy:GME260417C00023000:300@MID" --api yahoo

# What if I close 100 contracts of my long call?
wa analyze trade "sell:GME260501C00023000:100@0.70"

# What if I add a protective put?
wa analyze trade "sell:GME260501P00022000:455@0.25"

# Simulate running on a future date (e.g., after short leg expiration)
wa analyze trade "buy:GME260417C00023000:300@0.38" --date 2026-04-11

# Combine with report options (output to text, override underlying price)
wa analyze trade "sell:GME260410C00023000:300@0.14,buy:GME260417C00023000:300@0.38" --output text --current-underlying-price GME:23.20
```

When using `BID`, `MID`, or `ASK`, the command fetches live quotes from the configured API source (`--api webull` or `--api yahoo`) before building the hypothetical trades. The synthetic trades are appended after all real trades and processed through the full report pipeline — FIFO matching, strategy grouping, break-even analysis, and rendering all work normally. The original trade files are never modified.

#### `analyze roll`

Computes the theoretical roll credit/debit at various underlying prices using Black-Scholes, helping you find the optimal moment to roll a leg.

```
wa analyze roll "<spec>" [--side long|short] [--pair <SYMBOL:QTY>] [--cash <amount>] [--api <source>] [--iv <overrides>] [--date <YYYY-MM-DD>] [report options]
```

The `<spec>` is `OLD_SYMBOL>NEW_SYMBOL:QTY`. `--api` is required.

`--side` selects the math:

- **`short`** (default): closes a short on the OLD strike (buy at ask), opens a short on the NEW strike (sell at bid). Credit = `new_bid − old_ask`.
- **`long`**: closes a long on the OLD strike (sell at bid), opens a long on the NEW strike (buy at ask). Credit = `old_bid − new_ask`.

Examples:

```bash
# Short-side roll of the $23 short from Apr 10 to Apr 17 (300 contracts)
wa analyze roll "GME260410C00023000>GME260417C00023000:300" --api yahoo

# Same roll but I'm long the old position
wa analyze roll "GME260410C00023000>GME260417C00023000:300" --api yahoo --side long

# Roll to a different strike
wa analyze roll "GME260410C00023000>GME260417C00023500:300" --api yahoo

# Override IV for the analysis
wa analyze roll "GME260410C00023000>GME260417C00023000:300" --api yahoo --iv GME260410C00023000:37,GME260417C00023000:31

# Short-side roll paired with a static long call leg (calendar/diagonal margin)
wa analyze roll "GME260424C00025000>GME260424C00024500:499" --api yahoo --pair GME260515C00025000:499

# Short-side roll paired with long stock (covered-call margin)
wa analyze roll "GME260515C00025000>GME260522C00025000:5" --api yahoo --pair GME:500

# Check whether your available cash is enough to fund the roll
wa analyze roll "GME260424C00025000>GME260424C00024500:499" --api yahoo --pair GME260515C00025000:499 --cash 23015
```

The output is a 2D grid of roll net values across underlying prices (rows) and times (columns). For intraday scenarios (0–1 DTE), columns are hourly from 9:30 AM to 4 PM. For multi-day scenarios, columns are daily, adapting to terminal width. Each cell shows `Close|Open|Net` per contract (leg values in grey, net color-coded green for credit / red for debit). The current-price row is rendered in **bold yellow**, the best-net cell (globally) in **bold underline green**, and any row whose max net matches the global best in **green**. Live market credit from bid/ask quotes is shown below the grid.

When `--side short`, the command also prints a Reg-T margin analysis at the current spot. It shows three numbers: the **current requirement** (ongoing BPR of the pre-roll position), the **new requirement** (BPR required to open the post-roll position as a fresh order), and the **BP delta** between them. This answers "how much additional buying power do I need to free up to execute this roll?" — useful for gauging collateral impact alongside the credit/debit grid.

The two sides use different (but realistic) formulas:

- **Current requirement** is the ongoing BPR of the existing position. The debit you paid at entry is treated as sunk (already deducted from cash long ago), so covered structures (standard calendars, bull call/put spreads, covered calls, protective puts) show **$0** here. Inverted diagonals show only the strike-loss collateral. Naked shorts use the Reg-T naked formula.
- **New requirement** is the BPR required at order time to open the post-roll position from scratch. Covered structures charge the market debit (cash out). Inverted diagonals charge strike-loss collateral plus debit. Naked shorts use the Reg-T naked formula.

Both sides apply to whichever pair type you provide:

- **Without `--pair`**: both sides are treated as naked Reg-T.
- **With `--pair <SYMBOL:QTY>` (long stock)**: covers short calls one contract per 100 shares (drops to $0 for covered contracts); doesn't cover short puts.
- **With `--pair <SYMBOL:QTY>` (long option)**: must be the same underlying root and same call/put type. Coverage is valid when — for calls — long strike ≤ short strike and long expiry ≥ short expiry, or — for puts — long strike ≥ short strike and long expiry ≥ short expiry. When the strike relationship is inverted but the expiry relationship holds, the position is an inverted diagonal with a bounded max loss (strike-loss collateral required); when the expiry relationship fails, the long doesn't cover at all and the short falls back to naked.

The output line for each side labels its structure (`calendar`, `covered vertical`, `inverted diagonal (strike loss $X)`, `covered by stock`, `naked`, `no cover (reason)`) and shows the cost breakdown for that side.

If you pass `--cash <amount>`, a funding-check block is printed after the margin analysis:

- **Available** = cash + roll credit (or − roll debit). The roll's natural-market credit/debit is added here because it's received or paid at the moment of execution.
- **Required** = BP delta.
- **Net** = Available − Required. A positive value means the roll is fundable; a negative value means you're short that amount in free BP to execute.

Notable prices from `--notable-prices` are included as additional rows in the grid.

#### Options

Both `analyze trade` and `analyze roll` accept all `report` options, plus:

```
  --date <date>           Override 'today' for evaluation (YYYY-MM-DD). Simulates running on a future date — options expiring
                          on or before this date generate synthetic expirations, and all DTE/Black-Scholes calculations use it.

analyze roll only:
  --side <long|short>     Position side. Default: short. See above for the math each side uses.
  --pair <SYMBOL:QTY>     Static paired leg for spread margin calculation. SYMBOL is an equity ticker or OCC option
                          symbol; QTY is a positive integer. Only meaningful with --side short.
  --cash <amount>         Available cash for funding the roll. Prints a Net surplus/shortfall line that combines
                          cash with the roll's natural-market credit/debit and compares against the BP delta.
                          Only meaningful with --side short.
```

### Fetch Command

```bash
# Fetch order data from the Webull API
wa fetch
```

Reads API credentials from `data/api-config.json` and writes orders to `data/orders.jsonl`.

### Sniff Command

```bash
# Automatically capture fresh API session headers
wa sniff
```

Launches a browser with remote debugging, navigates to Webull, enters your unlock PIN, and captures the API session headers from the network traffic. The captured headers are written directly into `data/api-config.json`, replacing the existing `headers` object.

**Browser selection:**
- **Windows**: Microsoft Edge
- **Linux/macOS**: Microsoft Edge if installed, otherwise Firefox

**Requirements:**
- A supported browser must be installed
- The browser will be closed if running (prompts for confirmation)
- The `pin` field must be set in `data/api-config.json`

**Configuration** (in `config.json` under `"sniff"`):

| Field | Description |
|---|---|
| `autoCloseBrowser` | If `true`, closes the browser without prompting (default: `false`) |

### Trade Command

The `trade` command places, cancels, lists, and inspects orders via the Webull OpenAPI. It supports single-leg equity orders, single-leg option orders, and multi-leg option strategies (including stock+option combos like covered calls). Unlike `fetch` — which uses Webull's session-based web API — `trade` uses the OpenAPI with App Key + App Secret authentication.

Every `trade place` invocation runs a preview against the broker by default. The order is only submitted when you pass `--submit`, and every mutating action (`place --submit`, `cancel`, `cancel --all`) prompts interactively before sending.

#### Setup

Copy the example config and fill in your own account(s):

```bash
cp trade-config.example.json data/trade-config.json
```

The example ships with the three sandbox test accounts Webull publishes in its OpenAPI documentation. For a production account, add a new entry with `sandbox: false` and edit `defaultAccount` to point at it.

#### Commands

```bash
# Preview a single equity limit buy (no order is placed).
wa trade place --trade "buy:SPY:10" --limit 580

# Place the same order.
wa trade place --trade "buy:SPY:10" --limit 580 --submit

# Preview a vertical call spread for 1 contract, net debit $0.75.
wa trade place --trade "buy:SPY260515C00580000:1,sell:SPY260515C00590000:1" --limit -0.75

# Calendar roll — sell near, buy far.
wa trade place --trade "sell:GME260410C00023000:1,buy:GME260417C00023000:1" --limit -0.20

# Covered call — long 100 shares + short 1 call.
wa trade place --trade "buy:GME:100,sell:GME260501C00025000:1" --limit -23.50

# Market order, single equity.
wa trade place --trade "buy:SPY:10" --type market --submit

# Cancel a single order.
wa trade cancel <clientOrderId>

# Cancel every open order for the account.
wa trade cancel --all

# List all open orders for the account.
wa trade list

# Check an order's status.
wa trade status <clientOrderId>

# Use a non-default account.
wa trade place --trade "buy:SPY:1" --limit 1 --account test2
```

#### `--trade` syntax

Format: `ACTION:SYMBOL:QTY`, comma-separated for multiple legs.

- `ACTION` — `buy` or `sell` (explicit, no sign math).
- `SYMBOL` — equity ticker (e.g. `GME`) or OCC option symbol (e.g. `GME260501C00023000`).
- `QTY` — positive integer.

Per-leg prices (`@PRICE`) are **not** allowed in `trade` — combo orders use a single `--limit` for the net price across all legs. Positive `--limit` means net credit; negative means net debit.

#### Options

```
Options (place):
  --trade <legs>           Comma-separated legs in ACTION:SYMBOL:QTY format (required).
  --limit <net>             Net limit price. Required for --type limit. Positive = credit; negative = debit.
  --type <type>             limit or market. Default: limit. Market is rejected for multi-leg orders.
  --tif <tif>               Time-in-force: day or gtc. Default: day.
  --strategy <name>         Override auto-detected strategy. Values: single, stock, vertical, calendar,
                            diagonal, iron_condor, iron_butterfly, butterfly, condor, straddle, strangle,
                            covered_call, protective_put, collar.
  --account <id-or-alias>   Pick an account from trade-config.json. Defaults to defaultAccount.
  --submit                  Actually place the order. Without this, runs preview only.
  --debug                   Print the raw JSON payload that will be sent to the Webull API.

Options (cancel):
  <clientOrderId>           Client order ID of the order to cancel.
  --all                     Cancel every open order for the account.
  --account <id-or-alias>   Pick a non-default account.

Options (status):
  <clientOrderId>           Client order ID to look up.
  --account <id-or-alias>   Pick a non-default account.

Options (list):
  --account <id-or-alias>   Pick a non-default account.
```

#### Sandbox vs production

Each account in `trade-config.json` has a `sandbox: true|false` flag. Sandbox accounts hit `https://us-openapi-alb.uat.webullbroker.com`; production accounts hit `https://api.webull.com`. A colored banner (green `[SANDBOX]` / red `[PRODUCTION]`) is printed at the top of every `trade` invocation so you always know which environment you are in.

There is no `--yes` flag — every place, cancel, and cancel-all prompts interactively. Piped empty input aborts.

### AI Command

The `ai` command monitors open calendar/diagonal positions during market hours and emits structured proposals (roll / take-profit / stop-loss / defensive-roll) to a JSONL log. It is **read-only in phase 1**: the command never places orders.

Three subcommands share one evaluation engine:

```bash
# Continuous monitoring during market hours (default: until 4 PM ET)
wa ai watch

# Single evaluation pass, print proposals, exit
wa ai once

# Replay the rules against historical orders.jsonl with agreement analysis
wa ai replay --since 2026-01-01 --until 2026-04-17
```

#### Setup

1. Copy the example config:
   ```bash
   cp ai-config.example.json data/ai-config.json
   ```
2. Edit `data/ai-config.json` and set the `tickers` array to the symbols you want to monitor, and set `positionSource.account` to one of the aliases in your `data/trade-config.json`.
3. Ensure `data/trade-config.json` exists (same setup as the `trade` command) — the loop reads position state from the Webull OpenAPI.

#### Rules

Rules evaluate per-position in priority order — the first rule to match for a position wins; lower-priority rules are skipped for that position in that tick. All thresholds are configurable in `ai-config.json`.

| Rule | Priority | Trigger |
|---|---|---|
| `StopLossRule` | 1 | MTM debit ≥ 1.5× initial, or spot beyond break-even by > 3% |
| `TakeProfitRule` | 2 | MTM ≥ 60% of max projected profit |
| `DefensiveRollRule` | 3 | Spot within 1% of short strike and short DTE ≤ 3 |
| `RollShortOnExpiryRule` | 4 | Short DTE ≤ 2 and short mid ≤ $0.10 |
| `OpportunisticRollRule` | 5 | A roll scenario improves P&L-per-day by at least `minImprovementPerDayPerContract` vs holding, and passes all four safety gates |

#### OpportunisticRollRule

The opportunistic roll rule selects the highest-theta roll candidate from the scenario engine and accepts it only if it passes four sequential safety gates. If no candidate passes all four gates the rule fires nothing — there is no AlertOnly fallback.

1. **OTM guard** — the new short leg must be out-of-the-money. ITM proposals are blocked unconditionally.
2. **OTM buffer** — spot must clear a minimum distance from the new short strike. The buffer widens when technicals are extended:
   ```
   adjustedOtmPct = baseOtmBufferPct × (1 + |compositeScore| × technicalBufferMultiplier)
   ```
3. **Break-even margin** — the position must be profitable at current spot (evaluated at new short expiry) by at least `minBreakEvenMarginPct` of spot. This ensures a meaningful cushion above break-even rather than a bare sign check. The threshold widens with the same technical factor as the OTM buffer.
4. **Delta cap** — the roll may not increase the net position delta magnitude by more than `maxDeltaIncreasePct`.

When a proposal fires, the rationale includes a safety summary showing what was required vs. achieved:
```
[OTM: 2.6% (req 2.2%), BE: +$0.18/sh (min $0.13/sh), Δ: −0.12→−0.09]
```

**Technical filter** — before any scenario is evaluated, the rule checks a composite technical bias score built from SMA position, RSI, and short-term momentum. When the score exceeds `bullishBlockThreshold` (extended bullish setup) or falls below `bearishBlockThreshold` (extended bearish), the rule skips the position entirely for that tick.

**Config fields** (`rules.opportunisticRoll`):

| Field | Default | Description |
|---|---|---|
| `minImprovementPerDayPerContract` | 0.50 | Minimum P&L-per-day-per-contract improvement over holding, in dollars |
| `ivDefaultPct` | 40 | Default implied volatility used when live IV is unavailable, in percent |
| `strikeStep` | 0.50 | Strike increment for candidate roll selection |
| `baseOtmBufferPct` | 2.0 | Minimum OTM distance for new short at neutral technicals, as % of spot |
| `technicalBufferMultiplier` | 1.5 | Scales OTM buffer and break-even threshold by `(1 + \|score\| × multiplier)` |
| `maxDeltaIncreasePct` | 25.0 | Maximum allowed delta magnitude increase after the roll, as % of current delta |
| `minBreakEvenMarginPct` | 0.5 | Minimum required profit cushion at current spot, as % of spot |
| `technicalFilter.enabled` | true | Enable/disable the technical bias filter |
| `technicalFilter.lookbackDays` | 20 | Lookback window for SMA and momentum signals |
| `technicalFilter.bullishBlockThreshold` | 0.25 | Composite score above this blocks the rule (extended bullish) |
| `technicalFilter.bearishBlockThreshold` | −0.25 | Composite score below this blocks the rule (extended bearish) |

#### Output

Proposals are written to two places:

- **Console**: Spectre-formatted, color-coded by action (close = yellow, roll = cyan, alert-only = grey). Each proposal shows the legs and net credit/debit, followed by ready-to-run `wa trade place` and `wa analyze trade` commands, and the rule rationale.
- **JSONL log** at `data/ai-proposals.log`: one proposal per line, machine-parseable with `jq` or similar. Includes `mode` field ("once" / "watch" / "replay") to distinguish source runs.

AI commands accept `--pricing mid|bidask` to control both displayed command prices and the pricing basis used in proposal math. Default: `mid`.

#### Cash reserve

Every proposal is funding-checked. Proposals that would leave free cash below the configured reserve get a `⚠ blocked by cash reserve` tag. This is informational in phase 1; no action is blocked since nothing executes.

#### Historical replay: supplying price data

`ai replay` needs daily closes for each ticker to price options via Black-Scholes. The built-in Yahoo fetcher hits `query1.finance.yahoo.com/v7/finance/download` which currently returns 401 without authentication. Until an alternative automated fetcher is wired, you can supply historical closes manually:

1. Export daily closes for each ticker from any source (Yahoo Finance web UI → "Historical Data" → Download, or a broker export).
2. Save to `data/history/<TICKER>.csv`. The parser accepts either:
   - **Two-column native format** with header `date,close` and rows like `2026-04-17,24.55`, or
   - **Yahoo's seven-column export** with header `Date,Open,High,Low,Close,Adj Close,Volume` — drop in as-is.
3. Run `ai replay`. The cache picks up the CSV automatically; no further conversion needed.

The replay output includes an **agreement analysis** — for each day where rules fired and you also traded that position, it shows what the rule proposed alongside what you actually did, and scores each as `match`, `partial`, `miss`, or `divergent`.

#### Phase-1 note

**`TakeProfitRule`** is implemented but its profit-projector bridge currently returns null, so it never fires. The other four rules (StopLoss, DefensiveRoll, RollShortOnExpiry, OpportunisticRoll) work normally.

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

The `access_token`, `t_token`, `x-s`, and `x-sv` headers are session tokens that expire. When they expire, the API will return an error. To refresh them, either run `wa sniff` to capture fresh headers automatically, or log into Webull in your browser and copy the updated values from the Network tab manually.

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
    "autoCloseBrowser": false
  }
}
```

Copy and customize from `config.example.json`. The `report` section maps directly to the report/analyze command options. The `autoExpandTerminal` flag, when `true`, automatically resizes the terminal to fit wide tables.

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
- Session tokens expire; run `wa sniff` to capture fresh headers, or log into Webull in your browser and copy fresh values from the Network tab
- The `x-s` header may be request-specific; try copying it from a recent request

**No trades found:**
- Verify the JSONL file exists at the expected path (default: `data/orders.jsonl`)
- Run `wa fetch` to download fresh data
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

---

## Referral

If you're not on Webull yet, you can sign up using my referral link and get free rewards:
[https://www.webull.com/s/FCxAAumTOqwPwR1AgM](https://www.webull.com/s/FCxAAumTOqwPwR1AgM)

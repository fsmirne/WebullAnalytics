# WebullAnalytics

A C# command-line tool for reviewing Webull trading activity end-to-end. It generates realized P&L reports, models hypothetical option adjustments, inspects Webull OpenAPI account state, and emits AI-assisted trade proposals for stocks and options.

## Features

- **Webull API Integration**: Fetch order data from Webull's web API and inspect/place orders through the Webull OpenAPI
- **Position Tracking**: FIFO lot accounting for realized P&L calculations, average cost display for open positions (matching Webull)
- **Option Strategy Support**: Recognizes and properly handles multi-leg strategies including:
  - Calendar Spreads
  - Diagonals
  - Butterflies
  - Iron Condors
  - Straddles/Strangles
  - Vertical Spreads
- **Calendar Roll Tracking**: Intelligently groups rolled positions and tracks adjusted cost basis
- **Scenario Analysis**: Evaluate hypothetical trades, roll grids, scenario-ranked position adjustments, and structured risk diagnostics
- **Broker Utilities**: Preview/place/cancel orders, inspect status, list app subscriptions, check positions, and manage OpenAPI trade tokens
- **AI Proposal Engine**: Run one-shot, watch-loop, or historical replay evaluation for management proposals and new-opening ideas
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

`wa` has six top-level commands:

- `report` — generate realized P&L reports and open-position break-even analysis
- `analyze` — run hypothetical trade, roll, position, and risk analysis
- `fetch` — download order data from the Webull web API
- `sniff` — capture fresh Webull session headers for the web API
- `trade` — preview/place/cancel orders and inspect OpenAPI account state
- `ai` — emit management and opening proposals from live or replayed data

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
wa report --api yahoo --spot GME:24.88,SPY:580.50

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
  --spot <prices>           Override underlying spot price(s). Format: TICKER:PRICE (e.g., GME:24.88,SPY:580.50)
  --theoretical             Use Black-Scholes theoretical price instead of market mid for today's grid column
  --notable-prices <prices> Additional prices to show in break-even reports. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30,SPY:580/590)
  --tickers <list>          Show only these tickers in the report. Comma-separated (e.g., GME,SPY,AAPL)
  --help, -h                Show help message
```

### Analyze Command

The `analyze` command has four subcommands:

- `analyze trade` — inject hypothetical trades into the report pipeline for what-if analysis.
- `analyze roll` — show a 2D grid of theoretical roll credit/debit across underlying prices × times using Black-Scholes.
- `analyze risk` — render a structured risk diagnostic for an option structure using live quotes.
- `analyze position` — analyze an existing or manually specified option position and rank adjustment scenarios.

All four subcommands accept the `report` command's options plus `--date` for simulating a future evaluation date. Some subcommands add extra flags documented below.

#### `analyze trade`

Runs a hypothetical what-if analysis by injecting synthetic trades into the report pipeline without modifying any data files.

```
wa analyze trade "<spec>" [--date <YYYY-MM-DD>] [report options]
```

The `<spec>` is a comma-separated list of legs. You can also separate independent execution groups with `;` when you want multiple synthetic order events in one run:

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
wa analyze trade "sell:GME260410C00023000:300@0.14,buy:GME260417C00023000:300@0.38" --output text --spot GME:23.20
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

#### `analyze risk`

Evaluates an option structure with current market quotes and prints the same structured risk diagnostics used by the AI pipeline.

```
wa analyze risk "<spec>" [--iv-default <pct>] [--spot <TICKER:PRICE>] [--date <YYYY-MM-DD>] [report options]
```

The `<spec>` format is:

```
ACTION:SYMBOL[:QTY][@PRICE],ACTION:SYMBOL[:QTY][@PRICE],...
```

- `ACTION` — `buy` or `sell`.
- `SYMBOL` — OCC option symbol.
- `QTY` — optional; defaults to `1` if omitted.
- `@PRICE` — optional cost basis per share. Accepts a decimal or `BID`, `MID`, `ASK`. If omitted, `MID` is used.

Examples:

```bash
# Evaluate a 1-lot diagonal at current mid prices
wa analyze risk "sell:GME260501C00025500,buy:GME260522C00026000" --api yahoo

# Evaluate a 10-lot using explicit cost bases
wa analyze risk "sell:GME260501C00025500:10@0.38,buy:GME260522C00026000:10@0.12" --api yahoo

# Supply spot manually instead of fetching an underlying quote
wa analyze risk "sell:GME260501C00025500,buy:GME260522C00026000" --api yahoo --spot GME:24.88
```

The command appends a machine-readable record to `data/analyze-risk.jsonl` after each run.

#### `analyze position`

Loads an existing open option strategy from `data/orders.jsonl` or accepts a manual position spec, then ranks hold/roll/reset scenarios with projected P&L and buying-power impact.

```
wa analyze position ["<spec>"] [--iv-default <pct>] [--strike-step <step>] [--cash <amount>] [--account <alias>] [--date <YYYY-MM-DD>] [report options]
```

If `<spec>` is omitted, the command scans open strategy positions from the trade log and lets you pick one interactively.

Manual `<spec>` format:

```
ACTION:SYMBOL:QTY@PRICE,ACTION:SYMBOL:QTY@PRICE,...
```

Examples:

```bash
# Pick an existing open strategy from orders.jsonl and auto-detect available cash/BP from trade-config.json
wa analyze position --api yahoo --account test1

# Analyze a manually specified calendar position
wa analyze position "sell:GME260424C00025000:499@0.48,buy:GME260515C00025000:499@1.11" --api yahoo --cash 23015

# Analyze a single long call with a custom scenario strike step
wa analyze position "buy:GME260620C00025000:10@1.25" --api yahoo --strike-step 0.50 --spot GME:24.88
```

The command prints ranked scenarios, emits ready-to-run `wa trade place` and `wa analyze trade` reproduction commands, and appends a machine-readable record to `data/analyze-position.jsonl`.

#### Options

All `analyze` subcommands accept all `report` options, plus:

```
  --date <date>           Override 'today' for evaluation (YYYY-MM-DD). Simulates running on a future date — options expiring
                          on or before this date generate synthetic expirations, and all DTE/Black-Scholes calculations use it.

analyze risk only:
  --iv-default <pct>      Fallback implied volatility used when live IV is unavailable.

analyze position only:
  --iv-default <pct>      Fallback implied volatility used when live IV is unavailable.
  --strike-step <step>    Strike increment used for near-spot scenario generation.
  --cash <amount>         Available cash/BP used to flag scenarios as fundable or not fundable.
  --account <alias>       Account alias or ID from trade-config.json used to auto-detect available cash/BP
                          when you select an existing open position.

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

The `trade` command previews, places, cancels, lists, and inspects orders via the Webull OpenAPI. It also exposes account-subscription lookup, raw-position inspection, and token lifecycle helpers. It supports single-leg equity orders, single-leg option orders, and multi-leg option strategies (including stock+option combos like covered calls). Unlike `fetch` — which uses Webull's session-based web API — `trade` uses the OpenAPI with App Key + App Secret authentication.

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
wa trade place --trade "buy:SPY260515C00580000:1,sell:SPY260515C00590000:1" --limit 0.75

# Calendar roll — sell near, buy far.
wa trade place --trade "sell:GME260410C00023000:1,buy:GME260417C00023000:1" --limit 0.20

# Covered call — long 100 shares + short 1 call.
wa trade place --trade "buy:GME:100,sell:GME260501C00025000:1" --limit 23.50

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

# List the account subscriptions tied to this OpenAPI app.
wa trade accounts

# Show a readable positions summary for the account.
wa trade positions

# Dump the raw positions payload for the account.
wa trade positions --debug

# Start the OpenAPI trade-token approval flow.
wa trade token create

# Poll an existing token until it becomes usable.
wa trade token check

# Use a non-default account.
wa trade place --trade "buy:SPY:1" --limit 1 --account test2
```

#### `--trade` syntax

Format: `ACTION:SYMBOL:QTY`, comma-separated for multiple legs.

- `ACTION` — `buy` or `sell` (explicit, no sign math).
- `SYMBOL` — equity ticker (e.g. `GME`) or OCC option symbol (e.g. `GME260501C00023000`).
- `QTY` — positive integer.

Per-leg prices (`@PRICE`) are **not** allowed in `trade` — combo orders use a single `--limit` for the absolute net price across all legs. `--limit` is always positive. The broker-side direction is auto-inferred from the legs, and you can override it with `--side buy|sell` if needed.

#### Options

```
Options (place):
  --trade <legs>           Comma-separated legs in ACTION:SYMBOL:QTY format (required).
  --limit <net>            Absolute per-share net limit price. Required for --type limit.
  --side <buy|sell>        Override inferred combo direction. Use only when auto-inference is not what you want.
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

Options (accounts / positions / token create / token check):
  --account <id-or-alias>   Pick a non-default account.
```

#### Account and token utilities

- `trade accounts` lists the account subscriptions returned by the OpenAPI app and highlights the `account_id` value you should copy into `trade-config.json`.
- `trade positions` prints a readable account-positions summary; add `--debug` to dump the raw payload.
- `trade token create` starts the OpenAPI trade-token approval flow and caches the token locally.
- `trade token check` re-checks the cached token status and updates the local cache.

#### Sandbox vs production

Each account in `trade-config.json` has a `sandbox: true|false` flag. Sandbox accounts hit `https://us-openapi-alb.uat.webullbroker.com`; production accounts hit `https://api.webull.com`. A colored banner (green `[SANDBOX]` / red `[PRODUCTION]`) is printed at the top of every `trade` invocation so you always know which environment you are in.

There is no `--yes` flag — every place, cancel, and cancel-all prompts interactively. Piped empty input aborts.

### AI Command

The `ai` command evaluates live or replayed positions and emits structured proposal logs. It can emit both management proposals (roll / take-profit / stop-loss / defensive-roll) and opening candidates for new positions. It is **read-only in phase 1**: the command never places orders.

Three subcommands share one evaluation engine:

```bash
# Continuous monitoring during market hours (default: until 4 PM ET)
wa ai watch

# Single evaluation pass, print proposals, exit
wa ai scan

# Replay the rules against historical orders.jsonl with agreement analysis
wa ai replay --since 2026-01-01 --until 2026-04-17

# Run the watch loop every 30 seconds for 90 minutes, ignoring market-hours checks
wa ai watch --tick 30 --duration 90m --ignore-market-hours

# Emit only opening ideas
wa ai scan --proposals open
```

#### Setup

1. Copy the example config:
   ```bash
   cp ai-config.example.json data/ai-config.json
   ```
2. Edit `data/ai-config.json` and set the `tickers` array to the symbols you want to monitor, and set `positionSource.account` to one of the aliases in your `data/trade-config.json`.
3. Ensure `data/trade-config.json` exists (same setup as the `trade` command) — the loop reads position state from the Webull OpenAPI.

#### Rules

Rules evaluate per-position in priority order — the first rule to match for a position wins; lower-priority rules are skipped for that position in that tick. Ties on priority are broken alphabetically by rule name. All thresholds are configurable in `ai-config.json`.

| Rule | Priority | Trigger |
|---|---|---|
| `StopLossRule` | 1 | MTM debit ≥ 1.5× initial, or spot beyond break-even by > 3% |
| `CloseBeforeShortExpiryRule` | 2 | Short DTE = 0 and either MTM profit ≥ `minProfitPct` of initial debit, or spot is past the BE band ± `emergencyBreakEvenBufferPct` (emergency close) |
| `OpportunisticRollRule` | 2 | A roll scenario improves P&L-per-day by at least `minImprovementPerDayPerContract` vs holding, and passes all four safety gates |
| `TakeProfitRule` | 2 | MTM ≥ `pctOfMaxProfit` of the peak net value in the current-date column of the time-decay grid |
| `DefensiveRollRule` | 3 | Spot within 1% of short strike and short DTE ≤ 3 |
| `RollShortOnExpiryRule` | 4 | Short DTE ≤ 2 and short mid ≤ $0.10 |

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
- **JSONL log** at `data/ai-proposals.log`: one proposal per line, machine-parseable with `jq` or similar. Includes `mode` field ("scan" / "watch" / "replay") to distinguish source runs.

AI commands accept `--pricing mid|bidask` to control both displayed command prices and the pricing basis used in proposal math. Default: `mid`.

Shared AI options:

```
  --config <path>          Path to ai-config.json. Default: data/ai-config.json
  --tickers <list>         Override config tickers (comma-separated)
  --output <format>        console or text. `text` writes to a default .txt file when --output-path is omitted
  --output-path <path>     Optional path for --output text
  --api <source>           Override quote source: webull or yahoo
  --log-level <level>      debug | information | error
  --proposals <mode>       all | open | management
  --pricing <mode>         mid | bidask

ai scan only:
  --top <N>                Override opener.topNPerTicker from ai-config.json

ai watch only:
  --tick <seconds>         Override tickIntervalSeconds
  --duration <duration>    Stop after a duration such as 6h, 90m, or 30s
  --ignore-market-hours    Run regardless of market-hours checks

ai replay only:
  --since <date>           Start date YYYY-MM-DD. Default: earliest fill
  --until <date>           End date YYYY-MM-DD. Default: latest fill
  --granularity <level>    daily or hourly. Default: daily
```

#### Auto-execution (watch loop)

`wa ai watch` can optionally submit Close proposals automatically when `watch.autoExecute.enabled` is set in `ai-config.json`. Off by default; the executor logs the action it *would* take until `submit: true` is also set. The `rules` allow-list controls which rules can fire executions — by default only `CloseBeforeShortExpiryRule` is permitted.

For Close proposals at or above `scaleOut.minQty` contracts, the executor splits the close into three time-windowed tranches (default 10:00–10:30 / 12:30–13:00 / 15:00–15:30 ET). The final tranche always closes whatever remains, so partial fills earlier in the day still converge to a fully-closed position by the last window. Smaller closes fire as a single order.

#### Cash reserve

Every proposal is funding-checked. Proposals that would leave free cash below the configured reserve get a `⚠ blocked by cash reserve` tag. This is informational in phase 1; no action is blocked since nothing executes.

#### Historical replay: supplying price data

`ai replay` needs daily closes for each ticker to price options via Black-Scholes. The built-in Yahoo fetcher hits `query2.finance.yahoo.com/v8/finance/chart` and transparently retries with a session crumb when Yahoo requires authentication. If the fetch fails for a ticker, the cache is left empty for that ticker and you can supply historical closes manually:

1. Export daily closes for each ticker from any source (Yahoo Finance web UI → "Historical Data" → Download, or a broker export).
2. Save to `data/history/<TICKER>.csv`. The parser accepts either:
   - **Two-column native format** with header `date,close` and rows like `2026-04-17,24.55`, or
   - **Yahoo's seven-column export** with header `Date,Open,High,Low,Close,Adj Close,Volume` — drop in as-is.
3. Run `ai replay`. The cache picks up the CSV automatically; no further conversion needed.

The replay output includes an **agreement analysis** — for each day where rules fired and you also traded that position, it shows what the rule proposed alongside what you actually did, and scores each as `match`, `partial`, `miss`, or `divergent`.

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

## Risk Diagnostic Panel

The risk diagnostic is the structured snapshot rendered by `wa analyze risk`, `wa analyze position`, and the AI pipelines (`wa ai scan`, `wa ai watch`, `wa ai replay`) for every management proposal and opening idea. It is a single Spectre panel titled **Risk diagnostic** that combines: a fixed set of structural / pricing / Greek facts, an opener-style score with the multiplicative factor breakdown, optional probe rows (per-leg quotes, delta-band gate, broker margin), and the list of rule hits that fired against the structure.

The diagnostic is built by `RiskDiagnosticBuilder.Build` from the legs, current spot, an IV resolver, and an optional `TrendSnapshot`. Twelve rules are evaluated unconditionally — only the ones that match attach as `Rules fired` lines. The same record is appended to `data/analyze-risk.jsonl` (for `analyze risk`) or `data/analyze-position.jsonl` (for `analyze position`) so historical diagnostics can be re-analyzed later.

### Panel rows

Every row is per-contract unless explicitly noted. "Per share" means $1 of underlying movement; multiply by 100 to get the per-contract dollar figure.

| Row | What it shows | How it's computed |
|---|---|---|
| `Structure` | Structure label and directional bias (e.g. `calendar (neutral)`, `covered_diagonal (bullish)`, `vertical_credit (bearish)`) | `ClassifyStructure` inspects leg counts, expirations, strikes, and call/put types. The bias is *bullish*, *bearish*, or *neutral* depending on which side of spot the structure profits on. |
| `Greeks` | Δ, θ/day, ν per IV-point — all per contract | Δ and ν use Black-Scholes closed-form. θ uses a 1-day finite difference (BS today − BS tomorrow) so it correctly captures weekend decay. Each leg's per-share value is signed (long = +, short = −), summed × Qty × 100 for θ/ν, and divided by reference Qty so the result is *per contract*. |
| `DTE` | Earliest short-leg DTE, latest long-leg DTE, gap days | Calendar-day differences from `asOf.Date` (or the `--date` override). |
| `Premium` | Market view (long / short / ratio / net) and theoretical view side-by-side when both are available; otherwise the cost-basis or current-mid view alone | **Market** uses each leg's bid/ask midpoint. **Theoretical** prices each leg via Black-Scholes at its quoted IV. **Cost-basis** view uses each leg's entry price (manage pipeline). The `ratio` is `long_paid / short_received`. Net is signed: positive = debit, negative = credit. |
| `Spot` | Current underlying price, whether the short leg is OTM, and short-leg extrinsic value | Spot comes from `--spot`, the API, or the bootstrap chain quote. **Short OTM** is true when *every* short leg is OTM (call: spot ≤ strike; put: spot ≥ strike). **Short extrinsic** is `min(short_mid − intrinsic)` across short legs. |
| `Trend` | 5-day %, 20-day %, optional intraday %, ATR(14) % | `TrendFetcher.FetchAsync` pulls daily closes from the historical price cache. ATR(14) is the 14-day average true range as a percentage of spot. The intraday cell is omitted outside market hours. The line is omitted entirely if no historical data is available. |
| `P&L` | Cost / now / signed pnl/share — **manage pipeline only** | Only emitted when *every* leg has both a `CostBasisPerShare` (entry price) and a current `PricePerShare` (mark). `pnl = current_value − cost_basis`, where each is the signed leg sum. Open candidates have no cost basis and skip this row. |

### Reading the Greeks line

```
Greeks:    Δ +0.02   θ +$4.83/day   ν +$1.85/IV pt
```

Every value is **net, per contract** — long legs add, short legs subtract, then the aggregate is divided by the reference contract size. Multiply by your actual position size to get total exposure.

#### Δ (delta) — directional sensitivity

Delta is dimensionless: `Δ = 0.02` means the *option price* moves $0.02 for every $1 the underlying moves. To translate to **dollars per contract**, multiply by the 100-share multiplier:

```
$/contract per $1 underlying = Δ × 100
```

So `Δ +0.02` means **+$2 per contract for every $1 the underlying rises**, and `Δ −0.40` means **−$40 per contract** for every $1 it rises (or equivalently +$40 if it falls). The sign tells you the direction: positive delta wants the underlying up, negative wants it down. Calendars and iron condors typically run very small deltas (|Δ| < 0.10); a long call sits around +0.30 to +0.70 depending on moneyness.

The `directional_exposure` rule fires when `|Δ| > 0.25`, which is roughly $25/contract per $1 underlying — material enough that a normal session's price wiggle dominates whatever theta you're earning.

#### θ (theta) — time decay per day

Theta is already in **dollars per contract per day**. It's computed as a 1-day finite difference (`BS_today − BS_tomorrow`) on each leg's Black-Scholes value rather than the closed-form derivative — this means it correctly handles weekend decay (Friday's θ already reflects the value drop you'll see Monday morning).

```
θ = +$4.83/day  →  position gains $4.83 per contract per calendar day if spot and IV stay flat
θ = −$12.40/day →  position loses $12.40 per contract per day
```

A few reference points:
- **Short calendar / short iron condor**: positive θ — you collect time premium, typically $2–$15/day/contract depending on DTE and how close spot is to the short strike.
- **Long call / long put**: negative θ — you bleed time, often $5–$25/day/contract for near-the-money front-month longs.
- **Diagonals**: positive θ when the short's decay outpaces the long's; can flip sign as the short approaches expiry.

Theta accelerates as DTE shrinks for at-the-money options, so a structure that shows θ +$3/day at 30 DTE may show θ +$8/day in the final week. The `final` score's theta factor uses `θ / capital_at_risk` to normalize this — a $5/day theta on $200 risk is a much better return than the same $5/day on $2000 risk.

#### ν (vega) — IV sensitivity

Vega is in **dollars per contract per 1 percentage point of implied volatility**. The raw Black-Scholes vega from the math library is per 1.0 IV change (i.e. 100 percentage points), and the diagnostic divides by 100 so the displayed figure is the more intuitive "1-IV-point" units that match how IV is normally quoted.

```
ν = +$1.85/IV pt  →  if IV rises from 32% to 33%, position gains $1.85/contract
ν = −$8.20/IV pt  →  if IV rises from 32% to 33%, position loses $8.20/contract
ν = +$1.85/IV pt  →  if IV falls from 32% to 27% (5 points), position loses $9.25/contract
```

Sign by structure type:
- **Long calendars / long diagonals**: positive ν — the long leg has more time value than the short, so it benefits more from an IV pop. Typical range: +$1 to +$5/IV pt for single-contract calendars.
- **Long calls / long puts**: positive ν — pure long premium.
- **Short verticals / iron condors / iron flies**: negative ν — you're net short premium, an IV crush is in your favor and an IV spike hurts.
- **Inverted diagonals**: usually negative ν — the short leg dominates.

The `vega_adverse` rule fires when `ν < −$5/contract per IV pt` to flag positions that would take a meaningful hit from a typical earnings-style IV pop.

#### Worked example

For a `calendar (neutral)` showing `Δ +0.02   θ +$4.83/day   ν +$1.85/IV pt` held as 10 contracts:

| Scenario | Per-contract impact | Total (10 contracts) |
|---|---|---|
| Underlying up $1, IV flat, same day | +$2.00 | +$20 |
| Underlying flat, IV flat, +1 day | +$4.83 | +$48.30 |
| Underlying flat, IV +2 points, same day | +$3.70 | +$37 |
| Underlying down $2, IV +1 pt, +1 day | −$4 + $4.83 + $1.85 = $2.68 | +$26.80 |

The diagnostic doesn't print these scenarios — it gives you the three Greeks and you do the multiplication. The point of having all three on one line is that you can immediately see whether a structure makes sense: if your thesis is "spot drifts up slowly and IV rises into earnings," you want **positive Δ, positive θ, positive ν**. If the panel shows positive Δ but negative ν, the same thesis still makes money on the spot move but loses on the IV pop — a tradeoff the panel is forcing you to confront before you submit.

### Probe rows

These rows appear when `RiskDiagnosticProbe` is attached — which is always for `analyze risk`, `analyze position`, and AI opener candidates.

| Row | When shown | Meaning |
|---|---|---|
| `Enum delta` | Two-leg short call/put vertical only | Absolute Black-Scholes delta of the short leg vs the configured `opener.structures.shortVertical.shortDeltaMin/Max` band. PASS = inside the band; FAIL = outside. The opener pipeline uses this gate to filter verticals upstream. |
| `Long quote / Short quote / Leg N quote` | Always when quotes exist | Per-leg `bid=… ask=… mid=… iv=… hv=… iv5=… oi=… vol=… sym=…`. `iv`/`hv`/`iv5` are populated only by the Webull source; Yahoo leaves them null. |
| `Margin` | Always when an opener-style score exists | Broker margin per contract and total. Short verticals and iron spreads collateralize full capital-at-risk. Standard calendars and covered diagonals show `$0` (the debit is cash, not collateral). Inverted diagonals charge `(strike_gap + debit) × 100` per contract. |
| `Rationale` | Always when an opener-style score exists | Single-line trade summary: side ($credit/$debit), max profit / max loss, R/R ratio, premium ratio, break-evens, POP, EV. |
| `Score` | Always when an opener-style score exists | The four-stage score chain — `raw → tech-adjusted → adjusted → final` — with the bias tag in the middle. See **Score chain** below for what each stage means. |
| `Indicators` | When max-pain or stat-arb factors fired | Free-text breakdown: representative IV / HV richness, max-pain target, market-vs-theoretical edge per share. These feed the `vol`, `pain`, and `arb` factors below. |
| `Factors` | Always when an opener-style score exists | The multiplicative chain that turns *tech-adjusted* into *adjusted*: `tech-adjusted × pop X × scale X × setup X × geom X × runway X × bal X × vol X × pain X × assign X × arb X = adjusted`. Only factors that apply to the structure are shown. |
| `Result` | Always when an opener-style score exists | Final stage: `adjusted × theta factor X (θ/day on $risk) = final`. Theta factor is omitted on structures that don't earn theta. |

### Reading the Rationale line

```
Rationale: debit $92.00, maxProfit $408.00, maxLoss $92.00, R/R 4.43, prem 1.00x, BE $24.92, POP 38.0%, EV $60.92
```

Every dollar figure is **per contract** (already multiplied by the 100-share multiplier). Quote conventions in the option chain are usually per share; the diagnostic converts everything to per-contract so it lines up with margin, EV, and ranking math.

#### `debit / credit` — net entry cash flow

The signed cash entry of the structure at the chosen pricing mode (`mid` by default, `bidask` if requested). `debit $92.00` means it costs you $92/contract to open; `credit $135.00` means you collect $135/contract upfront. Sign convention follows broker semantics — debit = cash out, credit = cash in.

#### `maxProfit` / `maxLoss` — payoff envelope

- **Defined-risk structures** (verticals, iron condors, butterflies): hard payoff bounds at expiry. Max profit = the credit (for credit spreads) or `width × 100 − debit` (for debit spreads); max loss = `width × 100 − credit` or the debit.
- **Long single legs** (long call/put): `maxProfit` is approximated using the upper-σ end of the 5-point scenario grid since theoretical max is unbounded. `maxLoss = debit`.
- **Calendars / diagonals**: `maxProfit` is the peak P&L found on a wide spot grid evaluated at the short leg's expiry (using BS for the residual long leg); `maxLoss = debit` (covered structures) or `(strike_gap + debit) × 100` (inverted diagonals).

Both numbers are signed-then-displayed-positive — `maxLoss $92.00` means you can lose $92/contract, not −$92.

#### `R/R` — reward-to-risk ratio

```
R/R = maxProfit / abs(maxLoss)
```

A unitless multiplier. Higher = more asymmetric payoff in your favor.

| R/R | Meaning |
|---|---|
| `0.30` | Risk $1 to make $0.30 — typical for high-POP credit spreads sold near-the-money |
| `1.00` | Symmetric payoff — risk equals reward |
| `2.00` | Risk $1 to make $2 — typical for OTM debit spreads or moderate-delta long calls |
| `4.43` | The example above — risk $92 to make up to $408 |

R/R alone is misleading because high-R/R structures usually carry low POP (lottery tickets). The scoring engine combines R/R with POP and premium efficiency in the `bal` factor, which is why a 4× R/R doesn't automatically beat a 0.5× R/R in the final ranking.

#### `prem` — premium ratio (long paid / short received)

```
premium_ratio = total_long_paid / total_short_received
```

How much you're paying out per dollar of short premium taken in. Computed across all legs at the chosen pricing mode.

| Ratio | What it tells you |
|---|---|
| `< 1.0` | Net credit structure — you collect more than you pay (short verticals, iron condors). Always < 1 for these. |
| `≈ 1.0` | Long and short premium roughly cancel — typical for at-the-money calendars and tight diagonals. |
| `2.0` | You paid $2 for every $1 of short premium — front-month short doesn't fully fund the long. |
| `3.0+` | Short provides limited cushion. The `premium_ratio_imbalanced` rule fires above 3× on debit structures. |
| `n/a` | Single-leg structures (long call/put) have no shorts; ratio defaults to 1.0 and the `bal` factor treats it as neutral. |

Lower premium ratio = the short leg is doing more work to defray the cost of the long, which means more downside cushion if the underlying goes the wrong way. The `bal` factor uses `1/√premium_ratio` so a 4× ratio cuts the score by ~50% relative to a 1× ratio.

#### `BE` — break-even price(s) at the target expiry

The underlying price(s) where P&L crosses zero at the target evaluation date.

- **Single-strike directional** (long call, long put): one break-even — `strike + debit/100` for calls, `strike − debit/100` for puts.
- **Verticals / iron spreads**: one or two break-evens where the payoff line crosses the credit/debit threshold.
- **Calendars / diagonals**: two break-evens computed by bisection on the short-expiry P&L curve (long leg is BS-priced at residual time, short leg is intrinsic). Shown as `BE $X.XX/$Y.YY` — lower / upper bound of the profitable range.

Compare BE to current spot to read cushion at a glance: if spot is $25.00 and `BE $24.92`, you have $0.08/share of room before the trade enters the loss zone.

#### `POP` — probability of profit

The probability that `S_T` lands in the profitable region at the target expiry under the **risk-neutral log-normal distribution** (`σ` = the IV used for pricing, `T` = years to target). Computed as `N(d2)` for "above" gates, `1 − N(d2)` for "below" gates, or as the integrated tail mass between break-evens for two-sided structures.

`POP 38.0%` means: under the IV-implied distribution, there's a 38% chance the underlying settles in territory that gives this trade a positive P&L. Note the distribution uses risk-neutral drift (no expected return premium), so POP under-estimates real-world probability for bullish structures and over-estimates for bearish ones — useful for relative ranking, not as a literal forecast.

The scoring engine doesn't use POP linearly — it uses `(POP / 0.50)⁴` capped at 1.25 (the `pop` factor) so trades below 50% POP get cut sharply and trades above 50% get a modest boost.

#### `EV` — expected value at the target expiry

```
EV = Σ weight_i × pnl_at_expiry(S_T_i)
```

The expected P&L per contract, computed by integrating the structure's piecewise-linear payoff against a 5-point log-normal scenario grid (default `±1σ`, `±0.5σ`, `0σ`) weighted by the standard normal density. **EV is signed and already net of debit/credit** — it's the bottom-line number the model expects you to walk away with.

**Worked example.** Suppose you opened a long call for $0.92/share (= $92/contract debit) and the panel shows `EV $60.92`:

| Field | Value | Meaning |
|---|---|---|
| Per-contract debit | −$92.00 | Cash you put in |
| EV at expiry | +$60.92 | Expected P&L on top of (or in spite of) the debit |
| Expected exit value | $152.92 | What you'd recoup on average across the IV distribution |

So the model says: across the spread of outcomes the IV implies, the average outcome leaves you up $60.92/contract. Roughly 38% of outcomes (`POP`) finish profitable; the *magnitude* of those wins outweighs the smaller-but-more-probable losing outcomes, which is why EV is positive despite POP being below 50%.

What EV is *not*:
- It is not your most-likely outcome — it's a probability-weighted average across all five grid points.
- It is not a guarantee — the log-normal model misses skew, jumps, and earnings effects.
- It is not directly comparable across structures with different `daysToTarget`. The `raw` score normalizes by dividing EV by both days and capital-at-risk.

The scoring engine uses EV as the numerator of `raw` (`raw = EV / days / capital_at_risk`), which is why a $60.92 EV on $92 risk over 30 days produces a much higher raw score than the same $60.92 EV on $1000 risk over 90 days.

### Score chain

The opener pipeline produces four scores, each derived from the previous one. Higher is better; the final score is what the ranker uses for top-N selection per ticker.

```
raw  →  tech-adjusted  →  adjusted  →  final
```

#### 1. `raw` — payoff per dollar of risk per day

```
raw = EV / max(1, daysToTarget) / capitalAtRisk
```

`EV` is the expected value at the target expiry, computed by integrating the structure's piecewise-linear payoff against a 5-point log-normal scenario grid centered on spot at the IV-implied volatility. `capitalAtRisk` is the structure's broker margin requirement (covered structures use the debit; verticals/condors use width × 100 − credit). Returns 0 when `capitalAtRisk ≤ 0`.

#### 2. `tech-adjusted` — directional bias from technicals

```
tech-adjusted = raw × (1 + α · bias · fit)
```

`bias` is the composite technical score in `[−1, 1]` from the same SMA/RSI/momentum signals used by the OpportunisticRoll filter. `fit` is `+1` for bullish-fit structures (long call, short put vertical), `−1` for bearish-fit (long put, short call vertical), `0` for neutral structures (calendars, condors, butterflies — these get *no* tech adjustment regardless of bias). `α` = `opener.directionalFitWeight`. When `fit = 0`, this stage is a no-op.

#### 3. `adjusted` — multiplicative factor stack

`tech-adjusted` is multiplied by every factor whose precondition is met. Each factor is documented below in the order the rationale prints them.

| Factor | What it measures | How it's computed |
|---|---|---|
| `pop` | Probability of profit at target expiry | `clamp((POP / 0.50)⁴, 0.01, 1.25)`. POP = log-normal probability of `S_T` landing inside the profitable region. The 4th-power amplification means a 70% POP boosts ~1.7× over 50%, while 30% POP cuts to ~0.13×. |
| `scale` | Capital efficiency vs absolute size | `clamp(√(risk / (risk + 100)), 0.35, 1)`. A self-normalizing curve: a $50 risk scores ~0.58, a $200 scores ~0.82, a $1000 scores ~0.95. Penalizes tiny-risk trades whose `raw` score is misleadingly inflated. |
| `setup` | Spot position inside the breakeven band — *defined-range structures only* | For condors/butterflies/iron flies: combines an *edge factor* (√ of the safer breakeven distance over half-width) and a *center factor* (1 − offset²). Both clamp to `[0.10, 1]`. Returns `null` for directional structures (no penalty). |
| `geom` | Diagonal "rent coverage" — *long diagonal / double diagonal only* | Per matched leg pair: `rentFactor = 0.55 + 0.45 × clamp(short_credit / long_debit, 0, 1.25)`, then a small `gapPenalty` for unusually wide strike gaps. Product across all pairs, clamped `[0.20, 1]`. Diagonals where the short pays more than the long extracts get the full 1.10 boost. |
| `runway` | Long-leg adjustment runway after the target — *diagonals/calendars with longer-dated longs* | Average of (extrinsic ratio × residual-days ratio) across long legs, mapped to `clamp(1 + 0.18 × ratio, 1, 1.35)`. Rewards structures where the long leg has both meaningful time premium *and* meaningful days remaining after the short expires. |
| `bal` | Payoff balance: R/R asymmetry vs premium efficiency | `clamp(√min(R/R, 3) / √max(1, premium_ratio), 0.25, 1.25)`. `R/R = max_profit / abs(max_loss)`; `premium_ratio = long_paid / short_received`. High R/R with thin debit → boost; low R/R with bloated debit → cut. Continuous, no thresholds. |
| `vol` | IV/HV richness vs structure preference | `clamp(1 + weight × clamp(IV/HV − 1, −1, 1) × fit, ≥ 0.10, …)`. `fit = +1` for short-vol structures (short verticals, iron flies), `−1` for long-vol structures (long calls/puts, calendars, diagonals). Rewards short-premium structures when IV is rich vs realized; rewards long-premium structures when IV is cheap. |
| `pain` | Max-pain alignment with the proposed strikes | `clamp(1 + maxPainWeight × signal, ≥ 0.10, …)`. Signal blends *breakeven-band coverage* (45%), *side-of-spot agreement* (35%), and *short-strike pinning* (20%) for neutral structures; for directional structures the signed distance from spot to max-pain × `fit` is used directly. |
| `assign` | Assignment-risk discount for ITM-leaning short legs | Penalizes structures where the short leg sits dangerously close to or past spot given the strike step and current technical bias. |
| `arb` | Stat-arb edge: market mid vs Black-Scholes theoretical | `clamp(1 + statArbWeight × clamp(edge / gross, −1, 1), ≥ 0.10, …)`. `edge = theoretical_net − market_net`, `gross = theo_long + theo_short`. Positive edge means the market entry is favorable to whoever opens the structure (paid less than fair on a debit, received more than fair on a credit). Same sign for both directions because the signed-net difference encodes direction inherently. |
| `liq` | Worst-leg liquidity penalty | `clamp(1 − weight × (1 − spread_component × oi_component), 0.30, 1.00)`. Spread component is `√max(0, 1 − (worst_leg_spread − 0.05) / 0.45)` — full credit at ≤5% bid/ask spread, decays toward 0.30 as the worst leg approaches 50% wide. OI component is `max(√(min_oi / 200), 0.40)` for OI ≥ 5, hard-floor 0.30 below that. Because exit cost is gated by the *worst* leg, both components are computed against the worst-liquidity leg in the structure. The factor reflects forward-looking exit friction; for `analyze position`/`analyze risk` it always uses the *current market* quotes even when the score's pricing math is locked to cost basis. |

The `Factors` line in the panel prints only the factors that fired for this structure — single-leg long calls, for example, will not show `geom` or `setup`.

#### Liquidity hard filter (opener pipeline)

In addition to the `liq` score factor, the opener pipeline applies a *hard reject* before scoring. Any candidate where:

- the worst leg's bid/ask spread exceeds `opener.liquidity.maxBidAskSpreadPct` (default 0.50 = 50%), **or**
- the worst leg's open interest is below `opener.liquidity.minOpenInterest` (default 5)

is dropped silently. These are doomed-exit structures — even a great fair-value score can't compensate for the liquidity friction at exit.

The `analyze risk` and `analyze position` commands do *not* apply the hard filter (you may already be in a position with poor liquidity and need to evaluate it). They still surface the `wide_spread` and `thin_open_interest` rules, and the `liq` factor continues to penalize the score.

**Config keys** (`opener.liquidity`):

| Field | Default | Description |
|---|---|---|
| `maxBidAskSpreadPct` | 0.50 | Hard-reject worst-leg spread threshold, as fraction of mid. Set to 1.0 to effectively disable. |
| `minOpenInterest` | 5 | Hard-reject worst-leg OI threshold. Set to 0 to disable. |
| `weight` | 0.50 | Strength of the multiplicative `liq` factor on survivors. Higher = sharper penalty for borderline-liquidity candidates. |

#### 4. `final` — theta carry

```
final = adjusted × thetaFactor(theta_per_day, capital_at_risk)
       = adjusted × (1 + clamp(theta / risk × 1.5, 0, 0.25))
```

Adds up to a +25% boost when net theta is positive and large relative to capital at risk. Long-vol structures (theta ≤ 0) get a flat 1.0 here. The ranker sorts by `final` descending, then `adjusted`, then `theta_per_day`, then prefers earlier-expiry calendars/diagonals.

### Risk rules (the `Rules fired` block)

Twelve rules run unconditionally against `RiskDiagnosticFacts`; only those that match attach to the diagnostic. Rules are informational — they do *not* change the score. They surface concerns or geometry observations a human reviewer should know about before acting on the structure.

| Rule ID | Triggers when | What it tells you |
|---|---|---|
| `short_leg_low_extrinsic` | Short leg has `DTE ≤ 2` **and** `extrinsic < $0.30` | Little harvestable theta remains — the short can't deliver meaningful decay before expiry. |
| `directional_exposure` | `abs(net_delta) > 0.25` per contract | Position carries material directional risk; AI consumers correlate with `DirectionalBias` to judge intent fit. The message includes the implied $/contract per $1 underlying move. |
| `premium_ratio_imbalanced` | Net debit structure **and** `long_paid / short_received > 3×` | Short leg provides limited cushion — most of the cash outlay is on the long side, which decays faster than the short can offset. |
| `geometry_bullish_covered_diagonal` | Structure is `covered_diagonal` with bullish bias | Informational: gains on rally, loses on drop. Adds `trend_aligned` (1 if 5-day move agrees with the bullish bias, 0 otherwise). |
| `geometry_bearish_inverted_diagonal` | Structure is `inverted_diagonal` with bearish bias | Informational: gains on drop, loses on rally. Adds `trend_aligned` (1 if 5-day move agrees with the bearish bias, 0 otherwise). |
| `short_expires_before_long` | At least one short leg expires *strictly before* the latest long leg | After the short expires you hold a naked long with `net_delta_post_short` residual delta (re-evaluated at long_DTE − short_DTE remaining time). |
| `vega_adverse` | `net_vega < −$5` per contract per IV point | Position loses on IV expansion — typically short calendars / short iron spreads. |
| `directional_mismatch_near_term` | Trend available, bias non-neutral, **and** 5-day move > 3% against the bias | Bias runs against the recent 5-day trend; delta exposure is fighting the tape. |
| `directional_mismatch_today` | Trend available, intraday non-null, `abs(net_delta) > 0.25`, **and** intraday move > 1% against the delta sign | Entered against today's direction — useful for "should I wait?" decisions before submitting. |
| `high_realized_vol` | ATR(14) % > 4% of spot | Underlying is moving more than usual — position is exposed to larger-than-typical adverse swings. |
| `wide_spread` | Worst leg has bid/ask spread > 25% of mid | Exit cost is dominated by liquidity friction, not fair value. Mid quotes are not transactable; closing the structure walks the book against you. |
| `thin_open_interest` | Worst-leg OI < 50 contracts | Thin OI signals poor market-maker engagement — quotes are wide, fills walk the book, exiting a multi-contract position can move the price against you. |

Each fired rule renders as a colored bullet with its ID and an interpolated message that includes the actual measured values. The same `Inputs` dictionary is serialized to the JSONL log, making historical rule-fires queryable with `jq`.

### How to read the panel

1. **Read `Structure` and `Premium` first** — confirm the classification and net cash match what you intended to enter.
2. **Check `Greeks`** — does the directional/theta/vega exposure match your thesis?
3. **Read the `Rules fired` block** — these are concerns the system flagged; address them or accept them consciously.
4. **Walk the `Score` chain** — `raw` tells you the unbiased payoff/risk/day; the `Factors` line shows which structural properties helped or hurt; `final` is what the ranker uses.
5. **For execution decisions**, the `Margin` and `Result` lines give you the broker-collateral and theta-carry numbers needed to size the trade against available cash.

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

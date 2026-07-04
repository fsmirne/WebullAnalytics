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
- **GEX Heatmap**: 2D dealer-gamma-exposure heatmap over the option chain (strikes × expirations) with chain totals and call/put walls
- **Broker Utilities**: Preview/place/cancel orders, close/flatten open option positions, inspect status and fill history, pull the cash-record ledger, list app subscriptions, check positions, and manage OpenAPI trade tokens
- **AI Proposal Engine**: Run one-shot, watch-loop, historical-replay, or full historical-backtest evaluation for management proposals and new-opening ideas
- **Real-NBBO Option Data**: The backtester prices every leg off real minute NBBO (the `data/quotes.db` SQLite store) with real open interest (`data/oi`) — vendor-independent stores filled by a ThetaData historical backfill and live Schwab/Webull capture (no synthetic-spread/trade-bar pricing)
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

`wa` has nine top-level commands:

- `report` — generate realized P&L reports and open-position break-even analysis
- `analyze` — run hypothetical trade, roll, position, risk, GEX, sentiment, and regime analysis
- `fetch` — download order data from the Webull web API
- `ledger` — pull the Webull cash-record activity ledger (running-balance feed) on demand
- `sniff` — capture fresh Webull session headers for the web API
- `trade` — preview/place/cancel/close orders and inspect OpenAPI account state
- `ai` — emit management and opening proposals from live or replayed data
- `data` — back up/restore the local data directory and maintain the SQLite quote store
- `schwab` — authenticate the Schwab chains API used as an alternate live quote source

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

# Live option-chain data is fetched automatically from Webull (requires sniffed headers via
# the 'sniff' command). The break-even time-decay grid is populated from the fetched chain.
# Override implied volatility for specific option legs (per OCC symbol):
wa report --iv GME260213C00025000:50,GME260516C00025000:45

# Show P&L instead of contract value in the grid
wa report --display pnl

# Show each leg's contract value alongside the net in every grid cell
wa report --grid verbose

# Increase grid granularity (more rows between strikes, default: 2)
wa report --range 4

# Override the current underlying price (for "what-if" evaluation)
wa report --spot GME:24.88,SPY:580.50

# Use Black-Scholes theoretical prices instead of market mid for today's grid column
wa report --theoretical

# Back-solve each leg's IV from its live bid/ask mid so the grid's future columns decay on the
# market-consistent surface instead of Webull's reported IV (today's column already shows mid)
wa report --calibrated

# Add custom notable prices to break-even reports (e.g., support/resistance levels)
wa report --levels GME:20/25/30

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
  --range <granularity>     Grid granularity: rows per strike gap in the time-decay grid (default: 2, higher = more rows)
  --display <mode>          Grid display mode: 'value' (contract value, default) or 'pnl' (profit/loss)
  --grid <layout>           Grid cell layout: 'simple' (net only, default) or 'verbose' (per-leg values '1.23|0.45|$0.78')
  --spot <prices>           Override underlying spot price(s). Format: TICKER:PRICE (e.g., GME:24.88,SPY:580.50)
  --theoretical             Use Black-Scholes theoretical price instead of market mid for today's grid column
  --calibrated              Back-solve each leg's IV from its live bid/ask mid so the today column reproduces market mid and future columns decay on the mid-consistent surface (instead of Webull's reported IV)
  --levels <prices>         Additional reference price levels (support/resistance, targets) to show in break-even reports. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30,SPY:580/590)
  --tickers <list>          Show only these tickers in the report. Comma-separated (e.g., GME,SPY,AAPL)
  --help, -h                Show help message
```

### Analyze Command

The `analyze` command has seven subcommands:

- `analyze trade` — inject hypothetical trades into the report pipeline for what-if analysis.
- `analyze roll` — show a 2D grid of theoretical roll credit/debit across underlying prices × times using Black-Scholes.
- `analyze risk` — render a structured risk diagnostic for an option structure using live quotes.
- `analyze position` — analyze an existing or manually specified option position and rank adjustment scenarios.
- `analyze gex` — render a 2D dealer-gamma-exposure (GEX) heatmap over the option chain (strikes × expirations) plus chain totals and call/put walls.
- `analyze sentiment` — render the current CNN Fear & Greed Index (score, rating, historical comparison, and component breakdown).
- `analyze regime` — show the blended directional bias the live opener uses (daily technical composite + VIX term structure + intraday tape), its decomposition, and a per-structure-family ranking with flip-margin sweep.

All subcommands accept the `report` command's options plus `--date` for simulating a future evaluation date. Some subcommands add extra flags documented below.

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
- **PRICE**: Required. A decimal (e.g., `0.50`) or a market-price keyword (`BID`, `MID`, `ASK`, case-insensitive). Keywords trigger a live Webull quote fetch (requires sniffed headers via the `sniff` command).

Examples:

```bash
# What if I roll the calendar short from Apr 10 to Apr 17 for $0.24 credit?
wa analyze trade "sell:GME260410C00023000:300@0.14,buy:GME260417C00023000:300@0.38"

# Same roll but use live market prices (buy at ask, sell at bid)
wa analyze trade "sell:GME260410C00023000:300@BID,buy:GME260417C00023000:300@ASK"

# Use mid-market prices for both legs
wa analyze trade "sell:GME260410C00023000:300@MID,buy:GME260417C00023000:300@MID"

# What if I close 100 contracts of my long call?
wa analyze trade "sell:GME260501C00023000:100@0.70"

# What if I add a protective put?
wa analyze trade "sell:GME260501P00022000:455@0.25"

# Simulate running on a future date (e.g., after short leg expiration)
wa analyze trade "buy:GME260417C00023000:300@0.38" --date 2026-04-11

# Combine with report options (output to text, override underlying price)
wa analyze trade "sell:GME260410C00023000:300@0.14,buy:GME260417C00023000:300@0.38" --output text --spot GME:23.20
```

When using `BID`, `MID`, or `ASK`, the command fetches live quotes from Webull before building the hypothetical trades. The synthetic trades are appended after all real trades and processed through the full report pipeline — FIFO matching, strategy grouping, break-even analysis, and rendering all work normally. The original trade files are never modified.

#### `analyze roll`

Computes the theoretical roll credit/debit at various underlying prices using Black-Scholes, helping you find the optimal moment to roll a leg.

```
wa analyze roll "<spec>" [--side long|short] [--pair <SYMBOL:QTY>] [--cash <amount>] [--iv <overrides>] [--date <YYYY-MM-DD>] [report options]
```

The `<spec>` is `OLD_SYMBOL>NEW_SYMBOL:QTY`. Live Webull quotes are fetched automatically (requires sniffed headers via the `sniff` command).

`--side` selects the math:

- **`short`** (default): closes a short on the OLD strike (buy at ask), opens a short on the NEW strike (sell at bid). Credit = `new_bid − old_ask`.
- **`long`**: closes a long on the OLD strike (sell at bid), opens a long on the NEW strike (buy at ask). Credit = `old_bid − new_ask`.

Examples:

```bash
# Short-side roll of the $23 short from Apr 10 to Apr 17 (300 contracts)
wa analyze roll "GME260410C00023000>GME260417C00023000:300"

# Same roll but I'm long the old position
wa analyze roll "GME260410C00023000>GME260417C00023000:300" --side long

# Roll to a different strike
wa analyze roll "GME260410C00023000>GME260417C00023500:300"

# Override IV for the analysis
wa analyze roll "GME260410C00023000>GME260417C00023000:300" --iv GME260410C00023000:37,GME260417C00023000:31

# Short-side roll paired with a static long call leg (calendar/diagonal margin)
wa analyze roll "GME260424C00025000>GME260424C00024500:499" --pair GME260515C00025000:499

# Short-side roll paired with long stock (covered-call margin)
wa analyze roll "GME260515C00025000>GME260522C00025000:5" --pair GME:500

# Check whether your available cash is enough to fund the roll
wa analyze roll "GME260424C00025000>GME260424C00024500:499" --pair GME260515C00025000:499 --cash 23015
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

Reference levels from `--levels` are included as additional rows in the grid.

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
wa analyze risk "sell:GME260501C00025500,buy:GME260522C00026000"

# Evaluate a 10-lot using explicit cost bases
wa analyze risk "sell:GME260501C00025500:10@0.38,buy:GME260522C00026000:10@0.12"

# Supply spot manually instead of fetching an underlying quote
wa analyze risk "sell:GME260501C00025500,buy:GME260522C00026000" --spot GME:24.88
```

The command appends a machine-readable record to `data/analyze-risk.jsonl` after each run.

#### `analyze position`

Loads an existing open option strategy from `data/orders.jsonl` or accepts a manual position spec, then ranks hold/roll/reset scenarios with projected P&L and buying-power impact.

```
wa analyze position ["<spec>"] [--iv-default <pct>] [--strike-step <step>] [--cash <amount>] [--account <alias>] [--date <YYYY-MM-DD>] [--log-level <level>] [report options]
```

If `<spec>` is omitted, the command scans open strategy positions from the trade log and lets you pick one interactively.

`--log-level` is `error | information` (default) `| debug`. Under `debug` the scenario output adds a put-call-parity implied-dividend diagnostic (`PV(div) = S − (C − P) − K·e^(−rT)`) and surfaces the otherwise-hidden opposite-side double-structure add candidates.

Manual `<spec>` format:

```
ACTION:SYMBOL:QTY@PRICE,ACTION:SYMBOL:QTY@PRICE,...
```

Examples:

```bash
# Pick an existing open strategy from orders.jsonl and auto-detect available cash/BP from api-config.json
wa analyze position --account test1

# Analyze a manually specified calendar position
wa analyze position "sell:GME260424C00025000:499@0.48,buy:GME260515C00025000:499@1.11" --cash 23015

# Analyze a single long call with a custom scenario strike step
wa analyze position "buy:GME260620C00025000:10@1.25" --strike-step 0.50 --spot GME:24.88
```

The command prints ranked scenarios, emits ready-to-run `wa trade place` and `wa analyze trade` reproduction commands, and appends a machine-readable record to `data/analyze-position.jsonl`. The scenario table shows a probability-weighted **EV** P&L alongside the spot-pinned **Projected** value and ranks scenarios by EV-P&L-per-day; it also includes a "scale up existing" comparator that any proposed add must beat. For a held calendar/diagonal, the add block proposes the opposite-side double (calendar = same strike, diagonal = one strike further OTM), Black-Scholes-pricing the unquoted far leg (marked `[estimated]`), and flags the best add by incremental return per added margin.

#### `analyze gex`

Renders a 2D gamma-exposure heatmap over the option chain — rows = strikes (descending), columns = expirations (ascending). Cell hue encodes net polarity (green = call-dominated, red = put-dominated); cell brightness encodes |net GEX| relative to the chain max. Bold + underlined cells mark the per-expiry gravity strike (max gross gamma — call gamma×OI + put gamma×OI). The bold yellow strike row is the at-the-money strike. Below the heatmap, the command prints chain totals (call GEX, put GEX, gross, net, net fraction) and the top call walls (resistance) and put walls (support) ranked across the visible window.

```
wa analyze gex <ticker> [--expiry <YYYY-MM-DD>] [--dte <n>] [--strike-range <pct>] [--max-strikes <n>] [--top-walls <n>] [--source webull|schwab] [--dump] [--intraday [--interval <min>] [--exante]] [--spot <TICKER:PRICE>] [--date <YYYY-MM-DD>]
```

Per-strike call GEX = `gamma(strike, spot, dte, iv) × callOI × 100 × spot`. Put GEX is the same with putOI. Net = call GEX − put GEX (signed); gross = call GEX + put GEX (always non-negative).

Examples:

```bash
# Daily grid: 0DTE through 14 days out, ±20% strike window, 50 strikes closest to spot
wa analyze gex SPY

# Today's 0DTE only, or a single pinned expiry
wa analyze gex SPY --dte 0
wa analyze gex GME --expiry 2026-05-15

# Cross-check vendors (Schwab needs `wa schwab login`) and capture the raw per-strike IV/OI inputs
wa analyze gex SPY --source schwab --dump

# Historical/offline: rebuild the heatmap for a past date from the data/oi snapshot (no network)
wa analyze gex SPY --date 2026-06-05

# 0DTE intraday heatmap — GEX recomputed at each 30-min bucket's spot; --exante uses prior-day IVs
# so the picture shows what was hedgeable at the time instead of leaking the session's EOD outcome
wa analyze gex SPY --date 2026-06-05 --intraday --exante
```

Live runs require Webull API session headers (`data/api-config.json`) — run `wa sniff` first if missing (or `--source schwab` after `wa schwab login`). Yahoo isn't supported because chain-level GEX needs full OI + IV across every expiry, which only Webull's `strategy/list` + `queryBatch` combination reliably returns. Webull's `strategy/list` only inlines OI/IV for the front-most expiration, so the command refreshes in-window non-front-month contracts via `queryBatch` before computing the matrix. Every live run appends what it computed (gravity / walls / flip / max-pain per expiry, source-tagged) to `data/gex` so vendor-IV-dependent values stay reproducible later.

#### `analyze sentiment`

Fetches and renders the CNN Fear & Greed Index: the headline score (0–100) and rating, a historical comparison (previous close, 1 week / 1 month / 1 year ago), the seven component sub-indicators, and a short interpretation. This is the same sentiment signal the opener blends in via `opener.weights.sentiment` (contrarian regime overlay), exposed standalone for inspection.

```bash
# Current reading
wa analyze sentiment

# Reading as of a past date (for backtest cross-checks)
wa analyze sentiment --date 2026-04-07
```

> Macro overlay only — single-name catalysts (earnings, FDA, M&A) routinely override the index on a given ticker.

#### `analyze regime`

The directional-trend companion to `analyze gex` / `analyze sentiment`. Shows the blended scoring `bias` the live opener uses — daily technical composite + VIX term structure + intraday tape, directional-agreement calibrated — with its per-component decomposition, plus a per-structure-family ranking driven by the real opener and a flip-margin sweep (how much the bias would have to move before the top family changes).

```bash
# Live regime read for the ticker's merged AI config
wa analyze regime XSP

# Historical: score the regime as of a past day/time from the captured minute NBBO (data/quotes.db)
wa analyze regime XSP --date 2026-06-12 --time 09:35
```

It accepts the shared AI ticker/config options (`--strategy`, `--source`, `--log-level`, …) plus `--account` for the live position read, and `--date <YYYY-MM-DD>` / `--time <HH:mm>` for offline historical mode.

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
  --account <alias>       Account alias or ID from api-config.json used to auto-detect available cash/BP
                          when you select an existing open position.

analyze roll only:
  --side <long|short>     Position side. Default: short. See above for the math each side uses.
  --pair <SYMBOL:QTY>     Static paired leg for spread margin calculation. SYMBOL is an equity ticker or OCC option
                          symbol; QTY is a positive integer. Only meaningful with --side short.
  --cash <amount>         Available cash for funding the roll. Prints a Net surplus/shortfall line that combines
                          cash with the roll's natural-market credit/debit and compares against the BP delta.
                          Only meaningful with --side short.

analyze gex only:
  --expiry <date>         Restrict to a single expiration (YYYY-MM-DD). Default: show the --dte window.
  --dte <n>               Days-to-expiry cap: every expiry from 0DTE through N days out. Default: 14.
  --strike-range <pct>    Strike window as ± percent of spot. Default: 20.
  --max-strikes <n>       Max strike rows. Picks the N strikes closest to spot within --strike-range. Default: 50.
  --top-walls <n>         Number of top call/put walls to list in the resistance/support panels. Default: 5.
  --source <vendor>       Chain vendor for the live fetch: webull (default) or schwab (needs `wa schwab login`).
  --dump                  Append every in-window contract's raw bid/ask/IV/OI to data/iv/<TICKER>/<date>.csv, source-tagged.
  --intraday              0DTE intraday heatmap (offline-only, needs --date + a data/oi snapshot): strikes × RTH time buckets.
  --interval <min>        --intraday bucket size in minutes. Default: 30.
  --exante                --intraday with the PRIOR day's snapshot IVs instead of back-solved same-day EOD mids (no outcome leakage).
```

### Fetch Command

```bash
# Fetch order data from the Webull API
wa fetch
```

Reads API credentials from `data/api-config.json` and writes orders to `data/orders.jsonl`.

### Ledger Command

```bash
# Pull the 50 most recent cash-record entries (deposits, settlements, fees, ...)
wa ledger

# Pull more history (1-200)
wa ledger -n 200
```

Pulls the Webull cash-record activity ledger — the running-balance feed the platform shows — on demand, rendered oldest-first so the most recent activity is at the bottom. Uses the same scraped session as `fetch`/`sniff`; refresh expired headers with `wa sniff`. Useful for reconciling cash timing questions (e.g. ITM cash-settled expiries post here immediately while Webull's balance view lags ~T+1).

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

The `trade` command reads accounts from `data/api-config.json` (same file used by `fetch` / `sniff` / `ai`). Add an `accounts` array and a `defaultAccount` alias — see `api-config.example.json` for the full shape.

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

# Close an open option position: bare = interactive picker, <id> = close by broker position ID
# (unique prefix is enough), --all = flatten every open option position. Previews and prompts
# like `place`; --submit skips the prompt, --pricing mid|bidask picks patient vs marketable limits.
wa trade close
wa trade close 12345678 --pricing bidask
wa trade close --all --submit

# List all open orders for the account.
wa trade list

# List today's filled orders (read-only diagnostic; --all includes cancels etc.)
wa trade history

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
  --account <id-or-alias>   Pick an account from api-config.json. Defaults to defaultAccount.
  --submit                  Actually place the order. Without this, runs preview only.
  --debug                   Print the raw JSON payload that will be sent to the Webull API.

Options (cancel):
  <clientOrderId>           Client order ID of the order to cancel.
  --all                     Cancel every open order for the account.
  --account <id-or-alias>   Pick a non-default account.

Options (close):
  [id]                      Broker position ID to close (unique case-insensitive prefix accepted).
                            Omit for an interactive picker over open option positions.
  --all                     Close every open option position at the broker.
  --pricing <mode>          Per-leg limit pricing: mid (patient, default) or bidask (marketable, crosses the spread).
  --tif <tif>               day or gtc. Default: day.
  --submit                  Place without the y/N confirmation prompt.
  --account <id-or-alias>   Pick a non-default account.

Options (history):
  --start-date <date>       Start date yyyy-MM-dd. Default: today ET. Max 2-year look-back.
  --end-date <date>         End date yyyy-MM-dd. Default: start-date + 1.
  --all                     Include CANCELLED/REJECTED/WORKING orders. Default is FILLED-only.
  --debug                   Dump the raw JSON response instead of the formatted table.
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

- `trade accounts` lists the account subscriptions returned by the OpenAPI app and highlights the `account_id` value you should copy into `api-config.json`.
- `trade positions` prints a readable account-positions summary; add `--debug` to dump the raw payload.
- `trade token create` starts the OpenAPI trade-token approval flow and caches the token locally.
- `trade token check` re-checks the cached token status and updates the local cache.

#### Sandbox vs production

Each account in `api-config.json` has a `sandbox: true|false` flag. Sandbox accounts hit `https://us-openapi-alb.uat.webullbroker.com`; production accounts hit `https://api.webull.com`. A colored banner (green `[SANDBOX]` / red `[PRODUCTION]`) is printed at the top of every `trade` invocation so you always know which environment you are in.

There is no `--yes` flag — every place, cancel, and cancel-all prompts interactively. Piped empty input aborts.

### AI Command

The `ai` command evaluates live or replayed positions and emits structured proposal logs. It can emit both management proposals (roll / take-profit / stop-loss / defensive-roll) and opening candidates for new positions. It is **read-only in phase 1**: the command never places orders.

Seven subcommands — `scan` / `watch` / `replay` / `backtest` share one evaluation engine, `history` manages the caches they read, `config` inspects/creates the layered config files (`show` prints the fully-resolved effective config after all merges, `init` emits a complete base config with every parameter at its code default and everything disabled, `format` normalizes one), and `dip` is a standalone dip-signal research tool (see below):

```bash
# Continuous monitoring during market hours (default: until 4 PM ET)
wa ai watch SPXW

# Single evaluation pass, print proposals, exit
wa ai scan GME

# Replay the rules against historical orders.jsonl with agreement analysis
wa ai replay GME --since 2026-01-01 --until 2026-04-17

# Simulate the full strategy (opener + rules + intraday SL/TP) from scratch over a historical window
wa ai backtest SPXW --since 2025-01-01 --starting-cash 10000 --show-fills

# Refresh the daily bar / VIX / SMILE caches + intraday minute bars that the backtest needs
wa ai history SPXW

# Audit the intraday cache — reports missing/partial days without making network calls
wa ai history SPXW --audit

# Run the watch loop every 30 seconds for 90 minutes, ignoring market-hours checks
wa ai watch SPXW --tick 30 --duration 90m --ignore-market-hours

# Emit only opening ideas
wa ai scan GME --proposals open

# Study the intraday dip signal (RSI + Bollinger + MACD-sign) over a history of 1-min CSVs
wa ai dip SPXW --since 2025-01-01 --interval 5
```

The ticker is a required positional argument — every AI subcommand operates on exactly one ticker per run, and the config layer is selected by that argument.

#### Setup

1. Copy the base config:
   ```bash
   cp ai-config.example.json data/ai-config.json
   ```
2. Edit `data/ai-config.json` — this is the *base* and holds settings that apply to every ticker (rules, watch, log, position source, indicator parameters, default opener weights and structure DTEs). It deliberately does not contain ticker-specific tuning like the bid/ask `strikeStep` increment.
3. For each ticker you trade, create `data/ai-config.<TICKER>.json` containing only the keys that differ from the base. At minimum it must set `indicators.strikeStep` (the validator rejects 0). For example, a minimal GME override is just:
   ```json
   { "indicators": { "strikeStep": 0.50 } }
   ```
   A 0DTE SPXW override would also bump `intradayTape` / `vixTermStructure` weights, swap structure DTEs to 0, and adjust `ivDefaultPct` — keep only what differs from your base.
4. Run `wa ai scan <TICKER>` (or watch / replay). The loader deep-merges `data/ai-config.json` and `data/ai-config.<TICKER>.json`, with the per-ticker file winning on every overlapping key.
5. Ensure `data/api-config.json` has populated `accounts[]` and `defaultAccount` — the loop reads position state from the Webull OpenAPI.

**Config layering rules:**
- JSON objects merge recursively by key (override wins on overlap).
- Arrays and scalar values are *replaced* by the override (not concatenated). So `widthSteps: [5]` in the per-ticker file completely supersedes `widthSteps: [1, 2, 3]` in the base.
- The per-ticker file is required in practice because the base has no default `strikeStep`. If it's missing entirely, the loader falls back to whatever single file is present.
- An optional third **strategy layer** `data/ai-config.<TICKER>.<STRATEGY>.json` merges on top of the per-ticker file. Select it with `--strategy <STRATEGY>` or a `defaultStrategy` key in the per-ticker file — this is how one ticker carries multiple tuned strategies (e.g. `ai-config.SPY.0DTE.json` vs `ai-config.SPY.DC.json`) and how backtest experiments run isolated from the live config (`ai-config.SPY.test1.json` + `--strategy test1`).
- `wa ai config show <TICKER> [--strategy TOK]` prints the fully-resolved effective config, so what you see is exactly what runs.

#### Config Sections

The config file has four top-level functional sections plus the usual top-level plumbing (`watch`, `positionSource`, `cashReserve`, `log`).

**`indicators`** — pipeline-wide inputs read by BOTH the opener and the management rules. Centralized here so duplication is impossible.

| Key | Purpose |
|---|---|
| `ivDefaultPct` | Fallback IV when a leg has no live quote. Stored as a percentage. |
| `strikeStep` | Strike-grid increment in dollars. Ticker-specific — must be set in the per-ticker override. |
| `technicalFilter` | Composite technical bias (SMA5/20, RSI(14), N-day momentum, optional 200-day trend). Feeds the opener's macroBias AND the opportunistic-roll rule's bullish/bearish block gates. |
| `intradayTape` | Per-component config for the intraday tape signal (bar interval, lookback, gap/openToNow/VWAP weights). The blend weight that decides how much this matters lives in `opener.weights.intradayTape`. |
| `events` | Earnings + ex-div veto policy (blackout window, short-call ex-div rejection, override file path). |

**`opener`** — settings specific to opening new positions: which structures to enumerate, the multiplicative-factor weights on the candidate score chain, output limits, and per-trade risk caps.

The 12 scoring weights live under `opener.weights` (formerly flat `*Weight` fields at the opener root):

| Weight | Purpose |
|---|---|
| `directionalFit` | Strength of the technical-bias adjustment on the per-structure score (post-hoc tilt for bullish vs bearish setups). |
| `biasDrift` | Shifts the scenario-grid center by `bias × biasDrift × sigma` when computing realized EV. Critical for long-premium structures (LongCall/LongPut) whose negative raw EV can never be flipped positive by sign-symmetric ApplyFactor. **Aggressive values (≥ 2.0) heavily favor long calls/puts over neutral structures** when bias is strongly directional. |
| `whipsaw` | Penalty on credit structures when 3-day realized vol >> 30-day. |
| `volatilityFit` | Vega-aware HV-vs-IV fit factor. |
| `maxPain` | Pin-strike attraction factor. |
| `gex` | Dealer-gamma exposure factor (pin + regime). |
| `statArb` | Market-vs-theoretical mispricing factor. |
| `sentiment` | Contrarian Fear & Greed regime overlay. |
| `expectedMoveCredit` | EM-vs-short-strike credit-trade safety factor. |
| `ivRealizedPremium` | IV-vs-HV regime-alignment factor (credit favored when IV > HV). |
| `vixTermStructure` | Blend weight for the VIX9D/VIX term-structure regime signal. |
| `intradayTape` | Blend weight for the intraday tape signal (0DTE wants 0.5–0.8; swing wants 0.0–0.2). |

**`rules`** — management rule triggers and thresholds. Each rule's config holds only the gates specific to that rule (stop-loss multipliers, take-profit percentages, roll-specific thresholds). The `opportunisticRoll` block contains `bullishBlockThreshold` / `bearishBlockThreshold` — composite-bias score boundaries that block call rolls in extended-bullish setups and put rolls in extended-bearish setups.

**`watch`** — long-running `wa ai watch` loop knobs. `tickIntervalSeconds` sets the poll cadence (also the off-hours sleep when waiting for the bell). `startTime` (optional, format `HH:mm` or `HH:mm:ss` ET) schedules the first tick — useful when you want orders placed a few seconds after 09:30 rather than at the exact bell; CLI `--start` overrides.

#### Rules

Rules evaluate per-position in priority order — the first rule to match for a position wins; lower-priority rules are skipped for that position in that tick. Ties on priority are broken alphabetically by rule name. All thresholds are configurable in `ai-config.json`.

| Rule | Priority | Trigger |
|---|---|---|
| `StopLossRule` | 1 | MTM debit ≥ 1.5× initial, or spot beyond break-even by > 3% |
| `CloseBeforeShortExpiryRule` | 2 | Short DTE = 0 and either MTM profit ≥ `minProfitPct` of initial debit, or spot is past the BE band ± `emergencyBreakEvenBufferPct` (emergency close) |
| `LegInShortRule` | 2 | Single-leg long call/put goes ITM (≥ `minSpotPctITM`%), long delta ≥ `minLongDelta`, profit ≥ `triggerProfitPct` of debit, DTE ≥ `minDTE`, and a short at `targetShortDelta ± shortDeltaTolerance` exists with credit ≥ `minShortCreditPerShare`. Optional VIX / intraday-range regime gates |
| `OpportunisticRollRule` | 2 | A roll scenario improves P&L-per-day by at least `minImprovementPerDayPerContract` vs holding, and passes all four safety gates |
| `TakeProfitRule` | 2 | MTM ≥ `pctOfMaxProfit` of the peak net value in the current-date column of the time-decay grid, **or** MTM profit ≥ `profitTargetPctOfDebit` of the initial net debit (fires any day; default 0 = off) |
| `CompleteCondorRule` | 3 | Held short vertical whose side has earned real distance from spot: sells the opposite-side vertical at the same expiry to complete an iron condor (LegIn with two legs). Mirrors LegInShort's regime/delta-band/min-credit gates — the gates bound the trend-day risk of re-arming a loss on the freshly-sold side. Default off — backtest-negative on our configs; kept as an A/B knob |
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

#### LegInShortRule

Converts a single-leg long call/put into a vertical by selling a higher-strike short (debit-spread mode) or a deeper-ITM short (credit-spread mode). The intent is to lock in some profit on a winner that's gone gamma-saturated — the long keeps part of its delta, but capping upside is fair when each additional dollar of move is worth less in premium than the day's theta.

Emits `ProposalKind.LegIn` — distinct from `Close` and `Roll` because no existing leg is touched; the rule strictly *adds* a short leg. The backtest's `SimulatedBook.LegIn` preserves the long leg and basis, charges a single combo fee + slippage cross.

For 0DTE strategies the rule fires intraday: the backtest's minute-walk evaluates each open position at every minute and triggers the leg-in at the first qualifying minute. Multi-day positions get evaluated at start-of-day in the main rule loop.

**Modes:**

- **Debit-spread** (`creditSpread: false`, default) — sells an OTM short above the long strike (calls) / below (puts). Resulting structure: `LongCallVertical` / `LongPutVertical`. Net cash flow is a credit (collected on the short) reducing the original debit. Caps upside at the short strike.
- **Credit-spread** (`creditSpread: true`) — sells a deeper-ITM short below the long strike (calls) / above (puts). Resulting structure: `ShortCallVertical` (bear-call) / `ShortPutVertical` (bull-put). Monetizes the long's current ITM-ness immediately; credit collected typically exceeds the original debit. Caps upside at the long strike (since spot is already past the new short).

**Regime gates** (both default to `999` = disabled):

- **`maxVix`** — skip leg-in when VIX is at or above this level. High-VIX regimes have fat-tail moves; capping a winner during those moves gives up massive upside. Backtest tuning on SPXW 0DTE finds **18** is the sweet spot; sensitivity range [15, 20] all positive, ≥22 turns negative.
- **`maxIntradayRangePct`** — skip leg-in when today's running `(high − low) / open` (as percent) is at or above this. "Trend-day" filter; weaker than the VIX filter alone in our backtests.

**Config fields** (`rules.legInShort`):

| Field | Default | Description |
|---|---|---|
| `enabled` | false | Master gate |
| `minSpotPctITM` | 1.0 | Spot must be at least this % ITM relative to the long strike |
| `minLongDelta` | 0.65 | Long-leg absolute delta floor (gamma-saturation gate) |
| `triggerProfitPct` | 0.50 | Profit-to-date as fraction of initial debit; must meet or exceed |
| `minDTE` | 5 | Min days to expiry on the long; below this the short carries too little premium |
| `targetShortDelta` | 0.30 | Target |Δ| for the short. In credit-spread mode set to ~0.70 |
| `shortDeltaTolerance` | 0.05 | Tolerance band around `targetShortDelta` |
| `minShortCreditPerShare` | 0.30 | Minimum per-share credit from the short. Credit-spread mode wants ~$5+ |
| `creditSpread` | false | Mode flag (see above) |
| `maxVix` | 999.0 | Skip when VIX ≥ this. Sentinel 999 disables |
| `maxIntradayRangePct` | 999.0 | Skip when today's range ≥ this percent. Sentinel 999 disables |

**Tuned SPXW 0DTE example** (per-ticker override):

```json
"legInShort": { "enabled": true, "minSpotPctITM": 0.5, "minDTE": 0, "maxVix": 18.0 }
```

Backtest result on `2025-01-01 → 2026-05-22` (SPXW 0DTE, $10K start): +$210K (+7.4% over baseline), DD 4.16% vs 4.46% baseline. Robust across years — both 2025 and 2026 individually positive. See `data/ai-config.SPXW.tuning.md` for the full sweep.

#### Output

Proposals are written to two places:

- **Console**: Spectre-formatted, color-coded by action (close = yellow, roll = cyan, alert-only = grey). Open-proposal panel headers carry a `#N` prefix matching the ranked output order so you can refer to a candidate by position (e.g. `#3 LongCalendar GME x166`); the counter resets at the start of each `wa ai scan` and at each `wa ai watch` tick. Each proposal shows the legs and net credit/debit, followed by ready-to-run `wa trade place` and `wa analyze trade` commands, and the rule rationale. Double calendars and double diagonals render as a single panel listing both halves under `Put side:` / `Call side:` rows; because Webull cannot place a 4-leg double-calendar ticket, the panel emits two `wa trade place` lines (one per side, each with its own per-share limit) and a single `wa analyze trade` covering all four legs.
- **JSONL log** at `data/ai-proposals.<TICKER>.<STRATEGY>.jsonl` (one file per ticker+strategy layer): one proposal per line, machine-parseable with `jq` or similar. Includes `mode` field ("scan" / "watch" / "replay") to distinguish source runs.

Under `--log-level debug`, `wa ai scan` additionally lists the best positive-EV candidate of each enabled-but-unselected structure (typically the double calendar / double diagonal, which score ~25× below the singles) as non-executable **Informational** rows — a visibility aid that never auto-executes and is suppressed at `information` / `error`.

AI commands accept `--pricing mid|bidask` to control both displayed command prices and the pricing basis used in proposal math. Default: `mid`.

Shared AI options:

```
  <ticker>                 Required positional ticker (e.g. SPXW, GME). Loads ai-config.json + ai-config.<TICKER>.json (deep-merged).
  --strategy <token>       Strategy layer to merge on top: ai-config.<TICKER>.<STRATEGY>.json. Overrides the config's defaultStrategy.
  --output <format>        console or text. `text` writes to a default .txt file when --output-path is omitted
  --output-path <path>     Optional path for --output text
  --log-level <level>      debug | information | error
  --proposals <mode>       all | open | management
  --pricing <mode>         mid | bidask

ai scan + watch (live commands):
  --source <vendor>        Live option-chain vendor: webull (sniffed-session chain, default) or schwab (chains API NBBO;
                           needs `wa schwab login`). IV is re-based to the NBBO mid for both vendors.
  --dump                   Diagnostic: dump the first few raw Webull chain/queryBatch HTTP responses to data/quote-dumps/.
  --account <alias>        Account alias or ID from api-config.json; overrides defaultAccount for the position read and any executions.
  --submit                 Override autoExecute.{management,opener}.submit=true for this run (keep config at dry-run, flip live from the CLI).
  --tif <value>            Override autoExecute time-in-force: DAY or GTC.

ai scan only:
  --top <N>                Override opener.topNPerTicker from ai-config.json
  --theoretical            Bypass the live chain; price via Black-Scholes against an explicit --spot (pre-market / weekend planning).
  --premarket              Live chain, but spot is back-solved from put-call parity on the ATM straddle (chain active, underlying not ticking yet).
  --date <date>            With --theoretical: asOf date. Default: next business day.
  --spot <spec>            Spot override(s), TICKER:PRICE. Required with --theoretical; optional live override.
  --starting-cash <amt>    With --theoretical: account balance for sizing. Default: live broker balance.
  --submit-override        Like --submit but ignores the per-day opening cap for this ONE run (scan-only escape hatch).

ai watch only:
  --tick <seconds>         Override watch.tickIntervalSeconds
  --start <HH:mm[:ss]>     Wait until this ET time before first tick. Overrides watch.startTime.
  --duration <duration>    Stop after a duration such as 6h, 90m, or 30s
  --ignore-market-hours    Run regardless of market-hours checks

ai replay only:
  --since <date>           Start date YYYY-MM-DD. Default: earliest fill
  --until <date>           End date YYYY-MM-DD. Default: latest fill
  --granularity <level>    daily or hourly. Default: daily

ai history only:
  --lookback-years <N>     History window to ensure. Default: 2.
  --audit                  Report per-day intraday completeness without network calls (exit 2 on gaps).
  --import-webull-spx <f>  One-time SPX deep-history bootstrap from a browser-captured bar dump.
  --partial                Capture today's incomplete intraday tape up to now (recovers a missed live watch session; file stays unsealed).

ai backtest only:
  --since <date>           Start date YYYY-MM-DD. Default: Jan 1 of current year.
  --until <date>           End date YYYY-MM-DD. Default: today.
  --starting-cash <amt>    Starting cash balance. Default: 10000.
  --quote-db <path>        Override the SQLite quote-store path. Default: data/quotes.db.
  --fee-per-contract <amt> Per-leg-contract commission. Ticker defaults: SPX/SPXW $1.14, XSP $0.55, NDX $1.30, equity/ETF $0.05.
  --iv-hv-premium <ratio>  IV/HV multiplier for non-SPY tickers (SPY uses real VIX). Default: 1.15.
  --smile <mode>           Volatility smile model for BS-fallback legs: 'off' or 'static'. Default: static.
  --top-per-step <n>       Maximum new opens per trading day. Default: 1.
  --scan-stride <n>        Evaluate every Nth minute for entries. Default: 1 (every minute, matching live watch);
                           coarser strides alias past single-minute score crossings and under-count opens.
  --lots <n>               Fixed contracts per trade, cash gates bypassed — measures per-trade edge (expectancy,
                           profit factor) without the compounding/sizing feedback loop.
  --split                  Book split structures (double calendars/diagonals, diagonal/calendar verticals) as their
                           TWO combo orders, managed independently against their own debits — exactly how Webull holds them.
  --tp / --sl <value>      Override rules.takeProfit.pctOfMaxProfit / rules.stopLoss.pctOfMaxLoss for this run (0..1; 1.0 = off).
  --show-fills             Print per-fill ledger in addition to the summary.
  --fills-jsonl <path>     Also write each fill as a JSON line. Useful for parameter-sweep scripts.
  --oracle                 Research mode (by-design lookahead): forward-simulate each minute's proposal to expiry
                           and open the (minute, proposal) pair with the highest realized P&L. Upper bound only.
  --profile                Print a per-step wall-time breakdown at the end of the run.

ai backtest sweep knobs (override the merged config for this run — parameter exploration without copying config files):
  --bias-drift <v>              opener.weights.biasDrift
  --min-score-to-open <v>       opener.minScoreToOpen
  --intraday-tape-weight <v>    opener.weights.intradayTape (0..1)
  --intraday-w0 <v>             opener.intradayTapeDteCurve.weightAt0Dte — DTE-aware tape-blend curve (0..1)
  --gex-bias-pull <v>           opener.weights.gexBiasPull (GEX magnet grid-shift; 0 disables)
  --gamma-regime <v>            opener.weights.gammaRegime (net-gamma regime tilt; 0 disables)
  --max-pain <v>                opener.weights.maxPainBiasPull (max-pain magnet; 0 disables)
  --long-conviction <v>         opener.longConvictionGate.weight — de-rate low-conviction long-premium trades (0..1)
  --open-after <HHMM>           opener.earliestEntryTimeEt — withhold opens until this ET time
  --enable-structure <name>     Force-enable a structure for this run (repeatable)
```

#### Auto-execution (scan + watch)

`wa ai scan` and `wa ai watch` can both optionally submit Close proposals (rule-driven) and Open proposals (opener-driven) automatically when `autoExecute.management.enabled` / `autoExecute.opener.enabled` are set in `ai-config.json`. Off by default; the executors log the action they *would* take until `submit: true` is also set. The `management.rules` allow-list controls which rules can fire executions — by default only `CloseBeforeShortExpiryRule` is permitted. Scan triggers each executor once per invocation; watch triggers them on every tick.

**Broker-truth dedup:** before any live submission the auto-executors call Webull's `ListOpenOrders` endpoint (`BrokerStateService.RefreshAsync`) and fingerprint each pending order by its leg-set + side. Any proposal whose leg set matches a pending order is skipped. This covers:

- The same proposal across ticks (current proposal still pending)
- Across process restarts (other process's pending orders show up too)
- Manually-placed orders (user's `wa trade place` or Webull-app orders also block bot duplicates)
- The "limit placed but never filled" case (position still looks single-leg but a working order exists)

**Fail-closed on API errors.** If the broker query fails, the executor returns 0 and does nothing this tick — an order not placed is reversible, an over-placed order is not. There is no local-cache fallback by design.

**Opener per-day cap:** `autoExecute.opener.maxOrdersPerDay` (default 1, matching the backtest's `--top-per-step 1`) caps total LIVE opens per trading day. The count is `today's-opened positions` (from the position source's `OpenedAt` field) + opens issued in this tick. Dry-runs are NOT capped — they continue to emit so you can monitor what would be placed.

**Scope of the cap:** OPEN proposals only. Closing or managing existing positions is never throttled by `maxOrdersPerDay`. Management rules flow through `ManagementAutoExecutor`, which has its own logic:

- `Close` proposals (StopLoss, TakeProfit, CloseBeforeShortExpiry, rolls): no daily cap — every position hitting its trigger gets actioned. Tranche bookkeeping is still in-memory (a tranche order that didn't fill will appear in the broker pending list, so the leg-set match catches duplicates cross-process).
- `LegIn` proposals (LegInShortRule): broker-truth dedup only. Add `LegInShortRule` to `autoExecute.management.rules` to enable live execution. The rule's own structural guard prevents re-firing once a leg-in fills (position becomes a vertical, rule rejects); the broker-pending check covers the unfilled case.

**Edge case:** if you place an order manually via the Webull app, the bot's broker-state view picks it up. If that manual order looks like a bot-style open (same leg shape), the bot will skip its own open. Acceptable trade-off given Webull doesn't differentiate bot orders from manual ones (a future improvement could prefix `client_order_id` with a bot marker).

For Close proposals at or above `management.scaleOut.minQty` contracts, the executor splits the close into three time-windowed tranches (default 10:00–10:30 / 12:30–13:00 / 15:00–15:30 ET). The final tranche always closes whatever remains, so partial fills earlier in the day still converge to a fully-closed position by the last window. Smaller closes fire as a single order.

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

#### Historical backtest

`wa ai backtest` runs the full strategy end-to-end against a historical window: opener picks new positions, rules manage them, intraday SL/TP triggers fire on the minute, expirations settle at intrinsic. Reports a P&L summary plus an optional fill ledger.

Prerequisites:

1. Run `wa ai history <TICKER>` once per session. This populates `data/history/<TICKER>.csv` (daily underlying closes from Yahoo), the VIX / VIX1D / VIX9D / SMILE caches the backtest engine reads (all four fetched from CBOE's own daily-prices CSVs at full published history — CBOE is authoritative for its indices and Yahoo's mirror silently drops days and freezes mid-session rows, so it is not used even as a fallback), the sentiment / event / dividend caches, and `data/intraday/<TICKER>/<date>.csv` minute bars for every missing trading day in the lookback window. Existing intraday CSVs are never overwritten (preserves data from live `wa ai watch` captures); today is owned by live capture and never touched. Underlying intraday source routing:
   - **Non-SPX tickers (SPY, AAPL, QQQ, …)**: pulled from massive.com (Polygon mirror — SIP-consolidated NMS data). One range query covers the whole window; rate limits (5 req/min basic tier) and pagination are handled internally. Requires `massiveApiKey` in `api-config.json`.
   - **SPX-family (SPX/SPXW)**: SPX RTH from Webull's `query-mini` chart endpoint (the index isn't on massive.com), SPY ext-hours from massive scaled by the session's SPX/SPY ratio for pre/post-market filler.
2. **One-time SPX deep-history bootstrap** (`wa ai history SPXW --import-webull-spx <file>`): Webull's `query-mini` SPX endpoint requires per-URL `x-s` signatures we can't forge programmatically for deep historical anchors. To populate the 2-year historical SPXW window, run the browser console sniffer snippet (in `docs/` or paste in DevTools) on Webull's web SPX 1-min chart, scroll back ~2 years, and dump the captured bars to a text file. Then `--import-webull-spx <file>` parses those bars, pulls SPY ext-hours from massive in a single range query, and writes per-day CSVs. After bootstrap, the live capture and per-day Webull pagination keep the cache current.
3. `wa ai history <TICKER> --audit` reports per-day completeness (complete / partial / missing) over the on-disk window without making any network calls. Reconciles `sealed.json` (the manifest that tracks early-close days and other LooksComplete-passing files) so re-running the audit auto-seals any newly-validated CSV. Exit code 0 if everything is complete, 2 if there are gaps.

Mechanics:

- **Opener minute loop.** For each trading day, the runner walks `data/intraday/<TICKER>/<date>.csv` minute by minute. At each minute it re-prices the chain at the minute's `bar.Open` spot with a remaining-session TTE, evaluates the opener, and opens the first proposal that clears `opener.minScoreToOpen` + cash + qty gates. If no minute crosses, no fill for the day. Falls back to a single 09:30 fill when no minute data exists.
- **Intraday SL/TP.** Replaces the legacy bar.High/bar.Low 2-point sampling with a chronological minute walk: re-prices each open position at every minute's spot and fires SL or TP at the first real crossing. Skips the walk entirely when both thresholds are at 1.0 (effectively off), so the existing TP-off / SL-off SPXW config carries zero added overhead.
- **Pricing.** Real minute NBBO from the SQLite quote store (`data/quotes.db`) — marks at mid, fills cross the actual spread. Legs the store doesn't cover fall back to Black-Scholes + SMILE-scaled skew, with the ATM anchor read from the matching VIX tenor (VIX1D for 0–1 DTE, VIX9D for 2–9, VIX for 10+). The summary reports per-leg pricing provenance so you can gate results on real-quote coverage; a window with no real NBBO at all warns instead of silently reporting no fills.
- **Broker realism.** Models Webull's ~15:30 ET forced liquidation of ITM 0DTE short legs on physically-settled roots (cash-settled index roots like XSP/SPXW are exempt) and settles expirations at bar.Close intrinsic.
- **Oracle mode (`--oracle`).** Forward-simulates each minute's proposal to expiry intrinsic and opens the (minute, proposal) pair with the highest realized P&L for that day. Lookahead by design; use to size the gap between the realistic scan and a perfectly-timed entry.

Example invocations:

```bash
# Full-year SPXW backtest with the fill ledger and a starting account of $10k
wa ai backtest SPXW --since 2025-01-01 --starting-cash 10000 --show-fills

# YTD only, oracle ceiling (research)
wa ai backtest SPXW --since 2026-01-01 --starting-cash 10000 --oracle

# Profile where wall time goes (useful when a recent change makes a backtest slower)
wa ai backtest SPXW --since 2025-01-01 --starting-cash 10000 --profile

# Stream each fill as JSON for parameter-sweep tooling
wa ai backtest SPXW --since 2026-01-01 --starting-cash 10000 --fills-jsonl /tmp/sweep.jsonl
```

The fill ledger (`--show-fills`) carries per-trade and account-level return columns: `P&L %` (this trade's realized return as % of the opening debit/credit basis) and `Return` (cumulative realized return through the account, vs starting cash). Both blank on `Open` rows — nothing is realized until the lineage finalizes.

#### `ai dip`

A standalone research tool (not part of the live evaluation engine) that studies an intraday "dip" signal — MACD histogram negative ∧ RSI(14) oversold ∧ close below the lower Bollinger band — over a history of 1-minute CSVs, aggregated to a chosen bar interval. It reports signal frequency and forward statistics, and can overlay several hypothetical trade structures (all model estimates — no real option quotes unless `--real-chain` is used).

```bash
# Signal study over the year, 5-min bars, also list the first 20 signals
wa ai dip SPXW --since 2025-01-01 --interval 5 --list 20

# Swing round-trip mode: enter after a dip clears, exit after a top clears (RSI > --rsi-high)
wa ai dip SPXW --exit-on-top

# Round-trips bucketed by VIX regime, or filtered to VIX-gap-up days only
wa ai dip SPXW --exit-on-top --by-vix
wa ai dip SPXW --exit-on-top --vix-gap-up

# Price the round-trips off the real scraped 0DTE chain (naked-call / call-vert / put-credit)
wa ai dip SPXW --exit-on-top --real-chain --delta 0.25 --width 2
```

Key flags: `--rsi-low` / `--rsi-high` / `--bb-k` / `--interval` shape the signal; `--first-only` collapses a multi-bar dip to its first trigger; `--exit-on-top` switches from a frequency study to a one-at-a-time intraday swing (and is required by `--by-vix`, `--vix-gap-up`, and `--real-chain`); `--call-dte` / `--call-spread` and `--put-spread` / `--put-width` / `--put-friction` / `--put-stop` / `--put-skew` add BS-modeled call and 0DTE-credit-spread overlays; `--real-chain` (`--delta` / `--width` / `--legs`) replaces the model with the real scraped chain on days a snapshot exists.

### Option Data (quotes + OI)

`wa ai backtest` prices every leg off **real minute NBBO** — there is no synthetic-spread / trade-bar pricing path. Two canonical, vendor-independent stores back it (same format regardless of source, so a historical backfill and a live capture are interchangeable):

- **`data/quotes.db`** — a single SQLite database of minute NBBO time-series (ticker, expiry, minute, strike, right, bid, ask, sizes). The price/fill foundation: marks at mid, fills cross the real spread. Override per run with `--quote-db`; maintain with `wa data vacuum / optimize / check / stats`.
- **`data/oi/<TICKER>/<DATE>.jsonl`** — one daily full-chain snapshot (`underlyingPrice` + `options[]` with `openInterest` and `iv`). OI is constant intraday, so one record/day feeds the GEX / max-pain factors.

How they're filled:

- **Historical backfill** — `scripts/daily_backfill.sh` (published alongside the installed executable) drives `backfill_thetadata.py`, pulling minute NBBO and EOD open interest from ThetaData and importing into the SQLite store via `import_quotes_sqlite.py`. Tickers/DTE are passed at runtime (`--tickers NAME:DTE`); end-of-bar stamps are normalized to start-of-bar (−60s) at ingest. Run it evenings (≥19:00 ET) so the current day lands finalized; integrity checks via `backfill_thetadata.py --verify-quotes`. (ThetaData Value ≈ $40/mo, cancellable — re-subscribe to fill any gap.)
- **Forward capture** — the `wa-scraper` (Schwab primary, Webull backup) writes the same stores live, ±10% strike-banded.

> The old `wa options` subsystem (discover / backfill / audit / chain / reprice), the per-expiry CSV quote store, and the massive.com option-bar catalog were all retired with the move to real NBBO in SQLite. massive remains only as an underlying intraday-tape source (see the AI history section).

### Data Command

`wa data` snapshots and restores the local data directory, and maintains the SQLite quote store.

```bash
# Back up the top-level setting files (configs, orders, ledgers — ~MBs) to a timestamped tar.gz under <BaseDir>/backups/
wa data backup

# Full backup including the market-data subdirectories (quotes/, oi/, intraday/, ... — many GB)
wa data backup --full

# Back up to a specific path
wa data backup -o /mnt/d/wa-backups/before-sweep.tar.gz

# Restore the most recent backup (refuses to overwrite an existing data/ without --force);
# --settings restores only the top-level setting files, leaving data subdirectories untouched
wa data restore
wa data restore -i /mnt/d/wa-backups/before-sweep.tar.gz --force

# SQLite quote-store maintenance (data/quotes.db; every command takes --db for an alternate store)
wa data vacuum     # rebuild to reclaim freelist pages + truncate the WAL (slow; run while scraper/backfill are idle)
wa data optimize   # PRAGMA optimize (cheap, safe any time); --analyze forces a full ANALYZE
wa data check      # integrity check
wa data stats      # row counts and on-disk footprint (incl. WAL sidecars)
```

Backups default to **settings-only** (a full backup is opt-in via `--full`). With `--force`, `data restore` renames any existing `data/` to `data.bak.<timestamp>/` before restoring over it; a settings-only payload restores as an overlay.

### Schwab Command

Authenticates the Schwab chains API, which serves as an alternate live option-quote source (`--source schwab` on `ai scan` / `ai watch` / `analyze gex` / `analyze regime`) and as the primary vendor for the `wa-scraper` forward capture. Credentials live in `api-config.json`.

```bash
# Three-legged OAuth: prints the authorize URL, takes the pasted post-login redirect URL,
# exchanges the code for tokens, and stores the refresh token (valid 7 days — re-login weekly)
wa schwab login

# Show whether tokens are present and how long until the access/refresh tokens expire
wa schwab status
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
    "levels": null,
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

When implied volatility is available (from the auto-fetched Webull chain or `--iv` overrides), the break-even panel for each option position includes a 2D time-decay grid showing how the position value changes across underlying prices (rows) and dates (columns).

- **Date columns**: Evenly-spaced dates from today through expiration, evaluated at market open (9:30 AM). The last two columns show expiration day at market open (with remaining intraday time value via Black-Scholes) and "At Exp" at market close (4:30 PM, intrinsic value only). The number of columns adapts to the terminal width — dates are skipped as needed so the grid never overflows horizontally.
- **Price rows**: Step size is derived from the position's strike spacing (or strike-to-break-even distance for single-strike positions like calendars), so the grid adapts to any stock price. The `--range` parameter controls granularity — it sets how many rows fit per strike gap (default: 2). Break-even prices (marked with `*`) and strike prices are always included, with 2 padding rows beyond the outermost notable price. The current underlying-price row is marked with `>` and rendered in bold yellow.
- **Display modes**: `--display value` (default) shows the contract value per share; `--display pnl` shows total P&L.
- **Cell layout**: `--grid simple` (default) shows one value per cell (the net). `--grid verbose` prefixes each cell with the per-share Black-Scholes contract value of every leg, separated by `|` — e.g. `1.23|0.45|$0.78` for a two-leg spread. Leg order matches the leg descriptions shown above the grid, and a legend below repeats the order (e.g. `LC23|SC23.5|Net`).
- **Cell colors**: Green for profit, red for loss. Legs in verbose mode render in grey.
- **Calendar/diagonal spreads**: The grid ends at the short leg's expiration. The long leg's remaining time value is reflected via Black-Scholes pricing at each date, including the "At Exp" column.
- **Dividend adjustment**: Legs that trade through an ex-dividend date are priced on a discrete-dividend-adjusted (escrowed) forward — the present value of each cash dividend in the leg's window is subtracted from spot. This stops the grid from overstating a calendar/diagonal long leg that straddles an ex-date (the short leg, expiring before it, is unaffected). The ex-dividend schedule comes from the event calendar, with `opener.events.dividendYield` / `dividendFrequency` as the fallback when only an ex-date (no amount) is known.
- **IV calibration (`--calibrated`)**: By default each leg prices on Webull's reported IV, so the today column is anchored to market mid but the future columns can drift from where the market would mark them. With `--calibrated`, each leg's IV is back-solved from its live bid/ask mid (on the same dividend-adjusted spot), so the future columns decay on a mid-consistent surface. A user `--iv` override still wins; legs without a usable two-sided quote fall back to the reported IV. The leg's volatility line shows the reported IV struck through next to the calibrated value, tagged `cal`.

Without implied volatility data, the existing 1D price ladder (Price | Value | P&L at expiration) is shown instead.

## Volatility Analysis

When the Webull chain fetch succeeds (the default — requires sniffed headers), the break-even leg descriptions include volatility metrics for each option contract:

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

These metrics are sourced from the Webull option chain API. Webull is the default option-quote source (Schwab's chains API is the cross-check alternative via `--source schwab`) — Yahoo Finance lacks Greeks, HV, and iv5 and runs on a delay, so it was retired from the quote path. Yahoo is still used for daily underlying closes (historical price cache), the earnings/ex-dividend calendar, and the risk-free rate (`^IRX`); the VIX-family index history (VIX / VIX1D / VIX9D) and the SMILE index come from CBOE's own daily-prices CSVs, never Yahoo.

## Risk Diagnostic Panel

The risk diagnostic is the structured snapshot rendered by `wa analyze risk`, `wa analyze position`, and the AI pipelines (`wa ai scan`, `wa ai watch`, `wa ai replay`) for every management proposal and opening idea. It is a single Spectre panel titled **Risk diagnostic** that combines: a fixed set of structural / pricing / Greek facts, an opener-style score with the multiplicative factor breakdown, optional probe rows (per-leg quotes, delta-band gate, broker margin), and the list of rule hits that fired against the structure.

The diagnostic is built by `RiskDiagnosticBuilder.Build` from the legs, current spot, an IV resolver, and an optional `TrendSnapshot`. Sixteen rules are evaluated unconditionally — only the ones that match attach as `Rules fired` lines. The same record is appended to `data/analyze-risk.jsonl` (for `analyze risk`) or `data/analyze-position.jsonl` (for `analyze position`) so historical diagnostics can be re-analyzed later.

### Panel rows

Every row is per-contract unless explicitly noted. "Per share" means $1 of underlying movement; multiply by 100 to get the per-contract dollar figure.

| Row | What it shows | How it's computed |
|---|---|---|
| `Structure` | Structure label and directional bias (e.g. `calendar (neutral)`, `covered_diagonal (bullish)`, `vertical_credit (bearish)`, `double_calendar (neutral)`, `double_diagonal (neutral)`) | `ClassifyStructure` inspects leg counts, expirations, strikes, and call/put types. Multi-expiry 4-leg structures with one strike per side resolve to `double_calendar`; offset long wings resolve to `double_diagonal`. The bias is *bullish*, *bearish*, or *neutral* depending on which side of spot the structure profits on. |
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
| `Rationale` | Always when an opener-style score exists | Single-line trade summary: side ($credit/$debit), max profit / max loss, R/R ratio, premium ratio, break-evens, one-sigma expected-move bounds, POP, EV. |
| `Indicators` | When max-pain, stat-arb, sentiment, etc. factors fired | Free-text breakdown: representative IV / HV richness with the position's net vega (`ν +X.XX/IV pt`), max-pain target, market-vs-theoretical edge per share, F&G rating. These feed the `vol`, `pain`, `arb`, and `sentiment` factors below. |
| `Score` | Always when an opener-style score exists | The score chain — `raw → tech-adjusted [bias tag] → final` — collapsed to a single line. See **Score chain** below for what each stage means. |
| `Factors` | Always when an opener-style score exists | The full multiplicative chain from *tech-adjusted* to *final*: `tech-adjusted × pop X × scale X × setup X × runway X × be-room X × em-cred X × iv-rv X × bal X × liq X × vol X × pain X × gex X × assign X × arb X × sentiment X × theta factor X (θ/day on $risk) = final`. Only factors that apply to the structure are shown; long chains wrap to a second balanced line under the `Factors:` label. |

### Reading the Rationale line

```
Rationale: debit $92.00, maxProfit $408.00, maxLoss $92.00, R/R 4.43, prem 1.00x, BE $24.92, EM $21.23/26.77, POP 38.0%, EV $60.92 (real $42.16, −$4.00 fric)
```

Every dollar figure is **per contract** (already multiplied by the 100-share multiplier). Quote conventions in the option chain are usually per share; the diagnostic converts everything to per-contract so it lines up with margin, EV, and ranking math. The trailing `(real $X.XX, −$Y.YY fric)` annotation surfaces the realized-expectancy adjustment — `real` is the EV the scorer actually used (managed-exit clamped + slippage subtracted), `fric` is the friction component alone. See **Realized expectancy** below for how the two pieces compose.

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

#### `EM` — one-sigma expected-move bounds at the target expiry

```
EM_lower = spot − spot × IV × √(tradingDays/252)
EM_upper = spot + spot × IV × √(tradingDays/252)
```

The price envelope the option chain implies for the holding period at one standard deviation. Trading-day denominator (Mon–Fri, no US-holiday adjustment) matches the same convention used by `be-room` and `em-cred` so the rendered range lines up with what those factors evaluated against. Shown as `EM $X.XX/$Y.YY` next to `BE` whenever IV and DTE are available — every structure prints it; for credit trades this is the cushion the `em-cred` factor scores against, for debit trades it's the move you need to clear the break-even.

Read it in combination with `BE`: if `BE $24.92` and `EM $21.23/26.77`, the lower-σ point ($21.23) is well outside the break-even — under the IV-implied distribution the move needed to clear BE is well within one σ. Conversely, if BE sits *outside* the EM band, the trade is asking for more than a one-sigma move to land in the money.

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

The opener pipeline produces three named scores. Higher is better; the final score is what the ranker uses for top-N selection per ticker.

```
raw  →  tech-adjusted  →  final
```

`raw` captures the structure's payoff math, `tech-adjusted` overlays the technical bias for directional structures, and `final` is `tech-adjusted` multiplied through the full factor stack (described below) including the theta-carry factor at the tail.

#### 1. `raw` — payoff per dollar of risk per day

```
raw = EV / max(1, daysToTarget) / capitalAtRisk
```

`EV` here is the *realized* expected value — theoretical EV clamped to the managed-exit window and reduced by slippage friction (see **Realized expectancy** below). `capitalAtRisk` is the structure's broker margin requirement (covered structures use the debit; verticals/condors use width × 100 − credit; gap-bearing multi-leg debit structures are charged `max(debit, capital-at-risk)` so wide wings pay for their gap). Returns 0 when `capitalAtRisk ≤ 0`.

#### 2. `tech-adjusted` — directional bias from technicals

```
tech-adjusted = raw × (1 + α · bias · fit)
```

`bias` is the composite technical score in `[−1, 1]` from the same SMA/RSI/momentum signals used by the OpportunisticRoll filter. `fit` is the structure's directional sign:

- `+1` for bullish structures: long call, short put vertical, long call diagonal where `long_strike < short_strike`, long put diagonal where `long_strike < short_strike` (positive net delta in either case).
- `−1` for bearish structures: long put, short call vertical, long call diagonal where `long_strike > short_strike`, long put diagonal where `long_strike > short_strike`.
- `0` for neutral structures: calendars, double calendars, double diagonals, iron condors, iron butterflies, and diagonals with matching strikes (which collapse to calendars).

`α` = `opener.weights.directionalFit`. When `fit = 0`, this stage is a no-op. Single-side long diagonals pick up their sign from the strike layout via the strike-aware `DirectionalFit.SignFor(skel)` overload, so a bullish-shaped diagonal aligns with positive bias and a bearish-shaped one aligns with negative bias.

##### Intraday tape blend — making `bias` responsive at the 0DTE horizon

The daily-bar SMA/RSI/momentum composite reflects multi-week price action, which is the wrong time scale for 0DTE: a position that lives 6.5 hours doesn't care that the 5-day SMA is mildly bullish if today's tape is selling off. The opener can blend an *intraday* composite into `bias` to close that horizon mismatch.

```
bias = (1 − w) · bias_macro + w · bias_intraday
```

`w` = `opener.weights.intradayTape` (default `0` — the blend collapses to macro-only and existing behavior is bit-identical). `bias_intraday` is built from the minute-bar series for the underlying with three sub-components:

- **`gap`** — `(today_open − prev_close) / prev_close` clamped to `[−1, 1]` after × 100. Captures overnight news / futures action.
- **`open-to-now drift`** — `(now_close − today_open) / today_open` clamped to `[−1, 1]` after × 100. The primary intraday trend signal.
- **`vwap deviation`** — `(now_close − session_vwap) / session_vwap` clamped to `[−1, 1]` after × 100. Catches price stretching away from the volume-weighted mean. Falls back to TWAP (equal-weight typical-price average) when bars carry no volume — cash indexes like SPX always need this fallback.

Composite: `bias_intraday = (gap · gw + o2n · ow + vwap · vw) / (gw + ow + vw)`. Weights configurable via `opener.intradayTape`; defaults emphasize open-to-now (2.0) over gap (1.0) and vwap-deviation (1.0).

Pipeline mechanics:

- **Bar source.** Webull's `/api/quote/charts/query` endpoint, fetched once per scan tick. Disk-cached at `data/intraday/<TICKER>/<yyyy-mm-dd>.csv` (keyed by the *strategy ticker*, matching `data/history/<TICKER>.csv` for daily closes). Today's file grows during the session; past days are sealed. The chart endpoint uses a *different* tickerId namespace than the option-chain endpoint for cash indexes (chain-namespace `913324359` is actually SPXC stock, not the index). Chart-namespace ids live in `WebullChartsClient.ChartKnownTickerIds`; chain-namespace ids stay in `WebullOptionsClient.KnownTickerIds`.
- **Transparent SPY pre-market proxy for the SPX family.** SPXW and SPX route through a hybrid path: SPX RTH bars (no extended hours — the cash index doesn't trade pre/post-market) merged with SPY extended-hours bars scaled into SPX dollars. Ratio derivation uses the most-recent minute that exists in both series (an in-session overlap), not the prior session's close — this tracks intraday SPX/SPY basis drift more accurately. The merged series is in SPX scale throughout — the indicator and cache never see SPY, no separate SPY folder is created, and the resulting bars land in `data/intraday/SPXW/`. On SPY resolution failure or insufficient ratio data, falls back to SPX RTH only. Note: `wa ai history` historical backfill uses the same SPX-plus-SPY-scaled architecture but sources SPY from massive.com (SIP-consolidated) rather than Webull intraday — see the AI section above for routing.
- **Backtest support.** Active in backtest with a no-op (disk-only) fetcher so the same `IntradayBarCache` reads the backfilled `data/intraday/<TICKER>/<date>.csv` files without any HTTP. Backtest's `ctx.Now` is the simulated minute, so the tape signal computes minute-by-minute against the same in-process bars the opener consumes. Falls back to macro-only when minute data is absent for a date (pre-backfill window).
- **Prev-close source.** Derived from the bar series itself — yesterday's last bar's close in the same intraday source. Necessary because the daily-Yahoo close and the intraday-Webull bars may not be denominated identically (gap math becomes meaningless when scales mismatch).

**Config keys** (`opener.weights`):

| Field | Default | Description |
|---|---|---|
| `intradayTape` | `0` | Blend weight in `[0, 1]`. `0` = macro-only. `0.5` = even split. 0DTE strategies typically want `0.5–0.8`; swing strategies `0.0–0.2`. An optional `opener.intradayTapeDteCurve` block makes the weight DTE-aware (`weightAt0Dte` at 0 DTE decaying toward the base weight for longer-dated trades). |

**Config keys** (`indicators.intradayTape` — the sub-component shaping; the blend weight itself is `opener.weights.intradayTape`):

| Field | Default | Description |
|---|---|---|
| `barIntervalCode` | `m1` | Bar interval as Webull's chart-endpoint type code (`m1`/`m5`/`m15`/`m30`/`h1`/`d1`). |
| `lookbackMinutes` | `7200` | Bar range request span. Must reach back to the prior trading session for bar-derived prev-close; default 5 calendar days survives weekends and short holiday breaks. |
| `minBars` | `5` | Minimum bars on today's session before the indicator is allowed to contribute. Below this returns null and `bias` collapses to macro-only. |
| `gapWeight` | `1.0` | Weight on the overnight-gap sub-component. |
| `openToNowWeight` | `2.0` | Weight on the open-to-now-drift sub-component. The primary intraday trend signal. |
| `vwapDeviationWeight` | `1.0` | Weight on the VWAP-deviation sub-component. |
| `includeExtended` | `false` | Request pre/post-market bars where the symbol supports them. Cash indexes (SPX, NDX) ignore this and return RTH only; ETFs and single names honor it. The SPX family already merges SPY extended-hours automatically regardless of this setting. |

#### 3. Factor stack — `tech-adjusted → final`

`tech-adjusted` is multiplied by every factor whose precondition is met, ending with the theta-carry factor that produces `final`. Each factor is documented below in the order the rationale prints them. The same factor stack applies to every structure; factors not applicable to a structure simply don't fire (e.g., `setup` requires two breakevens, so single-leg longs and verticals skip it).

| Factor | What it measures | How it's computed |
|---|---|---|
| `pop` | Probability of profit at target expiry | `clamp((POP / 0.50)⁴, 0.01, 1.25)`. POP = log-normal probability of `S_T` landing inside the profitable region. The 4th-power amplification means a 70% POP boosts ~1.7× over 50%, while 30% POP cuts to ~0.13×. |
| `scale` | Capital efficiency vs absolute size | `clamp(√(risk / (risk + 100)), 0.35, 1)`. A self-normalizing curve: a $50 risk scores ~0.58, a $200 scores ~0.82, a $1000 scores ~0.95. Penalizes tiny-risk trades whose `raw` score is misleadingly inflated. |
| `setup` | Spot position inside the breakeven band — *structures with two breakevens* | Arithmetic mean of an *edge factor* (√ of the safer breakeven distance over half-width) and a *center factor* (1 − offset²). Both clamp to `[0.10, 1]`; the mean inherits the same floor. Calendars, diagonals (single and double), iron flies, condors, and butterflies all have two breakevens and earn this factor; single-leg longs and verticals only have one breakeven and skip it. The mean (rather than the product the older formula used) preserves the "centered + safe > off-center + edgy" ordering while compressing the range — the product double-counted a property wide-band combos get by construction, structurally over-rewarding them against narrower trades whose raw EV/capital was actually stronger. |
| `runway` | Long-leg adjustment runway after the target — *calendars and diagonals with longer-dated longs* | Average of (extrinsic ratio × residual-days ratio) across long legs, mapped to `clamp(1 + 0.18 × ratio, 1, 1.35)`. Rewards structures where the long leg has both meaningful time premium *and* meaningful days remaining after the short expires. |
| `be-room` | Path-aware breakeven cushion in EM units | `clamp(tanh(edgeDistance / expectedMove), 0.10, 1)` where `expectedMove = spot × IV × √(tradingDaysToTarget / 252)`. `edgeDistance` is the nearer breakeven for two-BE structures (or `0.10` floor if spot is already outside the band), the absolute spot-to-BE gap for single-BE structures. Trading-days/252 denominator matches retail-desk EM convention — close to calendar/365 on long DTE but meaningfully different on short-DTE structures that span a weekend. Distinct from `setup`, which measures static centeredness rather than vol-time cushion. |
| `em-cred` | EM-vs-short-strike cushion — *credit trades only* | `max(0.10, 1 + expectedMoveCreditWeight × signal)` where `signal = clamp((minShortDistanceInEMs − 1) / 0.5, −1, 1)`. `minShortDistanceInEMs` is the nearest short-strike distance from spot expressed in one-sigma EM units (call shorts measured above spot, put shorts measured below; spot already past a short → negative). −1 saturates at ≤0.5σ cushion, +1 at ≥1.5σ. Distinct from `be-room`: the credit cushion makes BE look ~credit/share safer than the short strike, overstating safety; this factor reads the short strike, where assignment risk and the loss zone actually begin. Null for debit trades, structures without short legs, or degenerate inputs. |
| `iv-rv` | "Trade vs vol regime" alignment | `max(0.10, 1 + ivRealizedPremiumWeight × signal)` where `signal = ±clamp(IV/HV − 1, −1, 1)`. Sign is `+` for credit trades (favored when IV > HV — rich premium to collect) and `−` for debit trades (favored when IV < HV — cheap premium to pay). Distinct from `vol`: that one is vega-aware and barely fires on near-zero-vega credit verticals; this one fires on trade-type sign alone, so even low-vega structures still get a regime read. Null when HV is unavailable or `ivRealizedPremiumWeight = 0`. |
| `bal` | Payoff balance: R/R asymmetry vs premium efficiency | `clamp(√min(R/R, 3) / √max(1, premium_ratio), 0.25, 1.25)`. `R/R = max_profit / abs(max_loss)`; `premium_ratio = long_paid / short_received`. High R/R with thin debit → boost; low R/R with bloated debit → cut. Continuous, no thresholds. |
| `vol` | IV/HV richness × position vega sensitivity | `max(0.10, 1 − weight × clamp(netVega/vegaRef, −1, 1) × clamp(IV/HV − 1, −1, 1))`, with `vegaRef = 3` ($/IV pt). The factor is driven by the candidate's actual net vega (not a structure label): long-vega positions (calendars, DCs, long calls/puts) get boosted when IV is cheap relative to HV and cut when rich; short-vega positions (verticals, iron flies, iron condors) are mirror-image. Magnitude scales with vega depth — a fat-vega DC swings sharper than a thin-vega DD; a wide iron condor swings sharper than a narrow short put vertical. |
| `pain` | Max-pain alignment with the proposed strikes | `clamp(1 + maxPainWeight × signal, ≥ 0.10, …)`. Signal blends *breakeven-band coverage* (45%), *side-of-spot agreement* (35%), and *short-strike pinning* (20%) for neutral structures; for directional structures the signed distance from spot to max-pain × `fit` is used directly. |
| `gex` | Gamma Exposure alignment — GEX pin gravity + dealer regime | Two sub-signals combined: **pin signal** (60%) uses the same positional logic as `pain` against the GEX pin strike (the strike with highest net dealer gamma); **environment signal** (40%) is `clamp(NetGexFraction × volFitSign, −1, 1)`, where positive NetGexFraction (call gamma dominates) benefits short-vol structures and hurts long-vol ones. `factor = clamp(1 + gexWeight × (0.60 × pinSignal + 0.40 × envSignal), ≥ 0.10, …)`. Null when `gexWeight = 0` or when insufficient IV data prevents gamma computation for the target expiry chain. GEX is computed via Black-Scholes gamma × open interest per strike, signed (calls positive, puts negative), then summed. |
| `assign` | Assignment-risk discount for ITM-leaning short legs | Penalizes structures where the short leg sits dangerously close to or past spot given the strike step and current technical bias. |
| `arb` | Stat-arb edge: market mid vs Black-Scholes theoretical | `clamp(1 + statArbWeight × clamp(edge / gross, −1, 1), ≥ 0.10, …)`. `edge = theoretical_net − market_net`, `gross = theo_long + theo_short`. Positive edge means the market entry is favorable to whoever opens the structure (paid less than fair on a debit, received more than fair on a credit). Same sign for both directions because the signed-net difference encodes direction inherently. |
| `liq` | Worst-leg liquidity penalty | `clamp(1 − weight × (1 − spread_component × oi_component), 0.30, 1.00)`. Spread component is `√max(0, 1 − (worst_leg_spread − 0.05) / 0.45)` — full credit at ≤5% bid/ask spread, decays toward 0.30 as the worst leg approaches 50% wide. OI component is `max(√(min_oi / 200), 0.40)` for OI ≥ 5, hard-floor 0.30 below that. Because exit cost is gated by the *worst* leg, both components are computed against the worst-liquidity leg in the structure. The factor reflects forward-looking exit friction; for `analyze position`/`analyze risk` it always uses the *current market* quotes even when the score's pricing math is locked to cost basis. |

The `Factors` line in the panel prints only the factors that fired for this structure — single-leg long calls, for example, will not show `setup` (no two-breakeven band) or `runway` (no later-dated long leg). The chain is fair across structures: every kind competes through the same factor stack, and no factor preferentially punishes or rewards a structure based on its label alone.

#### Liquidity hard filter (opener pipeline)

In addition to the `liq` score factor, the opener pipeline applies a *hard reject* before scoring. Any candidate where:

- the worst leg's open interest is below `opener.liquidity.minOpenInterest` (default 5), **or**
- the worst leg's OI is below `opener.liquidity.minRelativeOpenInterest` (default 0.25) of the max OI among same-expiry near-spot strikes AND its absolute OI is below `opener.liquidity.minAbsoluteOpenInterest` (default 100)

is dropped silently. These are doomed-exit structures — even a great fair-value score can't compensate for the liquidity friction at exit. Bid/ask spread is *not* a hard gate (a single dominant wide quote was wiping entire chains on lightly-traded names); it still penalizes survivors through the `liq` score factor.

The `analyze risk` and `analyze position` commands do *not* apply the hard filter (you may already be in a position with poor liquidity and need to evaluate it). They still surface the `wide_spread` and `thin_open_interest` rules, and the `liq` factor continues to penalize the score.

**Config keys** (`opener.liquidity`):

| Field | Default | Description |
|---|---|---|
| `minOpenInterest` | 5 | Hard-reject worst-leg OI threshold. Set to 0 to disable. |
| `minRelativeOpenInterest` | 0.25 | Worst-leg OI as a fraction of the max OI among same-expiry near-spot strikes. Combined with `minAbsoluteOpenInterest`. Set to 0 to disable the relative gate. |
| `minAbsoluteOpenInterest` | 100 | Absolute-OI escape hatch for the relative-OI gate. Legs with OI ≥ this value always pass regardless of their share of nearby liquidity. |
| `weight` | 0.50 | Strength of the multiplicative `liq` factor on survivors. Higher = sharper penalty for borderline-liquidity candidates. |

#### Event veto (opener pipeline)

Beyond the liquidity gate, the opener applies a *scheduled-catalyst veto* before scoring. Yahoo's `quoteSummary` endpoint feeds an in-memory event calendar at scan start (cached to `data/event-cache/{TICKER}.json` for 12h); two rules then drop candidates:

- **Earnings veto.** Any structure with at least one short leg whose target expiry falls in `[today, earningsDate + opener.events.earningsBlackoutDaysAfter]` is rejected. The IV crush + gap risk through earnings overruns the log-normal scoring assumption for short premium. Long-only structures (long call/put) are *not* vetoed — they often benefit from earnings vol, and the trader may explicitly want the catalyst.
- **Ex-dividend veto.** Any structure containing a short call leg whose expiry is on or after the next ex-dividend date is rejected. Early exercise to capture the dividend is rational on ITM short calls — assignment risk peaks the day before ex-div.

Both vetoes degrade gracefully: an empty event calendar (Yahoo outage, non-US ticker, etc.) is treated as "no events known" and skips both rules. The `earnings_proximity` risk rule still surfaces catalysts for candidates that *passed* the veto (long-only structures, or runs with the feature disabled) so the trader always sees the calendar.

A JSON override file supplements (and overrides) Yahoo-sourced data for missing or wrong entries:

```json
{
  "AAPL": { "earnings": "2026-08-01", "earningsTime": "AMC", "exDividend": "2026-08-09", "dividendAmount": 0.24 }
}
```

**Config keys** (`opener.events`):

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Master switch. False bypasses both veto rules; the diagnostic rule still fires. |
| `earningsBlackoutDaysAfter` | `0` | Extend the veto window past the target expiry by N days. Default 0 = veto only when `earnings ≤ expiry`. |
| `rejectShortCallsThroughExDiv` | `true` | Set false to skip the ex-div veto on short call legs. |
| `overrideFilePath` | `null` | Path to a JSON override file (relative paths resolve against the project root). |

#### Realized expectancy

The scorer doesn't rank on the theoretical EV that comes straight from the 5-point log-normal grid — it ranks on a *realized EV* that applies the two corrections every desk PM I've worked with treats as table stakes:

1. **Managed exits.** Each scenario's PnL is clamped to a profit-target / stop-loss window before the EV integral runs:

   ```
   realized_pnl(S_T) = clamp(theoretical_pnl(S_T),
                             -stopLossPctOfMaxLoss × |maxLoss|,
                             +profitTargetPctOfMaxProfit × maxProfit) − friction
   ```

   Defaults of 50% / 50% approximate the tastytrade convention for credit spreads (close at 50% of max profit / stop at -2× credit ≈ -50% of max loss on typical short-vertical widths). The clamping is *path-conservative* — it credits the managed exit only at terminal scenario points, ignoring the optionality of closing intra-life when the path crosses the target. The error is in the safe direction (under-estimates managed-exit value).

2. **Slippage.** A per-order friction is charged for each broker order required to enter the structure (and again to exit, scaled by `roundTrips`):

   ```
   friction = slippagePerSharePerOrder × ordersForStructure × 100 × roundTrips
   ```

   `ordersForStructure` is hardcoded based on what Webull actually supports as a combo: 2 for double calendar and double diagonal (which split into two separate combo trades), 1 for everything else (long calendar, diagonal, vertical, iron condor, iron butterfly, long call/put). This is the *right* shape for combo execution where the broker fills the whole structure at one net price — a per-leg slippage charge would systematically over-penalize multi-leg combos.

`slippagePerSharePerOrder` defaults to `0` (assume mid fills). Set to e.g. `0.02` to model paying 2¢/share above mid on each combo fill — typical for Webull-style net-price execution on liquid names. The clamping always applies when `enabled: true`, even with zero slippage; the friction is the optional part.

Two fields on `OpenProposal` carry the realized numbers for audit:
- `RealizedExpectedValuePerContract` — what the `raw` score divides by (replaces theoretical EV in the scoring chain when the feature is enabled)
- `EstimatedSlippagePerContract` — the friction charge alone

The `Rationale` line annotates the inline EV so you can see the gap at a glance:

```
... POP 64.8%, EV $28.38 (real $9.38, −$8.00 fric)
```

**Config keys.** There is no separate `opener.realizedExpectancy` block — the knobs live in their real homes, so the scorer clamps to the *same* exits the management rules will actually enforce:

| Field | Default | Description |
|---|---|---|
| `rules.takeProfit.pctOfMaxProfit` | `0.50` | Per-scenario profit cap as a fraction of max profit — the same knob TakeProfitRule fires on. `1.0` = ride to expiry. |
| `rules.stopLoss.pctOfMaxLoss` | `0.50` | Per-scenario stop floor as a fraction of \|max loss\| — the same knob StopLossRule fires on. `1.0` = no stop. |
| `execution.slippagePerSharePerOrder` | `0` | Dollars-per-share charged per broker order. `0` = mid fills; `0.02` = pay 2¢/share above mid on each combo fill. |
| `execution.roundTrips` | `2` | Number of full crossings the friction represents. 2 = open + close. |

#### Theta carry — the tail of the factor stack

```
theta_factor = 1 + clamp(theta / risk × 1.5, 0, 0.25)
```

The theta factor is the last multiplicand in the factor chain. It adds up to a +25% boost when net theta is positive and large relative to capital at risk. Long-vol structures (theta ≤ 0) get a flat 1.0 here. The ranker sorts by `final` descending, then by `tech-adjusted`, then by `theta_per_day`, then prefers earlier-expiry calendars/diagonals.

### Risk rules (the `Rules fired` block)

Sixteen rules run unconditionally against `RiskDiagnosticFacts`; only those that match attach to the diagnostic. Rules are informational — they do *not* change the score. They surface concerns or geometry observations a human reviewer should know about before acting on the structure.

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
| `sub_grid_strike` | Worst leg's effective liquidity (`max(OI, intraday volume)`) is below 25% of the max among same-expiry strikes within ±10% of spot | The strike exists in the chain but activity clusters on the round-number neighbors — e.g., a $0.50 slot next to a dominant $1.00 grid. Folding volume in keeps a recently-active sub-grid strike from being punished as harshly as a truly dead one. Different signal from `thin_open_interest`: a sub-grid strike can clear the absolute OI floor yet still be the wrong place to put a leg. |
| `market_sentiment_extreme` | CNN F&G composite ≥75 (extreme greed) aligned with a bullish position, ≤24 (extreme fear) aligned with a bearish position, **or** the 1-week composite delta is ≥30 points in either direction (regime change) | Flags crowded-side alignment — contrarian mean-reversion risk on a 1–2 week horizon — or a fast macro regime shift that may have invalidated the vol/momentum assumptions baked into the score. Macro overlay; single-name catalysts can dominate. Suppressed for contrarian-aligned positions. |
| `earnings_proximity` | Next earnings within 14 days of as-of, or next ex-dividend within 14 days when the structure has a short call leg | Surfaces scheduled catalysts the trader should weigh before submitting. Earnings risk: pre-print IV spike and the post-print gap routinely overrun the model's log-normal assumption. Ex-div risk on short calls: early exercise to capture the dividend is rational on ITM strikes. Pure information — the score isn't changed. The earnings/ex-div *veto* (next section) handles the hard rejection path. |
| `credit_divergence` | F&G composite and its `junk_bond_demand` sub-score sit on opposite sides of neutral with ≥30-pt absolute spread, **and** position bias is on the side facing mean-reversion (greed-composite + fear-credit vs. bullish/neutral, or mirror image vs. bearish/neutral) | Credit markets are pricing tail risk the equity-driven composite is masking (or recovering ahead of equities, mirror case). HY-IG spreads historically lead equity drawdowns / reversals by 1–3 weeks at major turning points (2007, 2018, 2020). Macro overlay — single-name catalysts can dominate. Suppressed for contrarian-aligned positions that already benefit from the resolution. |

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
- **Spectre.Console** (0.57.1): Console formatting and tables
- **Spectre.Console.Cli** (0.55.0): Command-line argument parsing with validation
- **EPPlus** (8.6.1): Excel file generation
- **Microsoft.Data.Sqlite** (10.0.9): the minute-NBBO quote store (`data/quotes.db`)

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

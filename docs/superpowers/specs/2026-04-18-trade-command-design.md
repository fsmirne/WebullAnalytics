# `trade` Command Design

**Date:** 2026-04-18
**Status:** Proposed

## Summary

Add a new `trade` command to WebullAnalytics that uses the **Webull OpenAPI** (distinct from the session-based web API used by `fetch`) to place, cancel, and inspect orders — including multi-leg option combos — against Webull's sandbox or production environment.

The command is structured as a subcommand branch with three actions: `place`, `cancel`, and `status`. Authentication uses HMAC-SHA1 request signing with an App Key + App Secret pair, configured per-account in a new `data/trade-config.json` file.

## Goals / Non-goals

**Goals (v1):**
- Place single-leg equity orders (limit and market).
- Place single-leg option orders (limit).
- Place multi-leg option combos (vertical, calendar, diagonal, iron condor, butterfly, straddle, strangle) with a single net limit price.
- Place stock-plus-option combos (covered call, protective put, collar).
- Cancel a specific open order by client order ID.
- Cancel all open orders for the account.
- Retrieve the current status of any placed order.
- Preview orders by default; only place when `--submit` is explicitly passed.
- Support multiple Webull OpenAPI accounts, with sandbox vs production tracked per-account.

**Non-goals (v1):**
- Stop, stop-limit, trailing-stop, or algo order types.
- IOC / FOK time-in-force (DAY and GTC only).
- Polling for fill status after placing (user runs `trade status` manually).
- A test project with automated tests (no test infrastructure exists today; pure-function seams are designed in so tests can be added later without rework).
- Migrating `analyze --trade` to the new shared syntax (deferred to a follow-up).
- Auto-retry, idempotency flags, or debug-mode signature dumps.

## Command Surface

```
wa trade place   --trade "<legs>" [--limit <net>] [--type limit|market]
                              [--tif day|gtc] [--strategy <name>] [--account <id>]
                              [--submit]

wa trade cancel  <clientOrderId>
wa trade cancel  --all [--account <id>]

wa trade status  <clientOrderId> [--account <id>]
```

### `trade place`

| Option | Description |
|---|---|
| `--trade "<legs>"` | Comma-separated leg list in `ACTION:SYMBOL:QTY` format. No per-leg `@PRICE` allowed (that is `analyze` syntax). |
| `--limit <net>` | Required for `--type limit` (default). Net limit price across all legs. Positive = net credit; negative = net debit. |
| `--type limit\|market` | Default `limit`. `market` is rejected for any multi-leg order. |
| `--tif day\|gtc` | Time-in-force. Default `day`. |
| `--strategy <name>` | Override auto-detected combo strategy. Values: `single`, `stock`, `vertical`, `calendar`, `diagonal`, `iron_condor`, `butterfly`, `straddle`, `strangle`, `covered_call`, `protective_put`, `collar`. |
| `--account <id-or-alias>` | Pick an account from `data/trade-config.json`. Defaults to the config's `defaultAccount`. |
| `--submit` | Actually place the order. Without this flag, the command runs preview only and exits after printing the preview result. |

There is no `--yes` flag. Every mutating action prompts interactively; piping empty input aborts.

### `--trade` syntax

Format: `ACTION:SYMBOL:QTY[@PRICE]`

- `ACTION` — `buy` or `sell` (explicit, no sign math).
- `SYMBOL` — equity ticker (e.g. `GME`) or OCC option symbol (e.g. `GME260501C00023000`).
- `QTY` — unsigned positive integer.
- `@PRICE` — optional; **rejected by `trade`**, accepted by `analyze` (in a later migration).

Examples:

| Scenario | `--trade` value | Other flags |
|---|---|---|
| Equity limit buy | `buy:SPY:10` | `--limit 580` |
| Equity market buy | `buy:SPY:10` | `--type market` |
| Option limit sell | `sell:GME260501C00023000:1` | `--limit 1.50` |
| Vertical spread | `buy:GME260501C00023000:1,sell:GME260501C00024000:1` | `--limit -0.75` |
| Calendar roll | `sell:GME260410C00023000:1,buy:GME260417C00023000:1` | `--limit -0.20` |
| Covered call | `buy:GME:100,sell:GME260501C00025000:1` | `--limit -23.50` |

### `trade cancel`

- `trade cancel <clientOrderId>` — cancels one order. Prompts before sending.
- `trade cancel --all` — lists all open orders, shows them to the user, prompts, then cancels each one. A single failure does not abort the loop; a summary line at the end reports successes and failures.

### `trade status`

- `trade status <clientOrderId>` — GETs `/openapi/trade/order/detail` and prints the combo type, status, filled vs total quantity, filled price, timestamps, and each leg's details.

## Configuration

New file: `data/trade-config.json` (gitignored). Example file `trade-config.example.json` ships with the three Webull-published sandbox test accounts pre-populated.

```json
{
  "defaultAccount": "test1",
  "accounts": [
    {
      "alias": "test1",
      "accountId": "J6HA4EBQRQFJD2J6NQH0F7M649",
      "appKey": "a88f2efed4dca02b9bc1a3cecbc35dba",
      "appSecret": "c2895b3526cc7c7588758351ddf425d6",
      "sandbox": true
    },
    { "alias": "test2", "accountId": "HBGQE8NM0CQG4Q34ABOM83HD09", "appKey": "6d9f1a0aa919a127697b567bb704369e", "appSecret": "adb8931f708ea3d57ec1486f10abf58c", "sandbox": true },
    { "alias": "test3", "accountId": "4BJITU00JUIVEDO5V3PRA5C5G8", "appKey": "eecbf4489f460ad2f7aecef37b267618", "appSecret": "8abf920a9cc3cb7af3ea5e9e03850692", "sandbox": true }
  ]
}
```

### Resolution rules

- `--account <value>` matches either `alias` or `accountId`.
- If `--account` is omitted, use `defaultAccount` (itself an alias).
- If `defaultAccount` is missing and `--account` is omitted, error.
- `sandbox: true` → base URL `https://us-openapi-alb.uat.webullbroker.com`.
- `sandbox: false` → base URL `https://api.webull.com`.

### Environment safety

Every `trade` invocation prints a banner before any other output:

- `[SANDBOX]` in green when the resolved account has `sandbox: true`.
- `[PRODUCTION]` in red when the resolved account has `sandbox: false`.

Mutating subcommands (`place --submit`, `cancel`, `cancel --all`) always prompt. There is no way to bypass the prompt; piped empty input aborts.

### Secret handling

- App Secret is never printed and never logged. Any diagnostic dump redacts it to `***`.
- `data/trade-config.json` is added to `.gitignore`. Only the `.example` file is committed.

## Webull OpenAPI Endpoints

| Operation | Method | Path | Input |
|---|---|---|---|
| Preview | POST | `/openapi/trade/order/preview` | body: `{ account_id, new_orders[] }` |
| Place | POST | `/openapi/trade/order/place` | body: `{ account_id, new_orders[] }` |
| Cancel | POST | `/openapi/trade/order/cancel` | body: `{ account_id, client_order_id }` |
| List open | GET | `/openapi/trade/order/open` | query: `account_id`, paginated via `last_client_order_id` |
| Status | GET | `/openapi/trade/order/detail` | query: `account_id`, `client_order_id` |

Every operation's lookup key is `client_order_id` — the system's `order_id` is returned in responses but never required as input.

### Request signing

All requests are signed per the Webull OpenAPI signature spec:

**Required headers**:

| Header | Value |
|---|---|
| `x-app-key` | The account's App Key |
| `x-timestamp` | Current UTC, ISO 8601: `YYYY-MM-DDThh:mm:ssZ` |
| `x-signature` | `base64(HMAC-SHA1(appSecret + "&", urlencode(canonicalString)))` |
| `x-signature-algorithm` | `HMAC-SHA1` |
| `x-signature-version` | `1.0` |
| `x-signature-nonce` | Fresh `uuid4().hex` per request |
| `x-version` | `v2` |

**Canonical string**:

1. `str1` = all query params + signing headers (excluding `x-signature` and `x-version`), sorted alphabetically, joined as `key1=val1&key2=val2&...`.
2. `str2` = uppercase MD5 of the compact JSON body (no spaces between keys and values), if a body is present.
3. `str3` = `path & str1` (no body) or `path & str1 & str2` (with body).
4. URL-encode `str3` before HMAC.

App Secret is used only client-side to derive the signing key; it is **never** transmitted in any header.

### Client order ID format

`OrderRequestBuilder` generates client order IDs as `YYMMDD-HHMMSS-XXXX` (4 random hex chars), 18 characters total. Chronological prefix for easy scanning, effectively unique under any realistic submission rate, and well under the 32-char API limit. The user pastes this ID into `trade status <id>` or `trade cancel <id>`.

### Response shape notes

- Place / preview responses include both `client_order_id` and `order_id`. We display both but only reference `client_order_id` in follow-up commands.
- Error responses (4xx): `{ error_code, message }`. Surface both verbatim.

## Component Breakdown

### New files

1. **`OpenApiSigner.cs`** — stateless, pure. Single method:
   ```csharp
   static Dictionary<string,string> SignRequest(
       string appKey, string appSecret, string path,
       IReadOnlyDictionary<string,string> queryParams, string? jsonBody)
   ```
   Returns the seven `x-*` headers. Timestamp and nonce are generated internally but injectable via overload for deterministic testing.

2. **`WebullOpenApiClient.cs`** — thin HTTP wrapper. Carries `appKey`, `appSecret`, `baseUrl`, `accountId`. Exposes:
   - `PreviewOrderAsync(OrderRequest)` → `PreviewResult`
   - `PlaceOrderAsync(OrderRequest)` → `PlaceResult`
   - `CancelOrderAsync(string clientOrderId)` → `CancelResult`
   - `ListOpenOrdersAsync()` → `Order[]` (handles pagination internally)
   - `GetOrderAsync(string clientOrderId)` → `OrderDetail`

   Every method routes through `OpenApiSigner` before `HttpClient.SendAsync`. On 4xx, deserializes `{ error_code, message }` and throws `WebullOpenApiException(errorCode, message, httpStatus)`.

3. **`TradeCommand.cs`** — Spectre.Console.Cli branch. Registers `place`, `cancel`, `status` subcommands. Loads `data/trade-config.json`, resolves the account, prints the environment banner, and passes the resolved account context into each subcommand.

4. **`TradeLegParser.cs`** — parses `ACTION:SYMBOL:QTY[@PRICE]` into `ParsedLeg` records. Rejects `@PRICE` when invoked from `trade`. Self-contained so the future `analyze` migration can adopt it without surgery.

5. **`OrderRequestBuilder.cs`** — assembles the JSON payload for preview/place from parsed legs + resolved strategy + CLI flags. Handles:
   - Single equity: `instrument_type: EQUITY`, no `legs[]`.
   - Single option: `instrument_type: OPTION`, one-entry `legs[]`, `combo_type: NORMAL`.
   - Multi-leg combo: `combo_type` + `option_strategy` set from classification; `legs[]` built from parsed legs.
   - Stock+option combo (covered call / protective put / collar): legs of mixed instrument types.

   Generates `client_order_id` using the 18-char format. For combos, also generates `client_combo_order_id`.

   **Strategy enum mapping**: the user-facing `--strategy` values (`vertical`, `calendar`, `covered_call`, etc.) map to the OpenAPI `option_strategy` enum. The exact enum strings Webull expects are to be resolved during implementation against the Webull `common-order-place` reference and empirically via sandbox preview calls. The mapping lives as a single `Dictionary<string,string>` inside `OrderRequestBuilder` so it is easy to adjust.

### Modified files

6. **`Program.cs`** — register the `trade` branch in `Main`.
7. **`StrategyGrouper.cs`** — extract `ClassifyStrategy(IEnumerable<ParsedLeg>) → StrategyKind` as a public helper. Existing classification logic currently works on `Trade` records from historical data; we need the same rules applied to pre-trade leg descriptions. Refactor to share, do not duplicate. Extend to recognize `covered_call`, `protective_put`, `collar` (stock + option patterns).
8. **`.gitignore`** — add `data/trade-config.json`.

### Supporting files

- `trade-config.example.json` at repo root, with the three sandbox credentials pre-populated.

## Data Flow

### `trade place`

```
1. Parse CLI args → PlaceSettings
2. Load data/trade-config.json → resolve account (alias or --account flag)
3. Print environment banner ([SANDBOX] green / [PRODUCTION] red)
4. TradeLegParser.Parse(--trade)  → ParsedLeg[]
5. Reject any leg with @PRICE
6. Determine strategy:
   - If --strategy provided: use it verbatim, skip auto-detection.
   - Otherwise: StrategyGrouper.ClassifyStrategy(legs) → StrategyKind.
     If classification cannot determine a strategy, error with:
     "could not classify legs; pass --strategy explicitly".
7. OrderRequestBuilder.Build(legs, strategy, --limit, --type, --tif) → OrderRequest
8. Pretty-print the request (shows generated client_order_id)
9. WebullOpenApiClient.PreviewOrderAsync(request)
10. Print preview: estimated_cost, estimated_transaction_fee
11. If --submit NOT set → exit 0
12. Prompt: "Place this order? [y/N]"
13. If confirmed → WebullOpenApiClient.PlaceOrderAsync(request)
14. Print: order_id, client_order_id, hint for `trade status <client_order_id>`
```

### `trade cancel <clientOrderId>`

```
1. Load config, resolve account, print banner
2. Prompt: "Cancel order <id>? [y/N]"
3. CancelOrderAsync(clientOrderId) on confirm
4. Print response
```

### `trade cancel --all`

```
1. Load config, resolve account, print banner
2. ListOpenOrdersAsync() across all pages → unique client_order_ids
3. Print the list (symbol, status, qty, client_order_id)
4. Prompt: "Cancel all N open orders? [y/N]"
5. Loop CancelOrderAsync — one failure does NOT abort; each result printed
6. Summary: "Cancelled X of N. Failed: Y."
```

### `trade status <clientOrderId>`

```
1. Load config, resolve account, print (quieter) banner
2. GetOrderAsync(clientOrderId)
3. Pretty-print: combo_type, status, filled/total, filled_price,
   place_time, filled_time, legs[]
```

### Signing path (shared)

```
1. Build path + query dict + JSON body (compact, no spaces)
2. OpenApiSigner.SignRequest → 7 x-* headers
3. Attach to HttpRequestMessage; SendAsync
4. On 2xx: deserialize typed response
5. On 4xx: deserialize { error_code, message }; throw WebullOpenApiException
```

## Error Handling

**HTTP errors**
- 4xx: surface `error_code` + `message`. `TradeCommand` catches the exception at the subcommand boundary, prints `Error [<code>]: <message>`, exits non-zero.
- 5xx: print `"Webull API unavailable (HTTP <status>): <body preview>"`, exit with a distinct code.
- Network errors: print the exception message, exit.

**Signing errors**
- If Webull returns a signature failure, surface the raw error and code. No auto-retry. Most likely cause is canonical-string construction or clock skew.

**Idempotency**
- Each place request generates a new `client_order_id` — running the same CLI twice creates two orders. This is correct, not a bug.
- Cancel is naturally idempotent; cancelling an already-cancelled order produces an `error_code` we surface without special-casing.

**Pre-network validation** (all error, exit before any API call):
- `--type market` together with `--limit` → error.
- `--type limit` with no `--limit` → error.
- `--type market` with multi-leg → error ("combo orders must be limit").
- Leg count mismatch with declared or inferred strategy (e.g. `--strategy vertical` with three legs) → error.
- `--strategy stock` with option legs present (and vice versa) → error.
- Combo crosses underlyings (different root symbols) → error.

**Net-limit sign sanity check**
- If all legs are `buy` and `--limit` is positive (i.e. asking for a credit on a net-debit order), **warn but do not block**. The user still has to confirm; a genuinely wrong limit will not fill anyway.

**Config errors**
- Missing file → actionable message: `"Run: cp trade-config.example.json data/trade-config.json and edit"`.
- Malformed JSON → print location from `System.Text.Json` exception.
- Alias collision → first wins, with a warning.
- `defaultAccount` references a missing alias → error.

**Prompt handling**
- `Console.ReadLine()` returns null on EOF → treat as "no", abort.
- Case-insensitive: `y`, `Y`, `yes`, `YES` confirm; anything else aborts.

**Race conditions**
- `cancel --all` lists then cancels; orders created between the two steps are not in the list and survive.
- Orders cancelled by someone else between list and our cancel return an error; we surface and continue.

## Testing

No existing test project. Components are designed with pure-function seams so tests can be added later without rework.

**Layer 1 — Signature correctness**: `OpenApiSigner.SignRequest` with fixed inputs (timestamp + nonce injected) bit-matches the expected base64 signature from the Webull Python reference example. This is the first thing to verify during implementation, before any other API call is attempted.

**Layer 2 — Leg parsing & strategy classification**: `TradeLegParser` round-trips known inputs to expected `ParsedLeg[]`. `StrategyGrouper.ClassifyStrategy` returns the correct `StrategyKind` for every supported strategy (single equity, single option, vertical, calendar, diagonal, iron condor, butterfly, straddle, strangle, covered call, protective put, collar).

**Layer 3 — Request payload snapshots**: `OrderRequestBuilder.Build` produces JSON matching hand-verified snapshots for each strategy. Implementable with a temporary local-only `--dump-request` flag during development, removed before merge.

**Layer 4 — Sandbox end-to-end matrix** (17 scenarios):

| # | Scenario |
|---|---|
| 1 | Single equity limit buy |
| 2 | Single option limit sell |
| 3 | Vertical spread |
| 4 | Calendar |
| 5 | Covered call |
| 6 | Preview only (no `--submit`) |
| 7 | Cancel single |
| 8 | Cancel all |
| 9 | Status lookup |
| 10 | Explicit `--strategy` override |
| 11 | Wrong `--strategy` — errors before network |
| 12 | `--type market` single equity |
| 13 | `--type market` with combo — errors before network |
| 14 | `--limit` with `--type market` — errors before network |
| 15 | Missing `--limit` on limit order — errors before network |
| 16 | Malformed leg — errors with position indicator |
| 17 | Account alias unknown — errors listing valid aliases |

This matrix doubles as the manual regression set after any change to the trade stack.

## Deferred / Follow-up

- Migrate `analyze --trade` to the new `ACTION:SYMBOL:QTY[@PRICE]` shared parser. Deferred until the `trade` command has been in use long enough to shake out any syntax-design issues.
- Automated test project (xUnit or similar). Once added, Layers 1–3 above convert directly.
- `--force` / `--yes` equivalent to bypass prompts when scripting becomes a real need.
- Additional order types (stop, stop-limit, trailing) and TIFs (IOC, FOK).
- After-submit polling mode (opt-in flag) for callers who want synchronous fill confirmation.
- `--debug` flag to dump the canonical string and signing key (with App Secret redacted) for signature debugging.

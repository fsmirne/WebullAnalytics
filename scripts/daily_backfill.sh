#!/usr/bin/env bash
# Daily ThetaData refresh of the canonical data stores:
#   1. --quotes        -> data/quotes.db (SQLite)            (minute NBBO, ±10% strike band — written directly)
#   2. --run           -> data/oi/<TICKER>/<date>.jsonl      (EOD open interest + back-solved IV)
#   3. verify          (SQL coverage + crossed-quote scan of quotes.db; no network)
#
# Quotes are written straight into the canonical SQLite store (no CSV staging) — per-expiry DELETE+INSERT,
# WAL so the scraper/backtest can touch it concurrently; quote sealing lives in the DB `sealed` table. The
# OI store stays as data/oi/<TICKER>/<date>.jsonl with its own sealed.json.
#
# Tickers: SPY/GME at 60 DTE (covers the longCalendar/diagonal longDteMax=60 long legs),
# QQQ at 30 DTE (covers the DC strategy's 21-30 long legs — cross-vehicle validation store),
# SPXW/XSP at 0 DTE. ThetaData allows ONE session per account, so the pulls run STRICTLY
# SEQUENTIALLY — never in parallel — each with --concurrency 2 (the Value-tier request limit).
#
# Re-run safe & incremental: --quotes skips sealed (expired) expirations; --run (OI) skips sealed
# (settled, past) days via oi/<ticker>/sealed.json. Only the live frontier (new days / unsealed
# expirations) is re-pulled each run — no full-history re-pull. ThetaData finalizes a session at
# ~17:15 ET: an evening run (>= 19:00) captures TODAY; a morning run captures through yesterday.
# Each pull also tees its own timestamped log to data/logs/backfill_*.log.
#
# NOTE: re-pulling an unsealed expiration REWRITES its CSV, replacing any rows the Schwab scraper
# captured live that day — snapshot them first if needed (scripts/snapshot_schwab_day.py).
#
# Self-contained: install.sh publishes this script and its two Python helpers (backfill_thetadata.py,
# import_quotes_sqlite.py) side-by-side into the install dir (alongside the wa executable), so it runs
# identically from there or from the repo's scripts/ dir. Nothing depends on the working directory —
# the Python helpers are resolved from this script's own location, and the data store from WA_DATA_DIR
# (exported below).
#
# Data dir: the prod data folder (the LocalApplicationData dir Program.cs treats as the single source
# of truth: $XDG_DATA_HOME/WebullAnalytics/data on Linux, ~/Library/Application Support/... on macOS).
# Creds: set THETADATA_CREDENTIALS_FILE to override, else the pull reads creds.txt from that folder.
#
# Exit status: 0 if all three steps succeed, 1 if any failed (the others still run).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Prod data folder (LocalApplicationData), matching Program.cs's BaseDir resolution.
# On WSL the wa executable is a WINDOWS process, so its LocalApplicationData is the Windows
# %LOCALAPPDATA% — from WSL that's /mnt/c/Users/<you>/AppData/Local/... — NOT the Linux XDG
# path. Detect WSL (uname reports Linux there) and scan /mnt/c/Users like resolve_data_dir().
if [ "$(uname)" = "Darwin" ]; then
  PROD_DATA="$HOME/Library/Application Support/WebullAnalytics/data"
elif grep -qi microsoft /proc/version 2>/dev/null; then
  PROD_DATA=""
  for d in /mnt/c/Users/*/AppData/Local/WebullAnalytics/data; do
    [ -d "$d" ] && PROD_DATA="$d" && break
  done
  [ -n "$PROD_DATA" ] || echo "[$(date '+%Y-%m-%d %H:%M:%S')] [WARN] no WebullAnalytics/data under /mnt/c/Users/*/AppData/Local — set WA_DATA_DIR"
else
  PROD_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/WebullAnalytics/data"
fi

# Point the Python helpers at the canonical store. They resolve it via WA_DATA_DIR; without this they
# fall back to Windows/WSL-only lookups (LOCALAPPDATA, /mnt/c) and abort on native Linux/macOS.
export WA_DATA_DIR="${WA_DATA_DIR:-$PROD_DATA}"

# Point ThetaData auth at creds.txt in the data folder unless THETADATA_CREDENTIALS_FILE overrides it.
if [ -z "${THETADATA_CREDENTIALS_FILE:-}" ]; then
  export THETADATA_CREDENTIALS_FILE="$WA_DATA_DIR/creds.txt"
  [ -f "$THETADATA_CREDENTIALS_FILE" ] || echo "[$(date '+%Y-%m-%d %H:%M:%S')] [WARN] creds not found at $THETADATA_CREDENTIALS_FILE"
fi

PY=python3
SCRIPT="$SCRIPT_DIR/backfill_thetadata.py"
TICKERS="SPXW:0 XSP:0 SPY:60 GME:60 QQQ:30"   # quotes + oi (per-ticker DTE)
VERIFY="SPXW XSP SPY GME QQQ"                 # verify-quotes (bare names, no DTE)
CONC=2

# Stop at the last COMPLETE day. ThetaData finalizes a session's data at ~17:15 ET, so an evening
# run (>= 19:00 local, comfortably past that) may include TODAY; earlier runs stop at yesterday —
# their docs and our own error ("Current day requests must have a start time less than current
# time") show intraday same-day minute requests aren't reliable. On a Mon morning this resolves to
# Sun and the pull simply ends at the prior Fri. Override by exporting BACKFILL_END=YYYY-MM-DD.
if [ -z "${BACKFILL_END:-}" ] && [ "$(date +%H)" -ge 19 ]; then
  END=$(date +%F)
else
  END="${BACKFILL_END:-$(date -d 'yesterday' +%F)}"
fi

ts() { date '+%Y-%m-%d %H:%M:%S'; }
rc=0

step() {  # "label" command args...
  local label=$1; shift
  echo "[$(ts)] $label"
  "$@"
  local ec=$?
  if [ "$ec" -ne 0 ]; then echo "[$(ts)] [FAIL] $label (exit $ec)"; rc=1; fi
}

# OI always stops at yesterday regardless of the evening gate: OCC publishes a session's open
# interest the NEXT morning, and ThetaData's wildcard-expiration EOD/OI requests reject the current
# day outright ("Cannot fetch current-day data without specifying an expiration"). Today's OI lands
# on tomorrow's run; quotes still capture today on evening runs.
END_OI="${BACKFILL_END:-$(date -d 'yesterday' +%F)}"

echo "[$(ts)] === daily data update: quotes through $END, oi through $END_OI, verify ==="

step "(1/3) minute-NBBO quotes -> data/quotes.db"  "$PY" "$SCRIPT" --quotes --tickers $TICKERS --end "$END" --concurrency "$CONC"
step "(2/3) EOD open interest -> data/oi"          "$PY" "$SCRIPT" --run    --tickers $TICKERS --end "$END_OI" --concurrency "$CONC"
step "(3/3) quote-store coverage + integrity"      "$PY" "$SCRIPT_DIR/import_quotes_sqlite.py" --root SPY --verify

if [ "$rc" -eq 0 ]; then
  echo "[$(ts)] === ALL OK ==="
else
  echo "[$(ts)] === COMPLETED WITH FAILURES (see above) ==="
fi
exit "$rc"

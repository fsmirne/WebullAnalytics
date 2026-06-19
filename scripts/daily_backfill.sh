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
# Creds: set THETADATA_CREDENTIALS_FILE (or have a creds.txt the script finds). Run from anywhere;
# the repo root is resolved from this script's own location.
#
# Exit status: 0 if all three steps succeed, 1 if any failed (the others still run).

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

PY=python3
SCRIPT=scripts/backfill_thetadata.py
TICKERS="SPY:60 SPXW:0 XSP:0 GME:60"   # quotes + oi (per-ticker DTE)
VERIFY="SPY SPXW XSP GME"              # verify-quotes (bare names, no DTE)
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
step "(3/3) quote-store coverage + integrity"      "$PY" scripts/import_quotes_sqlite.py --root SPY --verify

if [ "$rc" -eq 0 ]; then
  echo "[$(ts)] === ALL OK ==="
else
  echo "[$(ts)] === COMPLETED WITH FAILURES (see above) ==="
fi
exit "$rc"

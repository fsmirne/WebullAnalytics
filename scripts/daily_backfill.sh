#!/usr/bin/env bash
# Daily ThetaData refresh of the canonical data stores:
#   1. --quotes        -> data/quotes/<TICKER>/<expiry>.csv   (minute NBBO, ±10% strike band)
#   2. --run           -> data/oi/<TICKER>/<date>.jsonl       (EOD open interest + back-solved IV)
#   3. --verify-quotes  (local integrity scan of the quote store; no network)
#
# Tickers: SPY/GME at 60 DTE (covers the longCalendar/diagonal longDteMax=60 long legs),
# SPXW/XSP at 0 DTE. ThetaData allows ONE session per account, so the pulls run STRICTLY
# SEQUENTIALLY — never in parallel — each with --concurrency 2 (the Value-tier request limit).
#
# Re-run safe & incremental: --quotes skips sealed (expired) expirations; --run (OI) skips sealed
# (settled, past) days via oi/<ticker>/sealed.json. Only the live frontier (new days / unsealed
# expirations) is re-pulled each run — no full-history re-pull. ThetaData is T+1,
# so run this AFTER the prior session has settled (i.e. the next morning) to capture the latest
# complete trading day. Each pull also tees its own timestamped log to data/logs/backfill_*.log.
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

# Stop at the last COMPLETE day (yesterday). ThetaData is T+1 and rejects current-day minute
# requests ("Current day requests must have a start time less than current time"), so never
# request today. On a Mon this resolves to Sun and the pull simply ends at the prior Fri.
# Override by exporting BACKFILL_END=YYYY-MM-DD before running.
END="${BACKFILL_END:-$(date -d 'yesterday' +%F)}"

ts() { date '+%Y-%m-%d %H:%M:%S'; }
rc=0

step() {  # "label" command args...
  local label=$1; shift
  echo "[$(ts)] $label"
  "$@"
  local ec=$?
  if [ "$ec" -ne 0 ]; then echo "[$(ts)] [FAIL] $label (exit $ec)"; rc=1; fi
}

echo "[$(ts)] === daily data update through $END: quotes -> oi -> verify ==="

step "(1/3) minute-NBBO quotes -> data/quotes"  "$PY" "$SCRIPT" --quotes --tickers $TICKERS --end "$END" --concurrency "$CONC"
step "(2/3) EOD open interest -> data/oi"        "$PY" "$SCRIPT" --run    --tickers $TICKERS --end "$END" --concurrency "$CONC"
step "(3/3) quote-store integrity scan"          "$PY" "$SCRIPT" --verify-quotes --tickers $VERIFY --end "$END"

if [ "$rc" -eq 0 ]; then
  echo "[$(ts)] === ALL OK ==="
else
  echo "[$(ts)] === COMPLETED WITH FAILURES (see above) ==="
fi
exit "$rc"

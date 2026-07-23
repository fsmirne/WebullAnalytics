#!/usr/bin/env bash
# backtest_fullperiod_2022.sh — full-period OOS test of the LIVE SPY DC strategy, 2022-01-01 → 2026-07-20.
#
# WHY: DC was tuned almost entirely on 2025-2026 data. 2022-2024 is genuine out-of-sample and contains the
# regimes the edge has never seen: the 2022 bear (sustained VIX 25-35), the 2023 recovery, the 2024 low-vol
# grind, plus the Aug-2024 and Apr-2025 vol spikes inside the known window. Three sequential cells (no
# parallel backtests — quotes.db contention):
#   1. DC        --lots 1        sizing-neutral per-trade edge; fills feed the ex-post regime slicer
#   2. DC        --starting-cash 50000   compounded equity + real drawdown under the live sizing caps
#   3. DCfrict03 --lots 1        friction gate: verbatim DC with slippage 0.02→0.03
# Regime splits (calendar year, VIX band at entry, above/below 200-dma) are sliced from cell 1's fills by
# scripts/analyze_fullperiod.py — one backtest, many slices, instead of N regime-windowed backtests whose
# lineages would be truncated at window edges.
#
# PRE-REQ: OI store must cover the window (data/oi/SPY starts 2022-04-01 until the Jan-Mar 2022 backfill
# lands — run backfill_thetadata.py --run --tickers SPY:60 --start 2022-01-01 --end 2022-03-31 first).
# RUNTIME: campaign calibration was ~13-37 min per 1.5yr cell at stride 1; 4.5yr => ~40 min-2h per cell.
set -u
SINCE="${1:-2022-01-01}"
UNTIL="${2:-2026-07-20}"
WAHOME="$(ls -d /mnt/c/Users/*/AppData/Local/WebullAnalytics 2>/dev/null | head -1)"
WINUSER="$(echo "$WAHOME" | cut -d/ -f5)"
WA="$WAHOME/wa.exe"
RUNID="fullperiod-$(date +%Y%m%d-%H%M%S)"
LXDIR="$WAHOME/sweeps/$RUNID"
WINDIR="C:\\Users\\$WINUSER\\AppData\\Local\\WebullAnalytics\\sweeps\\$RUNID"
mkdir -p "$LXDIR"
echo "run dir: $LXDIR"

run_cell() {  # name, extra args...
  local name="$1"; shift
  echo "[$(date +%H:%M:%S)] cell $name starting"
  "$WA" ai backtest SPY --since "$SINCE" --until "$UNTIL" --scan-stride 1 --fills-jsonl "$WINDIR\\fills_$name.jsonl" "$@" > "$LXDIR/run_$name.log" 2>&1
  local rc=$?
  echo "[$(date +%H:%M:%S)] cell $name done rc=$rc"
  tail -40 "$LXDIR/run_$name.log" | grep -E "P&L|drawdown|Win rate|Opens|Real-bar|Trading days|Wall time" || tail -8 "$LXDIR/run_$name.log"
}

run_cell DC_lots1   --strategy DC        --lots 1
run_cell DC_50k     --strategy DC        --starting-cash 50000
run_cell DCfrict03  --strategy DCfrict03 --lots 1

echo "=== all cells done; regime slices ==="
python3 "$(dirname "$0")/analyze_fullperiod.py" "$LXDIR/fills_DC_lots1.jsonl"

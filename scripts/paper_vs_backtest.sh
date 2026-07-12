#!/usr/bin/env bash
# paper_vs_backtest.sh <YYYY-MM-DD> — daily FIDELITY check for the SPY winner.
#
# WHY: the backtest is only trustworthy for sizing/holding-through-DD if it predicts
# what live actually does. cec0170 showed the backtest can silently diverge from live.
# This compares, for one date, the LIVE paper pick (from the watch's proposal log) to
# the same-day BACKTEST pick for the same config. Agreement over ~1-2 weeks is what
# earns trust before real capital.
#
# LIVE pick   = top-finalScore 'open' proposal at the FIRST tick >= 09:30 ET that day
#               (the opener fires once at the RTH open — earliest-wins entry rule).
# BACKTEST pick = the Open fill from a single-day backtest of the same strategy.
# PASS = same structure AND same legs (side+strike+expiry). Also prints entry debit.
#
# Run AFTER the evening ThetaData backfill lands the day's quotes (~19:00 ET), else the
# single-day backtest has nothing to price. Reads the installed wa.exe + AppData.
set -u
DATE="${1:?usage: paper_vs_backtest.sh YYYY-MM-DD}"
STRATEGY=DC        # DC = the consolidated live strategy (diag-only 5-15/30-45 @ 0.20)
WA="/mnt/c/Users/USER/AppData/Local/WebullAnalytics/wa.exe"
PROPOSALS="/mnt/c/Users/USER/AppData/Local/WebullAnalytics/data/ai-proposals.SPY.${STRATEGY}.jsonl"
WINFILLS='C:\Users\USER\AppData\Local\WebullAnalytics\sweeps\pvb.jsonl'
LXFILLS="/mnt/c/Users/USER/AppData/Local/WebullAnalytics/sweeps/pvb.jsonl"

[ -x "$WA" ] || { echo "FATAL: wa.exe not found"; exit 2; }
[ -f "$PROPOSALS" ] || { echo "NOTE: no proposal log yet at $PROPOSALS — run 'wa ai watch SPY --strategy $STRATEGY' (submit off) first."; exit 2; }

rm -f "$LXFILLS" 2>/dev/null
"$WA" ai backtest SPY --strategy "$STRATEGY" --since "$DATE" --until "$DATE" --lots 1 --scan-stride 1 --fills-jsonl "$WINFILLS" >/dev/null 2>&1

python3 - "$DATE" "$PROPOSALS" "$LXFILLS" <<'PY'
import json, os, sys
date, proposals_path, fills_path = sys.argv[1], sys.argv[2], sys.argv[3]

def norm_legs(legs, sym_key, side_key):
    out=[]
    for l in legs:
        out.append(f"{str(l[side_key]).lower()}:{l[sym_key]}")
    return sorted(out)

# ---- LIVE pick: first tick >= 09:30 that day, top finalScore ----
day_rows=[]
for line in open(proposals_path):
    line=line.strip()
    if not line: continue
    try: r=json.loads(line)
    except: continue
    ts=r.get('ts','')
    if ts[:10]!=date: continue
    if r.get('type')!='open': continue
    if ts[11:19] < '09:30:00': continue   # skip pre-market ticks
    day_rows.append(r)
live=None
if day_rows:
    first_min=min(r['ts'][:16] for r in day_rows)      # earliest >=09:30 evaluation MINUTE
    tick_rows=[r for r in day_rows if r['ts'][:16]==first_min]   # all candidates from that eval
    live=max(tick_rows, key=lambda r: r.get('finalScore') or 0)  # top-1 = what the opener opens

# ---- BACKTEST pick: the Open fill for that day ----
bt=None
if os.path.exists(fills_path):
    for line in open(fills_path):
        line=line.strip()
        if not line: continue
        f=json.loads(line)
        if f.get('kind')=='Open' and f.get('ts','')[:10]==date:
            bt=f; break

def entry_debit(legs, price_key, side_key):
    # net debit per contract = sum(buy price) - sum(sell price)
    s=0.0
    for l in legs:
        p=float(l.get(price_key,0) or 0)
        s += p if str(l[side_key]).lower()=='buy' else -p
    return round(s,2)

print(f"=== paper vs backtest — SPY {date} ===")
if not live and not bt:
    print("BOTH: no open this day — consistent (no trade)."); sys.exit(0)
if bool(live) != bool(bt):
    print(f"*** MISMATCH: live {'OPENED' if live else 'no-open'} but backtest {'OPENED' if bt else 'no-open'} ***")
    if live: print("  live:", live.get('structure'), norm_legs(live['legs'],'symbol','action'))
    if bt:   print("  bt:  ", bt.get('strategy'), norm_legs(bt['legs'],'sym','side'))
    sys.exit(1)

live_struct=(live.get('structure') or '').lower()
bt_struct=(bt.get('strategy') or '').lower()
live_legs=norm_legs(live['legs'],'symbol','action')
bt_legs=norm_legs(bt['legs'],'sym','side')
struct_ok = live_struct==bt_struct
legs_ok = live_legs==bt_legs
bt_entry=entry_debit(bt['legs'],'price','side')
print(f"  live: {live.get('structure'):13s} legs={live_legs}  score={live.get('finalScore'):.5f}")
print(f"  bt:   {bt.get('strategy'):13s} legs={bt_legs}  entry_debit=${bt_entry}")
if struct_ok and legs_ok:
    print("  RESULT: MATCH ✓ (same structure + same legs)"); sys.exit(0)
print(f"  RESULT: MISMATCH — structure_ok={struct_ok} legs_ok={legs_ok}"); sys.exit(1)
PY

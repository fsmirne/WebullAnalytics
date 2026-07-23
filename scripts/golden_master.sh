#!/usr/bin/env bash
# golden_master.sh — regression guard for the SPY winner (DCdiagwin) backtest.
#
# WHY: recent commits silently changed backtest RESULTS (e.g. cec0170 flipped opens
# 232->358 / PF 1.85->1.34 while its message claimed "backtest unchanged"). That kind
# of drift is exactly what erodes trust in the backtest. This freezes the winner's
# backtest SELECTION + edge over a FIXED window into a baseline fixture. Run it after
# any change that could touch pricing/scoring/opener (or after installing a new wa.exe).
#   - PASS  => the backtest is byte-for-byte the same decisions as the frozen baseline.
#   - FAIL  => something moved the backtest. Review the diff. If the change is INTENDED
#              (a real fix/improvement), re-baseline:  bash scripts/golden_master.sh --update
#
# It pins: open COUNT, closed-trade PF, and a sha256 of the sorted (date|structure-key)
# of every open — so any change to WHICH trades open, or their pricing, trips it.
#
# Fixed window is a fast ~3-month slice (routine guard). Full-window headline for
# reference: 354 opens, PF 1.59 (2025-01-02..2026-07-11). Runs against the installed
# wa.exe + AppData quote store.
set -u

STRATEGY=DC        # DC is now the source of truth = the winner (diag-only 5-15/30-45 @ 0.20)
TICKER=SPY
SINCE=2025-01-02
UNTIL=2025-03-31
WAHOME="$(ls -d /mnt/c/Users/*/AppData/Local/WebullAnalytics 2>/dev/null | head -1)"
WINUSER="$(echo "$WAHOME" | cut -d/ -f5)"
WA="$WAHOME/wa.exe"
BASELINE="/mnt/c/dev/WebullAnalytics/scripts/golden_master.${STRATEGY}.baseline.json"
WINFILLS="C:\\Users\\$WINUSER\\AppData\\Local\\WebullAnalytics\\sweeps\\golden_master.jsonl"
LXFILLS="$WAHOME/sweeps/golden_master.jsonl"
UPDATE=0; [ "${1:-}" = "--update" ] && UPDATE=1

[ -x "$WA" ] || { echo "FATAL: wa.exe not found at $WA"; exit 2; }

echo "[golden] $STRATEGY backtest $TICKER $SINCE..$UNTIL (lots-1, stride-1) — ~7 min…"
"$WA" ai backtest "$TICKER" --strategy "$STRATEGY" --since "$SINCE" --until "$UNTIL" \
  --lots 1 --scan-stride 1 --fills-jsonl "$WINFILLS" >/dev/null 2>&1 || { echo "FATAL: backtest failed"; exit 2; }

python3 - "$LXFILLS" "$BASELINE" "$UPDATE" "$SINCE" "$UNTIL" <<'PY'
import json, hashlib, sys, os
fills_path, baseline_path, update, since, until = sys.argv[1], sys.argv[2], sys.argv[3]=="1", sys.argv[4], sys.argv[5]
if not os.path.exists(fills_path):
    print("FATAL: no fills produced (empty window or no quotes)"); sys.exit(2)
byl={}
for line in open(fills_path):
    line=line.strip()
    if not line: continue
    f=json.loads(line); d=byl.setdefault(f['lineage'], {'pnl':0.0,'closed':False,'open':None})
    d['pnl']+=f['net']-f['fees']
    if f['kind']=='Open': d['open']=f['ts'][:10]+'|'+f['key']
    else: d['closed']=True
opens=sorted(v['open'] for v in byl.values() if v['open'])
closed=[v['pnl'] for v in byl.values() if v['closed']]
gw=sum(x for x in closed if x>0); gl=sum(x for x in closed if x<=0)
pf=round(gw/abs(gl),2) if gl else None
sig={'window':f'{since}..{until}','opens':len(opens),'closed':len(closed),'pf':pf,
     'opens_sha256':hashlib.sha256('\n'.join(opens).encode()).hexdigest()}
if update or not os.path.exists(baseline_path):
    json.dump(sig, open(baseline_path,'w'), indent=2)
    print('[golden] baseline', 'UPDATED' if os.path.exists(baseline_path) and update else 'WRITTEN', '->', {k:sig[k] for k in ('window','opens','closed','pf')}); sys.exit(0)
base=json.load(open(baseline_path))
diffs=[k for k in ('window','opens','closed','pf','opens_sha256') if base.get(k)!=sig.get(k)]
if not diffs:
    print('[golden] PASS — backtest unchanged:', {k:sig[k] for k in ('opens','closed','pf')}); sys.exit(0)
print('[golden] *** FAIL — backtest CHANGED vs baseline ***')
for k in diffs: print(f'    {k}: baseline={base.get(k)}  now={sig.get(k)}')
print('[golden] If intended, re-baseline: bash scripts/golden_master.sh --update')
sys.exit(1)
PY

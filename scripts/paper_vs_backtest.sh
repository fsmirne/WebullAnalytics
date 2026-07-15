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

# Bar-align the backtest to live's ACTUAL entry minute. The opener normally fires at the 09:30 open, but a
# quote-vendor outage can delay the first successful proposal (e.g. Webull API down till 09:45). --open-after
# withholds backtest opens until that ET minute so BOTH sides evaluate the same bar; on a clean day it resolves
# to 09:30 (a no-op). NOTE: --open-after also lets the 09:30→entry intraday tape blend into the directional
# bias, so it aligns the entry BAR, not the bias provenance a broken live day actually had.
LIVE_MIN=$(python3 - "$DATE" "$PROPOSALS" <<'PY'
import json, sys
date, path = sys.argv[1], sys.argv[2]
mins=[]
for line in open(path):
    line=line.strip()
    if not line: continue
    try: r=json.loads(line)
    except Exception: continue
    if r.get('type')=='open' and r.get('ts','')[:10]==date and r['ts'][11:19]>='09:30:00':
        mins.append(r['ts'][11:16])
print(min(mins) if mins else '')
PY
)
OPEN_AFTER=()
[ -n "$LIVE_MIN" ] && OPEN_AFTER=(--open-after "$LIVE_MIN")
"$WA" ai backtest SPY --strategy "$STRATEGY" --since "$DATE" --until "$DATE" --lots 1 --scan-stride 1 ${OPEN_AFTER[@]+"${OPEN_AFTER[@]}"} --fills-jsonl "$WINFILLS" >/dev/null 2>&1

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
    # net debit per share = sum(buy price) - sum(sell price)
    s=0.0
    for l in legs:
        p=float(l.get(price_key,0) or 0)
        s += p if str(l[side_key]).lower()=='buy' else -p
    return round(s,4)

def live_quote_map(live):
    # symbol -> {mid,bid,ask,iv,oi} from the opener's captured per-leg NBBO (diagnostic.probe.legQuotes)
    q={}
    probe=(live.get('diagnostic') or {}).get('probe') or {}
    for lq in (probe.get('legQuotes') or []):
        bid,ask=lq.get('bid'),lq.get('ask')
        mid=(bid+ask)/2 if bid is not None and ask is not None else None
        q[lq.get('symbol')]={'mid':mid,'bid':bid,'ask':ask,'iv':lq.get('impliedVolatility'),'oi':lq.get('openInterest')}
    return q

def fmt(v, nd=3):
    return f"{v:.{nd}f}" if isinstance(v,(int,float)) else "n/a"

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

# ---- shared aligned grid: label(LW) + live(W) + bt(W) + Δ(W) [+ note] ----
LW, W = 26, 11
def cell(s): return f"{s:>{W}}"
def row(label, a, b, d, note=""):
    line=f"  {label:<{LW}}{cell(a)}{cell(b)}{cell(d)}"
    if note: line+=f"   {note}"
    print(line.rstrip())
def sf(v, nd, pct=False, sign=False):
    if not isinstance(v,(int,float)): return "n/a"
    x=v*100 if pct else v
    s=f"{x:+.{nd}f}" if sign else f"{x:.{nd}f}"
    return s+("pt" if pct and sign else "%" if pct else "")
def delta(a,b): return (b-a) if isinstance(a,(int,float)) and isinstance(b,(int,float)) else None
def verdict(label, detail, ok):
    print(f"  {label:<{LW}}{detail:<{2*W}}{'MATCH ✓' if ok else 'MISMATCH ✗'}")

diag=live.get('diagnostic') or {}
lq=live_quote_map(live)
bt_p={l['sym']:(float(l.get('price',0) or 0), str(l['side']).lower()) for l in bt['legs']}
live_side={l['symbol']:str(l['action']).lower() for l in live['legs']}

# ---- structure + legs (the PASS criterion) ----
verdict("structure", live.get('structure') if struct_ok else f"{live.get('structure')} vs {bt.get('strategy')}", struct_ok)
verdict("legs", f"{len(live_legs)} legs", legs_ok)

# ---- per-leg ----
print()
def leg_ctx(sym):   # live-side bid/ask, iv, oi for a symbol
    q=lq.get(sym) or {}
    parts=[]
    if isinstance(q.get('bid'),(int,float)): parts.append(f"{q['bid']:.2f}/{q['ask']:.2f}")
    if isinstance(q.get('iv'),(int,float)):  parts.append(f"iv {q['iv']*100:.1f}%")
    if isinstance(q.get('oi'),(int,float)):  parts.append(f"oi {q['oi']}")
    return f"({', '.join(parts)})" if parts else ""
if legs_ok:
    # same legs → align by symbol: live captured mid vs backtest fill
    row("per-leg", "live mid", "bt fill", "Δ(bt-lv)")
    for sym in sorted(live_side):
        mid,bp=(lq.get(sym) or {}).get('mid'),bt_p.get(sym,(None,None))[0]
        row(f"{live_side[sym]:<4} {sym}", sf(mid,3), sf(bp,3), sf(delta(mid,bp),3,sign=True), leg_ctx(sym))
else:
    # different legs → don't fake a symbol alignment; list each side's legs, each row prefixed live/bt
    print("  legs differ — listed per side (no per-leg Δ):")
    for sym in sorted(live_side):
        print(f"    live  {live_side[sym]:<4} {sym}  @ {fmt((lq.get(sym) or {}).get('mid'),3)}   {leg_ctx(sym)}".rstrip())
    for sym in sorted(bt_p):
        price,side=bt_p[sym]
        print(f"    bt    {side:<4} {sym}  @ {fmt(price,3)}")

# ---- headline metrics: all comparable (both sides emit these) ----
print()
row("metric", "live", "bt", "Δ(bt-lv)")
# entry-time alignment: --open-after should have pinned bt to live's minute; a residual gap flags a day the
# backtest couldn't bar-align (deltas below would then reflect timing, not the engine).
def hms(t):
    try: h,m,s=t.split(':'); return int(h)*3600+int(m)*60+int(float(s))
    except Exception: return None
lt_s,bt_s=hms(live['ts'][11:19]),hms(bt['ts'][11:19])
skew=(bt_s-lt_s) if lt_s is not None and bt_s is not None else None
row("entry time ET", live['ts'][11:19], bt['ts'][11:19], "aligned" if (skew is not None and abs(skew)<60) else (f"{skew//60:+d}m" if skew is not None else "n/a"))
live_net=diag.get('netMidPerShare')
if live_net is None and live.get('cashImpactPerContract') is not None: live_net=abs(live['cashImpactPerContract'])/100.0
bt_mid=entry_debit(bt['legs'],'price','side')                       # mid-based, no slippage
bt_fill=abs(bt.get('net',0))/max(1,bt.get('qty',1))/100.0           # what the sim actually paid (mid + slippage model)
row("net debit/share", sf(live_net,3), sf(bt_mid,3), sf(delta(live_net,bt_mid),3,sign=True), f"bt fill {sf(bt_fill,3)} incl. slippage")
lspot,bspot=diag.get('spotAtEvaluation'),bt.get('spot')
row("spot @ entry", sf(lspot,2), sf(bspot,2), sf(delta(lspot,bspot),2,sign=True))
# live rep IV = mean of the structure's per-leg IVs (== the scorer's representativeIv; see CandidateScorer)
ivs=[q['iv'] for q in lq.values() if isinstance(q.get('iv'),(int,float))]
live_iv=sum(ivs)/len(ivs) if ivs else None
lf,bf=live.get('finalScore'),bt.get('finalScore')
lr,br=live.get('rawScore'),bt.get('rawScore')
row("finalScore", sf(lf,5), sf(bf,5), sf(delta(lf,bf),5,sign=True))
row("rawScore", sf(lr,5), sf(br,5), sf(delta(lr,br),5,sign=True))
row("rep IV", sf(live_iv,2,pct=True), sf(bt.get('iv'),2,pct=True), sf(delta(live_iv,bt.get('iv')),2,pct=True,sign=True))

# ---- live-only context (no backtest-fill counterpart) ----
print()
be=live.get('breakevens') or []
betail=f"   BE {'/'.join(f'{b:.2f}' for b in be)}" if be else ""
if isinstance(live.get('pop'),(int,float)):
    print(f"  live-only   pop {live['pop']*100:.1f}%   ev ${fmt(live.get('ev'),2)}   θ/day ${fmt(live.get('thetaPerDayPerContract'),2)}{betail}")

if skew is not None and abs(skew)>=60:
    print(f"  ⚠ entry-time skew {skew//60:+d}m — backtest could NOT bar-align to live; deltas may reflect timing, not the engine.")

print()
if struct_ok and legs_ok:
    print("  RESULT: MATCH ✓ (same structure + same legs)"); sys.exit(0)
print(f"  RESULT: MISMATCH — structure_ok={struct_ok} legs_ok={legs_ok}"); sys.exit(1)
PY

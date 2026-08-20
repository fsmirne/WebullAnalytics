"""Study 9 (registered 2026-08-19): case-control test of the MRNA-style block-build fingerprint.
Three modes, run in order (see the campaign doc for the frozen design):
  --discover  scan massive grouped dailies for case events (+40% day, $5+, $25M+ dollar vol) -> cases.json
  --pull      ThetaData EOD OI/vol pulls for each case + 2 self-control windows (SINGLE session: pause any
              other ThetaData pull first)
  --score     apply the frozen fingerprint to every window; Fisher exact on the 2x2
MRNA (the discovery case) is excluded from scoring. cases.json lives in the LOCAL research dir (never in
the repo). Windows-native python. Usage: python research\\signed_flow\\study9_casecontrol.py --discover
"""
import argparse
import json
import math
import re
import sys
import urllib.request
from datetime import date, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from import_quotes_sqlite import resolve_data_dir  # noqa: E402

DATA = resolve_data_dir()
CASES = DATA.parent / "research" / "study9_cases.json"
MIN_MOVE, MIN_PX, MIN_DVOL, TOP_N, WIN = 0.40, 5.0, 25e6, 24, 10


def trading_days(start, end):
    d, out = start, []
    while d <= end:
        if d.weekday() < 5:
            out.append(d)
        d += timedelta(days=1)
    return out


def discover():
    import time
    key = json.load(open(DATA / "api-config.json"))["massive"]["apiKey"]
    closes = {}   # ticker -> list of (date, close, dollar_vol)
    days = trading_days(date(2025, 1, 2), date(2026, 8, 14))
    loaded = failed = 0
    for i, d in enumerate(days):
        url = f"https://api.massive.com/v2/aggs/grouped/locale/us/market/stocks/{d}?adjusted=true&apiKey={key}"
        r = None
        for attempt in range(4):
            try:
                r = json.load(urllib.request.urlopen(url, timeout=60))
                break
            except Exception:
                time.sleep(20 * (attempt + 1))   # the grouped endpoint is quota'd ~5/min: long backoff, few retries
        time.sleep(12.5)   # pace UNDER the quota so failures are the exception, not 22% of requests
        if not r or r.get("status") != "OK":
            failed += 1
            continue
        loaded += 1
        for row in r.get("results") or []:
            t = row.get("T")
            if not t or not t.isalpha() or len(t) > 5:
                continue
            closes.setdefault(t, []).append((str(d), row.get("c") or 0.0, (row.get("c") or 0) * (row.get("v") or 0)))
        if i % 50 == 0:
            print(f"  scanned {i}/{len(days)} sessions (loaded {loaded}, failed {failed})", flush=True)
    print(f"sessions loaded {loaded}, failed {failed}")
    if failed > len(days) * 0.05:
        print("ABORT: too many failed sessions — gap artifacts would corrupt the return pairs")
        return
    events = []
    for t, rows in closes.items():
        rows.sort()
        best = None
        for j in range(1, len(rows)):
            d0, c0, dv0 = rows[j - 1]
            d1, c1, _ = rows[j]
            # Adjacency guard: a "one-day" return must span consecutive sessions (<= 4 calendar days
            # covers weekends/holidays) — a gap from a failed load must never masquerade as a day move.
            if (date.fromisoformat(d1) - date.fromisoformat(d0)).days > 4:
                continue
            if c0 >= MIN_PX and dv0 >= MIN_DVOL and c0 > 0 and c1 / c0 - 1 >= MIN_MOVE:
                mv = c1 / c0 - 1
                if best is None or mv > best[0]:
                    best = (mv, t, d1, c0)
        if best:
            events.append(best)
    events.sort(reverse=True)
    events = [e for e in events if e[1] != "MRNA"][:TOP_N]
    CASES.parent.mkdir(parents=True, exist_ok=True)
    CASES.write_text(json.dumps([{"ticker": t, "event": d, "move": round(mv, 3), "prior_close": c} for mv, t, d, c in events], indent=2))
    print(f"cases -> {CASES}")
    for mv, t, d, c in events:
        print(f"  {t:6s} {d}  {mv:+.0%}  prior close {c:.2f}")


def window_bounds(all_days, end_idx):
    return all_days[end_idx - WIN + 1], all_days[end_idx]


def plan_windows(cases):
    """(ticker, start, end, kind) for case + 2 self-control windows; pulls need end+2 sessions for dOI."""
    plans = []
    for c in cases:
        ev = date.fromisoformat(c["event"])
        tds = trading_days(ev - timedelta(days=400), ev + timedelta(days=3))
        try:
            ei = tds.index(ev)
        except ValueError:
            continue
        plans.append((c["ticker"], tds[ei - WIN], tds[ei - 1], "case"))
        for off in (60, 120):   # control windows ending 60/120 weekdays before the event
            if ei - off - WIN >= 0:
                plans.append((c["ticker"], tds[ei - off - WIN], tds[ei - off - 1], "control"))
    return plans


def pull():
    from backfill_thetadata import run as bf_run  # noqa: E402  (sequential, one session per chunk child)
    cases = json.loads(CASES.read_text())
    plans = plan_windows(cases)
    print(f"{len(plans)} window pulls")
    for i, (t, s, e, kind) in enumerate(plans, 1):
        print(f"[{i}/{len(plans)}] {t} {kind} {s}..{e}", flush=True)
        try:
            bf_run([t], s, e + timedelta(days=5), DATA / "oi", 60, 0.045, None, 300, 2)
        except Exception as ex:
            print(f"  [error] {t} {s}: {type(ex).__name__}: {ex}")


def fingerprint(ticker, start, end):
    """Frozen stage-1 fingerprint over one window. Returns (flagged, total_doi, sessions, premium)."""
    occ = re.compile(rf"^{ticker}(\d{{6}})([CP])(\d{{8}})$")
    d = DATA / "oi" / ticker
    days = sorted(p.stem for p in d.glob("????-??-??.jsonl") if start.isoformat() <= p.stem)
    snaps = {}
    for day in days[:WIN + 3]:
        rec = json.loads((d / (day + ".jsonl")).read_text().strip().splitlines()[-1])
        m_ = {}
        for o in rec.get("options", []):
            m = occ.match(o.get("symbol") or "")
            if m:
                m_[(int("20" + m.group(1)), int(m.group(3)), m.group(2))] = (o.get("volume") or 0, o.get("openInterest") or 0, o.get("bid"), o.get("ask"))
        snaps[day] = (rec.get("underlyingPrice"), m_)
    wdays = [day for day in snaps if day <= end.isoformat()]
    total_doi, sess, prem = 0, set(), 0.0
    for i, day in enumerate(sorted(snaps)[:-1]):
        if day not in wdays:
            continue
        nxt_day = sorted(snaps)[sorted(snaps).index(day) + 1]
        spot, snap = snaps[day]
        nxt = snaps[nxt_day][1]
        if not spot:
            continue
        d_int = int(day.replace("-", ""))
        for (exp, k, r), (vol, oi, bid, ask) in snap.items():
            if r != "C":
                continue
            strike = k / 1000.0
            dte = (date(exp // 10000, exp // 100 % 100, exp % 100) - date.fromisoformat(day)).days
            if not (0.85 * spot <= strike <= 1.5 * spot) or dte > 45 or dte < 1:
                continue
            if vol < 250 or vol < 2 * max(oi, 1):
                continue
            n = nxt.get((exp, k, r))
            if not n:
                continue
            doi = n[1] - oi
            if doi < max(250, 2 * max(oi, 1)):
                continue
            total_doi += doi
            sess.add(day)
            mid = (bid + ask) / 2 if bid and ask else 0
            prem += doi * mid * 100
    flagged = total_doi >= 5000 and len(sess) >= 2 and prem >= 500_000
    return flagged, total_doi, len(sess), prem


def score():
    cases = json.loads(CASES.read_text())
    plans = plan_windows(cases)
    rows = []
    for t, s, e, kind in plans:
        if not (DATA / "oi" / t).exists():
            continue
        fl, doi, sess, prem = fingerprint(t, s, e)
        rows.append((kind, fl, t, s, doi, sess, prem))
        print(f"{kind:7s} {t:6s} {s}..{e}  dOI {doi:7,}  sessions {sess}  prem ${prem / 1e6:5.2f}M  -> {'FLAG' if fl else '-'}")
    ca = [r for r in rows if r[0] == "case"]
    co = [r for r in rows if r[0] == "control"]
    if len(ca) < 12:
        print(f"NOT EVALUABLE: {len(ca)} scoreable cases (< 12)")
        return
    a = sum(1 for r in ca if r[1])
    b = len(ca) - a
    c = sum(1 for r in co if r[1])
    dd = len(co) - c
    sens, spec = a / len(ca), dd / max(len(co), 1)

    def fisher_p(a, b, c, d):
        # two-sided Fisher via hypergeometric enumeration
        from math import comb
        n, r1, c1 = a + b + c + d, a + b, a + c
        p_obs = comb(r1, a) * comb(n - r1, c1 - a) / comb(n, c1)
        p = 0.0
        for x in range(max(0, c1 - (n - r1)), min(r1, c1) + 1):
            px = comb(r1, x) * comb(n - r1, c1 - x) / comb(n, c1)
            if px <= p_obs + 1e-12:
                p += px
        return p

    p = fisher_p(a, b, c, dd)
    print(f"\ncases {a}/{len(ca)} flagged (sensitivity {sens:.0%}) | controls {c}/{len(co)} flagged (specificity {spec:.0%}) | Fisher p={p:.4f}")
    ok = sens >= 0.40 and c / max(len(co), 1) <= 0.10 and p < 0.05
    print(f"REGISTERED VERDICT: {'PASS' if ok else 'KILLED'} (needs sens>=40%, control-flag<=10%, p<0.05)")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--discover", action="store_true")
    ap.add_argument("--pull", action="store_true")
    ap.add_argument("--score", action="store_true")
    a = ap.parse_args()
    if a.discover:
        discover()
    elif a.pull:
        pull()
    elif a.score:
        score()
    else:
        print("pick --discover / --pull / --score")

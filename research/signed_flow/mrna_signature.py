"""One-off forensic: Study 7 signature features for MRNA's pre-move confirmed call builds (2026-08-04..18).
Descriptive only — the frozen selector's IV term is SPY-scale and does not transfer; what transfers is the
execution signature: decisiveness (net/signed), size, print size, concentration."""
import json
import re
import sqlite3
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from import_quotes_sqlite import resolve_data_dir  # noqa: E402

data = resolve_data_dir()
conn = sqlite3.connect(rf"file:{data / 'quotes.db'}?mode=ro", uri=True)
d = data / "oi" / "MRNA"
days = sorted(p.stem for p in d.glob("????-??-??.jsonl"))
OCC = re.compile(r"^MRNA(\d{6})([CP])(\d{8})$")
snaps = {}
for day in days:
    rec = json.loads((d / (day + ".jsonl")).read_text().strip().splitlines()[-1])
    m_ = {}
    for o in rec["options"]:
        m = OCC.match(o.get("symbol") or "")
        if m:
            m_[(int("20" + m.group(1)), int(m.group(3)), m.group(2))] = (o.get("volume") or 0, o.get("openInterest") or 0, o.get("iv") or 0)
    snaps[day] = m_

print(f"{'day':10s} {'contract':ift}" if False else f"{'day':10s} {'contract':15s} {'dOI':>8s} {'decis':>6s} {'sgnbl':>6s} {'vol':>7s} {'prtsz':>6s} {'conc':>5s} {'iv':>5s}  verdict")
for i, day in enumerate(days[:-1]):
    snap, nxt = snaps[day], snaps[days[i + 1]]
    d_int = int(day.replace("-", ""))
    for (exp, k, r), (vol, oi, iv) in sorted(snap.items()):
        if r != "C" or vol < 250 or vol < 2 * max(oi, 1):
            continue
        n = nxt.get((exp, k, r))
        if not n or (n[1] - oi) < max(250, 2 * max(oi, 1)):
            continue
        bars = conn.execute("SELECT time_sec, close, volume, trades FROM ohlcv WHERE root='MRNA' AND expiry=? AND date=? AND strike_milli=? AND right='C'",
                            (exp, d_int, k)).fetchall()
        nbbo = dict((t, (b, a)) for t, b, a in conn.execute(
            "SELECT time_sec, bid, ask FROM quotes WHERE root='MRNA' AND expiry=? AND date=? AND strike_milli=? AND right='C'", (exp, d_int, k)))
        if not bars:
            continue
        net = signed = trades = tv = 0
        half = {}
        for t, close, v, tr in bars:
            tv += v
            trades += tr or 0
            half[t // 1800] = half.get(t // 1800, 0) + v
            ba = nbbo.get(t)
            if not ba or ba[0] <= 0 or ba[1] <= 0:
                continue
            mid, spr = (ba[0] + ba[1]) / 2.0, ba[1] - ba[0]
            if abs(close - mid) < 0.25 * spr:
                continue
            net += (1 if close > mid else -1) * v
            signed += v
        if signed == 0:
            continue
        decis = net / signed
        verdict = "QUIET-LARGE" if 0 < decis <= 0.338 and tv >= 5034 else ("quiet-small" if 0 < decis <= 0.338 else "LOUD" if decis > 0.338 else "net-sold?")
        print(f"{day} {exp} {k / 1000:6.1f}C {n[1] - oi:+8,} {decis:+6.2f} {signed / max(tv, 1):6.1%} {tv:7,} {tv / max(trades, 1):6.1f} {max(half.values()) / max(tv, 1):5.1%} {iv:5.2f}  {verdict}")

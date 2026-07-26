#!/usr/bin/env python3
"""Page historical 1-minute SPX bars from Webull's charts/query-mini endpoint — no browser needed.

Discovered 2026-07-26: the endpoint serves full 800-bar pages with ONLY the app identity headers (appid/
did/device-type/...) — no x-s signature, no access_token (bare requests degrade to 1 row; identity headers
unlock depth). This retires the browser-console sniffer for chart capture. Rows come back in the exact
sniffer dump format (`ts,o,c,h,l,prevClose,vol,vwap`), so the output file feeds `wa ai history
--import-webull-spx` unchanged.

Pages backward from --from-ts (default: the oldest real SPXW tape day) to --until (default 2022-01-01),
appending to the output file as it goes; resumable — on restart it continues below the file's oldest row.
Requires WEBULL_DID in the environment (a device id is semi-sensitive; keep it out of the repo).

Usage: WEBULL_DID=... pull_webull_spx_minutes.py [--out webull_spx_deep.txt] [--until 2022-01-01]
       [--from-ts <epoch>] [--ticker-id 913354362] [--sleep 0.4]
"""
import argparse, os, sys, time, urllib.request, json
from datetime import datetime, timezone

API = "https://quotes-gw.webullfintech.com/api/quote/charts/query-mini"


def headers(did):
    return {"appid": "wb_web_app", "app": "global", "app-group": "broker", "device-type": "Web",
            "did": did, "os": "web", "osv": "i9zh", "platform": "web", "ver": "6.5.3",
            "hl": "en", "tz": "America/New_York", "referer": "https://app.webull.com/",
            "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}


def fetch_page(did, ticker_id, ts, count=800, retries=4):
    url = f"{API}?type=m1&count={count}&timestamp={ts}&restorationType=0&tickerId={ticker_id}"
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers=headers(did))
            with urllib.request.urlopen(req, timeout=30) as r:
                j = json.loads(r.read())
            return (j[0].get("data") or []) if j else []
        except Exception as e:
            wait = 2 ** attempt
            print(f"  [warn] page @{ts}: {e} — retry in {wait}s", flush=True)
            time.sleep(wait)
    sys.exit(f"FATAL: page @{ts} failed after {retries} attempts — re-run to resume (file is append-safe)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="webull_spx_deep.txt")
    ap.add_argument("--until", default="2022-01-01", help="stop once rows are older than this ET date")
    ap.add_argument("--from-ts", type=int, default=None, help="epoch to start paging back from (default 2024-05-14 04:00 ET)")
    ap.add_argument("--ticker-id", default="913354362", help="Webull ticker id (default SPX)")
    ap.add_argument("--sleep", type=float, default=0.4)
    ap.add_argument("--did", default=None, help="Webull device id (`did` header from your browser session); overrides WEBULL_DID env")
    a = ap.parse_args()
    did = a.did or os.environ.get("WEBULL_DID") or sys.exit("FATAL: pass --did or set WEBULL_DID (the `did` header value from your browser session)")

    until_epoch = int(datetime.strptime(a.until, "%Y-%m-%d").replace(tzinfo=timezone.utc).timestamp())  # UTC midnight is early enough for any ET session
    cursor = a.from_ts or int(datetime(2024, 5, 14, 8, 0, tzinfo=timezone.utc).timestamp())

    seen = set()
    if os.path.exists(a.out):
        with open(a.out) as f:
            for line in f:
                try: seen.add(int(line.split(",")[0]))
                except ValueError: pass
        if seen:
            cursor = min(seen) - 60
            print(f"resuming below existing file: {len(seen):,} rows, cursor -> {datetime.fromtimestamp(cursor, timezone.utc):%Y-%m-%d %H:%M}Z")

    pages = added = 0
    with open(a.out, "a") as out:
        while cursor > until_epoch:
            rows = fetch_page(did, a.ticker_id, cursor)
            pages += 1
            if not rows:
                print(f"empty page @{datetime.fromtimestamp(cursor, timezone.utc):%Y-%m-%d}Z — assuming exchange gap, stepping back a day")
                cursor -= 86400
                continue
            new = [r for r in rows if int(r.split(",")[0]) not in seen]
            for r in new:
                seen.add(int(r.split(",")[0]))
                out.write(r + "\n")
            out.flush()
            added += len(new)
            oldest = min(int(r.split(",")[0]) for r in rows)
            if pages % 25 == 0:
                print(f"  {pages} pages, {added:,} rows, oldest {datetime.fromtimestamp(oldest, timezone.utc):%Y-%m-%d %H:%M}Z", flush=True)
            if oldest >= cursor:  # no backward progress — server refused to go deeper
                print(f"STOP: no backward progress at {datetime.fromtimestamp(oldest, timezone.utc):%Y-%m-%d}Z — this is the server's true horizon")
                break
            cursor = oldest - 60
            time.sleep(a.sleep)
    print(f"done: {pages} pages, {added:,} new rows -> {a.out}")


if __name__ == "__main__":
    main()

<#
options_backfill.ps1 - refresh a ticker's real-option dataset.

Two discovery passes (bd=1.3, bd=1.5) widen the contract catalog, then one
backfill pulls every cataloged OCC from Webull (live) / massive.com (expired,
rate-limited to 5 req/min). RESUMABLE: merge-by-timestamp + skip-complete mean
you can Ctrl-C and re-run, or run again later to finish. Re-running never
re-fetches contracts already on disk.

Runs the INSTALLED production `wa` (on PATH, or %LOCALAPPDATA%\WebullAnalytics\wa.exe)
- not a dev build. If `wa` rejects a flag (e.g. --pad), the installed binary is
stale: re-run install to update it, then re-run this script.

Run (leave the window open if it's a long pull):
  powershell -ExecutionPolicy Bypass -File .\scripts\options_backfill.ps1 -Ticker SPXW
  powershell -ExecutionPolicy Bypass -File .\scripts\options_backfill.ps1 -Ticker XSP -Since 2025-01-01
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\logs\options_backfill_<TICKER>-*.log" -Wait -Tail 20

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics\data) - the binary resolves
its base dir there automatically.
#>

param(
  [Parameter(Mandatory = $true)][string]$Ticker,
  [string]$Since = '2025-01-01'
)

$ErrorActionPreference = 'Continue'
$Ticker = $Ticker.ToUpperInvariant()

# Resolve the production wa: prefer PATH, fall back to the AppData install location.
$Wa = $null
$cmd = Get-Command wa -ErrorAction SilentlyContinue
if ($cmd) { $Wa = $cmd.Source }
if (-not $Wa) {
  $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'
  if (Test-Path $candidate) { $Wa = $candidate }
}

$Data    = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$Catalog = Join-Path $Data ("options-discovery\{0}.jsonl" -f $Ticker)
$OptDir  = Join-Path $Data ("options\{0}" -f $Ticker)
$Until   = (Get-Date -Format 'yyyy-MM-dd')

$LogDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Log = Join-Path $LogDir ("options_backfill_{0}-{1}.log" -f $Ticker, (Get-Date -Format 'yyyyMMdd-HHmmss'))

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

Log "=== $Ticker options backfill ==="
Log "log file: $Log"

if (-not $Wa) { Log "FATAL: 'wa' not found on PATH or in %LOCALAPPDATA%\WebullAnalytics. Install it first."; exit 1 }
Log "using wa: $Wa"

# 1. Discovery passes - catalog-only, no network, seconds each. --min-score-to-open 0
#    emits the full candidate pool (so sweeps at any gate have coverage). 1.0 is the live/
#    backtest default (the strikes the real run actually touches); 1.3/1.5 add sweep headroom
#    above it. Each pass unions onto the prior picks; padding is recomputed over the union.
foreach ($bd in '1.0', '1.3', '1.5') {
  Log "discover bd=$bd  (min-score-to-open=0, top-k=40, pad=3)"
  & $Wa options discover $Ticker --since $Since --until $Until --min-score-to-open 0 --bias-drift $bd --top-k 40 --pad 3 *>> $Log
  if ($LASTEXITCODE -ne 0) { Log "FATAL: discover bd=$bd failed (stale wa, or missing bar coverage for $Since?) - inspect $Log"; exit 1 }
}

# 2. Estimate the incremental fetch BEFORE the slow part, so the log records the
#    expected duration. Nearly all 2025-2026 contracts are expired -> massive.
#    Note: this counts only the discovery catalog; --webull-pad below adds more
#    Webull-routable contracts (live + future expiries) that aren't in the file,
#    but those run at 5 req/sec so they don't move the wall-clock meaningfully.
Log "Estimating incremental fetch..."
$rxOcc = [regex]'"occ":"([^"]+)"'
$rxSym = [regex]'^([A-Z]+)(\d{2})(\d{2})(\d{2})([CP])(\d{8})$'
$total = 0; $have = 0; $miss = 0
foreach ($line in [System.IO.File]::ReadLines($Catalog)) {
  $mo = $rxOcc.Match($line); if (-not $mo.Success) { continue }
  $occ = $mo.Groups[1].Value
  $ms = $rxSym.Match($occ); if (-not $ms.Success) { continue }
  $total++
  $expiry = "20{0}-{1}-{2}" -f $ms.Groups[2].Value, $ms.Groups[3].Value, $ms.Groups[4].Value
  $csv = Join-Path (Join-Path $OptDir $expiry) ($occ + '.csv')
  if (Test-Path $csv) { $have++ } else { $miss++ }
}
$hrs = [math]::Round($miss / 300.0, 1)
Log ("  catalog={0}  on-disk={1}  to-fetch={2}  ~{3}h at 5 req/min" -f $total, $have, $miss, $hrs)

# 3. Backfill - the long, rate-limited part. Partial failure (a few massive
#    SSL/empty contracts) is expected and not fatal; just log the exit code.
#    --webull-pad 30 widens each Webull-routable (expiry, right) by 30 strikes
#    beyond the touched range. Webull is unrestricted so this is cheap, and it
#    pre-captures strikes future strategy variants (wider delta bands, deeper
#    wings) might want - historical bars become inaccessible once a contract
#    drops out of the live chain. Massive (expired) contracts are unaffected.
Log "Backfill starting (long; rate-limited). Tail the log to watch progress."
& $Wa options backfill $Ticker --since $Since --webull-pad 30 *>> $Log
Log "Backfill exited rc=$LASTEXITCODE"

Log "=== Done ==="
Log "Next (sizing-neutral edge check on real prices):"
Log "  wa ai backtest $Ticker --since $Since --until $Until --lots 1"

<#
backtest_risk_sweep.ps1 - sweep the per-proposal capital-allocation cap on a $50k account.

Varies opener.maxRiskPctPerProposal over 0.05 .. 0.25 (layers ai-config.SPY.DCrisk*.json,
DC itself = 0.15) on the frozen DC strategy, COMPOUNDING at --starting-cash 50000.
This is a SIZING sweep, so it must run compounding -- NOT --lots 1, which bypasses the
cash/reserve gates and makes every risk% identical. The question it answers is
practical, not theoretical: "on my actual $50k account, what return and DRAWDOWN
would each allocation have produced, given the real caps and cash constraints?"

READ THIS BEFORE TRUSTING THE RANKING (see the sizing-caps-are-load-bearing note):
the compounding backtest CANNOT cleanly isolate the sizing frontier. Three things
clamp contract count as equity grows -- maxQtyPerProposal (500), maxDollarRiskPerProposal
($100k), and affordableQty = buyingPower / marginPerContract (cash). At $50k the $100k
dollar cap doesn't bind early, but CASH does: high-risk% cells converge on "deploy all
cash", so 0.20 and 0.25 will likely look near-identical (a prior run reported 15% and
20% DD identical to the cent). Consequences:
  - RAW RETURN is compounding fantasy at the high end (unrealizable contract counts) --
    do NOT pick the highest-return cell.
  - The DECISION METRIC is MAX DRAWDOWN vs your tolerance (~20-25%), read at the LOW end
    (0.05, 0.10) where the caps do not bind and the risk/return tradeoff is real.
  - Watch the Opens column: if a low-risk% cell opens FEWER trades than DC, that cell is
    cash-starved (a proposal sized below one contract is skipped), which is itself a
    finding -- 0.05 on $50k may be unable to afford the structure.

Max drawdown here is the engine's MTM equity-curve DD (cash + mark-to-market of open
positions), the authoritative number -- not a realized-only reparse.

Run (sequential; concurrent backtests contend on quotes.db):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_risk_sweep.ps1
Watch from another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\risk-sweep-*\sweep.log" -Wait -Tail 20

RUNTIME: full window stride 1 ~= 13-37 min/cell; 5 cells => ~1-3 h. Reads/writes PROD
data (%LOCALAPPDATA%\WebullAnalytics).
#>

param(
  [string]$Since = '2025-01-02',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [decimal]$StartingCash = 50000,
  [int]$ScanStride = 1,
  [string[]]$Strategies = @('DCrisk05', 'DCrisk10', 'DC', 'DCrisk20', 'DCrisk25'),
  [string]$RunId = ('risk-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# `-File ... -Strategies a,b,c` from an external shell arrives as ONE string; split it back.
if ($Strategies.Count -eq 1 -and $Strategies[0] -match ',') { $Strategies = $Strategies[0] -split '\s*,\s*' }

$Wa = $null
$cmd = Get-Command wa -ErrorAction SilentlyContinue
if ($cmd) { $Wa = $cmd.Source }
if (-not $Wa) {
  $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'
  if (Test-Path $candidate) { $Wa = $candidate }
}
if (-not $Wa) {
  Write-Host "FATAL: 'wa' not found on PATH or in %LOCALAPPDATA%\WebullAnalytics. Install it first."
  exit 1
}

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Scrape the engine's own "Backtest summary" table from the redirected cell log. When
# stdout is not a TTY, Spectre renders each metric on one un-wrapped line with no ANSI/
# markup, so a per-row regex is robust (validated against a live run). We take the
# engine's MTM max drawdown rather than reparsing fills, because DD must include the
# mark of still-open positions.
function Get-SummaryStats {
  param([string]$Path)

  $stats = [ordered]@{ EndingEquity = $null; TotalPnl = $null; TotalPct = $null; MaxDdDollar = $null; MaxDdPct = $null; PeakEquity = $null; TroughEquity = $null; Opens = $null }
  if (-not (Test-Path $Path)) { return $stats }

  $num = { param($s) [double](($s -replace '[\$,]', '')) }

  foreach ($line in Get-Content $Path) {
    if ($line -match 'Ending equity\D+\$([\d,]+\.\d{2})')                     { $stats.EndingEquity = & $num $matches[1] }
    elseif ($line -match 'Total P&L\D+\$(-?[\d,]+\.\d{2})\s*\((-?[\d.]+)%\)')  { $stats.TotalPnl = & $num $matches[1]; $stats.TotalPct = [double]$matches[2] }
    elseif ($line -match 'Max drawdown\D+\$([\d,]+\.\d{2})\s*\(([\d.]+)% of peak\)') { $stats.MaxDdDollar = & $num $matches[1]; $stats.MaxDdPct = [double]$matches[2] }
    elseif ($line -match 'Peak equity\D+\$([\d,]+\.\d{2})')                   { $stats.PeakEquity = & $num $matches[1] }
    elseif ($line -match 'Trough equity\D+\$([\d,]+\.\d{2})')                 { $stats.TroughEquity = & $num $matches[1] }
    elseif ($line -match 'Opens\D+(\d+)\s*\(')                                { $stats.Opens = [int]$matches[1] }
    elseif ($line -match 'Opens\s+\D+(\d+)\s*$')                              { $stats.Opens = [int]$matches[1] }
  }
  return $stats
}

Log "=== SPY DC capital-allocation (maxRiskPctPerProposal) sweep ==="
Log "wa: $Wa"
Log "ticker=$Ticker since=$Since until=$Until startingCash=$StartingCash scanStride=$ScanStride (COMPOUNDING)"
Log "strategies: $($Strategies -join ', ')"
Log "run dir: $RunDir"

$total = $Strategies.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($s in $Strategies) {
  $idx++
  $cellLog = Join-Path $RunDir ("run_" + $s + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $s)
  & $Wa ai backtest $Ticker --strategy $s --since $Since --until $Until --starting-cash $StartingCash --scan-stride $ScanStride *>&1 |
    Tee-Object -FilePath $cellLog | Out-Null

  $rc = $LASTEXITCODE
  $sw.Stop()
  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    continue
  }

  $st = Get-SummaryStats -Path $cellLog
  # Recover the swept risk% from the strategy name for the table (DC = 0.15).
  $riskPct = if ($s -eq 'DC') { 0.15 } else { [double]("0." + ($s -replace '\D', '')) }
  $row = [PSCustomObject]@{
    Strategy     = $s
    RiskPct      = $riskPct
    Opens        = $st.Opens
    EndingEquity = $st.EndingEquity
    ReturnPct    = $st.TotalPct
    MaxDdPct     = $st.MaxDdPct
    MaxDdDollar  = $st.MaxDdDollar
    PeakEquity   = $st.PeakEquity
    TroughEquity = $st.TroughEquity
    Elapsed      = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> risk={0:P0} opens={1} return={2}% maxDD={3}% endEq={4:N0} took={5}s" -f $row.RiskPct, $row.Opens, $row.ReturnPct, $row.MaxDdPct, $row.EndingEquity, $row.Elapsed)
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

Write-Host ""
Write-Host "--- Capital allocation on `$$StartingCash, by risk% (DC = 0.15 baseline) ---"
$results | Sort-Object -Property RiskPct | Format-Table Strategy, RiskPct, Opens, ReturnPct, MaxDdPct, MaxDdDollar, EndingEquity, TroughEquity -AutoSize
Write-Host ""
Write-Host "READ: pick by MAX DRAWDOWN vs your tolerance (~20-25%), NOT by return."
Write-Host "If Opens flattens or DD/return stop moving from 0.20 -> 0.25, the high end is"
Write-Host "cash-capped (converging on deploy-all-cash) -- that plateau is the confound,"
Write-Host "not a free lunch. If a low-risk cell opens FEWER trades than DC, it is cash-"
Write-Host "starved at `$$StartingCash (can't afford the structure)."

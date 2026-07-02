<#
biasdrift_sweep.ps1 - fine-grained 1-D sweep over opener.weights.biasDrift.

biasDrift is the amplifier that converts directional bias into a scenario-grid
shift; it sets HOW MUCH bias is needed before a long-premium structure (long
call/put) out-scores a defined-risk vertical. This sweep answers: does nudging
biasDrift above the live value (1.0) earn money on real NBBO fills across the
full window, and how does it change the vertical-vs-long-call structure mix?

Each cell overrides ONLY --bias-drift; every other knob comes from the resolved
config (ai-config.<TICKER>.<STRATEGY>.json), so the sweep isolates biasDrift.
Cell bd=1.0 is the control = the current live value.

Sizing-neutral by default (--lots 1): one contract per trade, so totals measure
per-trade edge (expectancy, profit factor), not a compounding curve.

Run (leave the window open):
  powershell -ExecutionPolicy Bypass -File .\scripts\biasdrift_sweep.ps1
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\bdsweep-*\sweep.log" -Wait -Tail 20

RUNTIME WARNING: full-window stride-1 is the faithful (live-cadence) setting but
slow — many minutes per cell over 2025-01-01..today. For a fast first read use a
coarser stride (same on every cell => fair relative ranking), then confirm the
winning cell at stride 1:
  # fast first pass:
  .\scripts\biasdrift_sweep.ps1 -ScanStride 15
  # full-window confirm at faithful cadence:
  .\scripts\biasdrift_sweep.ps1

Customize:
  -Since       Start date (YYYY-MM-DD). Default: 2025-01-01.
  -Until       End date (YYYY-MM-DD). Default: today (uses whatever data exists).
  -Ticker      Underlying. Default: XSP.
  -Strategy    Strategy layer (ai-config.<TICKER>.<STRATEGY>.json). Default: 0DTE.
  -Lots        Contracts per trade. Default: 1 (sizing-neutral).
  -ScanStride  Open-scan minute stride. Default: 1 (faithful). Raise to speed up.
  -BiasDrifts  The grid. Default: 0.90..1.50 in fine steps.
  -RunId       Override the auto-generated run folder name.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). Each run creates a
fresh sweep folder so reruns don't overwrite each other.
#>

param(
  [string]$Since = '2025-01-01',
  [string]$Until = (Get-Date -Format 'yyyy-MM-dd'),
  [string]$Ticker = 'XSP',
  [string]$Strategy = '0DTE',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [double[]]$BiasDrifts = @(0.90, 0.95, 1.00, 1.05, 1.10, 1.15, 1.20, 1.30, 1.50),
  [string]$RunId = ('bdsweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# Resolve installed wa: PATH first, then AppData fallback — runs against the
# deployed production binary, not a stale dev build.
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

# Parse a fills.jsonl produced by `wa ai backtest --fills-jsonl`. Each line is
# one fill (open / expire / rule-close / roll). Per-lineage P&L = sum(net) -
# sum(fees) over all fills with that lineageId. Total P&L = sum across lineages.
# Win rate = fraction of lineages with positive P&L. Profit factor = gross
# wins / gross losses (the key cost-aware edge metric). Structure mix is tallied
# from the 'key' field of Open fills (XSP_<Structure>_<strike>_<date>).
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0; LongCallPct = 0.0; VerticalPct = 0.0; StructMix = '' }
  if (-not (Test-Path $Path)) { return $empty }

  $lineagePnl = @{}
  $totalFees = 0.0
  $structCount = @{}
  $opens = 0

  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0 }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $totalFees += [double]$f.fees
    if ([string]$f.kind -eq 'Open') {
      $opens++
      $parts = ([string]$f.key).Split('_')
      $struct = if ($parts.Length -ge 2) { $parts[1] } else { 'Unknown' }
      if (-not $structCount.ContainsKey($struct)) { $structCount[$struct] = 0 }
      $structCount[$struct]++
    }
  }

  $trades = $lineagePnl.Count
  if ($trades -eq 0) { return $empty }

  $wins   = @($lineagePnl.Values | Where-Object { $_ -gt 0 }).Count
  $losses = @($lineagePnl.Values | Where-Object { $_ -le 0 }).Count
  $total  = ($lineagePnl.Values | Measure-Object -Sum).Sum
  $best   = ($lineagePnl.Values | Measure-Object -Maximum).Maximum
  $worst  = ($lineagePnl.Values | Measure-Object -Minimum).Minimum
  $grossWin  = (($lineagePnl.Values | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($lineagePnl.Values | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }

  # Structure mix: long calls/puts (single-leg directional) vs verticals (spreads).
  $longCall = 0; $vertical = 0
  foreach ($k in $structCount.Keys) {
    if     ($k -match 'LongCall$' -or $k -match 'LongPut$') { $longCall += $structCount[$k] }
    elseif ($k -match 'Vertical$') { $vertical += $structCount[$k] }
  }
  $mix = ($structCount.GetEnumerator() | Sort-Object -Property Value -Descending | ForEach-Object { "{0}:{1}" -f $_.Key, $_.Value }) -join ' '

  return [ordered]@{
    Trades   = $trades
    Wins     = $wins
    Losses   = $losses
    WinRate  = [math]::Round($wins / $trades, 3)
    ProfitFactor = $pf
    TotalPnl = [math]::Round($total, 2)
    AvgPnl   = [math]::Round($total / $trades, 2)
    BestPnl  = [math]::Round($best, 2)
    WorstPnl = [math]::Round($worst, 2)
    TotalFees = [math]::Round($totalFees, 2)
    LongCallPct = if ($opens -gt 0) { [math]::Round($longCall / $opens, 3) } else { 0.0 }
    VerticalPct = if ($opens -gt 0) { [math]::Round($vertical / $opens, 3) } else { 0.0 }
    StructMix = $mix
  }
}

Log "=== biasDrift backtest sweep ==="
Log "wa: $Wa"
Log "ticker=$Ticker strategy=$Strategy since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log "grid: biasDrift=[$($BiasDrifts -join ', ')]  (control = 1.0 = current live)"
Log "run dir: $RunDir"

$total = $BiasDrifts.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($bd in $BiasDrifts) {
  $idx++
  $tag = "bd$bd"
  $fillsPath = Join-Path $RunDir ("fills_" + $tag + '.jsonl')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  $cellLog = Join-Path $RunDir ("run_" + $tag + '.log')
  Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $tag)
  & $Wa ai backtest $Ticker --strategy $Strategy --since $Since --until $Until --lots $Lots --scan-stride $ScanStride `
    --bias-drift $bd `
    --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $cellLog | Out-Null

  $rc = $LASTEXITCODE
  $sw.Stop()
  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    continue
  }

  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    BiasDrift    = $bd
    Trades       = $stats.Trades
    Wins         = $stats.Wins
    Losses       = $stats.Losses
    WinRate      = $stats.WinRate
    ProfitFactor = $stats.ProfitFactor
    TotalPnl     = $stats.TotalPnl
    AvgPnl       = $stats.AvgPnl
    BestPnl      = $stats.BestPnl
    WorstPnl     = $stats.WorstPnl
    LongCallPct  = $stats.LongCallPct
    VerticalPct  = $stats.VerticalPct
    StructMix    = $stats.StructMix
    TotalFees    = $stats.TotalFees
    Elapsed      = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> trades={0} wr={1:P0} PF={2} total={3:N2} avg={4:N2} longCall={5:P0} vert={6:P0} took={7}s" -f $row.Trades, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.AvgPnl, $row.LongCallPct, $row.VerticalPct, $row.Elapsed)

  # Write results incrementally so the CSV is usable mid-sweep if you Ctrl-C.
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

# Baseline = the bd=1.0 control cell; everything is measured against it.
$baseline = $results | Where-Object { $_.BiasDrift -eq 1.0 } | Select-Object -First 1
if ($baseline) {
  Write-Host ""
  Write-Host ("--- Control (biasDrift=1.0): PF={0} total={1:N2} avg={2:N2} trades={3} wr={4:P0} longCall={5:P0} ---" -f $baseline.ProfitFactor, $baseline.TotalPnl, $baseline.AvgPnl, $baseline.Trades, $baseline.WinRate, $baseline.LongCallPct)
}

Write-Host ""
Write-Host "--- All cells by biasDrift (ascending) ---"
$results | Sort-Object -Property BiasDrift | Format-Table BiasDrift, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl, LongCallPct, VerticalPct -AutoSize
Write-Host ""
Write-Host "--- All cells by total P&L ---"
$results | Sort-Object -Property TotalPnl -Descending | Format-Table BiasDrift, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl, LongCallPct -AutoSize
Write-Host ""
Write-Host "--- All cells by profit factor ---"
$results | Sort-Object -Property ProfitFactor -Descending | Format-Table BiasDrift, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl, LongCallPct -AutoSize

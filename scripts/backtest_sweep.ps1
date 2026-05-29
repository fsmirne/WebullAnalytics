<#
backtest_sweep.ps1 - Grid sweep over backtest tuning parameters.

Runs `wa ai backtest` across a parameter grid, captures fills per run, computes
summary stats (trades, win rate, total P&L, avg P&L), and writes the matrix
to a CSV ranked by total P&L. The fills .jsonl per combination is kept on
disk under data/sweeps/<run-id>/ so you can drill into any row.

Sweep grid is the three arrays at the top — edit those to change the search
space. The current defaults (2x4x3 = 24 combinations) are a second-pass
sweep around the optimum found in the first 3x3x3 run on 2026-01-01..today:
the first sweep put the winning cell at the GRID EDGE (tw=0.50 lowest tested,
ms=0.07 highest), which usually means the real optimum sits outside what was
tested — so this grid extends the tape weight DOWN (0.25-0.45) and the min
score UP (to 0.10), while pruning biasDrift to the two values that mattered.

Sizing-neutral by default (--lots 1) — every trade is exactly one contract,
so the totals measure per-trade edge rather than compounding-curve P&L. The
sweep is about ranking parameter sets, not projecting account growth.

Run (leave the window open):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sweep.ps1
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\sweep-*\sweep.log" -Wait -Tail 20

Multi-regime validation — re-run with the longer window to make sure the
optimum holds across 2025 H1 / H2 regimes, not just the recent 2026 stretch:
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sweep.ps1 -Since 2025-01-01

Customize:
  -Since      Start date (YYYY-MM-DD). Default: 2026-01-01.
  -Until      End date (YYYY-MM-DD). Default: today.
  -Ticker     Underlying. Default: SPXW.
  -Lots       Contracts per trade. Default: 1 (sizing-neutral).
  -RunId      Override the auto-generated run folder name.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). Each run creates a
fresh sweep folder so reruns don't overwrite each other.
#>

param(
  [string]$Since = '2026-01-01',
  [string]$Until = (Get-Date -Format 'yyyy-MM-dd'),
  [string]$Ticker = 'SPXW',
  [int]$Lots = 1,
  [string]$RunId = ('sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# ---- SWEEP GRID --------------------------------------------------------------
# Second-pass grid extending the first sweep's edges. The first sweep on
# 2026-01-01..today (3x3x3, bd={1.0,1.3,1.5} × ms={0.03,0.05,0.07} × tw=
# {0.5,0.65,0.8}) showed: tape weight 0.5 dominated everything (0.65 was
# break-even, 0.8 was decisively negative); bias drift was statistical noise
# at this resolution; min score 0.07 paired with low tape weight gave the
# highest avg P&L. Optimum cell (bd=1.3, ms=0.07, tw=0.5) sat at the edges,
# so widen on both sides. Pruning bd=1.5 since it didn't outperform 1.0/1.3.
$BiasDrifts   = @(1.0, 1.3)                       # SPXW config default = 1.3
$MinScores    = @(0.03, 0.05, 0.07, 0.10)         # SPXW config default = 0.05
$TapeWeights  = @(0.25, 0.35, 0.45)               # SPXW config default = 0.65 (and ALL the prior sweep's negative cells were at tw>=0.65)
# ------------------------------------------------------------------------------

# Resolve installed wa: PATH first, then AppData fallback. Same pattern as
# options_backfill.ps1 — uses the production binary so the sweep is against
# whatever the user has actually deployed, not a stale dev build.
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
# sum(fees) over all fills with that lineageId. Total P&L = sum across all
# lineages. Win rate = fraction of lineages with positive P&L.
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0 }
  if (-not (Test-Path $Path)) { return $empty }

  $lineagePnl = @{}
  $totalFees = 0.0

  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0 }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $totalFees += [double]$f.fees
  }

  $trades = $lineagePnl.Count
  if ($trades -eq 0) { return $empty }

  $wins   = @($lineagePnl.Values | Where-Object { $_ -gt 0 }).Count
  $losses = @($lineagePnl.Values | Where-Object { $_ -le 0 }).Count
  $total  = ($lineagePnl.Values | Measure-Object -Sum).Sum
  $best   = ($lineagePnl.Values | Measure-Object -Maximum).Maximum
  $worst  = ($lineagePnl.Values | Measure-Object -Minimum).Minimum

  return [ordered]@{
    Trades   = $trades
    Wins     = $wins
    Losses   = $losses
    WinRate  = [math]::Round($wins / $trades, 3)
    TotalPnl = [math]::Round($total, 2)
    AvgPnl   = [math]::Round($total / $trades, 2)
    BestPnl  = [math]::Round($best, 2)
    WorstPnl = [math]::Round($worst, 2)
    TotalFees = [math]::Round($totalFees, 2)
  }
}

Log "=== Backtest sweep ==="
Log "wa: $Wa"
Log "ticker=$Ticker since=$Since until=$Until lots=$Lots"
Log "grid: biasDrifts=[$($BiasDrifts -join ', ')] minScores=[$($MinScores -join ', ')] tapeWeights=[$($TapeWeights -join ', ')]"
Log "run dir: $RunDir"

$total = $BiasDrifts.Count * $MinScores.Count * $TapeWeights.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($bd in $BiasDrifts) {
  foreach ($ms in $MinScores) {
    foreach ($tw in $TapeWeights) {
      $idx++
      $tag = "bd${bd}_ms${ms}_tw${tw}"
      $fillsPath = Join-Path $RunDir ("fills_" + $tag + '.jsonl')
      $sw = [System.Diagnostics.Stopwatch]::StartNew()

      Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $tag)
      & $Wa ai backtest $Ticker --since $Since --until $Until --lots $Lots `
        --bias-drift $bd --min-score-to-open $ms --intraday-tape-weight $tw `
        --fills-jsonl $fillsPath 2>&1 | Out-Null

      $rc = $LASTEXITCODE
      $sw.Stop()
      if ($rc -ne 0) {
        Log ("  -> rc={0} (skipping stats)" -f $rc)
        continue
      }

      $stats = Get-FillsStats -Path $fillsPath
      $row = [PSCustomObject]@{
        BiasDrift  = $bd
        MinScore   = $ms
        TapeWeight = $tw
        Trades     = $stats.Trades
        Wins       = $stats.Wins
        Losses     = $stats.Losses
        WinRate    = $stats.WinRate
        TotalPnl   = $stats.TotalPnl
        AvgPnl     = $stats.AvgPnl
        BestPnl    = $stats.BestPnl
        WorstPnl   = $stats.WorstPnl
        TotalFees  = $stats.TotalFees
        Elapsed    = [math]::Round($sw.Elapsed.TotalSeconds, 1)
      }
      [void]$results.Add($row)
      Log ("  -> trades={0} wr={1:P0} totalP&L={2:N2} avgP&L={3:N2} took={4}s" -f $row.Trades, $row.WinRate, $row.TotalPnl, $row.AvgPnl, $row.Elapsed)

      # Write results incrementally so the CSV is usable mid-sweep if you Ctrl-C.
      $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
    }
  }
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

# Print top 5 by total P&L, then top 5 by avg P&L (per-trade edge — more stable
# than total when trade counts vary a lot across cells).
Write-Host ""
Write-Host "--- Top 5 by total P&L ---"
$results | Sort-Object -Property TotalPnl -Descending | Select-Object -First 5 | Format-Table BiasDrift, MinScore, TapeWeight, Trades, WinRate, TotalPnl, AvgPnl -AutoSize
Write-Host ""
Write-Host "--- Top 5 by avg P&L per trade ---"
$results | Sort-Object -Property AvgPnl -Descending | Select-Object -First 5 | Format-Table BiasDrift, MinScore, TapeWeight, Trades, WinRate, TotalPnl, AvgPnl -AutoSize

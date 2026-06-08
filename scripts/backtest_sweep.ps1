<#
backtest_sweep.ps1 - 2D grid sweep over the GEX signal weights.

Validates the two GEX scoring signals that steer (and are currently de-risked
OFF of) live SPY capital, now that data/oi/SPY carries real OI+IV for the whole
backtest window so the signals are no longer inert in the backtest:

  --gex-bias-pull : the directional magnet. Pulls the strike grid toward the
                    gravity strike (max gross gamma x OI). gravity-below-spot =>
                    bias toward downside structures. This is the DIRECTION signal.
  --gamma-regime  : the regime tilt. Boosts long-vol structures when net gamma is
                    negative (amplifying/trending), short-vol when positive
                    (suppressive/pinning). This is the CHARACTER signal, not direction.

The grid is the two arrays at the top. Cell (0,0) is the control = both signals
OFF, i.e. today's de-risked live config. Every other cell turns one or both on,
so the sweep answers: do these signals earn money (PF, total, avg) on real NBBO
fills across the OI window, or are they flat / a single-trade lottery?

Sizing-neutral by default (--lots 1): every trade is one contract, so totals
measure per-trade edge, not a compounding curve. The sweep ranks parameter sets.

Run (leave the window open):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sweep.ps1
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\sweep-*\sweep.log" -Wait -Tail 20

RUNTIME WARNING: full-window stride-1 is ~80-90 min PER CELL. A 3x3 grid at
stride 1 over 2025-01-01..today is ~12h. For a fast first read use a coarser
stride (same on every cell => fair relative ranking) or a shorter window, then
confirm the winning cell on the full window at stride 1:
  # fast first pass (~minutes/cell):
  .\scripts\backtest_sweep.ps1 -ScanStride 15
  # full-window confirm of the winner:
  .\scripts\backtest_sweep.ps1 -Since 2025-01-01

Customize:
  -Since       Start date (YYYY-MM-DD). Default: 2025-01-01 (start of OI coverage).
  -Until       End date (YYYY-MM-DD). Default: today.
  -Ticker      Underlying. Default: SPY (the only ticker with validated OI).
  -Lots        Contracts per trade. Default: 1 (sizing-neutral).
  -ScanStride  Open-scan minute stride. Default: 1 (faithful). Raise to speed up.
  -RunId       Override the auto-generated run folder name.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). Each run creates a
fresh sweep folder so reruns don't overwrite each other.
#>

param(
  [string]$Since = '2025-01-01',
  [string]$Until = (Get-Date -Format 'yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [string]$RunId = ('sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# ---- SWEEP GRID --------------------------------------------------------------
# 2D over the two GEX signal weights. (0,0) is the both-OFF control = current
# de-risked live config; compare every other cell against it. Keep it small —
# each cell is a full backtest. Edit these to widen/refine the search.
$GexBiasPulls = @(0.0, 0.5, 1.0)    # directional magnet strength (0 = off)
$GammaRegimes = @(0.0, 0.5, 1.0)    # net-gamma regime tilt strength (0 = off)
# ------------------------------------------------------------------------------

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
# wins / gross losses (the key cost-aware edge metric).
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0 }
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
  $grossWin  = (($lineagePnl.Values | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($lineagePnl.Values | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }

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
  }
}

Log "=== GEX-signal backtest sweep ==="
Log "wa: $Wa"
Log "ticker=$Ticker since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log "grid: gexBiasPull=[$($GexBiasPulls -join ', ')] gammaRegime=[$($GammaRegimes -join ', ')]"
Log "run dir: $RunDir"

$total = $GexBiasPulls.Count * $GammaRegimes.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($gbp in $GexBiasPulls) {
  foreach ($gr in $GammaRegimes) {
    $idx++
    $tag = "gbp${gbp}_gr${gr}"
    $fillsPath = Join-Path $RunDir ("fills_" + $tag + '.jsonl')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    $cellLog = Join-Path $RunDir ("run_" + $tag + '.log')
    Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $tag)
    & $Wa ai backtest $Ticker --since $Since --until $Until --lots $Lots --scan-stride $ScanStride `
      --gex-bias-pull $gbp --gamma-regime $gr `
      --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $cellLog | Out-Null

    $rc = $LASTEXITCODE
    $sw.Stop()
    if ($rc -ne 0) {
      # Surface why instead of swallowing it: echo the last few lines of the cell log.
      $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
      Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
      continue
    }

    $stats = Get-FillsStats -Path $fillsPath
    $row = [PSCustomObject]@{
      GexBiasPull  = $gbp
      GammaRegime  = $gr
      Trades       = $stats.Trades
      Wins         = $stats.Wins
      Losses       = $stats.Losses
      WinRate      = $stats.WinRate
      ProfitFactor = $stats.ProfitFactor
      TotalPnl     = $stats.TotalPnl
      AvgPnl       = $stats.AvgPnl
      BestPnl      = $stats.BestPnl
      WorstPnl     = $stats.WorstPnl
      TotalFees    = $stats.TotalFees
      Elapsed      = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
    [void]$results.Add($row)
    Log ("  -> trades={0} wr={1:P0} PF={2} totalP&L={3:N2} avgP&L={4:N2} took={5}s" -f $row.Trades, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.AvgPnl, $row.Elapsed)

    # Write results incrementally so the CSV is usable mid-sweep if you Ctrl-C.
    $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
  }
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

# Baseline = the both-OFF control cell; everything is measured against it.
$baseline = $results | Where-Object { $_.GexBiasPull -eq 0.0 -and $_.GammaRegime -eq 0.0 } | Select-Object -First 1
if ($baseline) {
  Write-Host ""
  Write-Host ("--- Control (gexBiasPull=0, gammaRegime=0): PF={0} total={1:N2} avg={2:N2} trades={3} wr={4:P0} ---" -f $baseline.ProfitFactor, $baseline.TotalPnl, $baseline.AvgPnl, $baseline.Trades, $baseline.WinRate)
}

Write-Host ""
Write-Host "--- All cells by total P&L ---"
$results | Sort-Object -Property TotalPnl -Descending | Format-Table GexBiasPull, GammaRegime, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl -AutoSize
Write-Host ""
Write-Host "--- All cells by profit factor ---"
$results | Sort-Object -Property ProfitFactor -Descending | Format-Table GexBiasPull, GammaRegime, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl -AutoSize

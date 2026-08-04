<#
backtest_exhaust_sweep.ps1 - 1D sweep over the SPY DC theta-exhaustion stop floor (rules.stopLoss.thetaExhaustShortMid).

Modeled on backtest_tp_sweep.ps1 (same fills.jsonl parser, closed-lifecycles-only). The axis: close an UNDERWATER
cross-expiry position (calendar/diagonal/double) once EVERY short leg expiring before the longest long has decayed
to a mid <= N cents, with >= 1 day to short expiry. Hypothesis (registered 2026-08-04, before running): once the
short is at pennies the structure's theta engine is gone and what remains is an unchosen naked-long directional bet
— cutting it should trim the LEFT TAIL (WorstPnl, max DD) without giving up meaningful PF. This is distinct from
the loss-%-of-max-loss stop that failed OOS: that one fires on recoverable mid-path drawdowns while the short is
still fat; this one fires only when the recovery fuel is spent.

Motivating trade: live 2026-07-31 SPY 746P Aug-31 / 745P Aug-6 diagonal, 6.65 debit; by 08-04 11:05 the short sat
at $0.12 (97% of its premium captured) while the long bled delta into a rally — closed discretionarily at -$4.4k.

Each cell passes --exhaust N/100 (dollars per share); N=0 turns the knob off. Cell N=0 is the baseline: exits are
the frozen DC config's (takeProfit 0.20 + CloseBeforeShortExpiry). PASS GATE (pre-registered): a cell wins only if
PF >= baseline AND WorstPnl / drawdown improve; a PF collapse means the knob is just a loss stop in disguise —
reject and keep the no-stop baseline.

Sizing-neutral (--lots 1): closed-lifecycle P&L is additive, so PF / total / avg measure per-trade EDGE, not a
compounding curve. Rank here, then confirm the winner's drawdown at the real balance separately (phase 2, one run,
no --lots, --starting-cash 50000).

Runs SEQUENTIALLY (concurrent backtests contend on quotes.db). Nothing else may run a backtest while this is live.
Point -Wa at the FIXED binary: either the pinned dev build (...\.sweep-bin\wa.dll, needs -Dotnet) or the installed
wa.exe AFTER running install.bat to deploy the --exhaust flag.

Run on Windows (leave the window open):
  pwsh -ExecutionPolicy Bypass -File .\scripts\backtest_exhaust_sweep.ps1 -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll'
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\exhaust-sweep-*\sweep.log" -Wait -Tail 20

RUNTIME: DC is management-bound; ~80 min/cell over 2025-01-02..now (measured by the TP sweep). The default 5-cell
grid is ~7h. The results.csv is written incrementally, so a long run can be Ctrl-C'd and the remaining cells
resumed with -Floors <the rest>.

Customize:
  -Floors      Grid of exhaustion floors in CENTS per share. Default: 0 (off) + 5,10,15,25.
  -Since/-Until  Backtest window. Default: 2025-01-02 .. yesterday.
  -Ticker      Underlying. Default: SPY.
  -Lots        Contracts per trade. Default: 1 (sizing-neutral).
  -ScanStride  Open-scan minute stride. Default: 1.
  -Wa          Path to wa.exe OR wa.dll (dll is run via -Dotnet). Default: installed wa on PATH / %LOCALAPPDATA%.
  -Dotnet      dotnet executable for running a .dll -Wa. Default: 'dotnet'.
  -RunId       Override the run-folder name.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). No strategy-layer files are created (the axis is a CLI
override), so the sweep reads only the frozen DC config; it is unaffected by the pinned binary being rebuilt.
#>

param(
  # Comma-separated string (NOT [double[]]): culture-safe parsing, see backtest_tp_sweep.ps1.
  [string]$Floors = '0,5,10,15,25',
  [string]$Since = '2025-01-02',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [string]$Strategy = 'DC',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [string]$Wa = '',
  [string]$Dotnet = 'dotnet',
  [string]$RunId = ('exhaust-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$Grid = $Floors -split '\s*,\s*' | Where-Object { $_ -ne '' } | ForEach-Object { [double]::Parse($_, $inv) }
if (-not $Grid -or @($Grid).Count -eq 0) { Write-Host "FATAL: -Floors parsed to an empty grid: '$Floors'"; exit 1 }
$Grid = @($Grid)

# Resolve wa: explicit -Wa wins; else PATH; else installed AppData binary.
if (-not $Wa) {
  $cmd = Get-Command wa -ErrorAction SilentlyContinue
  if ($cmd) { $Wa = $cmd.Source }
}
if (-not $Wa) {
  $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'
  if (Test-Path $candidate) { $Wa = $candidate }
}
if (-not $Wa -or -not (Test-Path $Wa)) {
  Write-Host "FATAL: wa binary not found. Pass -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll' or install wa first."
  exit 1
}
$UseDotnet = $Wa.ToLower().EndsWith('.dll')

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Closed-lifecycles-only fills parser (see backtest_tp_sweep.ps1 for why open-at-until must be excluded).
# ExhaustCloses counts lineages whose terminal Close carried rule=StopLossRule — with stopLoss.enabled=false in
# the frozen DC config, the only StopLossRule closes are theta-exhaustion fires, so this is the knob's fire count.
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0; OpenAtEnd = 0; ExhaustCloses = 0 }
  if (-not (Test-Path $Path)) { return $empty }

  $lineagePnl = @{}
  $lineageClosed = @{}
  $lineageExhaust = @{}
  $totalFees = 0.0

  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageClosed[$lid] = $false; $lineageExhaust[$lid] = $false }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $totalFees += [double]$f.fees
    if ($f.kind -eq 'Close' -or $f.kind -eq 'Expire') { $lineageClosed[$lid] = $true }
    if ($f.kind -eq 'Close' -and $f.rule -eq 'StopLossRule') { $lineageExhaust[$lid] = $true }
  }

  $closedPnl = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] } | ForEach-Object { $lineagePnl[$_] })
  $openAtEnd = $lineagePnl.Count - $closedPnl.Count
  $trades = $closedPnl.Count
  if ($trades -eq 0) { return $empty }

  $wins   = @($closedPnl | Where-Object { $_ -gt 0 }).Count
  $losses = @($closedPnl | Where-Object { $_ -le 0 }).Count
  $total  = ($closedPnl | Measure-Object -Sum).Sum
  $best   = ($closedPnl | Measure-Object -Maximum).Maximum
  $worst  = ($closedPnl | Measure-Object -Minimum).Minimum
  $grossWin  = (($closedPnl | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($closedPnl | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }
  $exhaustCloses = @($lineageExhaust.Keys | Where-Object { $lineageExhaust[$_] }).Count

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
    OpenAtEnd = $openAtEnd
    ExhaustCloses = $exhaustCloses
  }
}

Log "=== theta-exhaustion floor backtest sweep ==="
Log ("wa: {0}{1}" -f $Wa, $(if ($UseDotnet) { " (via $Dotnet)" } else { "" }))
Log "ticker=$Ticker strategy=$Strategy since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log "grid: floor=[$($Grid -join ', ')] cents/share  (0 = knob off = baseline)"
Log "run dir: $RunDir"

$total = $Grid.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($fl in $Grid) {
  $idx++
  $tag = "ex{0:000}" -f [int]$fl
  $label = if ([int]$fl -eq 0) { "off" } else { "$([int]$fl)c" }
  $fillsPath = Join-Path $RunDir ("fills_" + $tag + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $tag + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  Log ("[{0}/{1}] floor={2} ({3}) -> running" -f $idx, $total, $fl, $label)
  # Grid values are CENTS for ergonomics; --exhaust takes dollars per share. Format invariant: a
  # culture-default ToString can emit "0,15" on comma-decimal locales.
  $floorDollars = ([double]$fl / 100.0).ToString('0.####', $inv)
  $args = @('ai','backtest',$Ticker,'--strategy',$Strategy,'--since',$Since,'--until',$Until,
            '--lots',$Lots,'--scan-stride',$ScanStride,'--exhaust',$floorDollars,'--fills-jsonl',$fillsPath)
  if ($UseDotnet) { & $Dotnet $Wa @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  else            { & $Wa      @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  $rc = $LASTEXITCODE
  $sw.Stop()

  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    continue
  }

  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    FloorCents   = $fl
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
    OpenAtEnd    = $stats.OpenAtEnd
    ExhaustCloses = $stats.ExhaustCloses
    Elapsed      = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> trades={0} wr={1:P0} PF={2} totalP&L={3:N2} worst={4:N2} exhaustCloses={5} took={6}s" -f $row.Trades, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.WorstPnl, $row.ExhaustCloses, $row.Elapsed)

  # Write results incrementally so the CSV is usable mid-sweep if you Ctrl-C.
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

$baseline = $results | Where-Object { $_.FloorCents -eq 0 } | Select-Object -First 1
if ($baseline) {
  Write-Host ""
  Write-Host ("--- Baseline (exhaust off): PF={0} total={1:N2} worst={2:N2} trades={3} wr={4:P0} ---" -f $baseline.ProfitFactor, $baseline.TotalPnl, $baseline.WorstPnl, $baseline.Trades, $baseline.WinRate)
}

Write-Host ""
Write-Host "--- All cells by profit factor ---"
$results | Sort-Object -Property ProfitFactor -Descending | Format-Table FloorCents, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl, ExhaustCloses -AutoSize
Write-Host ""
Write-Host "--- All cells by worst closed lifecycle (tail) ---"
$results | Sort-Object -Property WorstPnl -Descending | Format-Table FloorCents, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl, ExhaustCloses -AutoSize

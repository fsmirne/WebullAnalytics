<#
backtest_sv_frequency_sweep.ps1 - STAGE 1 of a two-stage sweep for SPY SV trade frequency, aimed at "trade
as many days as possible while keeping a similar profit factor to the 2022-2026 baseline (PF 2.99, 124
distinct positions / 1163 trading days = 13.8% of days)". Stage 2 (scorer-weight tuning) is
backtest_sv_weights_sweep.ps1, which takes this stage's winning cell as its fixed base.

WHY MULTI-AXIS: a single-axis stop-loss sweep (see backtest_sv_stoploss_sweep.ps1) tests only one lever —
how fast a position exits. The 2022-2026 baseline showed 1423 opener attempts blocked by HeldLegGuard (the
day's #1 candidate opposed an already-held leg) against only 161 successful opens — an ~8.8:1 ratio. Faster
exits (stop-loss) address ONE cause (positions occupying strikes for a long time); a narrow delta band
recycling similar strikes day-to-day is a SEPARATE, compounding cause; and how fast winners get taken off
(take-profit) is a third lever pulling the same direction as stop-loss but on the other side of the P&L. This
stage crosses all three:

  StopLossPct   off, 40, 60          (rules.stopLoss — currently disabled entirely)
  DeltaBand     cur, wide, widest    (opener.structures.shortVertical.shortDeltaMin/Max — currently 0.15/0.30)
  TakeProfitPct 75, 50, 30           (rules.takeProfit.profitTargetPctOfPremium — currently 0.75)

3 x 3 x 3 = 27 cells, full factorial (not staged/coordinate-descent WITHIN this stage) so INTERACTIONS between
these three are visible — e.g. a wider delta band might only pay off combined with a faster take-profit.

DTE (dteMin/dteMax, currently 45/60) is DELIBERATELY NOT an axis here: prior testing on this strategy found
increasing DTE consistently improved profit factor, so a "shorten DTE for faster turnover" axis fights the
one lever already known to help PF. DTE stays fixed at the live config's 45/60 in every cell.

Structure diversification (enabling ironCondor/longVertical/calendars alongside shortVertical) is a bigger,
qualitatively different change and is NOT in this grid — planned as a later follow-up once this region and
the stage-2 weight region are both known.

Each cell writes its own temp strategy file ai-config.SPY.<tag>.json (a clone of the live SV config with the
three axes' fields overwritten — the live SV.json is never touched) and runs `--strategy <tag>`. Temp files
are deleted at the end of a clean run (left behind on a Ctrl-C so a resume/inspection can still find them).

Runs SEQUENTIALLY — nothing else may touch quotes.db while this is live (no parallel backtests, no concurrent
ThetaData backfill). RUNTIME below is unmeasured for this exact grid, budget generously — that's fine, the CSV
is incremental and Ctrl-C-safe.

Run on Windows (leave the window open):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sv_frequency_sweep.ps1 -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll'
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\sv-freq-sweep-*\sweep.log" -Wait -Tail 20
Resume after an interruption (same RunId, already-completed cells are skipped via results.csv):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sv_frequency_sweep.ps1 -RunId 'sv-freq-sweep-20260823-...' -Wa '...'

RUNTIME: the reference full-history (2022-2026) run of the CURRENT config took 27.3 min for one cell; a
6-month 2025 slice took 3.5 min. Default window here is 2025-01-01..now (~19mo) to keep cells faster during
exploration, but wider delta bands / faster exits mean MORE opens per cell than the baseline, which means MORE
position-management work per day scanned — cells here will likely run slower than the stop-loss-only sweep's
cells, not just proportional to window length. Watch the first few cells' Elapsed column in results.csv and
extrapolate the full budget before assuming a number.
PHASE AFTER STAGE 2: take the best cell across BOTH stages (frequency target hit without dropping PF much)
and re-run it on the FULL 2022-01-01..now window to confirm it holds over the whole history — including the
Jan-Oct 2022 dead stretch and the 2023-H2 / 2025-H2 loss clusters, which a 19-month window doesn't see at all.

Customize:
  -StopLossPcts   Comma grid, PERCENT, 'off' allowed. Default: off,40,60.
  -DeltaBands     Comma grid of NAMED presets (min:max pairs defined in $DeltaBandPresets below). Default: cur,wide,widest.
  -TakeProfitPcts Comma grid, PERCENT. Default: 75,50,30.
  -Since/-Until   Backtest window. Default: 2025-01-01 .. yesterday.
  -Ticker/-Lots/-ScanStride/-Wa/-Dotnet/-RunId  Same as backtest_sv_stoploss_sweep.ps1.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics): temp per-cell strategy files in the main config dir
(cleaned up on a normal exit), fills/logs/results in its own sweeps\<RunId>\ subfolder.
#>

param(
  [string]$StopLossPcts = 'off,40,60',
  [string]$DeltaBands = 'cur,wide,widest',
  [string]$TakeProfitPcts = '75,50,30',
  [string]$Since = '2025-01-01',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [string]$Wa = '',
  [string]$Dotnet = 'dotnet',
  [string]$RunId = ('sv-freq-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# Named presets so the grid strings stay short — edit the numbers here, not on the command line.
$DeltaBandPresets = @{
  'cur'    = @{ Min = 0.15; Max = 0.30 }   # live SV config, unchanged
  'wide'   = @{ Min = 0.10; Max = 0.35 }
  'widest' = @{ Min = 0.05; Max = 0.40 }
}
# DTE is fixed at the live config's values in every cell (see header) — not swept in this stage.
$FixedDte = @{ Min = 45; Max = 60 }

function ParseGrid($s) { @($s -split '\s*,\s*' | Where-Object { $_ -ne '' }) }
$SlGrid = ParseGrid $StopLossPcts
$DeltaGrid = ParseGrid $DeltaBands
$TpGrid = ParseGrid $TakeProfitPcts
foreach ($d in $DeltaGrid) { if (-not $DeltaBandPresets.ContainsKey($d)) { Write-Host "FATAL: unknown delta band preset '$d' (known: $($DeltaBandPresets.Keys -join ', '))"; exit 1 } }

if (-not $Wa) { $cmd = Get-Command wa -ErrorAction SilentlyContinue; if ($cmd) { $Wa = $cmd.Source } }
if (-not $Wa) { $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'; if (Test-Path $candidate) { $Wa = $candidate } }
if (-not $Wa -or -not (Test-Path $Wa)) { Write-Host "FATAL: wa binary not found. Pass -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll' or install wa first."; exit 1 }
$UseDotnet = $Wa.ToLower().EndsWith('.dll')

$DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$BaseConfigPath = Join-Path $DataDir "ai-config.$Ticker.SV.json"
if (-not (Test-Path $BaseConfigPath)) { Write-Host "FATAL: base config not found: $BaseConfigPath"; exit 1 }
$BaseConfigRaw = Get-Content $BaseConfigPath -Raw

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Same closed-lifecycles-only stats as the other sweep scripts, plus DistinctPositions.
function Get-FillsStats {
  param([string]$Path)
  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; TotalFees = 0.0; OpenAtEnd = 0; DistinctPositions = 0 }
  if (-not (Test-Path $Path)) { return $empty }
  $lineagePnl = @{}; $lineageClosed = @{}; $totalFees = 0.0
  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageClosed[$lid] = $false }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $totalFees += [double]$f.fees
    if ($f.kind -eq 'Close' -or $f.kind -eq 'Expire') { $lineageClosed[$lid] = $true }
  }
  $distinctPositions = $lineagePnl.Count
  $closedPnl = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] } | ForEach-Object { $lineagePnl[$_] })
  $openAtEnd = $lineagePnl.Count - $closedPnl.Count
  $trades = $closedPnl.Count
  if ($trades -eq 0) { $empty.DistinctPositions = $distinctPositions; return $empty }
  $wins = @($closedPnl | Where-Object { $_ -gt 0 }).Count
  $losses = @($closedPnl | Where-Object { $_ -le 0 }).Count
  $total = ($closedPnl | Measure-Object -Sum).Sum
  $grossWin = (($closedPnl | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($closedPnl | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }
  return [ordered]@{
    Trades = $trades; Wins = $wins; Losses = $losses; WinRate = [math]::Round($wins / $trades, 3)
    ProfitFactor = $pf; TotalPnl = [math]::Round($total, 2); AvgPnl = [math]::Round($total / $trades, 2)
    TotalFees = [math]::Round($totalFees, 2); OpenAtEnd = $openAtEnd; DistinctPositions = $distinctPositions
  }
}

# Clones the live SV config with the three axes overwritten via the object graph (not text surgery). DTE is
# always written back as $FixedDte — explicit, not just "left alone" — so this stage is provably DTE-neutral.
function New-CellConfig {
  param([string]$Tag, [bool]$SlEnabled, [double]$SlPct, [double]$DeltaMin, [double]$DeltaMax, [double]$TpPct)
  $cfg = $BaseConfigRaw | ConvertFrom-Json
  $cfg.rules.stopLoss.enabled = $SlEnabled
  if ($SlEnabled) { $cfg.rules.stopLoss.pctOfMaxLoss = $SlPct }
  $cfg.rules.takeProfit.profitTargetPctOfPremium = $TpPct
  $cfg.opener.structures.shortVertical.shortDeltaMin = $DeltaMin
  $cfg.opener.structures.shortVertical.shortDeltaMax = $DeltaMax
  $cfg.opener.structures.shortVertical.dteMin = $FixedDte.Min
  $cfg.opener.structures.shortVertical.dteMax = $FixedDte.Max
  $path = Join-Path $DataDir "ai-config.$Ticker.$Tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $path -Encoding utf8
  return $path
}

Log "=== SV frequency sweep STAGE 1 (stopLoss x deltaBand x takeProfit; DTE fixed at 45/60) ==="
Log ("wa: {0}{1}" -f $Wa, $(if ($UseDotnet) { " (via $Dotnet)" } else { "" }))
Log "ticker=$Ticker since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log "grid: stopLoss=[$($SlGrid -join ', ')] delta=[$($DeltaGrid -join ', ')] takeProfit=[$($TpGrid -join ', ')]"
Log ("total cells: {0}" -f ($SlGrid.Count * $DeltaGrid.Count * $TpGrid.Count))
Log "run dir: $RunDir"

$results = New-Object System.Collections.ArrayList
$doneTags = @{}
if (Test-Path $ResultsCsv) {
  Import-Csv $ResultsCsv | ForEach-Object { [void]$results.Add($_); $doneTags[$_.Tag] = $true }
  Log ("resuming: {0} cell(s) already completed, will be skipped" -f $doneTags.Count)
}

$allCells = @()
foreach ($sl in $SlGrid) { foreach ($db in $DeltaGrid) { foreach ($tp in $TpGrid) {
  $allCells += [PSCustomObject]@{ Sl = $sl; Delta = $db; Tp = $tp }
}}}
$total = $allCells.Count
$idx = 0

foreach ($cell in $allCells) {
  $idx++
  $isOff = ($cell.Sl -eq 'off')
  $tag = "SVfreq1_sl{0}_d{1}_tp{2}" -f $(if ($isOff) { 'off' } else { $cell.Sl }), $cell.Delta, $cell.Tp

  if ($doneTags.ContainsKey($tag)) { Log ("[{0}/{1}] {2} -> already done, skipping" -f $idx, $total, $tag); continue }

  $slPct = if ($isOff) { 0.0 } else { [double]$cell.Sl / 100.0 }
  $tpPct = [double]$cell.Tp / 100.0
  $deltaPreset = $DeltaBandPresets[$cell.Delta]

  $configPath = New-CellConfig -Tag $tag -SlEnabled (-not $isOff) -SlPct $slPct `
    -DeltaMin $deltaPreset.Min -DeltaMax $deltaPreset.Max -TpPct $tpPct

  $fillsPath = Join-Path $RunDir ("fills_" + $tag + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $tag + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $tag)
  $args = @('ai','backtest',$Ticker,'--strategy',$tag,'--since',$Since,'--until',$Until,
            '--lots',$Lots,'--scan-stride',$ScanStride,'--fills-jsonl',$fillsPath)
  if ($UseDotnet) { & $Dotnet $Wa @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  else            { & $Wa      @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  $rc = $LASTEXITCODE
  $sw.Stop()
  Remove-Item -Path $configPath -ErrorAction SilentlyContinue

  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    continue
  }

  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    Tag = $tag; StopLossPct = $cell.Sl; DeltaBand = $cell.Delta; TakeProfitPct = $cell.Tp
    DistinctPositions = $stats.DistinctPositions; Trades = $stats.Trades; Wins = $stats.Wins; Losses = $stats.Losses
    WinRate = $stats.WinRate; ProfitFactor = $stats.ProfitFactor; TotalPnl = $stats.TotalPnl; AvgPnl = $stats.AvgPnl
    OpenAtEnd = $stats.OpenAtEnd; Elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> positions={0} trades={1} wr={2:P0} PF={3} totalP&L={4:N2} took={5}s" -f `
    $row.DistinctPositions, $row.Trades, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.Elapsed)

  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== Stage 1 complete ==="
Log "results: $ResultsCsv"

# Leaderboard + WINNER file: baseline = cur/off/75. Rank cells whose PF is within 20% of baseline by TOTAL
# P&L (not raw position count — frequency is a means, not the objective; a cell with more trades but much
# less P&L, like SL40/TP30 vs SL60/TP75 in the first real run of this script, is a worse outcome, not a
# better one). The top row is written to winner.json for stage 2 to consume directly — this is what lets
# the driver chain stage 1 -> stage 2 with no human picking a winner by hand.
$baseline = $results | Where-Object { $_.StopLossPct -eq 'off' -and $_.DeltaBand -eq 'cur' -and $_.TakeProfitPct -eq '75' } | Select-Object -First 1
if ($baseline) {
  $pfFloor = [double]$baseline.ProfitFactor * 0.8
  Log ("baseline: positions={0} PF={1} totalP&L={2}" -f $baseline.DistinctPositions, $baseline.ProfitFactor, $baseline.TotalPnl)
  Log ("leaderboard (PF >= {0:N2}, i.e. within 20% of baseline, ranked by TotalPnl desc):" -f $pfFloor)
  $leaders = $results | Where-Object { [double]$_.ProfitFactor -ge $pfFloor } | Sort-Object { [double]$_.TotalPnl } -Descending
  foreach ($r in ($leaders | Select-Object -First 10)) {
    Log ("  {0,-30} positions={1,4} PF={2,5} totalP&L={3,10}" -f $r.Tag, $r.DistinctPositions, $r.ProfitFactor, $r.TotalPnl)
  }
  $winner = $leaders | Select-Object -First 1
  if ($winner) {
    $winnerPath = Join-Path $RunDir 'winner.json'
    $winner | ConvertTo-Json | Set-Content -Path $winnerPath -Encoding utf8
    Log ("winner written to {0}: {1}" -f $winnerPath, $winner.Tag)
  }
} else {
  Log "baseline cell (cur/off/75) not found in results — check the grid included it."
}

<#
backtest_sv_weights_sweep.ps1 - STAGE 2 of the two-stage SV frequency sweep. Takes stage 1's winning
StopLoss/DeltaBand/TakeProfit combo (backtest_sv_frequency_sweep.ps1's winner.json, or explicit -BaseSlPct
etc.) as a FIXED base, then sweeps the opener's 11 scorer weights PLUS balanceRrExponent COORDINATE-WISE:
one at a time, +/-50% from its live value, everything else held at the live config's value (not stage 1's —
stage 1 never touches weights/balanceRrExponent). A full cross-product across all 12 would be 3^12 ~= 530,000
cells; coordinate-wise is 1 baseline + 12 axes x 2 alt values = 25 cells, still touching every one, at the
cost of missing axis-axis interactions (acceptable for a first pass — cross a promising pair as a targeted
follow-up if one shows up).

Why weights at all: these rank candidates against each other, so tuning them changes WHICH strike the day's
#1 pick lands on — a plausible lever for the same "why does the top pick collide with an already-held
position so often" problem stage 1 attacks from the exit-speed/band-width side instead. whipsaw (3.0) and
biasDrift (1.0) are the two highest-weighted and the likeliest to be causing "stickiness" toward similar
strikes day over day, but this sweeps all of them rather than assuming which one(s) matter. balanceRrExponent
is a separate, non-weights knob (opener.balanceRrExponent, a candidate-ranking R/R exponent) that's currently
0 — completely inert, since rr^0=1 regardless of the candidate's actual reward/risk — so it's a real lever
nobody has touched yet, included for the same reason as the weights.

Live SV config weights (the +/-50% grid is built from these; fields currently at 0 — volatilityFit,
balanceRrExponent — get an absolute 0/0.5/1.0 grid instead, since a multiplicative +/-50% of 0 is degenerate):
  directionalFit 0.3, biasDrift 1.0, whipsaw 3.0, volatilityFit 0, gammaRegime 1, statArb 0.15,
  sentiment 0.6, expectedMoveCredit 0.5, ivRealizedPremium 0.3, vixTermStructure 0.25, intradayTape 0.45,
  balanceRrExponent 0 (opener.balanceRrExponent, not in opener.weights)

Each cell writes its own temp strategy file ai-config.SPY.<tag>.json (clone of the live SV config with
StopLoss/DeltaBand/TakeProfit set to the stage-1 winner and exactly one weight changed) and runs
`--strategy <tag>`. Temp files are deleted at the end of a clean run.

Runs SEQUENTIALLY — nothing else may touch quotes.db while this is live.

Run on Windows, pointing at stage 1's winner file:
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sv_weights_sweep.ps1 `
    -WinnerJson "$env:LOCALAPPDATA\WebullAnalytics\sweeps\sv-freq-sweep-.../winner.json" -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll'
Or with explicit values instead of a winner file:
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_sv_weights_sweep.ps1 -BaseSlOff -BaseDelta cur -BaseTp 75 -Wa '...'
Resume: re-run with the same -RunId; already-completed cells (by Tag in results.csv) are skipped.

Customize:
  -WinnerJson   Path to stage 1's winner.json (Tag/StopLossPct/DeltaBand/TakeProfitPct columns). If given,
                -BaseSlOff/-BaseSlPct/-BaseDelta/-BaseTp are ignored.
  -BaseSlOff    Switch: stage-1 base has stop-loss off. Mutually exclusive with -BaseSlPct.
  -BaseSlPct    Stage-1 base stop-loss pctOfMaxLoss, PERCENT (e.g. 40).
  -BaseDelta    Stage-1 base delta preset: cur | wide | widest. Default: cur.
  -BaseTp       Stage-1 base take-profit PERCENT. Default: 75.
  -Since/-Until/-Ticker/-Lots/-ScanStride/-Wa/-Dotnet/-RunId  Same as the other sweep scripts.
#>

param(
  [string]$WinnerJson = '',
  [switch]$BaseSlOff,
  [double]$BaseSlPct = 0,
  [string]$BaseDelta = 'cur',
  [double]$BaseTp = 75,
  [string]$Since = '2025-01-01',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [string]$Wa = '',
  [string]$Dotnet = 'dotnet',
  [string]$RunId = ('sv-weights-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

$DeltaBandPresets = @{
  'cur' = @{ Min = 0.15; Max = 0.30 }; 'wide' = @{ Min = 0.10; Max = 0.35 }; 'widest' = @{ Min = 0.05; Max = 0.40 }
}
$FixedDte = @{ Min = 45; Max = 60 }

# Resolve the stage-1 base: from -WinnerJson if given, else the explicit -Base* params.
if ($WinnerJson) {
  if (-not (Test-Path $WinnerJson)) { Write-Host "FATAL: -WinnerJson not found: $WinnerJson"; exit 1 }
  $w = Get-Content $WinnerJson -Raw | ConvertFrom-Json
  $baseSlOffFlag = ($w.StopLossPct -eq 'off')
  $baseSlPctVal = if ($baseSlOffFlag) { 0.0 } else { [double]$w.StopLossPct }
  $baseDeltaName = [string]$w.DeltaBand
  $baseTpVal = [double]$w.TakeProfitPct
  Write-Host "Base from stage 1 winner ($WinnerJson): sl=$($w.StopLossPct) delta=$baseDeltaName tp=$($w.TakeProfitPct)"
} else {
  $baseSlOffFlag = [bool]$BaseSlOff
  $baseSlPctVal = $BaseSlPct
  $baseDeltaName = $BaseDelta
  $baseTpVal = $BaseTp
}
if (-not $DeltaBandPresets.ContainsKey($baseDeltaName)) { Write-Host "FATAL: unknown delta preset '$baseDeltaName'"; exit 1 }
$baseDeltaPreset = $DeltaBandPresets[$baseDeltaName]
$baseSlFraction = if ($baseSlOffFlag) { 0.0 } else { $baseSlPctVal / 100.0 }
$baseTpFraction = $baseTpVal / 100.0

if (-not $Wa) { $cmd = Get-Command wa -ErrorAction SilentlyContinue; if ($cmd) { $Wa = $cmd.Source } }
if (-not $Wa) { $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'; if (Test-Path $candidate) { $Wa = $candidate } }
if (-not $Wa -or -not (Test-Path $Wa)) { Write-Host "FATAL: wa binary not found. Pass -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll' or install wa first."; exit 1 }
$UseDotnet = $Wa.ToLower().EndsWith('.dll')

$DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$BaseConfigPath = Join-Path $DataDir "ai-config.$Ticker.SV.json"
if (-not (Test-Path $BaseConfigPath)) { Write-Host "FATAL: base config not found: $BaseConfigPath"; exit 1 }
$BaseConfigRaw = Get-Content $BaseConfigPath -Raw
$LiveConfigParsed = $BaseConfigRaw | ConvertFrom-Json
$LiveWeights = $LiveConfigParsed.opener.weights
$LiveBalanceRrExponent = [double]$LiveConfigParsed.opener.balanceRrExponent

# Weight name -> +/-50% grid, EXCEPT weights currently at 0 (volatilityFit, balanceRrExponent) get an
# absolute grid since a multiplicative +/-50% of 0 is degenerate. balanceRrExponent isn't in opener.weights
# (it's opener.balanceRrExponent directly — a candidate-ranking R/R exponent, currently 0 = completely inert
# since rr^0=1 always) but is swept the same coordinate-wise way; New-CellConfig special-cases its path.
# 0.5/1.0 are the reference points from its own doc comment (0.5 = sqrt softening, 1.0 = linear).
$WeightNames = @('directionalFit','biasDrift','whipsaw','volatilityFit','gammaRegime','statArb','sentiment','expectedMoveCredit','ivRealizedPremium','vixTermStructure','intradayTape','balanceRrExponent')
function Get-WeightAlts($name) {
  $cur = if ($name -eq 'balanceRrExponent') { $LiveBalanceRrExponent } else { [double]$LiveWeights.$name }
  if ($cur -eq 0) { return @($cur, 0.5, 1.0) | Select-Object -Unique }
  return @($cur, [math]::Round($cur * 0.5, 4), [math]::Round($cur * 1.5, 4))
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

# Clones the live SV config with StopLoss/DeltaBand/TakeProfit pinned to the stage-1 base, DTE fixed, and
# exactly ONE weight overridden (all others left at their live config values).
function New-CellConfig {
  param([string]$Tag, [string]$WeightName, [double]$WeightValue)
  $cfg = $BaseConfigRaw | ConvertFrom-Json
  $cfg.rules.stopLoss.enabled = (-not $baseSlOffFlag)
  if (-not $baseSlOffFlag) { $cfg.rules.stopLoss.pctOfMaxLoss = $baseSlFraction }
  $cfg.rules.takeProfit.profitTargetPctOfPremium = $baseTpFraction
  $cfg.opener.structures.shortVertical.shortDeltaMin = $baseDeltaPreset.Min
  $cfg.opener.structures.shortVertical.shortDeltaMax = $baseDeltaPreset.Max
  $cfg.opener.structures.shortVertical.dteMin = $FixedDte.Min
  $cfg.opener.structures.shortVertical.dteMax = $FixedDte.Max
  if ($WeightName -eq 'balanceRrExponent') { $cfg.opener.balanceRrExponent = $WeightValue }
  elseif ($WeightName) { $cfg.opener.weights.$WeightName = $WeightValue }
  $path = Join-Path $DataDir "ai-config.$Ticker.$Tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $path -Encoding utf8
  return $path
}

function Run-Cell {
  param([string]$Tag, [string]$WeightName, [double]$WeightValue, [string]$Label)
  if ($script:doneTags.ContainsKey($Tag)) { Log ("  {0} -> already done, skipping" -f $Tag); return }
  $configPath = New-CellConfig -Tag $Tag -WeightName $WeightName -WeightValue $WeightValue
  $fillsPath = Join-Path $RunDir ("fills_" + $Tag + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $Tag + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  Log ("  {0} ({1}) -> running" -f $Tag, $Label)
  $args = @('ai','backtest',$Ticker,'--strategy',$Tag,'--since',$Since,'--until',$Until,
            '--lots',$Lots,'--scan-stride',$ScanStride,'--fills-jsonl',$fillsPath)
  if ($UseDotnet) { & $Dotnet $Wa @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  else            { & $Wa      @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  $rc = $LASTEXITCODE
  $sw.Stop()
  Remove-Item -Path $configPath -ErrorAction SilentlyContinue
  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("    -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    return
  }
  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    Tag = $Tag; Weight = $(if ($WeightName) { $WeightName } else { 'baseline' }); Value = $WeightValue; Label = $Label
    DistinctPositions = $stats.DistinctPositions; Trades = $stats.Trades; Wins = $stats.Wins; Losses = $stats.Losses
    WinRate = $stats.WinRate; ProfitFactor = $stats.ProfitFactor; TotalPnl = $stats.TotalPnl; AvgPnl = $stats.AvgPnl
    OpenAtEnd = $stats.OpenAtEnd; Elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$script:results.Add($row)
  Log ("    -> positions={0} trades={1} wr={2:P0} PF={3} totalP&L={4:N2} took={5}s" -f `
    $row.DistinctPositions, $row.Trades, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.Elapsed)
  $script:results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== SV frequency sweep STAGE 2 (coordinate-wise scorer-weight sweep) ==="
Log ("wa: {0}{1}" -f $Wa, $(if ($UseDotnet) { " (via $Dotnet)" } else { "" }))
Log "ticker=$Ticker since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log ("stage-1 base: stopLoss=$(if($baseSlOffFlag){'off'}else{"$BaseSlPct%"}) delta=$baseDeltaName takeProfit=$BaseTp%")
Log ("weights swept: {0}" -f ($WeightNames -join ', '))
Log "run dir: $RunDir"

$results = New-Object System.Collections.ArrayList
$doneTags = @{}
if (Test-Path $ResultsCsv) {
  Import-Csv $ResultsCsv | ForEach-Object { [void]$results.Add($_); $doneTags[$_.Tag] = $true }
  Log ("resuming: {0} cell(s) already completed, will be skipped" -f $doneTags.Count)
}

Log "[baseline] all weights at live config values"
Run-Cell -Tag 'SVfreq2_baseline' -WeightName '' -WeightValue 0 -Label 'baseline (all live weights)'

foreach ($name in $WeightNames) {
  $alts = Get-WeightAlts $name
  $cur = $alts[0]
  Log ("[{0}] live={1}, alts={2}" -f $name, $cur, (($alts | Select-Object -Skip 1) -join ', '))
  foreach ($v in ($alts | Select-Object -Skip 1)) {
    $vTag = ($v.ToString('0.####', $inv)) -replace '\.', 'p' -replace '-', 'neg'
    $tag = "SVfreq2_${name}_$vTag"
    Run-Cell -Tag $tag -WeightName $name -WeightValue $v -Label "$name = $v (live was $cur)"
  }
}

Log "=== Stage 2 complete ==="
Log "results: $ResultsCsv"

# Ranked by TOTAL P&L (not raw position count) — frequency is a means, not the objective; P&L is what
# actually matters, with PF as a floor so a big-P&L cell isn't just one lucky fat trade with poor edge
# quality otherwise. See the "SL40/TP30 optimizes for frequency at the cost of 58% less P&L" finding.
$baseline = $results | Where-Object { $_.Tag -eq 'SVfreq2_baseline' } | Select-Object -First 1
if ($baseline) {
  $pfFloor = [double]$baseline.ProfitFactor * 0.8
  Log ("baseline: positions={0} PF={1} totalP&L={2}" -f $baseline.DistinctPositions, $baseline.ProfitFactor, $baseline.TotalPnl)
  Log ("leaderboard (PF >= {0:N2}, ranked by TotalPnl desc):" -f $pfFloor)
  $leaders = $results | Where-Object { [double]$_.ProfitFactor -ge $pfFloor } | Sort-Object { [double]$_.TotalPnl } -Descending
  foreach ($r in ($leaders | Select-Object -First 15)) {
    Log ("  {0,-35} positions={1,4} PF={2,5} totalP&L={3,10}" -f $r.Tag, $r.DistinctPositions, $r.ProfitFactor, $r.TotalPnl)
  }
  $winner = $leaders | Select-Object -First 1
  if ($winner) {
    $winnerPath = Join-Path $RunDir 'winner.json'
    $winner | ConvertTo-Json | Set-Content -Path $winnerPath -Encoding utf8
    Log ("winner written to {0}: {1}" -f $winnerPath, $winner.Tag)
  }
}

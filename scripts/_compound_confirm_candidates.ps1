<#
_compound_confirm_candidates.ps1 - Re-runs ALL of phase 3's full-history candidates with real compounding
(--starting-cash 50000, no --lots) over the same 2022-01-01..now window, instead of just the sizing-neutral
winner. Justified directly by what phase 1 found: SL40/TP75 had LESS sizing-neutral total P&L than SL60/TP75
($7,902 vs $9,438) but WON decisively under compounding (PF 8.99 vs 4.72, $141k vs $111k ending equity, LOWER
drawdown) — the sizing-neutral ranking and the real-money ranking are not guaranteed to be the same candidate,
so every phase-3 candidate gets re-checked here rather than just the sizing-neutral top pick.

Reads full_history_confirm\results.csv (written by _confirm_candidates_full_history.ps1) for the candidate
list + parameters, reconstructs each temp config, and runs it compounded. Produces its own leaderboard sorted
by ending equity (the real answer to "which config would you actually want to trade").
#>

param(
  [string]$ResultsCsvPath = (Join-Path 'C:\dev\WebullAnalytics' 'full_history_confirm\results.csv'),
  [string]$Since = '2022-01-01',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [double]$StartingCash = 50000,
  [string]$Ticker = 'SPY',
  [string]$RunDirName = 'full_history_compound_confirm'
)

$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot
$Wa = "$RepoRoot\.sweep-bin\wa.dll"
$DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$BaseConfigPath = Join-Path $DataDir "ai-config.$Ticker.SV.json"
$BaseConfigRaw = Get-Content $BaseConfigPath -Raw

$RunDir = Join-Path $RepoRoot $RunDirName
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'compound_confirm.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

if (-not (Test-Path $ResultsCsvPath)) { Log "FATAL: $ResultsCsvPath not found — phase 3 must complete first."; exit 1 }
$candidates = Import-Csv $ResultsCsvPath
Log ("=== compounded confirmation of {0} phase-3 candidates (\${1} start, real sizing, {2}..{3}) ===" -f $candidates.Count, $StartingCash, $Since, $Until)

$results = New-Object System.Collections.ArrayList
foreach ($c in $candidates) {
  $cfg = $BaseConfigRaw | ConvertFrom-Json
  $slEnabled = [bool]::Parse($c.SlEnabled)
  $cfg.rules.stopLoss.enabled = $slEnabled
  if ($slEnabled) { $cfg.rules.stopLoss.pctOfMaxLoss = [double]$c.SlPct }
  $cfg.rules.takeProfit.profitTargetPctOfPremium = [double]$c.TpPct
  $cfg.opener.structures.shortVertical.shortDeltaMin = [double]$c.DeltaMin
  $cfg.opener.structures.shortVertical.shortDeltaMax = [double]$c.DeltaMax
  $cfg.opener.structures.shortVertical.dteMin = 45
  $cfg.opener.structures.shortVertical.dteMax = 60
  if ($c.WeightName -and $c.WeightName -ne '') {
    if ($c.WeightName -eq 'balanceRrExponent') { $cfg.opener.balanceRrExponent = [double]$c.WeightValue }
    else { $cfg.opener.weights.($c.WeightName) = [double]$c.WeightValue }
  }
  $tag = "SVcompound_$($c.Name)"
  $configPath = Join-Path $DataDir "ai-config.$Ticker.$tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $configPath -Encoding utf8

  $fillsPath = Join-Path $RunDir ("fills_" + $c.Name + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $c.Name + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  Log ("{0} -> running compounded (\${1} start, full history)" -f $c.Name, $StartingCash)
  & dotnet $Wa ai backtest $Ticker --starting-cash $StartingCash --since $Since --until $Until --strategy $tag `
    --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $cellLog | Out-Null
  $sw.Stop()
  Remove-Item -Path $configPath -ErrorAction SilentlyContinue

  $endingEquity = (Select-String -Path $cellLog -Pattern 'Ending equity\s+.*?\$([\d,\.]+)' | Select-Object -Last 1)
  $endingEquityVal = if ($endingEquity) { [double]($endingEquity.Matches[0].Groups[1].Value -replace ',','') } else { $null }
  $maxDD = (Select-String -Path $cellLog -Pattern '([\d\.]+)%\s+worst' | Select-Object -Last 1)
  $maxDDVal = if ($maxDD) { [double]$maxDD.Matches[0].Groups[1].Value } else { $null }
  $pfLine = (Select-String -Path $cellLog -Pattern '^\S*\s*Profit factor\s+\S*\s+([\d\.]+|\S+)\s*\S*$' | Select-Object -Last 1)
  $pfVal = if ($pfLine) { $pfLine.Matches[0].Groups[1].Value } else { $null }
  $totalPnlLine = (Select-String -Path $cellLog -Pattern 'Total P&L\s+.*?\$?(-?[\d,\.]+)\s*\(' | Select-Object -Last 1)
  $totalPnlVal = if ($totalPnlLine) { [double]($totalPnlLine.Matches[0].Groups[1].Value -replace ',','') } else { $null }

  $row = [PSCustomObject]@{
    Name = $c.Name; EndingEquity = $endingEquityVal; MaxDrawdownPct = $maxDDVal; ProfitFactor = $pfVal; TotalPnl = $totalPnlVal
    SizingNeutralPF = $c.ProfitFactor; SizingNeutralTotalPnl = $c.TotalPnl
    Elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> endingEquity={0} maxDD={1}% PF={2} totalP&L={3} (sizing-neutral was PF={4} P&L={5}) took={6}s" -f `
    $row.EndingEquity, $row.MaxDrawdownPct, $row.ProfitFactor, $row.TotalPnl, $row.SizingNeutralPF, $row.SizingNeutralTotalPnl, $row.Elapsed)
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== compounded confirmation complete ==="
Log "results: $ResultsCsv"
Log "leaderboard (ranked by ending equity desc — the real 'which config would you actually trade' answer):"
$leaders = $results | Where-Object { $null -ne $_.EndingEquity } | Sort-Object { [double]$_.EndingEquity } -Descending
foreach ($r in $leaders) {
  Log ("  {0,-18} endingEquity=\${1,12:N2} maxDD={2,5}% PF={3,6} (sizing-neutral rank was PF={4})" -f $r.Name, $r.EndingEquity, $r.MaxDrawdownPct, $r.ProfitFactor, $r.SizingNeutralPF)
}
$compoundWinner = $leaders | Select-Object -First 1
if ($compoundWinner) {
  Log ("compounded winner: {0} — this may NOT match the sizing-neutral phase-3 winner; that's the point of this phase." -f $compoundWinner.Name)
}

<#
_run_phase4_and_phase5.ps1 - Runs the two remaining queued phases that never actually executed:

  Phase 4 never ran: the original _run_remaining_phases.ps1 process had its whole script body parsed into
  memory at launch (2026-08-24 07:38), before phase 4 was appended to the file on disk -- so the running
  process's final "driver finished" log line silently omitted phase 4 and skipped straight past it. Confirmed
  by: the log's own text omits "minScoreToOpen validation", and phase4_msto0.txt/.jsonl never got created.
  Phase 3 (full-history confirmation) DID complete correctly and wrote winner.json (stage2_winner: SL60/cur/
  TP75 + volatilityFit=1.0, PF 3.26, $19,716.10 over 2022-2026) -- this script reuses that real winner.

  Phase 5 (compounded confirmation of all 7 phase-3 candidates) also never started: the standalone watcher
  I launched for it had the SAME class of bug -- it polled for a "full-history confirmation complete" string
  inside remaining_phases.log, but that string is only ever written to the CHILD script's own log file
  (full_history_confirm\confirm.log / phase3_full_history_confirm.log), never to remaining_phases.log. The
  watcher would have waited forever. Killed it (was PID 37880) rather than let it hang silently.

  Both phases are safe to run now, sequentially, with no other backtest process alive.
#>

$ErrorActionPreference = 'Stop'
$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path (Join-Path $RepoRoot 'remaining_phases.log') -Value $line
}

function Wait-ForNoBacktestProcess {
  while ((Get-Process -Name dotnet -ErrorAction SilentlyContinue) -or (Get-Process -Name wa -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 20
  }
}

Log "=== phase4+5 runner starting (recovering from the phase 4 skip + phase 5 watcher bug) ==="
Wait-ForNoBacktestProcess
Log "no backtest process running — proceeding."

Log "=== phase 4: minScoreToOpen validation (0 vs the live config's -10) on phase 3's actual winner ==="
$phase3WinnerPath = Join-Path $RepoRoot 'full_history_confirm\winner.json'
if (-not (Test-Path $phase3WinnerPath)) {
  Log "phase 3 produced no winner.json — skipping phase 4."
} else {
  $w = Get-Content $phase3WinnerPath -Raw | ConvertFrom-Json
  Log ("phase 3 winner: {0} (-10 results: trades={1} PF={2} totalP&L={3})" -f $w.Name, $w.Trades, $w.ProfitFactor, $w.TotalPnl)

  $DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
  $BaseConfigPath = Join-Path $DataDir 'ai-config.SPY.SV.json'
  $cfg = (Get-Content $BaseConfigPath -Raw) | ConvertFrom-Json
  $slEnabled = [bool]$w.SlEnabled
  $cfg.rules.stopLoss.enabled = $slEnabled
  if ($slEnabled) { $cfg.rules.stopLoss.pctOfMaxLoss = [double]$w.SlPct }
  $cfg.rules.takeProfit.profitTargetPctOfPremium = [double]$w.TpPct
  $cfg.opener.structures.shortVertical.shortDeltaMin = [double]$w.DeltaMin
  $cfg.opener.structures.shortVertical.shortDeltaMax = [double]$w.DeltaMax
  $cfg.opener.structures.shortVertical.dteMin = 45
  $cfg.opener.structures.shortVertical.dteMax = 60
  if ($w.WeightName -and $w.WeightName -ne '') {
    if ($w.WeightName -eq 'balanceRrExponent') { $cfg.opener.balanceRrExponent = [double]$w.WeightValue }
    else { $cfg.opener.weights.($w.WeightName) = [double]$w.WeightValue }
  }
  $tag = 'SVconfirm_phase3winner_msto0'
  $configPath = Join-Path $DataDir "ai-config.SPY.$tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $configPath -Encoding utf8

  $fillsPath = Join-Path $RepoRoot 'phase4_msto0.jsonl'
  $cellLog = Join-Path $RepoRoot 'phase4_msto0.txt'
  Remove-Item $fillsPath, $cellLog -ErrorAction SilentlyContinue
  Log "running phase 3's winner with --min-score-to-open 0 over the full 2022-2026 window..."
  & dotnet "$RepoRoot\.sweep-bin\wa.dll" ai backtest SPY --lots 1 --since 2022-01-01 --strategy $tag `
    --min-score-to-open 0 --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $cellLog | Out-Null
  Remove-Item $configPath -ErrorAction SilentlyContinue

  $lineagePnl = @{}; $lineageClosed = @{}
  if (Test-Path $fillsPath) {
    Get-Content $fillsPath | ForEach-Object {
      if ([string]::IsNullOrWhiteSpace($_)) { return }
      $f = $_ | ConvertFrom-Json
      $lid = [string]$f.lineage
      if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageClosed[$lid] = $false }
      $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
      if ($f.kind -eq 'Close' -or $f.kind -eq 'Expire') { $lineageClosed[$lid] = $true }
    }
  }
  $closedPnl = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] } | ForEach-Object { $lineagePnl[$_] })
  $trades0 = $closedPnl.Count
  $grossWin0 = (($closedPnl | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss0 = (($closedPnl | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf0 = if ($grossLoss0 -and $grossLoss0 -ne 0) { [math]::Round($grossWin0 / [math]::Abs($grossLoss0), 2) } elseif ($grossWin0 -gt 0) { [double]::PositiveInfinity } else { 0.0 }
  $total0 = ($closedPnl | Measure-Object -Sum).Sum

  Log ("=== minScoreToOpen comparison for {0} ===" -f $w.Name)
  Log ("  -10 (live default): trades={0} PF={1} totalP&L={2}" -f $w.Trades, $w.ProfitFactor, $w.TotalPnl)
  Log ("  0   (positive-EV only): trades={0} PF={1} totalP&L={2}" -f $trades0, $pf0, [math]::Round($total0, 2))
  $verdict = if ([math]::Round($total0,2) -ge [double]$w.TotalPnl) { "0 is at least as good -> minScoreToOpen can safely be raised to 0" } else { "-10 outperforms 0 on this config -> the negative-score opens are net contributors, keep -10" }
  Log ("  verdict: $verdict")
}

Log "=== phase 5: compounded confirmation of ALL phase 3 candidates (not just the sizing-neutral winner) ==="
Log "justification: phase 1 showed SL40/TP75 beat SL60/TP75 under compounding despite LOSING on sizing-neutral total P&L on the short window -- and phase 3's full-history results just showed the OPPOSITE flip too (sl40_tp75 fell to PF 1.97 vs baseline's 3.08 on full history) -- so every phase-3 candidate gets re-checked under real compounding rather than trusting either sizing-neutral ranking."
$phase3ResultsCsv = Join-Path $RepoRoot 'full_history_confirm\results.csv'
if (-not (Test-Path $phase3ResultsCsv)) {
  Log "phase 3 produced no results.csv -- skipping phase 5."
} else {
  Wait-ForNoBacktestProcess
  & "$RepoRoot\scripts\_compound_confirm_candidates.ps1" -ResultsCsvPath $phase3ResultsCsv *>&1 |
    Tee-Object -FilePath (Join-Path $RepoRoot 'phase5_compound_all_candidates.log')
}

Log "=== phase4+5 runner finished — all queued phases now genuinely complete ==="

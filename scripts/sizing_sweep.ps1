<#
sizing_sweep.ps1 - Grid sweep over the LIVE SIZING knobs (maxRiskPctPerProposal
x maxQtyPerProposal), measured on a COMPOUNDED run at a fixed starting cash.

Why this is a separate script from backtest_sweep.ps1:
  backtest_sweep.ps1 tunes SCORING knobs (biasDrift / minScore / tapeWeight) with
  `--lots 1`, which makes every trade exactly one contract and BYPASSES the sizing
  knobs entirely — so it cannot measure them. Sizing only shows up when the
  position is scaled off equity, i.e. a compounded run with real `--starting-cash`.
  Different methodology, different metric (compounded growth + drawdown, not
  per-trade edge), hence a different script.

  Neither sweep is affected by the lottery-ticket entry-timing fix in the same way:
  the scoring sweep (--lots 1) is provably unchanged by it; THIS sweep is the one
  that exercises it (a too-small budget now skips the day instead of buying a cheap
  junk strike).

There is no CLI flag for the sizing knobs, so each cell edits the per-ticker
config (ai-config.<TICKER>.json), runs the backtest, then the config is restored
in a finally block (also on Ctrl-C). Compounded P&L is path-dependent / lottery-
shaped here, so DO NOT read TotalPnl% as a forecast — use it together with
MaxDrawdown% and treat the ranking as directional, not precise.

Run:
  powershell -ExecutionPolicy Bypass -File .\scripts\sizing_sweep.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\sizing_sweep.ps1 -StartingCash 100000
  powershell -ExecutionPolicy Bypass -File .\scripts\sizing_sweep.ps1 -Since 2026-01-01

Customize:
  -Since         Start date (YYYY-MM-DD). Default: 2025-01-01 (full, regime-diverse).
  -Until         End date. Default: today.
  -Ticker        Underlying. Default: SPXW.
  -StartingCash  Compounded starting equity. Default: 10000. Run a few sizes to see
                 how each config behaves small vs grown.
  -RunId         Override the auto-generated run folder name.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). Edits the per-ticker
config in place and restores it; back up first if you are paranoid.
#>

param(
  [string]$Since = '2025-01-01',
  [string]$Until = (Get-Date -Format 'yyyy-MM-dd'),
  [string]$Ticker = 'SPXW',
  [double]$StartingCash = 10000,
  [string]$RunId = ('sizing-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# ---- SWEEP GRID --------------------------------------------------------------
# maxRiskPctPerProposal: the per-trade budget as a fraction of equity. At $10k:
#   0.15 -> $1.5k (skips mid-priced winners), 0.25 -> $2.5k (sweet spot so far),
#   1.0 -> whole account on one 0DTE (ruinous, -98% in the first manual test).
# maxQtyPerProposal: hard ceiling on contracts. 1 = never scale; high = let the
#   risk% budget govern (auto-scales as the account grows).
$Risks = @(0.15, 0.20, 0.25, 0.40, 1.0)
$Qtys  = @(1, 3, 10)
# ------------------------------------------------------------------------------

# Resolve installed wa (PATH first, AppData fallback) — same pattern as backtest_sweep.ps1.
$Wa = $null
$cmd = Get-Command wa -ErrorAction SilentlyContinue
if ($cmd) { $Wa = $cmd.Source }
if (-not $Wa) {
  $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'
  if (Test-Path $candidate) { $Wa = $candidate }
}
if (-not $Wa) { Write-Host "FATAL: 'wa' not found on PATH or in %LOCALAPPDATA%\WebullAnalytics."; exit 1 }

$DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$Cfg = Join-Path $DataDir ("ai-config.$Ticker.json")
if (-not (Test-Path $Cfg)) { Write-Host "FATAL: config not found: $Cfg"; exit 1 }

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Set both sizing fields in the per-ticker config (regex, value-agnostic).
function Set-Sizing([double]$risk, [int]$qty) {
  $json = Get-Content $Cfg -Raw
  $json = $json -replace '"maxRiskPctPerProposal":\s*[0-9.]+', ('"maxRiskPctPerProposal": ' + $risk)
  $json = $json -replace '"maxQtyPerProposal":\s*[0-9]+',      ('"maxQtyPerProposal": ' + $qty)
  Set-Content -Path $Cfg -Value $json -NoNewline
}

# Pull compounded metrics from the backtest's own summary table (authoritative —
# the engine tracks the equity curve, so MaxDrawdown is exact). Output is redirected
# (not a TTY) so Spectre keeps each metric on one line.
function Get-RunStats([string[]]$out) {
  $text = $out -join "`n"
  $pnlPct = if ($text -match 'Total P&L[^\(]*\(([-0-9.]+)%\)')          { [double]$Matches[1] } else { $null }
  $ddPct  = if ($text -match 'Max drawdown[^\(]*\(([0-9.]+)% of peak\)') { [double]$Matches[1] } else { 0.0 }
  $opens  = if ($text -match 'Opens\s*[^0-9]*([0-9]+)\s*\(')            { [int]$Matches[1] }    else { 0 }
  $wr     = if ($text -match 'Win rate[^0-9]*([0-9.]+)%')               { [double]$Matches[1] } else { $null }
  $endEq  = if ($text -match 'Ending equity[^\$]*\$([0-9,]+\.[0-9]+)')  { [double]($Matches[1] -replace ',','') } else { $null }
  return [ordered]@{ PnlPct = $pnlPct; DrawdownPct = $ddPct; Opens = $opens; WinRatePct = $wr; EndingEquity = $endEq }
}

Log "=== Sizing sweep (compounded) ==="
Log "wa: $Wa  | config: $Cfg"
Log "ticker=$Ticker since=$Since until=$Until startingCash=$StartingCash"
Log "grid: risks=[$($Risks -join ', ')] qtys=[$($Qtys -join ', ')]"

$backup = Get-Content $Cfg -Raw
$results = New-Object System.Collections.ArrayList
$idx = 0
$total = $Risks.Count * $Qtys.Count

try {
  foreach ($risk in $Risks) {
    foreach ($qty in $Qtys) {
      $idx++
      $tag = "risk${risk}_qty${qty}"
      Set-Sizing -risk $risk -qty $qty
      Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $tag)
      $sw = [System.Diagnostics.Stopwatch]::StartNew()
      $out = & $Wa ai backtest $Ticker --since $Since --until $Until --starting-cash $StartingCash 2>&1
      $sw.Stop()
      if ($LASTEXITCODE -ne 0) { Log ("  -> rc={0} (skipping)" -f $LASTEXITCODE); continue }

      $s = Get-RunStats -out $out
      $mar = if ($s.PnlPct -ne $null -and $s.DrawdownPct -gt 0) { [math]::Round($s.PnlPct / $s.DrawdownPct, 2) } else { $null }
      $row = [PSCustomObject]@{
        Risk = $risk; MaxQty = $qty; TotalPnlPct = $s.PnlPct; MaxDrawdownPct = $s.DrawdownPct
        RetPerDD = $mar; Opens = $s.Opens; WinRatePct = $s.WinRatePct; EndingEquity = $s.EndingEquity
        Elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
      }
      [void]$results.Add($row)
      Log ("  -> pnl={0}% dd={1}% ret/dd={2} opens={3} wr={4}% took={5}s" -f $s.PnlPct, $s.DrawdownPct, $mar, $s.Opens, $s.WinRatePct, $row.Elapsed)
      $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
    }
  }
}
finally {
  Set-Content -Path $Cfg -Value $backup -NoNewline
  Log "config restored."
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"
Write-Host ""
Write-Host "--- Ranked by return / drawdown (survival-adjusted) ---"
$results | Sort-Object -Property RetPerDD -Descending | Format-Table Risk, MaxQty, TotalPnlPct, MaxDrawdownPct, RetPerDD, Opens, WinRatePct, EndingEquity -AutoSize

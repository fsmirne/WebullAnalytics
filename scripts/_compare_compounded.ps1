<#
_compare_compounded.ps1 - Phase-2 confirmation (matches the existing sweep scripts' own documented pattern:
sweep sizing-neutral with --lots 1, then re-run the candidates WITH real compounding at a real starting
balance to see whether PF's advantage under --lots 1 actually translates into better real-money growth, or
whether the two candidates just compound differently). Compares:
  SL40/cur/TP75  (PF 6.01, 46 positions, $7,902 sizing-neutral)
  SL60/cur/TP75  (PF 4.87, 45 positions, $9,438 sizing-neutral)
Both at --starting-cash 50000, no --lots override (real risk-scaled sizing), same 2025-01-01..now window.
#>

$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot
$DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$BaseConfigPath = Join-Path $DataDir 'ai-config.SPY.SV.json'
$BaseConfigRaw = Get-Content $BaseConfigPath -Raw
$Wa = "$RepoRoot\.sweep-bin\wa.dll"
$Since = '2025-01-01'
$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd')

function New-Config($Tag, $SlPct) {
  $cfg = $BaseConfigRaw | ConvertFrom-Json
  $cfg.rules.stopLoss.enabled = $true
  $cfg.rules.stopLoss.pctOfMaxLoss = $SlPct / 100.0
  $cfg.rules.takeProfit.profitTargetPctOfPremium = 0.75
  $cfg.opener.structures.shortVertical.shortDeltaMin = 0.15
  $cfg.opener.structures.shortVertical.shortDeltaMax = 0.30
  $cfg.opener.structures.shortVertical.dteMin = 45
  $cfg.opener.structures.shortVertical.dteMax = 60
  $path = Join-Path $DataDir "ai-config.SPY.$Tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $path -Encoding utf8
  return $path
}

function Run-One($Tag, $SlPct) {
  $configPath = New-Config $Tag $SlPct
  $fillsPath = Join-Path $RepoRoot "compound_$Tag.jsonl"
  $logPath = Join-Path $RepoRoot "compound_$Tag.txt"
  Remove-Item $fillsPath, $logPath -ErrorAction SilentlyContinue
  Write-Host "[$(Get-Date -Format 'HH:mm:ss')] running $Tag (SL=$SlPct%, TP=75%, compounded, \$50000 start)..."
  & dotnet $Wa ai backtest SPY --starting-cash 50000 --since $Since --until $Until --strategy $Tag `
    --show-fills --book-cmd --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $logPath | Out-Null
  Remove-Item $configPath -ErrorAction SilentlyContinue
  Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Tag done -> $logPath"
}

Run-One 'SVcmp_sl40' 40
Run-One 'SVcmp_sl60' 60

Write-Host ""
Write-Host "=== Comparison ==="
foreach ($tag in @('SVcmp_sl40','SVcmp_sl60')) {
  $log = Join-Path $RepoRoot "compound_$tag.txt"
  Write-Host ""
  Write-Host "--- $tag ---"
  Select-String -Path $log -Pattern 'Ending cash|Ending equity|Realized P&L|Unrealized P&L|Total P&L|Peak equity|Trough equity|Max drawdown|Profit factor|Win rate|Opens\s|Closes \(rules\)' | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
}

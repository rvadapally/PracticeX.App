param(
    [string]$ApiBase = "https://localhost:7100",
    [string]$Targets = "synexar_memo_targets.txt",
    [string]$LogFile = "synexar_memo_run.log"
)

# Sequential runner for Synexar memo batch. One doc at a time → no
# OpenRouter rate-limit issues (the parallel run hit 5 of 8 with 400s
# at ~31 min retry exhaustion). Writes progress to $LogFile so progress
# survives between checks.

$ErrorActionPreference = "Continue"
$lines = Get-Content $Targets
$total = $lines.Count
"started=$(Get-Date -Format o) total=$total api=$ApiBase" | Set-Content $LogFile

$ok = 0; $fail = 0; $partial = 0; $i = 0
$runSw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($line in $lines) {
    $i++
    $parts = $line -split '\|'
    $id = $parts[0]; $name = $parts[1]; $type = $parts[2]
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Uri "$ApiBase/api/legal-advisor/memos/$id" -Method Post -SkipCertificateCheck -TimeoutSec 600
        $sw.Stop()
        $sec = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        $status = $resp.status
        if ($status -eq 'completed') { $ok++ } elseif ($status -eq 'partial') { $partial++ } else { $fail++ }
        $msg = "[$i/$total] $status risk=$($resp.riskScore) sec=$sec tok=$($resp.tokensIn)/$($resp.tokensOut) [$type] $name"
    } catch {
        $sw.Stop()
        $fail++
        $sec = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        $msg = "[$i/$total] FAIL sec=$sec [$type] $name :: $($_.Exception.Message -replace '\s+', ' ')"
    }
    Write-Output $msg
    $msg | Add-Content $LogFile
}

$runSw.Stop()
$summary = "done=$(Get-Date -Format o) total=$total ok=$ok partial=$partial fail=$fail elapsed_min=$([Math]::Round($runSw.Elapsed.TotalMinutes,1))"
Write-Output $summary
$summary | Add-Content $LogFile

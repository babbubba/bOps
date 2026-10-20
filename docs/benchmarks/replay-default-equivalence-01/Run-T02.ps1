# REPLAY-DEFAULT-EQUIVALENCE-01 re-validation driver: T02 x3 against the real model, same limits as Run-Campaign.ps1 of QWEN-LIVE-BASELINE-01.
# Runs go to a SEPARATE root (never the original campaign's), one process per run, sequential, external watchdog. No run is discarded or repeated.
param(
    [string]$Root = 'C:\source\repos\babbubba\bOps-backups\replay-default-equivalence-01',
    [string]$Repo = 'C:\source\repos\babbubba\bOps-knowbench01a',
    [int]$Repetitions = 3,
    [int]$MaxCalls = 40,
    [int]$MaxMinutes = 25,
    [int]$HardKillMinutes = 30
)
$ErrorActionPreference = 'Stop'
$docs = Join-Path $Repo 'docs\benchmarks\qwen-live-baseline-01'      # scenarios.json + fixtures: unchanged from the original campaign
$dll = Join-Path $Repo 'tests\bOps.QwenLiveBaseline\bin\Debug\net10.0\bOps.QwenLiveBaseline.dll'
$config = Join-Path $Root 'shared\appsettings.json'
New-Item -ItemType Directory -Force (Join-Path $Root 'logs') | Out-Null
$env:BOPS_MODEL_API_KEY = [Environment]::GetEnvironmentVariable('BOPS_MODEL_API_KEY', 'User')
try {
    foreach ($run in 1..$Repetitions) {
        $id = "T02-r$run"
        $runDir = Join-Path $Root "runs\$id"
        if (Test-Path $runDir) { "SKIP $id (directory exists; never overwritten)" | Tee-Object (Join-Path $Root 'logs\campaign.log') -Append; continue }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $started = Get-Date
        $argList = @($dll, '--scenario', 'T02', '--run', $run, '--root', $Root, '--docs', $docs, '--config', $config, '--max-calls', $MaxCalls, '--max-minutes', $MaxMinutes)
        $p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -NoNewWindow -PassThru `
            -RedirectStandardOutput (Join-Path $Root "logs\$id.out.txt") -RedirectStandardError (Join-Path $Root "logs\$id.err.txt")
        if (-not $p.WaitForExit($HardKillMinutes * 60 * 1000)) { $p.Kill($true); $exit = 'HARD-KILLED' } else { $exit = $p.ExitCode }
        "$id exit=$exit elapsed=$($sw.Elapsed.ToString('c')) startedAtLocal=$($started.ToString('s'))" | Tee-Object (Join-Path $Root 'logs\campaign.log') -Append
    }
    'DONE' | Tee-Object (Join-Path $Root 'logs\campaign.log') -Append
}
finally { Remove-Item Env:BOPS_MODEL_API_KEY -ErrorAction SilentlyContinue }

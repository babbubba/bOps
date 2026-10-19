# QWEN-LIVE-BASELINE-01 campaign driver. One process per run (isolation), sequential (the server runs --parallel 1), external watchdog.
# Order is fixed by the brief: T08 x3, then T10 x3, then T02 x3. No result is ever discarded or re-run to obtain a better one.
param(
    [string]$Root = 'C:\source\repos\babbubba\bOps-backups\qwen-live-baseline-01',
    [string]$Repo = 'C:\source\repos\babbubba\bOps-knowbench01a',
    [int]$Repetitions = 3,
    [int]$MaxCalls = 40,          # LLM HTTP requests per run (identical for every run)
    [int]$MaxMinutes = 25,        # in-process watchdog per run (identical for every run)
    [int]$HardKillMinutes = 30    # external watchdog per run
)
$ErrorActionPreference = 'Stop'
$docs = Join-Path $Repo 'docs\benchmarks\qwen-live-baseline-01'
$dll = Join-Path $Repo 'tests\bOps.QwenLiveBaseline\bin\Debug\net10.0\bOps.QwenLiveBaseline.dll'
$config = Join-Path $Root 'shared\appsettings.json'
New-Item -ItemType Directory -Force (Join-Path $Root 'logs') | Out-Null
# The credential is passed through the existing mechanism (BOPS_MODEL_API_KEY, user scope) to the child process only; never printed or stored.
$env:BOPS_MODEL_API_KEY = [Environment]::GetEnvironmentVariable('BOPS_MODEL_API_KEY', 'User')
try {
    foreach ($scenario in 'T08', 'T10', 'T02') {
        foreach ($run in 1..$Repetitions) {
            $id = "$scenario-r$run"
            $runDir = Join-Path $Root "runs\$id"
            if (Test-Path $runDir) { "SKIP $id (directory exists; never overwritten)" | Tee-Object (Join-Path $Root 'logs\campaign.log') -Append; continue }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $args = @($dll, '--scenario', $scenario, '--run', $run, '--root', $Root, '--docs', $docs, '--config', $config, '--max-calls', $MaxCalls, '--max-minutes', $MaxMinutes)
            $p = Start-Process -FilePath 'dotnet' -ArgumentList $args -NoNewWindow -PassThru `
                -RedirectStandardOutput (Join-Path $Root "logs\$id.out.txt") -RedirectStandardError (Join-Path $Root "logs\$id.err.txt")
            if (-not $p.WaitForExit($HardKillMinutes * 60 * 1000)) { $p.Kill($true); $exit = 'HARD-KILLED' } else { $exit = $p.ExitCode }
            "$id exit=$exit elapsed=$($sw.Elapsed.ToString('c')) startedAtLocal=$((Get-Date).AddSeconds(-$sw.Elapsed.TotalSeconds).ToString('s'))" | Tee-Object (Join-Path $Root 'logs\campaign.log') -Append
        }
    }
    'DONE' | Tee-Object (Join-Path $Root 'logs\campaign.log') -Append
}
finally { Remove-Item Env:BOPS_MODEL_API_KEY -ErrorAction SilentlyContinue }

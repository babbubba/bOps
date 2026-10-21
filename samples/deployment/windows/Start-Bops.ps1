# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

<#
.SYNOPSIS
  F-26 sample: starts the published bOps.Api on Windows with its data directory as working directory.

.DESCRIPTION
  Reads KEY=VALUE lines from the environment file (same format as the Linux EnvironmentFile), points ASPNETCORE_CONTENTROOT at the
  installation directory, and runs bOps.Api.exe with the data directory as its working directory, so every relative state file
  (sessions.db, tasks.db, audit.jsonl, vault.dat, settings.json, policy.yaml, plugins\ ...) is created there and an upgrade that replaces
  the installation directory never touches it. It waits for the process, so a Scheduled Task sees bOps's lifetime and can restart it.

  Register it (elevated PowerShell; adapt the account and paths; see docs/deployment.md):
    $action   = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument '-NoProfile -File "C:\Program Files\bOps\Start-Bops.ps1"'
    $trigger  = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName 'bOps' -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = 'C:\Program Files\bOps',
    [string]$DataDirectory = 'C:\ProgramData\bOps\data',
    [string]$EnvironmentFile = 'C:\ProgramData\bOps\bops.env'
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallDirectory 'bOps.Api.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "bOps.Api.exe not found in '$InstallDirectory'." }
if (-not (Test-Path -LiteralPath $EnvironmentFile)) { throw "Environment file '$EnvironmentFile' not found (copy samples/deployment/bops.env.example)." }
New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null

foreach ($line in Get-Content -LiteralPath $EnvironmentFile) {
    if ($line -match '^\s*#' -or $line -notmatch '^\s*([^=\s]+)\s*=(.*)$') { continue }
    [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2].Trim(), 'Process')
}
$env:ASPNETCORE_CONTENTROOT = $InstallDirectory

$process = Start-Process -FilePath $exe -WorkingDirectory $DataDirectory -PassThru -NoNewWindow
$process.WaitForExit()
exit $process.ExitCode

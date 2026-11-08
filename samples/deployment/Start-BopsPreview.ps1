# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0
#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [ValidateRange(1024, 65535)][int]$Port = 5080
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$policy = Join-Path $PSScriptRoot 'preview-read-only.yaml'
$approvedHash = 'EB2D4DBD10DDAEA0B9039C833AD52AE41BBC6303DD68635167BE89DA8F1135F4'
if (-not $env:BOPS_API_KEY -or -not $env:BOPS_VAULT_MASTER_KEY) {
    throw 'Set the preview API and vault keys before starting.'
}
$data = (Resolve-Path -LiteralPath $DataDirectory).ProviderPath
$hostName = if ($IsWindows) { 'bOps.Api.exe' } else { 'bOps.Api' }
$url = "http://localhost:$Port"

$start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $PSScriptRoot $hostName))
$start.WorkingDirectory = $data
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
foreach ($name in @($start.Environment.Keys)) {
    if ($name -match '^(?:(?:ASPNETCORE|DOTNET)_)?KESTREL(?::|__)') {
        throw 'Remove Kestrel endpoint overrides from the preview environment.'
    }
}
foreach ($argument in @(
    "--contentRoot=$PSScriptRoot",
    '--environment=Production',
    "--Kestrel:Endpoints:Http:Url=$url",
    "--Policy:FilePath=$policy",
    '--Logging:Console:FormatterName=simple',
    '--Logging:Console:LogLevel:bOps.Api.Policy=Information',
    '--Logging:Console:LogLevel:Microsoft.Hosting.Lifetime=Information',
    "--BrowserSession:Origins=$url",
    '--Authentication:ApiKeys:0:Roles=viewer,operator'
)) {
    $start.ArgumentList.Add($argument)
}

# Keep the policy open without write/delete sharing on Windows. On Linux, the installation directory
# and file must be protected from replacement by the host account for the duration of the run.
$policyFile = [IO.File]::Open($policy, 'Open', 'Read', 'Read')
try {
    if ((Get-FileHash -InputStream $policyFile -Algorithm SHA256).Hash -cne $approvedHash) {
        throw 'The installed preview policy does not match the approved SHA-256.'
    }

    $child = [System.Diagnostics.Process]::Start($start)
    try {
        $expected = "Policy file '$policy' (Loaded)."
        $loaded = $false
        $listening = $false
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        do {
            $remaining = [TimeSpan]::FromSeconds(30) - $clock.Elapsed
            if ($remaining -le [TimeSpan]::Zero) { throw 'Preview startup timed out.' }
            $line = $child.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
            if ($null -eq $line) { throw 'The preview host exited before passing the startup checks.' }
            Write-Host $line

            if ($line -cmatch '^(?:info|fail): bOps\.Api\.Policy\[0\] ') {
                if (-not $line.EndsWith($expected, [StringComparison]::Ordinal)) {
                    throw 'The preview host did not load the approved policy path.'
                }
                $loaded = $true
            }
            if ($line -cmatch '^info: Microsoft\.Hosting\.Lifetime\[14\] Now listening on: (.+)$') {
                if (-not $loaded -or $Matches[1] -cne $url) {
                    throw 'The preview host reported an unexpected listener or policy state.'
                }
                $listening = $true
            }
        } until ($line -cmatch '^info: Microsoft\.Hosting\.Lifetime\[0\] Application started\.')

        if (-not $loaded -or -not $listening -or
            (Get-FileHash -LiteralPath $policy -Algorithm SHA256).Hash -cne $approvedHash) {
            throw 'The preview startup checks did not pass.'
        }
        Write-Host "Preview startup verified. Open $url."
        while ($null -ne ($line = $child.StandardOutput.ReadLine())) { Write-Host $line }
        $child.WaitForExit()
        if ($child.ExitCode -ne 0) { throw "The preview host exited with code $($child.ExitCode)." }
    } finally {
        if (-not $child.HasExited) {
            $child.Kill($true)
            $child.WaitForExit()
        }
        $child.Dispose()
    }
} finally {
    $policyFile.Dispose()
}

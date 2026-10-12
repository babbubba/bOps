[CmdletBinding()]
param(
    [string] $TargetRepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path,
    [string] $ConfigSourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')),
    [string] $ConfigSnapshotPath,
    [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $ExpectedTargetCommit,
    [ValidateRange(1, 3)] [int] $RunsPerScenario = 3,
    [switch] $PreflightOnly,
    [string] $ExpectedUserSid,
    [switch] $Elevated
)

$ErrorActionPreference = 'Stop'
$resolvedTarget = (Resolve-Path -LiteralPath $TargetRepoRoot).Path
$resolvedConfigSource = (Resolve-Path -LiteralPath $ConfigSourceRoot).Path
if ($ConfigSnapshotPath) { $resolvedSnapshot = [IO.Path]::GetFullPath($ConfigSnapshotPath) }
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python 3 is required. Install Python and retry; no packages are required.' }
$pythonExe = $python.Source
$benchmarkScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'live_benchmark.py'))
$pythonArgs = @()
if ($python.Name -eq 'py.exe') { $pythonArgs += '-3' }
$pythonArgs += $benchmarkScript

function ConvertTo-WindowsArgument([string] $Value) {
    $builder = [Text.StringBuilder]::new('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (2 * $slashes + 1)))
            [void]$builder.Append('"')
        } else {
            [void]$builder.Append(('\' * $slashes))
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    [void]$builder.Append(('\' * (2 * $slashes)))
    [void]$builder.Append('"')
    return $builder.ToString()
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$actualUserSid = $identity.User.Value
if ($ExpectedUserSid -and $ExpectedUserSid -ne $actualUserSid) {
    throw 'The elevated benchmark process is running under a different Windows user identity.'
}
$expectedUserSid = if ($ExpectedUserSid) { $ExpectedUserSid } else { $actualUserSid }
if ([string]::IsNullOrEmpty($env:BOPS_VAULT_MASTER_KEY)) {
    $userVaultMasterKey = [Environment]::GetEnvironmentVariable('BOPS_VAULT_MASTER_KEY', 'User')
    if (-not [string]::IsNullOrEmpty($userVaultMasterKey)) { $env:BOPS_VAULT_MASTER_KEY = $userVaultMasterKey }
    $userVaultMasterKey = $null
}
if ([string]::IsNullOrEmpty($env:BOPS_VAULT_MASTER_KEY)) {
    throw 'BOPS_VAULT_MASTER_KEY is unavailable in this Windows user context; no API process was started.'
}
$env:BENCH_EXPECTED_WINDOWS_USER_SID = $expectedUserSid
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { throw 'UAC elevation did not grant administrator rights.' }
    $script64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($PSCommandPath))
    $target64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($resolvedTarget))
    $configSource64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($resolvedConfigSource))
    $snapshot64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$resolvedSnapshot))
    $commit64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$ExpectedTargetCommit))
    $userSid64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expectedUserSid))
    $cwd64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path))
    $elevationScript = @"
`$scriptPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$script64'))
`$targetPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$target64'))
`$configSourcePath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$configSource64'))
`$snapshotPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$snapshot64'))
`$expectedCommit = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$commit64'))
`$expectedSid = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$userSid64'))
`$originalCwd = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$cwd64'))
Set-Location -LiteralPath `$originalCwd
`$benchArgs = @('-TargetRepoRoot', `$targetPath, '-ConfigSourceRoot', `$configSourcePath, '-RunsPerScenario', '$RunsPerScenario', '-ExpectedUserSid', `$expectedSid, '-Elevated')
if (`$snapshotPath) { `$benchArgs += @('-ConfigSnapshotPath', `$snapshotPath) }
if (`$expectedCommit) { `$benchArgs += @('-ExpectedTargetCommit', `$expectedCommit) }
if ('$([bool]$PreflightOnly)' -eq 'True') { `$benchArgs += '-PreflightOnly' }
& `$scriptPath @benchArgs
exit `$LASTEXITCODE
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevationScript))
    $elevatedProcess = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Wait -PassThru
    exit $elevatedProcess.ExitCode
}

$pythonArgs += @('--target-repo-root', $resolvedTarget, '--config-source-root', $resolvedConfigSource,
                 '--runs-per-scenario', [string]$RunsPerScenario, '--expected-user-sid', $expectedUserSid)
if ($resolvedSnapshot) { $pythonArgs += @('--config-snapshot-path', $resolvedSnapshot) }
if ($ExpectedTargetCommit) { $pythonArgs += @('--expected-target-commit', $ExpectedTargetCommit) }
if ($PreflightOnly) { $pythonArgs += '--preflight-only' }
$argumentLine = ($pythonArgs | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' '
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $pythonExe
if ($startInfo.PSObject.Properties.Name -contains 'ArgumentList') {
    foreach ($argument in $pythonArgs) { [void]$startInfo.ArgumentList.Add([string]$argument) }
} else {
    $startInfo.Arguments = $argumentLine
}
$startInfo.UseShellExecute = $false
$pythonProcess = [Diagnostics.Process]::Start($startInfo)
$pythonProcess.WaitForExit()
exit $pythonProcess.ExitCode

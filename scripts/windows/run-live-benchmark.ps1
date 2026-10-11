[CmdletBinding()]
param(
    [string] $TargetRepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path,
    [switch] $Elevated
)

$ErrorActionPreference = 'Stop'
$resolvedTarget = (Resolve-Path -LiteralPath $TargetRepoRoot).Path
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python 3 is required. Install Python and retry; no packages are required.' }
$pythonExe = $python.Source
$pythonArgs = if ($python.Name -eq 'py.exe') { @('-3', (Join-Path $PSScriptRoot 'live_benchmark.py')) } else { @((Join-Path $PSScriptRoot 'live_benchmark.py')) }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { throw 'UAC elevation did not grant administrator rights.' }
    $script64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($PSCommandPath))
    $target64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($resolvedTarget))
    $cwd64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path))
    $elevationScript = @"
`$scriptPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$script64'))
`$targetPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$target64'))
`$originalCwd = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$cwd64'))
Set-Location -LiteralPath `$originalCwd
& `$scriptPath -TargetRepoRoot `$targetPath -Elevated
exit `$LASTEXITCODE
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevationScript))
    $elevatedProcess = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Wait -PassThru
    exit $elevatedProcess.ExitCode
}

& $pythonExe @pythonArgs --target-repo-root $resolvedTarget
exit $LASTEXITCODE

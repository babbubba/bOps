[CmdletBinding()]
param(
    [string] $TargetRepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path,
    [ValidateRange(1, 3)] [int] $RunsPerScenario = 3,
    [switch] $Elevated
)

$ErrorActionPreference = 'Stop'
$resolvedTarget = (Resolve-Path -LiteralPath $TargetRepoRoot).Path
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
& `$scriptPath -TargetRepoRoot `$targetPath -RunsPerScenario $RunsPerScenario -Elevated
exit `$LASTEXITCODE
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevationScript))
    $elevatedProcess = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Wait -PassThru
    exit $elevatedProcess.ExitCode
}

$pythonArgs += @('--target-repo-root', $resolvedTarget, '--runs-per-scenario', [string]$RunsPerScenario)
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

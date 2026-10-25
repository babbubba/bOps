<#
.SYNOPSIS
  BOPS-0125 MAF-CLI-AB-01 launcher: serial campaign of the real bOps CLI over four conditions.
  A = MAF off / Knowledge off, B = MAF on / Knowledge off, C = MAF off / Knowledge on, D = MAF on / Knowledge on.

.DESCRIPTION
  Wraps scripts\windows\maf_cli_benchmark.py (Python 3 standard library only). Nothing is uploaded. A live run needs -AllowLive.
  -PreflightOnly submits no task and calls no model: it lists the endpoint's models, reads the model server's real configuration
  (read-only GETs: context window, KV cache types, generation limit, slots), and runs the real CLI once per variant against a loopback stub
  to read the MAF mode back from the process and to have the real CLI admit (or refuse) the signed Knowledge pack.

  Actions (exactly one; default is a run when -AllowLive is given, otherwise a preflight):
    -PreflightOnly            checks only
    -Estimate                 token / duration / cost estimate for the chosen scenarios (no CLI, no model)
    -Analyze  <campaignDir>   regenerate the reports (ab-report.* and abcd-report.md)
    -Verify   <campaignDir>   re-hash the captured evidence
    -Review   <campaignDir>   verify + regenerate + print the A/B/C/D comparison (exit 0 only if intact, complete and safe)

  Knowledge (variants C and D): -KnowledgePacksDirectory, -KnowledgeTrustStore, -KnowledgePackId and -KnowledgePackVersion go together; the
  pack is built and signed by scripts\windows\prepare-knowledge-benchmark-pack.ps1. -Resume continues an interrupted campaign in -OutputRoot
  (same commit, profile, harness, pack and plan) so a campaign keeps one identity.
#>
[CmdletBinding()]
param(
    [string] $TargetRepoRoot,
    [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $ExpectedTargetCommit,
    [string] $ModelProfile,
    [string] $Scenarios = 'B01',
    [ValidatePattern('^[ABCD](,[ABCD]){0,3}$')] [string] $Variants,
    [ValidateRange(1, 3)] [int] $Rounds = 1,
    [ValidateSet('alternate', 'fixed', 'rotate')] [string] $Order = 'alternate',
    [string] $OutputRoot,
    [int] $TaskTimeoutSeconds = 1800,
    [int] $CampaignTimeoutSeconds = 21600,
    [int] $TokenBudget,
    [int] $PerRunTokenCap,
    [double] $CostBudgetUsd,
    [string] $CliDll,
    [string] $HistoryDir,
    [string] $KnowledgePacksDirectory,
    [string] $KnowledgeTrustStore,
    [string] $KnowledgePackId,
    [string] $KnowledgePackVersion,
    [string] $Analyze,
    [string] $Verify,
    [string] $Review,
    [switch] $PreflightOnly,
    [switch] $Estimate,
    [switch] $AllowLive,
    [switch] $AllowRemoteEndpoint,
    [switch] $AllowUnverifiedServer,
    [switch] $Resume,
    [switch] $NoBuild,
    [switch] $SkipEndpointProbe,
    [switch] $SkipModeProbe,
    [switch] $ContinueOnTimeout,
    [switch] $ContinueOnProviderFailure,
    [switch] $RequireElevated
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $TargetRepoRoot) { $TargetRepoRoot = Join-Path $scriptDirectory '..\..' }
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python 3 is required (standard library only).' }
$script = [IO.Path]::GetFullPath((Join-Path $scriptDirectory 'maf_cli_benchmark.py'))
$prefix = @(); if ($python.Name -eq 'py.exe') { $prefix += '-3' }

if ($RequireElevated) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This shell is not elevated. Start PowerShell as Administrator (the same context for the whole campaign), then retry.'
    }
}

$target = (Resolve-Path -LiteralPath $TargetRepoRoot).Path
function Add-Common([System.Collections.Generic.List[string]] $list) {
    $list.Add('--target-repo-root'); $list.Add($target)
    if ($ExpectedTargetCommit) { $list.Add('--expected-target-commit'); $list.Add($ExpectedTargetCommit) }
    if ($ModelProfile) { $list.Add('--model-profile'); $list.Add([IO.Path]::GetFullPath($ModelProfile)) }
    if ($OutputRoot) { $list.Add('--output-root'); $list.Add([IO.Path]::GetFullPath($OutputRoot)) }
    if ($CliDll) { $list.Add('--cli-dll'); $list.Add([IO.Path]::GetFullPath($CliDll)) }
    if ($KnowledgePacksDirectory) { $list.Add('--knowledge-packs-dir'); $list.Add([IO.Path]::GetFullPath($KnowledgePacksDirectory)) }
    if ($KnowledgeTrustStore) { $list.Add('--knowledge-trust-store'); $list.Add([IO.Path]::GetFullPath($KnowledgeTrustStore)) }
    if ($KnowledgePackId) { $list.Add('--knowledge-pack-id'); $list.Add($KnowledgePackId) }
    if ($KnowledgePackVersion) { $list.Add('--knowledge-pack-version'); $list.Add($KnowledgePackVersion) }
    foreach ($flag in @(@('NoBuild', '--no-build'), @('SkipEndpointProbe', '--skip-endpoint-probe'), @('SkipModeProbe', '--skip-mode-probe'),
                        @('AllowRemoteEndpoint', '--allow-remote-endpoint'), @('AllowLive', '--allow-live'),
                        @('AllowUnverifiedServer', '--allow-unverified-server'))) {
        if ((Get-Variable -Name $flag[0]).Value) { $list.Add($flag[1]) }
    }
}

# The historical default of a run is A,B; a preflight with a Knowledge pack checks all four conditions.
$runVariants = if ($Variants) { $Variants } else { 'A,B' }

$arguments = [System.Collections.Generic.List[string]]::new()
if ($Analyze) { $arguments.Add('analyze'); $arguments.Add('--campaign-dir'); $arguments.Add([IO.Path]::GetFullPath($Analyze)) }
elseif ($Verify) { $arguments.Add('verify'); $arguments.Add('--campaign-dir'); $arguments.Add([IO.Path]::GetFullPath($Verify)) }
elseif ($Review) { $arguments.Add('review'); $arguments.Add('--campaign-dir'); $arguments.Add([IO.Path]::GetFullPath($Review)) }
elseif ($Estimate) {
    if (-not $ModelProfile) { throw '-ModelProfile is required.' }
    $arguments.Add('estimate'); $arguments.Add('--model-profile'); $arguments.Add([IO.Path]::GetFullPath($ModelProfile))
    $arguments.Add('--scenarios'); $arguments.Add($Scenarios); $arguments.Add('--variants'); $arguments.Add($runVariants)
    $arguments.Add('--rounds'); $arguments.Add([string]$Rounds); $arguments.Add('--task-timeout'); $arguments.Add([string]$TaskTimeoutSeconds)
    if ($PSBoundParameters.ContainsKey('PerRunTokenCap')) { $arguments.Add('--per-run-token-cap'); $arguments.Add([string]$PerRunTokenCap) }
    if ($HistoryDir) { $arguments.Add('--history-dir'); $arguments.Add([IO.Path]::GetFullPath($HistoryDir)) }
}
else {
    if (-not $ModelProfile) { throw '-ModelProfile is required.' }
    if ($PreflightOnly -or -not $AllowLive) {
        if (-not $PreflightOnly) { Write-Host 'No -AllowLive: running the preflight only. A live run needs the operator''s explicit go-ahead.' }
        $arguments.Add('preflight'); Add-Common $arguments
        if ($Variants) { $arguments.Add('--variants'); $arguments.Add($Variants) }
    } else {
        if (-not $ExpectedTargetCommit) { $ExpectedTargetCommit = (git -C $target rev-parse HEAD).Trim() }
        $arguments.Add('run'); Add-Common $arguments
        $arguments.Add('--scenarios'); $arguments.Add($Scenarios); $arguments.Add('--variants'); $arguments.Add($runVariants)
        $arguments.Add('--rounds'); $arguments.Add([string]$Rounds); $arguments.Add('--order'); $arguments.Add($Order)
        $arguments.Add('--task-timeout'); $arguments.Add([string]$TaskTimeoutSeconds)
        $arguments.Add('--campaign-timeout'); $arguments.Add([string]$CampaignTimeoutSeconds)
        if (-not $PSBoundParameters.ContainsKey('TokenBudget')) { throw '-TokenBudget is mandatory for a live run (campaign-wide token ceiling).' }
        if ($PSBoundParameters.ContainsKey('PerRunTokenCap')) { $arguments.Add('--per-run-token-cap'); $arguments.Add([string]$PerRunTokenCap) }
        if ($PSBoundParameters.ContainsKey('TokenBudget')) { $arguments.Add('--token-budget'); $arguments.Add([string]$TokenBudget) }
        if ($PSBoundParameters.ContainsKey('CostBudgetUsd')) { $arguments.Add('--cost-budget-usd'); $arguments.Add([string]$CostBudgetUsd) }
        if ($ContinueOnTimeout) { $arguments.Add('--continue-on-timeout') }
        if ($ContinueOnProviderFailure) { $arguments.Add('--continue-on-provider-failure') }
        if ($Resume) { $arguments.Add('--resume') }
    }
}

function ConvertTo-WindowsArgument([string] $Value) {
    # Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList: quote by the CommandLineToArgvW rules.
    $builder = [Text.StringBuilder]::new('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') { [void]$builder.Append(('\' * (2 * $slashes + 1))); [void]$builder.Append('"') }
        else { [void]$builder.Append(('\' * $slashes)); [void]$builder.Append($character) }
        $slashes = 0
    }
    [void]$builder.Append(('\' * (2 * $slashes))); [void]$builder.Append('"')
    return $builder.ToString()
}

$all = @($prefix) + @('-I', $script) + @($arguments)
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $python.Source
$startInfo.Arguments = ($all | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' '
$startInfo.UseShellExecute = $false
$process = [Diagnostics.Process]::Start($startInfo)
$process.WaitForExit()
exit $process.ExitCode

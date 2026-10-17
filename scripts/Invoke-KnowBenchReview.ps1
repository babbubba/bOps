# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
  KNOW-BENCH D7 blind review: export the reviewer package from the protected diagnosis archive, then (after the review) join the
  reviewer's evaluations back to the runs. Drives the opt-in KnowledgeDiagnosisReviewTools entry points; contacts no provider and reads no Event Log.
.DESCRIPTION
  ExportPackage  Decrypts the archived diagnoses and writes the blind package (cards, corpus, rubric, blank evaluation forms) into a NEW directory.
  Join           Reads the reviewer's filled forms plus the PRIVATE map and writes quality-results-*.json inside <ArchiveRoot>\private\quality.
  Both need -AcceptLocalArchiveAccess: it is the operator's explicit authorization to decrypt the archive on this machine.
  The package directory must be NEW, outside the archive and outside the checkout. Never hand the archive, the map or quality-results to the reviewer.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('ExportPackage', 'Join')][string]$Action,
    [Parameter(Mandatory)][string]$ArchiveRoot,
    [switch]$AcceptLocalArchiveAccess,
    # Windows uses DPAPI (current user). Supply a secret to use the portable operator-secret protection instead (required off Windows).
    [securestring]$OperatorSecret,
    # ExportPackage
    [string]$PackageDirectory,
    [string]$CorpusPath,
    [string]$CorpusSha256,
    [string]$AttestationSha256,
    # Join
    [string]$PackageId,
    [string]$EvaluationsDirectory,
    [string[]]$ResultsRoots = @()
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj'

function Assert-Absolute([string]$Name, [string]$Path) {
    if (!$Path -or ![IO.Path]::IsPathFullyQualified($Path)) { throw "$Name must be an absolute path." }
}
function Test-Inside([string]$Parent, [string]$Child) {
    $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Parent), [IO.Path]::GetFullPath($Child))
    return $relative -eq '.' -or (!$relative.StartsWith('..') -and ![IO.Path]::IsPathRooted($relative))
}

if (!$AcceptLocalArchiveAccess) { throw 'Pass -AcceptLocalArchiveAccess to authorize decrypting the diagnosis archive on this machine.' }
Assert-Absolute 'ArchiveRoot' $ArchiveRoot
if (!(Test-Path -LiteralPath (Join-Path $ArchiveRoot 'private/key.json'))) { throw 'ArchiveRoot is not an existing diagnosis archive.' }
if (Test-Inside $root $ArchiveRoot) { throw 'Keep the diagnosis archive outside the benchmark checkout.' }

$variables = @{
    BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE        = $ArchiveRoot
    BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE_ACCEPT = '1'
}
if ($OperatorSecret) {
    $variables.BOPS_KNOW_BENCH_ARCHIVE_PROTECTION = 'operator-secret'
    $variables.BOPS_KNOW_BENCH_ARCHIVE_SECRET = [Net.NetworkCredential]::new('', $OperatorSecret).Password
}

switch ($Action) {
    'ExportPackage' {
        foreach ($pair in @{ PackageDirectory = $PackageDirectory; CorpusPath = $CorpusPath }.GetEnumerator()) { Assert-Absolute $pair.Key $pair.Value }
        if ($CorpusSha256 -cnotmatch '^[0-9a-f]{64}$' -or $AttestationSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Pin the corpus and attestation SHA-256 (lowercase hex).' }
        if (Test-Path -LiteralPath $PackageDirectory) { throw 'PackageDirectory must be NEW; no overwrite.' }
        if ((Test-Inside $ArchiveRoot $PackageDirectory) -or (Test-Inside $PackageDirectory $ArchiveRoot) -or (Test-Inside $root $PackageDirectory)) {
            throw 'PackageDirectory must be outside the archive and outside the checkout.'
        }
        if (!(Test-Path -LiteralPath (Split-Path -Parent $PackageDirectory) -PathType Container)) { throw 'Create the parent of PackageDirectory first.' }
        $variables.BOPS_KNOW_BENCH_REVIEW_OP = 'export-package'
        $variables.BOPS_KNOW_BENCH_REVIEW_PACKAGE_DIR = $PackageDirectory
        $variables.BOPS_EVENTS_CORPUS_PATH = $CorpusPath
        $variables.BOPS_EVENTS_CORPUS_SHA256 = $CorpusSha256
        $variables.BOPS_EVENTS_CORPUS_ATTESTATION_SHA256 = $AttestationSha256
        $test = 'bOps.AgentFramework.Tests.KnowledgeDiagnosisReviewTools.ExportBlindPackage'
    }
    'Join' {
        if ($PackageId -cnotmatch '^pk-[2-9defghjkmnpqrstuvwxyz]{26}$') { throw 'PackageId must be the id in the package-manifest.json the reviewer received.' }
        Assert-Absolute 'EvaluationsDirectory' $EvaluationsDirectory
        if (!(Test-Path -LiteralPath $EvaluationsDirectory -PathType Container)) { throw 'EvaluationsDirectory not found.' }
        $variables.BOPS_KNOW_BENCH_REVIEW_OP = 'join'
        $variables.BOPS_KNOW_BENCH_REVIEW_PACKAGE_ID = $PackageId
        $variables.BOPS_KNOW_BENCH_REVIEW_EVALUATIONS_DIR = $EvaluationsDirectory
        $variables.BOPS_KNOW_BENCH_REVIEW_RESULTS_ROOTS = ($ResultsRoots -join ';')
        $test = 'bOps.AgentFramework.Tests.KnowledgeDiagnosisReviewTools.JoinEvaluations'
    }
}

$results = Join-Path ([IO.Path]::GetTempPath()) ('bops-review-' + [guid]::NewGuid().ToString('N'))
$previous = @{}
foreach ($name in $variables.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($pair in $variables.GetEnumerator()) { [Environment]::SetEnvironmentVariable($pair.Key, $pair.Value) }
    & dotnet test $project --nologo --filter "FullyQualifiedName=$test" --logger 'trx;LogFileName=review.trx' --results-directory $results | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Action failed; see the output above (closed error codes only, no diagnosis text)." }
    # A skipped test also exits 0: require the explicit 'Passed' outcome, never infer success from the exit code.
    $trx = [xml](Get-Content -LiteralPath (Join-Path $results 'review.trx') -Raw)
    $outcomes = @($trx.TestRun.Results.UnitTestResult | ForEach-Object { $_.outcome })
    if ($outcomes.Count -ne 1 -or $outcomes[0] -ne 'Passed') { throw "$Action did not run (outcome: $($outcomes -join ',')); nothing was produced." }
}
finally {
    foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Remove-Item -LiteralPath $results -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Action -eq 'ExportPackage') {
    $manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Raw | ConvertFrom-Json
    Write-Output "Blind package: $PackageDirectory"
    Write-Output "PackageId: $($manifest.packageId)  cards: $(@($manifest.diagnosisIds).Count)"
    Write-Output 'Give the reviewer ONLY this directory. Keep ArchiveRoot, its private map and quality-results private until every evaluation is Final.'
}
else {
    $latest = Get-ChildItem -LiteralPath (Join-Path $ArchiveRoot 'private/quality') -Filter 'quality-results-*.json' | Sort-Object Name | Select-Object -Last 1
    Write-Output "Quality results: $($latest.FullName)"
    Write-Output 'Quality (human scores) is kept separate from Knowledge-access metrics; combine them by runId only in your own analysis.'
}

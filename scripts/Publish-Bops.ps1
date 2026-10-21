# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

<#
.SYNOPSIS
  Builds the complete bOps artifact (API + compiled Angular SPA) for one runtime. F-26 (BOPS-0120).

.DESCRIPTION
  A plain `dotnet build` never needs Node.js. This script is the full-artifact path: it builds the Angular application with
  `npm ci && npm run build`, checks that the build is complete, then runs `dotnet publish` of bOps.Api, which copies the SPA to
  `wwwroot-ui/` and fails if it is missing. The published folder needs neither Node.js nor the Angular CLI at run time.

  Backend only (no UI), on purpose:  dotnet publish src/core/bOps.Api/bOps.Api.csproj -p:BopsWebUi=false

.PARAMETER Runtime
  win-x64 or linux-x64 (a publish for another runtime identifier also works but is not part of the verified matrix).

.PARAMETER Output
  Output folder; default artifacts/bops-<Runtime>. It is emptied first.

.PARAMETER SkipUiBuild
  Reuse an existing web/bops-ui/dist/bops-ui/browser (still checked for completeness).

.PARAMETER FrameworkDependent
  Publish without the .NET runtime (the target needs the ASP.NET Core 10 runtime). Default is self-contained.

.EXAMPLE
  ./scripts/Publish-Bops.ps1 -Runtime linux-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Runtime,
    [string]$Output,
    [switch]$SkipUiBuild,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ui = Join-Path $root 'web/bops-ui'
$uiBuild = Join-Path $ui 'dist/bops-ui/browser'
if (-not $Output) { $Output = Join-Path $root "artifacts/bops-$Runtime" }
$Output = [System.IO.Path]::GetFullPath($Output)

function Invoke-Native([string]$File, [string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
    } finally { Pop-Location }
}

if (-not $SkipUiBuild) {
    Write-Host '== Angular build'
    Invoke-Native 'npm' @('ci') $ui
    Invoke-Native 'npm' @('run', 'build') $ui
}

# Fail here, with the reason, rather than ship (or let dotnet publish trip over) a missing or partial frontend.
& (Join-Path $PSScriptRoot 'Test-PublishedArtifact.ps1') -UiDirectory $uiBuild -StructureOnly

Write-Host "== dotnet publish ($Runtime)"
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Invoke-Native 'dotnet' @(
    'publish', 'src/core/bOps.Api/bOps.Api.csproj',
    '--configuration', 'Release', '--runtime', $Runtime, '--self-contained', $selfContained,
    '-p:ContinuousIntegrationBuild=true', '-p:PublishTrimmed=false',
    "-p:BopsWebUiDir=$uiBuild", '--output', $Output) $root

Write-Host '== Artifact structure'
& (Join-Path $PSScriptRoot 'Test-PublishedArtifact.ps1') -ArtifactDirectory $Output -StructureOnly

Write-Host "Published: $Output"
Write-Host 'Smoke test the artifact on the matching OS with: ./scripts/Test-PublishedArtifact.ps1 -ArtifactDirectory <that folder>'

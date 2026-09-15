# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/sbom",
    [string]$Configuration = "Release",
    [string]$Version = "0.9.1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedOutputDirectory = Join-Path $repositoryRoot $OutputDirectory
$solutionPath = Join-Path $repositoryRoot "bOps.slnx"
$webUiPath = Join-Path $repositoryRoot "web/bops-ui"

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution not found: $solutionPath"
}

if (-not (Test-Path -LiteralPath (Join-Path $webUiPath "package-lock.json") -PathType Leaf)) {
    throw "UI package lock not found: $webUiPath/package-lock.json"
}

if (-not (Test-Path -LiteralPath (Join-Path $webUiPath "node_modules") -PathType Container)) {
    throw "UI dependencies are not installed. Run npm ci in web/bops-ui before generating the release SBOM."
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

& dotnet tool run dotnet-CycloneDX -- $solutionPath `
    --output $resolvedOutputDirectory `
    --filename "bops-dotnet-solution.cdx.json" `
    --output-format Json `
    --configuration $Configuration `
    --include-license-text `
    --disable-package-restore `
    --set-name "bOps" `
    --set-version $Version `
    --spec-version 1.7

if ($LASTEXITCODE -ne 0) {
    throw "CycloneDX .NET SBOM generation failed with exit code $LASTEXITCODE."
}

Push-Location $webUiPath
try {
    $npmSbom = & npm sbom `
        --sbom-format cyclonedx `
        --sbom-type application

    if ($LASTEXITCODE -ne 0) {
        throw "npm SBOM generation failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$npmSbomPath = Join-Path $resolvedOutputDirectory "bops-ui-runtime.cdx.json"
$npmDocument = ($npmSbom -join [Environment]::NewLine) | ConvertFrom-Json
$packageLock = Get-Content -Raw -LiteralPath (Join-Path $webUiPath "package-lock.json") |
    ConvertFrom-Json -AsHashtable

# npm 10 can omit runtime packages that also participate as peer dependencies when `--omit dev`
# is passed to `npm sbom`. Generate from the real installed tree first, then retain exactly the
# installed packages that package-lock.json does not classify as development-only.
$runtimePackagePaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal
)
foreach ($packageEntry in $packageLock.packages.GetEnumerator()) {
    $packagePath = $packageEntry.Key
    if (-not $packagePath.StartsWith("node_modules/", [System.StringComparison]::Ordinal)) {
        continue
    }

    $isDevelopmentOnly = $packageEntry.Value.ContainsKey("dev") -and
        [bool]$packageEntry.Value.dev
    $installedPath = Join-Path $webUiPath ($packagePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not $isDevelopmentOnly -and (Test-Path -LiteralPath $installedPath -PathType Container)) {
        [void]$runtimePackagePaths.Add($packagePath)
    }
}

$runtimeComponents = @($npmDocument.components | Where-Object {
    $packagePathProperty = $_.properties | Where-Object name -eq "cdx:npm:package:path" |
        Select-Object -First 1
    $null -ne $packagePathProperty -and
        $runtimePackagePaths.Contains([string]$packagePathProperty.value)
})

if ($runtimeComponents.Count -ne $runtimePackagePaths.Count) {
    throw "npm SBOM runtime filtering mismatch: found $($runtimeComponents.Count) components for " +
        "$($runtimePackagePaths.Count) installed runtime packages."
}

$runtimeReferences = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal
)
foreach ($component in $runtimeComponents) {
    [void]$runtimeReferences.Add($component.'bom-ref')
}

$rootReference = $npmDocument.metadata.component.'bom-ref'
$runtimeDependencies = @($npmDocument.dependencies | Where-Object {
    $_.ref -eq $rootReference -or $runtimeReferences.Contains($_.ref)
})
foreach ($dependency in $runtimeDependencies) {
    $dependency.dependsOn = @($dependency.dependsOn | Where-Object {
        $runtimeReferences.Contains($_)
    })
}

$npmDocument.components = $runtimeComponents
$npmDocument.dependencies = $runtimeDependencies
$npmJson = $npmDocument | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText($npmSbomPath, $npmJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$expectedFiles = @(
    (Join-Path $resolvedOutputDirectory "bops-dotnet-solution.cdx.json"),
    $npmSbomPath
)

foreach ($file in $expectedFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Expected SBOM was not generated: $file"
    }

    $document = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json
    if ($document.bomFormat -ne "CycloneDX" -or -not $document.specVersion) {
        throw "Invalid CycloneDX document: $file"
    }
}

Write-Host "Generated CycloneDX SBOM artifacts in $resolvedOutputDirectory"
foreach ($file in $expectedFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Write-Host "- $(Split-Path -Leaf $file): SHA256 $hash"
}

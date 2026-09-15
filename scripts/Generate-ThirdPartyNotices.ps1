# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

[CmdletBinding()]
param(
    [string]$BomPath = "artifacts/sbom/bops-dotnet-solution.cdx.json",
    [string]$PackageLockPath = "web/bops-ui/package-lock.json",
    [string]$OutputPath = "THIRD-PARTY-NOTICES"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedBomPath = Join-Path $repositoryRoot $BomPath
$resolvedPackageLockPath = Join-Path $repositoryRoot $PackageLockPath
$resolvedOutputPath = Join-Path $repositoryRoot $OutputPath

if (-not (Test-Path -LiteralPath $resolvedBomPath -PathType Leaf)) {
    throw "SBOM not found: $resolvedBomPath"
}

if (-not (Test-Path -LiteralPath $resolvedPackageLockPath -PathType Leaf)) {
    throw "package-lock.json not found: $resolvedPackageLockPath"
}

$bom = Get-Content -Raw -LiteralPath $resolvedBomPath | ConvertFrom-Json
$packageLock = Get-Content -Raw -LiteralPath $resolvedPackageLockPath | ConvertFrom-Json -AsHashtable

# CycloneDX cannot infer file-based NuGet licenses. These values were verified against the
# license files embedded in the exact package versions and their upstream project sources.
$nugetLicenseOverrides = @{
    "Docker.DotNet@3.125.15"    = "MIT"
    "Fractions@7.3.0"           = "BSD-2-Clause"
    "Json.More.Net@3.0.1"       = "MIT + OSMFEULA (binary release)"
    "JsonPatch.Net@5.0.2"       = "MIT + OSMFEULA (binary release)"
    "JsonPointer.Net@7.0.1"     = "MIT + OSMFEULA (binary release)"
    "SourceGear.sqlite3@3.50.4.5" = "Public-Domain"
    "xunit.abstractions@2.0.3"  = "MIT"
}

function Get-CycloneDxLicense {
    param([Parameter(Mandatory)]$Component)

    $key = "$($Component.name)@$($Component.version)"
    if ($nugetLicenseOverrides.ContainsKey($key)) {
        return $nugetLicenseOverrides[$key]
    }

    $licenses = @(
        @($Component.licenses | ForEach-Object {
            if ($_.PSObject.Properties.Name -contains "expression" -and $_.expression) {
                $_.expression
            }
            elseif (
                $_.PSObject.Properties.Name -contains "license" -and
                $_.license.PSObject.Properties.Name -contains "id" -and
                $_.license.id
            ) {
                $_.license.id
            }
            elseif (
                $_.PSObject.Properties.Name -contains "license" -and
                $_.license.PSObject.Properties.Name -contains "name" -and
                $_.license.name -and
                $_.license.name -ne "Unknown - See URL"
            ) {
                $_.license.name
            }
        }) | Sort-Object -Unique
    )

    if ($licenses.Count -eq 0) {
        throw "Unresolved NuGet license: $key"
    }

    return $licenses -join " OR "
}

function Add-InventorySection {
    param(
        [Parameter(Mandatory)][System.Text.StringBuilder]$Builder,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][object[]]$Components
    )

    [void]$Builder.AppendLine("## $Title")
    [void]$Builder.AppendLine()

    foreach ($licenseGroup in ($Components | Group-Object License | Sort-Object Name)) {
        [void]$Builder.AppendLine("### $($licenseGroup.Name) ($($licenseGroup.Count))")
        [void]$Builder.AppendLine()
        foreach ($component in ($licenseGroup.Group | Sort-Object Name, Version)) {
            [void]$Builder.AppendLine("- $($component.Name)@$($component.Version)")
        }
        [void]$Builder.AppendLine()
    }
}

$dotNetComponents = @(
    foreach ($component in $bom.components) {
        [PSCustomObject]@{
            Name = $component.name
            Version = $component.version
            License = Get-CycloneDxLicense -Component $component
        }
    }
)

$npmOccurrences = @(
    foreach ($entry in $packageLock.packages.GetEnumerator()) {
        if ([string]::IsNullOrEmpty($entry.Key)) {
            continue
        }

        $package = $entry.Value
        $name = if ($package.ContainsKey("name") -and $package.name) {
            $package.name
        }
        else {
            $entry.Key -replace "^.*node_modules/", ""
        }
        if (-not $package.ContainsKey("license") -or -not $package.license) {
            throw "Unresolved npm license: $name@$($package.version)"
        }

        [PSCustomObject]@{
            Name = $name
            Version = $package.version
            License = $package.license
            IsDevelopment = $package.ContainsKey("dev") -and $package.dev -eq $true
        }
    }
)

$npmComponents = @(
    $npmOccurrences |
        Group-Object Name, Version |
        ForEach-Object {
            $occurrences = $_.Group
            [PSCustomObject]@{
                Name = $occurrences[0].Name
                Version = $occurrences[0].Version
                License = (($occurrences.License | Sort-Object -Unique) -join " OR ")
                Scope = if ($occurrences.IsDevelopment -contains $false) { "runtime" } else { "development" }
            }
        }
)

$npmRuntimeComponents = @($npmComponents | Where-Object Scope -eq "runtime")
$npmDevelopmentComponents = @($npmComponents | Where-Object Scope -eq "development")
$bomTimestamp = ([DateTimeOffset]$bom.metadata.timestamp).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine("# Third-Party Notices")
[void]$builder.AppendLine()
[void]$builder.AppendLine("This file records third-party components identified for bOps v0.9.1. It does not")
[void]$builder.AppendLine("replace the license text supplied by each upstream component. Where an artifact")
[void]$builder.AppendLine("redistributes a component, its applicable copyright, attribution, notice and license")
[void]$builder.AppendLine("terms must remain with that artifact.")
[void]$builder.AppendLine()
[void]$builder.AppendLine("Inventory sources:")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- .NET: CycloneDX 1.7 solution SBOM at ``artifacts/sbom/bops-dotnet-solution.cdx.json``, generated $bomTimestamp")
[void]$builder.AppendLine("  ($($dotNetComponents.Count) unique NuGet components). This solution-wide inventory includes")
[void]$builder.AppendLine("  runtime, build, development and test dependencies; inclusion does not by itself mean that a")
[void]$builder.AppendLine("  component ships in every bOps artifact.")
[void]$builder.AppendLine("- Web UI: npm lockfile v$($packageLock.lockfileVersion) at ``web/bops-ui/package-lock.json``")
[void]$builder.AppendLine("  ($($npmComponents.Count) unique package/version pairs: $($npmRuntimeComponents.Count) runtime and")
[void]$builder.AppendLine("  $($npmDevelopmentComponents.Count) development/build-only according to the lockfile).")
[void]$builder.AppendLine()
[void]$builder.AppendLine("A release must regenerate this inventory from its final restored and bundled artifacts. The")
[void]$builder.AppendLine("checked-in snapshot is evidence for the v0.9.1 licensing gate, not a claim that every listed")
[void]$builder.AppendLine("development dependency is redistributed.")
[void]$builder.AppendLine()
[void]$builder.AppendLine("## License finding requiring release review")
[void]$builder.AppendLine()
[void]$builder.AppendLine('The binary NuGet packages `Json.More.Net@3.0.1`, `JsonPatch.Net@5.0.2` and')
[void]$builder.AppendLine('`JsonPointer.Net@7.0.1` contain an Open Source Maintenance Fee Agreement (OSMFEULA) in')
[void]$builder.AppendLine("addition to the upstream MIT source license. The agreement states that some users of the")
[void]$builder.AppendLine("project's precompiled binary releases in revenue-generating activities owe a maintenance fee.")
[void]$builder.AppendLine('These packages enter this repository only through `Aspire.Hosting@13.5.3` and are part of the')
[void]$builder.AppendLine("development AppHost dependency graph, not the bOps CLI/API runtime graph. Do not classify or")
[void]$builder.AppendLine("distribute those binary packages as plain MIT without reviewing the embedded agreement and the")
[void]$builder.AppendLine("intended commercial use. Upstream sources:")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- https://github.com/json-everything/json-everything/blob/master/LICENSE")
[void]$builder.AppendLine("- https://github.com/json-everything/json-everything/blob/master/OSMFEULA.txt")
[void]$builder.AppendLine()
[void]$builder.AppendLine("## Verified file-based NuGet licenses")
[void]$builder.AppendLine()
[void]$builder.AppendLine("CycloneDX did not resolve seven file/URL-based declarations automatically. The exact package")
[void]$builder.AppendLine("license files and upstream sources were checked as follows:")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- Docker.DotNet@3.125.15 — MIT; copyright .NET Foundation and Contributors.")
[void]$builder.AppendLine("- Fractions@7.3.0 — BSD-2-Clause; copyright 2013-2022 Daniel Mueller.")
[void]$builder.AppendLine("- Json.More.Net@3.0.1 — MIT source plus OSMFEULA for the supplied binary release.")
[void]$builder.AppendLine("- JsonPatch.Net@5.0.2 — MIT source plus OSMFEULA for the supplied binary release.")
[void]$builder.AppendLine("- JsonPointer.Net@7.0.1 — MIT source plus OSMFEULA for the supplied binary release.")
[void]$builder.AppendLine("- SourceGear.sqlite3@3.50.4.5 — SQLite public-domain dedication; package copyright")
[void]$builder.AppendLine("  2014-2025 SourceGear, LLC.")
[void]$builder.AppendLine("- xunit.abstractions@2.0.3 — MIT; resolved from the package's declared license URL.")
[void]$builder.AppendLine()
[void]$builder.AppendLine("Primary references:")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- https://www.nuget.org/packages/Docker.DotNet/3.125.15")
[void]$builder.AppendLine("- https://www.nuget.org/packages/Fractions/7.3.0")
[void]$builder.AppendLine("- https://www.sqlite.org/copyright.html")
[void]$builder.AppendLine("- https://github.com/xunit/xunit/blob/v2/license.txt")
[void]$builder.AppendLine()

Add-InventorySection -Builder $builder -Title ".NET solution inventory" -Components $dotNetComponents
Add-InventorySection -Builder $builder -Title "npm runtime inventory" -Components $npmRuntimeComponents
Add-InventorySection -Builder $builder -Title "npm development/build inventory" -Components $npmDevelopmentComponents

[void]$builder.AppendLine("## Canonical license references")
[void]$builder.AppendLine()
[void]$builder.AppendLine("The component inventories above use SPDX identifiers when available. Canonical texts:")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- Apache-2.0: https://spdx.org/licenses/Apache-2.0.html")
[void]$builder.AppendLine("- MIT: https://spdx.org/licenses/MIT.html")
[void]$builder.AppendLine("- BSD-2-Clause: https://spdx.org/licenses/BSD-2-Clause.html")
[void]$builder.AppendLine("- BSD-3-Clause: https://spdx.org/licenses/BSD-3-Clause.html")
[void]$builder.AppendLine("- ISC: https://spdx.org/licenses/ISC.html")
[void]$builder.AppendLine("- 0BSD: https://spdx.org/licenses/0BSD.html")
[void]$builder.AppendLine("- BlueOak-1.0.0: https://blueoakcouncil.org/license/1.0.0")
[void]$builder.AppendLine("- MPL-2.0: https://www.mozilla.org/MPL/2.0/")
[void]$builder.AppendLine("- CC0-1.0: https://creativecommons.org/publicdomain/zero/1.0/legalcode")
[void]$builder.AppendLine("- CC-BY-3.0: https://creativecommons.org/licenses/by/3.0/legalcode")
[void]$builder.AppendLine("- CC-BY-4.0: https://creativecommons.org/licenses/by/4.0/legalcode")
[void]$builder.AppendLine()
[void]$builder.AppendLine('The Apache-2.0 text applying to bOps itself is in the repository root `LICENSE`. That file does')
[void]$builder.AppendLine("not relicense any third-party component.")

[System.IO.File]::WriteAllText($resolvedOutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $resolvedOutputPath"
Write-Host ".NET components: $($dotNetComponents.Count)"
Write-Host "npm components: $($npmComponents.Count) ($($npmRuntimeComponents.Count) runtime, $($npmDevelopmentComponents.Count) development)"

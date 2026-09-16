# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

param(
    [Parameter(Mandatory = $true)][string]$FirstDirectory,
    [Parameter(Mandatory = $true)][string]$SecondDirectory
)

$ErrorActionPreference = 'Stop'
$first = (Resolve-Path -LiteralPath $FirstDirectory).Path
$second = (Resolve-Path -LiteralPath $SecondDirectory).Path

function Get-TreeDigest([string]$root) {
    $rows = [System.IO.Directory]::EnumerateFiles($root, '*', [System.IO.SearchOption]::AllDirectories) |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($root, $_).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            "$relative`t$hash"
        } |
        Sort-Object
    return $rows
}

$firstDigest = @(Get-TreeDigest $first)
$secondDigest = @(Get-TreeDigest $second)
$difference = Compare-Object -ReferenceObject $firstDigest -DifferenceObject $secondDigest
if ($difference) {
    $difference | Format-Table | Out-String | Write-Error
    throw 'Publish outputs are not reproducible.'
}

Write-Output "Reproducibility check passed for $($firstDigest.Count) files."

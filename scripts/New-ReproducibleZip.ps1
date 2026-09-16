# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$DestinationFile
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$destination = [System.IO.Path]::GetFullPath($DestinationFile)
$destinationDirectory = [System.IO.Path]::GetDirectoryName($destination)
if ($destinationDirectory) {
    [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
}

Add-Type -AssemblyName System.IO.Compression
if ([System.IO.File]::Exists($destination)) {
    [System.IO.File]::Delete($destination)
}

$stream = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew)
try {
    $archive = [System.IO.Compression.ZipArchive]::new(
        $stream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        $files = [System.IO.Directory]::EnumerateFiles($source, '*', [System.IO.SearchOption]::AllDirectories) |
            Sort-Object { [System.IO.Path]::GetRelativePath($source, $_).Replace('\', '/') }
        foreach ($file in $files) {
            $relative = [System.IO.Path]::GetRelativePath($source, $file).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
            $entryStream = $entry.Open()
            try {
                $input = [System.IO.File]::OpenRead($file)
                try { $input.CopyTo($entryStream) } finally { $input.Dispose() }
            } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

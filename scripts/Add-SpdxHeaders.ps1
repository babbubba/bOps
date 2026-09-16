<#
    Mechanically verifiable V0.9.1 rewrite (the source plan is now archived): prepends the
    SPDX license header and copyright notice to every OSS .cs source file under src/ and tests/
    that does not already carry one. Idempotent — re-running is a no-op for files already
    headered. Excludes bin/obj (build output, never a source file) and this scripts/ directory.
#>
$root = Split-Path -Parent $PSScriptRoot
$header = "// Copyright 2026 Fabio Cavallari`r`n// SPDX-License-Identifier: Apache-2.0`r`n`r`n"

$files = Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$changed = 0
foreach ($file in $files) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    if ($content.StartsWith('// SPDX-License-Identifier:')) {
        continue
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($file.FullName, $header + $content, $utf8NoBom)
    $changed++
}

Write-Output "Headered $changed of $($files.Count) file(s)."

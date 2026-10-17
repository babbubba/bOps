# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
  Builds and signs a bOps knowledge pack from a directory of skills (ADR-0058).

.DESCRIPTION
  PUBLISHER TOOLING. This script is never part of the bOps runtime. It reads the publisher's private key from a file the publisher
  holds and writes, under <OutputRoot>/<PackId>/<Version>/, exactly the layout the CLI's pack directory expects:

    bops-pack.json        the manifest: pack id, version, kind, engine range, publisher, key id, every file with size and SHA-256,
                          and the content hash over that list. THESE EXACT BYTES ARE WHAT IS SIGNED.
    bops-pack.sig.json    the detached RSA-PSS-SHA256 signature over the SHA-256 of the manifest bytes.
    <skill>/SKILL.md ...  the payload files, copied byte for byte.

  Source layout: -SourceDirectory contains one directory per skill, each with a SKILL.md (YAML frontmatter with 'name' and
  'description', then the procedure) and optional resources below it. Only plain-text extensions are allowed (.md .txt .json .yaml
  .yml .csv .xml); anything else, any link and any file outside a skill directory is refused here and again by bOps at admission.

  Publishing a pack authorises nothing: the operator still has to install the directory, trust the key and grant the exact pack
  version in the CLI configuration.

.PARAMETER SourceDirectory   Directory of skills to publish.
.PARAMETER OutputRoot        The pack directory root (the CLI's 'Knowledge:PacksDirectory', or a staging folder to copy from).
.PARAMETER PackId            Lower-case pack id, for example 'bsoft.windows-event-runbooks'.
.PARAMETER Version           Exact version major.minor.patch (optional pre-release suffix). A version is never reused with other bytes.
.PARAMETER Publisher         Publisher name; must equal the one in the operator's trust store entry.
.PARAMETER KeyId             Key id; must equal the one in the operator's trust store entry.
.PARAMETER PrivateKeyPath    PEM file with the publisher's private key (see New-KnowledgeSigningKey.ps1). Never inside the repository.
.PARAMETER EngineMin         Lowest bOps engine version the pack supports (default 1.3.0).
.PARAMETER EngineMax         Highest bOps engine version the pack supports (default 1.9.9).

.EXAMPLE
  pwsh scripts/Publish-KnowledgePack.ps1 -SourceDirectory docs/knowledge/examples/windows-event-triage-skills `
       -OutputRoot D:\bops\packs -PackId bsoft.windows-event-runbooks -Version 1.0.0 `
       -Publisher acme -KeyId acme-2026 -PrivateKeyPath D:\keys\acme\acme-acme-2026.private.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SourceDirectory,
    [Parameter(Mandatory)] [string] $OutputRoot,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$')] [string] $PackId,
    [Parameter(Mandatory)] [ValidatePattern('^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z.-]{1,32})?$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')] [string] $Publisher,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')] [string] $KeyId,
    [Parameter(Mandatory)] [string] $PrivateKeyPath,
    [ValidatePattern('^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$')] [string] $EngineMin = '1.3.0',
    [ValidatePattern('^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$')] [string] $EngineMax = '1.9.9'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allowedExtensions = @('.md', '.txt', '.json', '.yaml', '.yml', '.csv', '.xml')
$utf8 = [System.Text.UTF8Encoding]::new($false)
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$destination = Join-Path (Join-Path $OutputRoot $PackId) $Version

if (Test-Path -LiteralPath $destination) { throw "'$destination' already exists: a pack version is never rewritten. Publish a new version." }
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw "The private key file '$PrivateKeyPath' does not exist." }

# 1. Collect and check the payload. Anything that is not a plain-text file inside a skill directory is refused.
$files = [System.Collections.Generic.List[object]]::new()
foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force) {
    $relative = [System.IO.Path]::GetRelativePath($source, $item.FullName).Replace('\', '/')
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "'$relative' is a link or reparse point; packs carry none." }
    if ($item.PSIsContainer) { continue }
    if ($allowedExtensions -notcontains $item.Extension.ToLowerInvariant()) { throw "'$relative' has an extension packs may not carry ($($allowedExtensions -join ' '))." }
    if ($relative -notmatch '^[^/]+/.+') { throw "'$relative' is outside a skill directory; every file lives under <skill>/." }
    $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
    if ($bytes -contains 0) { throw "'$relative' is not plain text (contains a NUL byte)." }
    try { $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes) } catch { throw "'$relative' is not valid UTF-8." }
    if ($text.Contains('PRIVATE KEY-----')) { throw "'$relative' contains key material." }
    $files.Add([pscustomobject]@{ Path = $relative; Bytes = $bytes; Sha256 = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant(); Size = $bytes.Length })
}
if ($files.Count -eq 0) { throw "'$source' holds no files." }
$skills = @($files | ForEach-Object { $_.Path.Split("/")[0] } | Sort-Object -Unique)
foreach ($skill in $skills) {
    if (-not ($files | Where-Object { $_.Path -ceq "$skill/SKILL.md" })) { throw "Skill '$skill' has no SKILL.md." }
}

# 2. Order the files by ordinal path, compute the content hash exactly as bOps does, and write the manifest.
$paths = [string[]]($files | ForEach-Object { $_.Path })
[System.Array]::Sort($paths, [System.StringComparer]::Ordinal)
$ordered = @($paths | ForEach-Object { $path = $_; $files | Where-Object { $_.Path -ceq $path } })
$listing = [System.Text.StringBuilder]::new()
foreach ($file in $ordered) { $null = $listing.Append($file.Path).Append("`n").Append($file.Sha256).Append("`n") }
$contentSha256 = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($utf8.GetBytes($listing.ToString()))).ToLowerInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    packId        = $PackId
    version       = $Version
    kind          = 'Knowledge'
    engine        = [ordered]@{ min = $EngineMin; max = $EngineMax }
    publisher     = $Publisher
    keyId         = $KeyId
    contentSha256 = $contentSha256
    files         = @($ordered | ForEach-Object { [ordered]@{ path = $_.Path; sha256 = $_.Sha256; size = $_.Size } })
}
$manifestBytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 6) + "`n")

# 3. Sign the exact manifest bytes: RSA-PSS over SHA-256, the only algorithm bOps accepts.
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([System.IO.File]::ReadAllText($PrivateKeyPath))
    if ($rsa.KeySize -lt 2048) { throw 'The signing key is shorter than 2048 bits; bOps refuses such keys.' }
    $digest = [System.Security.Cryptography.SHA256]::HashData($manifestBytes)
    $signature = $rsa.SignHash($digest, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
}
finally {
    $rsa.Dispose()
}
$envelope = [ordered]@{ schemaVersion = 1; publisher = $Publisher; keyId = $KeyId; algorithm = 'RSA-PSS-SHA256'; signature = [System.Convert]::ToBase64String($signature) }

# 4. Write the pack (payload first, then the manifest and the signature), into a fresh directory.
$null = New-Item -ItemType Directory -Force -Path $destination
foreach ($file in $ordered) {
    $target = Join-Path $destination $file.Path
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)
    [System.IO.File]::WriteAllBytes($target, $file.Bytes)
}
[System.IO.File]::WriteAllBytes((Join-Path $destination 'bops-pack.json'), $manifestBytes)
[System.IO.File]::WriteAllBytes((Join-Path $destination 'bops-pack.sig.json'), $utf8.GetBytes(($envelope | ConvertTo-Json) + "`n"))

Write-Host "Published $PackId $Version ($($ordered.Count) files, $($skills.Count) skill(s)) to $destination"
Write-Host "Manifest SHA-256: $([System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant())"

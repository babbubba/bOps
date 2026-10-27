<#
.SYNOPSIS
  Builds, signs and installs the Knowledge pack the benchmark variants C and D use, outside the repository.

.DESCRIPTION
  Reuses the existing publisher tooling (scripts\New-KnowledgeSigningKey.ps1 and scripts\Publish-KnowledgePack.ps1, PowerShell 7) on the
  skills under docs\knowledge\examples\windows-event-triage-skills. Nothing is added to the repository: the private signing key, the signed
  pack, the public trust store and a build record (builds\) all go under -OutputRoot, which must not be inside a Git work tree.

  * The skills source must be committed and clean, so the pack is exactly what a commit says.
  * A pack version is never rewritten: building the same -PackId/-Version again is refused. Bump -Version after any change to the skills.
  * The private key is created once (per -Publisher/-KeyId) and kept where it is; the trust store holds only its public half.
  * Nothing here contacts a model, a server or the network.

  The printed -KnowledgePacksDirectory / -KnowledgeTrustStore / -KnowledgePackId / -KnowledgePackVersion are the values the benchmark takes.
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'bOps-MafBenchmarks\knowledge-pack'),
    [ValidatePattern('^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$')] [string] $PackId = 'bsoft.windows-event-runbooks',
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')] [string] $Version = '2.0.0',
    [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')] [string] $Publisher = 'bsoft',
    [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')] [string] $KeyId = 'bench-2026',
    [string] $RepositoryRoot,
    [switch] $AllowDirtySource
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepositoryRoot) { $RepositoryRoot = Join-Path $scriptDirectory '..\..' }
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$output = [IO.Path]::GetFullPath($OutputRoot)

if ($output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $output -eq $root) {
    throw "OutputRoot ($output) is inside the repository. Keys, packs and trust stores stay outside it."
}
for ($probe = $output; $probe; $probe = Split-Path -Parent $probe) {
    if (Test-Path -LiteralPath (Join-Path $probe '.git')) { throw "OutputRoot ($output) is inside a Git work tree ($probe)." }
}

$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if (-not $pwsh) { throw 'PowerShell 7 (pwsh) is required by the publisher scripts.' }
$sourceRelative = 'docs/knowledge/examples/windows-event-triage-skills'
$source = Join-Path $root ($sourceRelative -replace '/', '\')
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Skills source not found: $source" }

$dirty = (& git -C $root status --porcelain -- $sourceRelative) -join "`n"
if ($dirty -and -not $AllowDirtySource) { throw "The skills source has uncommitted changes; commit them first so the pack matches a commit:`n$dirty" }
$commit = (& git -C $root rev-parse HEAD).Trim()
$tree = (& git -C $root rev-parse "HEAD:$sourceRelative").Trim()

$keys = Join-Path $output 'keys'
$packs = Join-Path $output 'packs'
$trustStore = Join-Path $output 'publisher-trust.json'
$packDirectory = Join-Path $packs "$PackId\$Version"
if (Test-Path -LiteralPath $packDirectory) { throw "Pack $PackId $Version already exists at $packDirectory. A version is never rewritten: choose a new -Version." }
New-Item -ItemType Directory -Force -Path $output, $packs | Out-Null

$privateKey = Join-Path $keys "$Publisher-$KeyId.private.pem"
$publicTrust = Join-Path $keys "$Publisher-$KeyId.trust.json"
if (-not (Test-Path -LiteralPath $privateKey)) {
    & $pwsh.Source -NoProfile -File (Join-Path $root 'scripts\New-KnowledgeSigningKey.ps1') -Publisher $Publisher -KeyId $KeyId -OutputDirectory $keys
    if ($LASTEXITCODE -ne 0) { throw 'Creating the signing key failed.' }
}
& $pwsh.Source -NoProfile -File (Join-Path $root 'scripts\Publish-KnowledgePack.ps1') -SourceDirectory $source -OutputRoot $packs `
    -PackId $PackId -Version $Version -Publisher $Publisher -KeyId $KeyId -PrivateKeyPath $privateKey
if ($LASTEXITCODE -ne 0) { throw 'Publishing the pack failed.' }

# The trust store holds public keys only. Refuse to install one that mentions private material.
$trustText = Get-Content -LiteralPath $publicTrust -Raw
if ($trustText -match 'PRIVATE KEY') { throw 'The trust file contains private key material; refusing to install it.' }
[IO.File]::WriteAllText($trustStore, $trustText, [Text.UTF8Encoding]::new($false))

function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$record = [ordered]@{
    packId = $PackId; version = $Version; publisher = $Publisher; keyId = $KeyId
    sourceCommit = $commit; sourceTree = $tree; builtAtUtc = [DateTime]::UtcNow.ToString('o')
    manifestSha256 = Get-Sha256 (Join-Path $packDirectory 'bops-pack.json')
    signatureSha256 = Get-Sha256 (Join-Path $packDirectory 'bops-pack.sig.json')
    trustStoreSha256 = Get-Sha256 $trustStore
    files = (Get-ChildItem -LiteralPath $packDirectory -Recurse -File | Measure-Object).Count
}
# The build record lives beside the packs, never inside a pack directory: a file the signed manifest does not list would be refused.
$builds = Join-Path $output 'builds'
New-Item -ItemType Directory -Force -Path $builds | Out-Null
[IO.File]::WriteAllText((Join-Path $builds "$PackId-$Version.build.json"), ($record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Pack built and signed: $PackId $Version  (source commit $commit)"
Write-Host "  KnowledgePacksDirectory : $packs"
Write-Host "  KnowledgeTrustStore     : $trustStore"
Write-Host "  KnowledgePackId         : $PackId"
Write-Host "  KnowledgePackVersion    : $Version"
Write-Host "  manifest sha256         : $($record.manifestSha256)"
Write-Host "  The private key stays in $keys (never commit it, never copy it to the machine that runs bOps)."

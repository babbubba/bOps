# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
  Creates the signing key pair of a knowledge pack publisher and the public trust-store entry for it (ADR-0058).

.DESCRIPTION
  PUBLISHER TOOLING. This script is never part of the bOps runtime and the runtime never holds, reads or asks for the private key.

  It writes two things into -OutputDirectory:
    <publisher>-<keyId>.private.pem   the RSA private key (PKCS#8, 3072 bits). KEEP IT OFFLINE, outside any repository and outside the
                                      machine bOps runs on. Whoever holds it can make bOps trust any pack.
    <publisher>-<keyId>.trust.json    a complete bOps trust store holding the PUBLIC key only. This file is what the operator installs
                                      and points 'Knowledge:TrustStorePath' at.

  Use a key id that changes with every rotation (for example 'acme-2026'). Revoking a compromised key is done in the operator's trust
  store (set "revoked": true, or list the publisher in "revokedPublishers"); the next admission or resume refuses the packs it signed.

.PARAMETER Publisher
  The publisher name that packs and signatures will carry. Letters, digits, '.', '_' and '-'; at most 64 characters.

.PARAMETER KeyId
  The key id. Same character rules as the publisher.

.PARAMETER OutputDirectory
  Where the two files are written. It must not already contain files of the same names.

.EXAMPLE
  pwsh scripts/New-KnowledgeSigningKey.ps1 -Publisher acme -KeyId acme-2026 -OutputDirectory D:\keys\acme
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')] [string] $Publisher,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')] [string] $KeyId,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$privatePath = Join-Path $OutputDirectory "$Publisher-$KeyId.private.pem"
$trustPath = Join-Path $OutputDirectory "$Publisher-$KeyId.trust.json"
foreach ($path in @($privatePath, $trustPath)) {
    if (Test-Path -LiteralPath $path) { throw "'$path' already exists; refusing to overwrite a key or a trust store." }
}

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($privatePath, $rsa.ExportPkcs8PrivateKeyPem() + "`n", $utf8)
    $trust = [ordered]@{
        schemaVersion     = 1
        keys              = @([ordered]@{ publisher = $Publisher; keyId = $KeyId; publicKeyPem = $rsa.ExportSubjectPublicKeyInfoPem(); revoked = $false })
        revokedPublishers = @()
    }
    [System.IO.File]::WriteAllText($trustPath, ($trust | ConvertTo-Json -Depth 5) + "`n", $utf8)
}
finally {
    $rsa.Dispose()
}

Write-Host "Private key  : $privatePath   (keep it offline; bOps never needs it)"
Write-Host "Trust store  : $trustPath   (public key only; install it on the machines that run bOps)"

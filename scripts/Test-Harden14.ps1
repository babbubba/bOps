# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

[CmdletBinding()]
param(
    [ValidateSet('All', 'DotNet', 'Browser')]
    [string]$Surface = 'All',

    [switch]$NoBuild,

    [switch]$RequireDocker
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Harden14DotNetGate {
    if ($RequireDocker) {
        $dockerServerOs = & docker version --format '{{.Server.Os}}'
        if ($LASTEXITCODE -ne 0 -or $dockerServerOs.Trim() -ne 'linux') {
            throw 'HARDEN-14 requires a reachable Docker daemon running Linux containers on this gate.'
        }
    }

    $common = @('--configuration', 'Release', '--filter')
    if ($NoBuild) {
        $common = @('--configuration', 'Release', '--no-build', '--no-restore', '--filter')
    }

    $runtimeFilter = @(
        'FullyQualifiedName~ToolContractConstraintTests.E2E2_',
        'FullyQualifiedName~EvidenceDisclosureLoopTests.E2E3_',
        'FullyQualifiedName~ModelFailureEndToEndTests',
        'FullyQualifiedName~ResumeStateMachineTests',
        'FullyQualifiedName~ProviderWireEndToEndTests',
        'FullyQualifiedName~HardenEightE2ETests',
        'FullyQualifiedName~HardenFourteenMutationCompositionTests'
    ) -join '|'

    Invoke-Checked -Command dotnet -Arguments (@('test', 'tests/bOps.Runtime.Tests/bOps.Runtime.Tests.csproj') + $common + @($runtimeFilter))
    Invoke-Checked -Command dotnet -Arguments (@('test', 'tests/bOps.Api.Tests/bOps.Api.Tests.csproj') + $common + @(
        'FullyQualifiedName~FallbackExecutionTests.E2E4_|FullyQualifiedName~TaskResumeEndpointTests'))
    Invoke-Checked -Command dotnet -Arguments (@('test', 'tests/bOps.Architecture.Tests/bOps.Architecture.Tests.csproj') + $common + @(
        'FullyQualifiedName~HardenFourteenToolCatalogTests'))
    Invoke-Checked -Command dotnet -Arguments (@('test', 'tests/bOps.Packages.Docker.Tests/bOps.Packages.Docker.Tests.csproj') + $common + @(
        'FullyQualifiedName~HardenFourteenDockerDiagnosisTests'))
}

function Invoke-Harden14BrowserGate {
    Push-Location (Join-Path $repoRoot 'web/bops-ui')
    try {
        Invoke-Checked -Command npx -Arguments @(
            'ng', 'test', '--watch=false', '--browsers=ChromeHeadless',
            '--include=src/app/test-setup.spec.ts',
            '--include=src/app/features/dashboard/dashboard-lifecycle.spec.ts')
        Invoke-Checked -Command npx -Arguments @(
            'playwright', 'test',
            'e2e/browser-session.e2e.ts',
            'e2e/delegation-operability.e2e.ts',
            'e2e/layout-localization.e2e.ts')
    }
    finally {
        Pop-Location
    }
}

Push-Location $repoRoot
try {
    if ($Surface -in @('All', 'DotNet')) {
        Invoke-Harden14DotNetGate
    }

    if ($Surface -in @('All', 'Browser')) {
        Invoke-Harden14BrowserGate
    }
}
finally {
    Pop-Location
}

Write-Host "HARDEN-14 $Surface scenario gate passed."

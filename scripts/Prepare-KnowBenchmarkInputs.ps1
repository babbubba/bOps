# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param(
    [switch]$VerifyPackOnly,
    [string]$SettingsPath,
    [string]$ServerFactsPath,
    [string]$MetadataPath,
    [string]$ResultsPath,
    [string]$BinaryDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$BinaryDirectory) { $BinaryDirectory = Join-Path $root 'tests/bOps.AgentFramework.Tests/bin/Debug/net10.0' }
$BinaryDirectory = [IO.Path]::GetFullPath($BinaryDirectory)
foreach ($name in 'bOps.Abstractions', 'bOps.Runtime', 'bOps.Knowledge') {
    if (!(Test-Path -LiteralPath (Join-Path $BinaryDirectory "$name.dll"))) { throw "Missing existing test dependency: $name.dll" }
}
function Read-Git([string[]]$Arguments) {
    $value = & git -C $root @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Git check failed' }
    return ($value -join "`n").Trim()
}
if (!$VerifyPackOnly) {
    foreach ($path in $SettingsPath, $ServerFactsPath, $MetadataPath, $ResultsPath) {
        if (!$path -or ![IO.Path]::IsPathFullyQualified($path)) { throw 'Supply four absolute input/output paths; no defaults for operational inputs.' }
    }
    if (Read-Git @('status', '--porcelain', '--untracked-files=no')) { throw 'Commit tracked changes before preparing metadata.' }
    $commit = Read-Git @('rev-parse', 'HEAD') # AFTER all preparation-file/signing commits.
    if ($commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Full commit required' }
    if (Test-Path -LiteralPath $MetadataPath) { throw 'Metadata already exists; no overwrite' }
    if (Test-Path -LiteralPath $ResultsPath) { throw 'Results path must be NEW, not reused' }
    foreach ($path in $MetadataPath, $ResultsPath) {
        $full = [IO.Path]::GetFullPath($path)
        if ($full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $full -eq $root) {
            throw 'Keep operational artifacts outside the benchmark checkout'
        }
        if (!(Test-Path -LiteralPath (Split-Path -Parent $full) -PathType Container)) { throw 'Create the parent directory explicitly first' }
    }
}
# Temporary .NET 10 console utility, no packages or project references: only this
# helper is compiled, never the repository. SettingsStore reads NON-SECRET settings;
# neither appsettings, Vault, credentials nor the local configuration helper is opened.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('bops-input-prep-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$references = foreach ($name in 'bOps.Abstractions', 'bOps.Runtime', 'bOps.Knowledge') {
    $hint = [Security.SecurityElement]::Escape((Join-Path $BinaryDirectory "$name.dll"))
    "<Reference Include=`"$name`"><HintPath>$hint</HintPath></Reference>"
}
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />' + ($references -join '') + '</ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $scratch 'Prep.csproj'), $project)
$source = @'
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Runtime;

var root = args[0];
var delivery = Path.Combine(root, "docs/benchmarks/know-bench-01d");
var raw = await new DirectoryKnowledgePackSource(Path.Combine(delivery, "signed-pack"))
    .OpenAsync(new PackReference("acme.windows-event-runbooks", "1.0.0"), new TenantId("tenant-a"))
    ?? throw new InvalidOperationException("Signed pack missing");
var verdict = new KnowledgePackVerifier(KnowledgeTrustStore.FromFile(Path.Combine(delivery, "test-trust.json")), new Version(1, 3, 0)).Verify(raw);
if (verdict.Code != KnowledgeAdmissionCode.Admitted) throw new InvalidOperationException("Pack refused: " + verdict.Code);
var pin = KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span);
if (args.Length == 1) { Console.WriteLine(pin); return; }
var profile = new SettingsStore(args[1], TimeProvider.System).FindProviderProfile("LlamaCpp")
    ?? throw new InvalidOperationException("Non-secret LlamaCpp profile missing");
if (profile.ProviderId != "LlamaCpp" || profile.Model != "Qwen3.6-35B") throw new InvalidOperationException("Exact provider/model required");
var options = new ChatModelOptions(profile.ProviderId, profile.BaseUrl, null, profile.Model, profile.SupportsNativeToolCalling)
    { RequestTimeout = TimeSpan.FromSeconds(120) };
// EXACT anonymous projection, property order, default options and nullable TimeSpan
// from KnowledgeBenchmarkLiveTests.ExecuteAsync. No ConvertTo-Json approximation.
var bytes = JsonSerializer.SerializeToUtf8Bytes(new { options.Provider, options.BaseUrl, options.Model,
    options.SupportsNativeToolCalling, options.RequestTimeout });
var facts = JsonNode.Parse(File.ReadAllBytes(args[2]))!.AsObject();
foreach (var key in new[] { "llamaCppVersion", "modelFileSha256", "quantization", "ctxSize", "nPredict", "reasoning", "sampling",
    "backendBuildFlags", "chatTemplate", "concurrency", "loadingState", "residentModel", "requestOverrides", "evidence" })
    if (facts[key] is null) throw new InvalidOperationException("Operator fact missing: " + key);
foreach (var key in new[] { "llamaCppVersion", "quantization", "residentModel", "evidence" })
    if (string.IsNullOrWhiteSpace(facts[key]!.GetValue<string>())) throw new InvalidOperationException("Empty operator fact: " + key);
if (!System.Text.RegularExpressions.Regex.IsMatch(facts["modelFileSha256"]!.GetValue<string>(), "^[0-9a-f]{64}$") ||
    facts["ctxSize"]!.GetValue<int>() <= 0 || facts["nPredict"]!.GetValue<int>() != 8192 || !facts["reasoning"]!.GetValue<bool>() ||
    facts["sampling"] is not JsonObject sampling || sampling.Count == 0)
    throw new InvalidOperationException("Incomplete/incompatible server facts; do not substitute defaults");
var runner = new AgentRunnerOptions { ModelCallAttemptTimeout = TimeSpan.FromSeconds(90),
    ModelCallBudget = TimeSpan.FromSeconds(300), MaxAttemptDuration = TimeSpan.FromSeconds(600) };
runner.Validate(options.EffectiveRequestTimeout);
var metadata = new JsonObject {
    ["baseline"] = "Qwen8192", ["model"] = options.Model,
    ["bopsCommit"] = args[4], ["corpusSha256"] = "single-run-in-process",
    ["providerProfileSha256"] = KnowledgePackFormat.Sha256Hex(bytes),
    ["runtimeLimits"] = new JsonObject {
        ["maxSteps"] = runner.MaxSteps, ["maxLifetimeSteps"] = runner.MaxLifetimeSteps,
        ["maxTotalTokens"] = runner.MaxTotalTokens, ["maxReplans"] = runner.MaxReplans,
        ["maxLifetimeReplans"] = runner.MaxLifetimeReplans, ["requestTimeoutMs"] = 120000,
        ["attemptTimeoutMs"] = runner.ModelCallAttemptTimeout.TotalMilliseconds,
        ["totalTimeoutMs"] = runner.ModelCallBudget.TotalMilliseconds,
        ["maxAttemptDurationMs"] = runner.MaxAttemptDuration!.Value.TotalMilliseconds,
        ["maximumHttpInvocations"] = 5, ["knowledgeLimits"] = "4/4/8000",
        ["allRunnerOptions"] = JsonSerializer.SerializeToNode(runner) },
    ["retryPolicy"] = new JsonObject { ["maxAttempts"] = runner.ModelCallMaxAttempts,
        ["baseDelayMs"] = runner.ModelRetryBaseDelay.TotalMilliseconds,
        ["maxDelayMs"] = runner.ModelRetryMaxDelay.TotalMilliseconds,
        ["emptyFinalResponseRetries"] = runner.EmptyFinalResponseRetries, ["jitter"] = true },
    ["operatorFactsLocallyVerified"] = false
};
foreach (var key in new[] { "llamaCppVersion", "modelFileSha256", "quantization", "ctxSize", "nPredict", "reasoning", "sampling",
    "backendBuildFlags", "chatTemplate", "concurrency", "loadingState", "residentModel", "requestOverrides", "evidence" })
    metadata[key] = facts[key]!.DeepClone();
var encoded = JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true });
using (var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write)) output.Write(encoded);
Console.WriteLine(pin);
'@
[IO.File]::WriteAllText((Join-Path $scratch 'Program.cs'), $source)
& dotnet build (Join-Path $scratch 'Prep.csproj') --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Preparation helper compilation failed; scratch: $scratch" }
$arguments = @($root)
if (!$VerifyPackOnly) { $arguments += @($SettingsPath, $ServerFactsPath, $MetadataPath, $commit) }
$pin = & dotnet (Join-Path $scratch 'bin/Debug/net10.0/Prep.dll') @arguments
if ($LASTEXITCODE -ne 0 -or $pin -cnotmatch '^[0-9a-f]{64}$') { throw 'Preparation failed; no operational environment set' }
if ($VerifyPackOnly) { Write-Output "Verified manifest SHA-256: $pin"; return }
if ((Read-Git @('rev-parse', 'HEAD')) -cne $commit -or (Read-Git @('status', '--porcelain', '--untracked-files=no'))) {
    throw 'Checkout changed during preparation; discard metadata and prepare again'
}
# Create a NEW result root. Never put metadata inside it or create A/B ahead of export.
if (Test-Path -LiteralPath $ResultsPath) { throw 'Result root appeared during preparation; refused' }
[void](New-Item -ItemType Directory -Path $ResultsPath -ErrorAction Stop)
if (Get-ChildItem -LiteralPath $ResultsPath -Force) { throw 'Results root must be empty' }
$env:BOPS_KNOW_BENCH_METADATA = $MetadataPath
$env:BOPS_KNOW_BENCH_METADATA_SHA256 = (Get-FileHash -LiteralPath $MetadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
$env:BOPS_KNOW_BENCH_COMMIT = $commit
$env:BOPS_KNOW_BENCH_MANIFEST_SHA256 = $pin
# The grant is deliberate operator authorization, not automatic preparation.
# SETTINGS_VERIFIED is also never assigned by this script.
Remove-Item Env:BOPS_KNOW_BENCH_GRANT, Env:BOPS_KNOW_BENCH_SETTINGS_VERIFIED -ErrorAction SilentlyContinue
$env:BOPS_KNOW_BENCH_RESULTS = $ResultsPath
Write-Output "Prepared metadata SHA-256: $env:BOPS_KNOW_BENCH_METADATA_SHA256"
Write-Output "Pinned commit: $commit; verified manifest: $pin"
Write-Output 'DGX facts remain locally unverified. Explicit operator settings attestation and grant are still required.'

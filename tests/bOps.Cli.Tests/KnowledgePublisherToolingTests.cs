// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world passes to the test, which disposes it.
#pragma warning disable CA2000

using System.Diagnostics;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Cli.Tests;

/// <summary>
/// ADR-0058: the instructions an operator follows to get a real signed pack into the CLI work as written. The publisher scripts (outside
/// the runtime, holding the private key in a file of their own) create a fresh key pair and sign the example pack; the CLI then admits and serves
/// it with nothing but the public trust store the script wrote. The test needs PowerShell 7 (<c>pwsh</c>), which both CI images have;
/// where it is absent the test has nothing to run and returns.
/// </summary>
public sealed class KnowledgePublisherToolingTests
{
    [Fact]
    public async Task APackSignedByThePublisherScripts_IsAdmittedAndServedByTheCli_WithOnlyThePublicTrustStore()
    {
        var pwsh = FindPwsh();
        if (pwsh is null)
        {
            return;
        }

        var root = FindRepositoryRoot();
        using var world = new CliKnowledgeWorld();
        var keys = Path.Combine(world.Root, "keys");
        var packs = Path.Combine(world.Root, "installed-packs");

        var made = await RunAsync(pwsh, Path.Combine(root, "scripts", "New-KnowledgeSigningKey.ps1"),
            "-Publisher", "acme", "-KeyId", "acme-2027", "-OutputDirectory", keys);
        Assert.True(made.ExitCode == 0, made.Output);
        var privateKey = Path.Combine(keys, "acme-acme-2027.private.pem");
        var trustFile = Path.Combine(keys, "acme-acme-2027.trust.json");
        Assert.DoesNotContain("PRIVATE", await File.ReadAllTextAsync(trustFile), StringComparison.Ordinal);

        var published = await RunAsync(pwsh, Path.Combine(root, "scripts", "Publish-KnowledgePack.ps1"),
            "-SourceDirectory", Path.Combine(root, "docs", "knowledge", "examples", "windows-event-triage-skills"),
            "-OutputRoot", packs, "-PackId", "bsoft.windows-event-runbooks", "-Version", "1.0.0",
            "-Publisher", "acme", "-KeyId", "acme-2027", "-PrivateKeyPath", privateKey);
        Assert.True(published.ExitCode == 0, published.Output);
        Assert.True(File.Exists(Path.Combine(packs, "bsoft.windows-event-runbooks", "1.0.0", "bops-pack.sig.json")));

        // A version is never rewritten.
        var again = await RunAsync(pwsh, Path.Combine(root, "scripts", "Publish-KnowledgePack.ps1"),
            "-SourceDirectory", Path.Combine(root, "docs", "knowledge", "examples", "windows-event-triage-skills"),
            "-OutputRoot", packs, "-PackId", "bsoft.windows-event-runbooks", "-Version", "1.0.0",
            "-Publisher", "acme", "-KeyId", "acme-2027", "-PrivateKeyPath", privateKey);
        Assert.NotEqual(0, again.ExitCode);

        var settings = world.Settings();
        settings["Knowledge:PacksDirectory"] = packs;
        settings["Knowledge:TrustStorePath"] = trustFile;
        var rig = world.Runner(
        [
            CliScript.Plan("system.events"),
            CliScript.Read("k1", "read_resource", "K1.R1"),
            CliScript.Tool("c1", "system.events"),
            CliScript.Final("diagnosis"),
        ], settings);

        var state = await rig.Runner.RunAsync(CliKnowledgeWorld.Goal, CliKnowledgeWorld.Operator);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(("acme", "acme-2027", "bsoft.windows-event-runbooks"), (state.KnowledgeBinding!.Publisher, state.KnowledgeBinding.KeyId, state.KnowledgeBinding.PackId));
        var read = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal(KnowledgeReadResultCode.Success, read.ResultCode);
        Assert.Contains("Kernel-Power 41", rig.Model.Requests[2].History.Single(turn => turn.Role == ChatRole.Tool && turn.Provenance?.Origin == ContextOrigin.Knowledge).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePublisherScript_RefusesWhatBopsWouldRefuse_BeforeItSignsAnything()
    {
        var pwsh = FindPwsh();
        if (pwsh is null)
        {
            return;
        }

        var root = FindRepositoryRoot();
        using var world = new CliKnowledgeWorld();
        var keys = Path.Combine(world.Root, "keys");
        Assert.Equal(0, (await RunAsync(pwsh, Path.Combine(root, "scripts", "New-KnowledgeSigningKey.ps1"),
            "-Publisher", "acme", "-KeyId", "acme-2027", "-OutputDirectory", keys)).ExitCode);

        var source = Path.Combine(world.Root, "source");
        Directory.CreateDirectory(Path.Combine(source, "triage"));
        await File.WriteAllTextAsync(Path.Combine(source, "triage", "SKILL.md"), "---\nname: triage\ndescription: x\n---\nbody\n");
        await File.WriteAllTextAsync(Path.Combine(source, "triage", "run.ps1"), "Write-Host hello");

        var result = await RunAsync(pwsh, Path.Combine(root, "scripts", "Publish-KnowledgePack.ps1"),
            "-SourceDirectory", source, "-OutputRoot", Path.Combine(world.Root, "out"), "-PackId", "bsoft.x", "-Version", "1.0.0",
            "-Publisher", "acme", "-KeyId", "acme-2027", "-PrivateKeyPath", Path.Combine(keys, "acme-acme-2027.private.pem"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("extension packs may not carry", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(world.Root, "out")));
    }

    private static string? FindPwsh()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (bOps.slnx) was not found.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string pwsh, string script, params string[] args)
    {
        var start = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script }.Concat(args))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
}

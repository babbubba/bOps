// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.AccessControl;
using bOps.Abstractions;
using bOps.Policy;
using bOps.Runtime;

namespace bOps.Cli.Tests;

/// <summary>
/// ADR-0044 sections 10–12: <c>bops delegate readiness</c>, <c>profiles init --read-only</c> and <c>profiles check</c> — output,
/// accepted exit codes, the write guards (no silent overwrite, generator-equivalent <c>--overwrite</c> only, the re-check
/// immediately before replacement, permissions kept), the resolved path printed and an existing file's contents never printed.
/// The clock is a fixed <see cref="TimeProvider"/>.
/// </summary>
public sealed class DelegateSetupCommandTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private const string ExistingMarker = "operator-private-marker-7f3a";

    private readonly string _dir = Directory.CreateTempSubdirectory("bops-cli-setup-").FullName;

    private string PolicyPath => Path.Combine(_dir, "policy.yaml");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is not a test failure.
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ToolManifest Manifest(string name, RiskLevel risk = RiskLevel.Read) => new()
    {
        Name = name, Description = "x", Risk = risk, Platforms = [CurrentPlatform.Id], Requires = [], Parameters = [],
        Verification = risk == RiskLevel.Read ? null : new VerificationSpec("system.cpu", [], "x"),
    };

    private static readonly IReadOnlyList<ToolManifest> Tools = [Manifest("system.cpu"), Manifest("system.memory"), Manifest("service.restart", RiskLevel.High)];

    private CliPolicy Load()
    {
        if (!File.Exists(PolicyPath))
        {
            return new CliPolicy(PolicyConfig.SafeDefault, PolicyLoadState.NoFile, PolicyPath, null);
        }

        try
        {
            return new CliPolicy(PolicyConfigLoader.Load(File.ReadAllText(PolicyPath)), PolicyLoadState.Loaded, PolicyPath, null);
        }
        catch (PolicyConfigurationException ex)
        {
            return new CliPolicy(PolicyConfig.AllForbidden, PolicyLoadState.LoadFailed, PolicyPath, ex.Message);
        }
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args, IReadOnlyList<ToolManifest>? tools = null, Action? beforeRecheck = null)
    {
        var (invocation, parseError) = DelegateArguments.Parse(args);
        Assert.True(invocation is not null, parseError);
        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DelegateSetupCommand(Load(), tools ?? Tools, new FixedTime(T0), output, error) { BeforeOverwriteRecheck = beforeRecheck };
        var code = await command.RunAsync(invocation!);
        return (code, output.ToString(), error.ToString());
    }

    private static string Generated(params string[] tools) =>
        ReadOnlyProfileGenerator.Generate([.. tools.Select(name => Manifest(name))], new FixedTime(T0)).Document;

    // ---- readiness ----

    [Fact]
    public async Task Readiness_OnAFreshInstall_IsNotReady_PrintsFourRolesThePolicyPathAndExitsTwo()
    {
        var (code, output, _) = await RunAsync(["readiness"]);

        Assert.Equal(2, code);
        Assert.Contains("Discovery: missing [Profile] — Discovery role: no usable profile is configured.", output, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: missing [Profile]", output, StringComparison.Ordinal);
        Assert.Contains("Remediation: not required", output, StringComparison.Ordinal);
        Assert.Contains("Verification: not required", output, StringComparison.Ordinal);
        Assert.Contains("Ready: no", output, StringComparison.Ordinal);
        Assert.Contains($"Policy: {PolicyPath} (noFile)", output, StringComparison.Ordinal);
        Assert.Contains("Profile drift: 0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_AfterInit_IsReadyForADiagnosis_ExitZero_AndNotForARemediation_ExitTwo()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu", "system.memory"));

        var diagnosis = await RunAsync(["readiness"]);
        var remediation = await RunAsync(["readiness", "--remediation"]);

        Assert.Equal(0, diagnosis.Code);
        Assert.Contains("Discovery: ready", diagnosis.Output, StringComparison.Ordinal);
        Assert.Contains("Ready: yes", diagnosis.Output, StringComparison.Ordinal);
        Assert.Contains("(loaded)", diagnosis.Output, StringComparison.Ordinal);
        Assert.Equal(2, remediation.Code);
        Assert.Contains("Remediation: missing [Profile]", remediation.Output, StringComparison.Ordinal);
        Assert.Contains("validated when you submit", remediation.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_WithAPolicyThatFailsToLoad_ShowsNotUsable_AndTheLoadersMessageLocally()
    {
        await File.WriteAllTextAsync(PolicyPath, "delegation:\n  roles:\n    discovery:\n      maxRisk: nonsense\n");

        var (code, output, _) = await RunAsync(["readiness"]);

        Assert.Equal(2, code);
        Assert.Contains("Discovery: not usable [Profile]", output, StringComparison.Ordinal);
        Assert.Contains("(loadFailed)", output, StringComparison.Ordinal);
        Assert.Contains("policy.yaml (line", output, StringComparison.Ordinal);
    }

    // ---- profiles init --read-only ----

    [Fact]
    public async Task Init_PrintsTheGeneratedFileForReview_WritesNothing_AndNamesThePath()
    {
        var (code, output, error) = await RunAsync(["profiles", "init", "--read-only"]);

        Assert.Equal(0, code);
        Assert.False(File.Exists(PolicyPath));
        Assert.Equal(Generated("system.cpu", "system.memory"), output);
        Assert.Contains($"Policy file: {PolicyPath}", error, StringComparison.Ordinal);
        Assert.Contains("Read tools listed: 2", error, StringComparison.Ordinal);
        Assert.Contains("Review it, then run again with --write.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("service.restart", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Write_CreatesTheWholeFile_WhichLoadsAsTheGeneratedProfiles()
    {
        var (code, output, _) = await RunAsync(["profiles", "init", "--read-only", "--write"]);

        Assert.Equal(0, code);
        Assert.Contains($"Wrote {PolicyPath}", output, StringComparison.Ordinal);
        Assert.Contains("restart bOps.Api", output, StringComparison.Ordinal);
        Assert.Equal(Generated("system.cpu", "system.memory"), await File.ReadAllTextAsync(PolicyPath));
        Assert.DoesNotContain(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories), file => file != PolicyPath);
    }

    [Fact]
    public async Task Init_Write_OverAnExistingFile_IsRefused_LeavesItUntouched_PrintsTheFragment_AndNeverTheFile()
    {
        var existing = $"# {ExistingMarker}\ndefaults:\n  read: automatic\n";
        await File.WriteAllTextAsync(PolicyPath, existing);

        var (code, output, error) = await RunAsync(["profiles", "init", "--read-only", "--write"]);

        Assert.Equal(1, code);
        Assert.Equal(existing, await File.ReadAllTextAsync(PolicyPath));
        Assert.Contains("never overwritten silently", error, StringComparison.Ordinal);
        Assert.StartsWith("delegation:", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ExistingMarker, output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Overwrite_RefreshesTheToolsOfAPreviouslyGeneratedFile()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu"));

        var (code, output, _) = await RunAsync(["profiles", "init", "--read-only", "--write", "--overwrite"]);

        Assert.Equal(0, code);
        Assert.Contains("Comments in the replaced file were not kept", output, StringComparison.Ordinal);
        Assert.Equal(Generated("system.cpu", "system.memory"), await File.ReadAllTextAsync(PolicyPath));
    }

    [Theory]
    [InlineData("maxSteps: 15", "maxSteps: 3", "condition 4")]
    [InlineData("medium: approval", "medium: automatic", "condition 2")]
    [InlineData("  roles:\n", "  roles:\n    remediation:\n      skills: [s]\n      capabilities: [c]\n      tools: [\"service.restart\"]\n      maxRisk: high\n      maxBlastRadius: single\n      targets: [local]\n      environments: [local]\n      maxSteps: 1\n      maxDuration: 00:01:00\n", "condition 3")]
    public async Task Init_Overwrite_OfAFileThatIsNotGeneratorEquivalent_IsRefused_AndLeavesItUntouched(string from, string to, string condition)
    {
        var existing = Generated("system.cpu").Replace(from, to, StringComparison.Ordinal) + $"# {ExistingMarker}\n";
        await File.WriteAllTextAsync(PolicyPath, existing);

        var (code, output, error) = await RunAsync(["profiles", "init", "--read-only", "--write", "--overwrite"]);

        Assert.Equal(1, code);
        Assert.Contains(condition, error, StringComparison.Ordinal);
        Assert.Equal(existing, await File.ReadAllTextAsync(PolicyPath));
        Assert.DoesNotContain(ExistingMarker, output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Overwrite_OfAFileChangedBetweenTheCheckAndTheReplacement_IsRefused()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu"));
        var changed = Generated("system.cpu").Replace("maxSteps: 15", "maxSteps: 2", StringComparison.Ordinal);

        var (code, _, error) = await RunAsync(
            ["profiles", "init", "--read-only", "--write", "--overwrite"], beforeRecheck: () => File.WriteAllText(PolicyPath, changed));

        Assert.Equal(1, code);
        Assert.Contains("condition 5", error, StringComparison.Ordinal);
        Assert.Equal(changed, await File.ReadAllTextAsync(PolicyPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Init_Overwrite_KeepsTheExistingFilesRestrictivePermissions()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu"));
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(PolicyPath);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                security.RemoveAccessRule(rule);
            }

            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
            var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            var (code, _, _) = await RunAsync(["profiles", "init", "--read-only", "--write", "--overwrite"]);

            Assert.Equal(0, code);
            Assert.Equal(before, new FileInfo(PolicyPath).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        else
        {
            File.SetUnixFileMode(PolicyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var (code, _, _) = await RunAsync(["profiles", "init", "--read-only", "--write", "--overwrite"]);

            Assert.Equal(0, code);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PolicyPath));
        }

        Assert.Equal(Generated("system.cpu", "system.memory"), await File.ReadAllTextAsync(PolicyPath));
    }

    [Fact]
    public async Task Init_WithNoReadToolAvailable_Refuses_AndWritesNothing()
    {
        var (code, output, error) = await RunAsync(["profiles", "init", "--read-only", "--write"], tools: [Manifest("service.restart", RiskLevel.High)]);

        Assert.Equal(1, code);
        Assert.Empty(output);
        Assert.Contains("No Read tool", error, StringComparison.Ordinal);
        Assert.False(File.Exists(PolicyPath));
    }

    [Fact]
    public async Task Init_WithAToolNameTheContractCannotRepresent_FailsNamingIt_AndWritesNothing()
    {
        var (code, output, error) = await RunAsync(["profiles", "init", "--read-only", "--write"], tools: [Manifest("system.cpu"), Manifest("odd*name")]);

        Assert.Equal(1, code);
        Assert.Empty(output);
        Assert.Contains("odd*name", error, StringComparison.Ordinal);
        Assert.False(File.Exists(PolicyPath));
    }

    // ---- profiles check ----

    [Fact]
    public async Task Check_OfAFreshGeneratedFile_HasNoDrift_AndExitsZero()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu", "system.memory"));

        var (code, output, _) = await RunAsync(["profiles", "check"]);

        Assert.Equal(0, code);
        Assert.StartsWith($"Policy: {PolicyPath} (loaded)", output, StringComparison.Ordinal);
        Assert.Contains("Profile drift: 0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ANewReadToolIsInformationalDrift_ExitEight()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu"));

        var (code, output, _) = await RunAsync(["profiles", "check"]);

        Assert.Equal(8, code);
        Assert.Contains("Discovery: read_tool_not_granted system.memory", output, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: read_tool_not_granted system.memory", output, StringComparison.Ordinal);
        Assert.Contains("Profile drift: 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_AnUnusableProfileBlocks_ExitTwo()
    {
        await File.WriteAllTextAsync(PolicyPath, Generated("system.cpu", "system.memory").Replace("maxTokens: 150000", "maxTokens: 0", StringComparison.Ordinal));

        var (code, output, _) = await RunAsync(["profiles", "check"]);

        Assert.Equal(2, code);
        Assert.Contains("Discovery: unusable_profile Tokens", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_APolicyThatFailsToLoadBlocks_ExitTwo_AndSaysWhyLocally()
    {
        await File.WriteAllTextAsync(PolicyPath, "delegation: [broken");

        var (code, output, _) = await RunAsync(["profiles", "check"]);

        Assert.Equal(2, code);
        Assert.Contains("(loadFailed)", output, StringComparison.Ordinal);
        Assert.Contains("(policy): policy_load_failed", output, StringComparison.Ordinal);
        Assert.Contains("Profile drift: 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WithNoFile_HasNoDrift_BecauseNoProfileIsReadinessNotDrift()
    {
        var (code, output, _) = await RunAsync(["profiles", "check"]);

        Assert.Equal(0, code);
        Assert.Contains("(noFile)", output, StringComparison.Ordinal);
    }
}

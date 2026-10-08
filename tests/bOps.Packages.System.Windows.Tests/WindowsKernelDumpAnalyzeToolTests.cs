// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>ADR-0048 §8: orchestration, outcome classification and bounds of the Windows <c>system.dump_analyze</c>.</summary>
public sealed class WindowsKernelDumpAnalyzeToolTests : IDisposable
{
    private const string Nonce = KernelDumpFixtures.Nonce;
    private readonly FakeSystemRoot roots = new();
    private readonly List<ProcessStartInfo> launches = [];

    public void Dispose() => roots.Dispose();

    [Fact]
    public void Manifest_IsTheReadOnlyWindowsOnlyCapabilityGatedSinglePathContract()
    {
        var manifest = new WindowsKernelDumpAnalyzeTool().Manifest;

        Assert.Equal("system.dump_analyze", manifest.Name);
        Assert.Equal(RiskLevel.Read, manifest.Risk);
        Assert.Equal(["windows"], manifest.Platforms);
        Assert.Equal([WindowsDebuggerCapabilities.KernelDumpAnalysis], manifest.Requires);
        Assert.Equal("windows.debugger.kd", WindowsDebuggerCapabilities.KernelDumpAnalysis);
        Assert.Null(manifest.Verification);
        var parameter = Assert.Single(manifest.Parameters);
        Assert.Equal(("path", ToolParameterType.Path, true), (parameter.Name, parameter.Type, parameter.Required));
        Assert.Equal((7, 260), (parameter.MinLength, parameter.MaxLength));
        Assert.Contains("never that the named module caused it", manifest.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAnalysis_RunsDumpChkThenKdOnce_WithTheFixedArguments()
    {
        var dump = roots.Create("Minidump", "100626-20984-01.dmp");
        var tool = Tool(Installation(dumpChk: true), Kd(KernelDumpFixtures.Load("normal-complete")), dumpChkExit: 0);

        var result = await tool.ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
        Assert.Equal(("complete", true, false), (json["status"]!.GetValue<string>(), json["complete"]!.GetValue<bool>(), json["truncated"]!.GetValue<bool>()));
        Assert.Null(json["failure"]);
        Assert.Equal(2, launches.Count);
        Assert.Equal(@"C:\Debuggers\dumpchk.exe", launches[0].FileName);
        Assert.Equal([dump], launches[0].ArgumentList);
        Assert.Equal(@"C:\Debuggers\kd.exe", launches[1].FileName);
        Assert.Equal(KdCommandScript.Arguments(dump, tool.SymbolPath, Nonce), launches[1].ArgumentList);
        Assert.All(launches, launch => Assert.False(launch.UseShellExecute));
        Assert.Equal("kernel-small", json["dump"]!["kind"]!.GetValue<string>());
        Assert.Equal("100626-20984-01.dmp", json["dump"]!["fileName"]!.GetValue<string>());
        Assert.Equal(4, json["dump"]!["sizeBytes"]!.GetValue<long>());
        Assert.True(json["debugger"]!["dumpCheckPassed"]!.GetValue<bool>());
        Assert.Equal("0x9f", json["bugcheck"]!["code"]!.GetValue<string>());
        Assert.Equal("debugger-attribution", json["analysis"]!["qualifier"]!.GetValue<string>());
        Assert.Equal("loaded", json["symbols"]!["status"]!.GetValue<string>());
        Assert.True(json["blackbox"]!["pnp"]!["available"]!.GetValue<bool>());
        Assert.Equal(DumpAnalysisFormatting.Interpretation, json["interpretation"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(DataRoot, "DebuggerWork", Nonce)), "the per-run working directory must be removed");
        Assert.StartsWith("srv*" + Path.Combine(DataRoot, "Symbols") + "*https://msdl.microsoft.com/download/symbols", tool.SymbolPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bugcheck0x1E_WithUnresolvedThirdPartySymbols_IsPartialNotInvalid()
    {
        var dump = roots.Create("Minidump", "SYNTHETIC-02.dmp");

        var result = await Tool(Installation(dumpChk: false), Kd(KernelDumpFixtures.Load("bugcheck-1e-third-party"))).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Equal("0x1e", json["bugcheck"]!["code"]!.GetValue<string>());
        Assert.Equal("KMODE_EXCEPTION_NOT_HANDLED", json["bugcheck"]!["name"]!.GetValue<string>());
        Assert.Equal("synthdrv", json["analysis"]!["moduleName"]!.GetValue<string>());
        var warnings = Warnings(json);
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningDumpChkUnavailable, warnings);
        Assert.Contains(KdAnalysisParser.WarningSymbolsPartial, warnings);
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningSmallDump, warnings);
        Assert.False(json["blackbox"]!["pnp"]!["available"]!.GetValue<bool>());
        Assert.False(json["debugger"]!["dumpCheckUsed"]!.GetValue<bool>());
        Assert.Single(launches);
    }

    [Fact]
    public async Task DumpChkAbsent_DoesNotHideOrBlockTheAnalysis()
    {
        var dump = roots.Create("Minidump", "a.dmp");

        var result = await Tool(Installation(dumpChk: false), Kd(KernelDumpFixtures.Load("normal-complete"))).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningDumpChkUnavailable, Warnings(json));
        Assert.Null(json["debugger"]!["dumpCheckPassed"]);
    }

    [Fact]
    public async Task DumpChkFailure_WithNoDebuggerEvidence_IsInvalid()
    {
        var dump = roots.Create("Minidump", "corrupt.dmp");

        var result = await Tool(Installation(dumpChk: true), Kd("Loading Dump File failed\n", exitCode: 1), dumpChkExit: 1).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal(("invalid", "invalid-dump"), (json["status"]!.GetValue<string>(), json["failure"]!.GetValue<string>()));
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningDumpChkProblem, Warnings(json));
    }

    [Fact]
    public async Task DumpChkFailure_WithDebuggerEvidence_IsPartialAndSaysSo()
    {
        var dump = roots.Create("Minidump", "odd.dmp");

        var result = await Tool(Installation(dumpChk: true), Kd(KernelDumpFixtures.Load("normal-complete")), dumpChkExit: 1).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.False(json["debugger"]!["dumpCheckPassed"]!.GetValue<bool>());
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningDumpChkProblem, Warnings(json));
    }

    [Fact]
    public async Task KdNonZeroExitWithoutEvidence_IsDebuggerFailure()
    {
        var dump = roots.Create("Minidump", "a.dmp");

        var result = await Tool(Installation(dumpChk: false), Kd("Microsoft (R) Windows Debugger\nCould not open dump file\n", exitCode: 1)).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal(("unavailable", "debugger-failure"), (json["status"]!.GetValue<string>(), json["failure"]!.GetValue<string>()));
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningKdExitCode, Warnings(json));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task KdTimeoutWithPartialOutput_IsPartialWithTimeoutFailure()
    {
        var dump = roots.Create("Minidump", "a.dmp");

        var result = await Tool(Installation(dumpChk: false), Kd(KernelDumpFixtures.Load("malformed-incomplete"), timedOut: true)).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.True(result.Succeeded);
        Assert.Equal(("partial", "timeout"), (json["status"]!.GetValue<string>(), json["failure"]!.GetValue<string>()));
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningKdTimeout, Warnings(json));
        Assert.Contains("section-incomplete-analyze", Warnings(json));
    }

    [Fact]
    public async Task KdTimeoutWithNoOutput_IsATypedTimeoutOutcome()
    {
        var dump = roots.Create("Minidump", "a.dmp");

        var result = await Tool(Installation(dumpChk: false), Kd(string.Empty, timedOut: true)).ExecuteAsync(Arguments(dump));

        Assert.Equal(ToolOutcome.Timeout, result.Outcome);
        Assert.Equal(ToolFailureKind.Timeout, result.FailureKind);
        Assert.Equal("timeout", Json(result)["failure"]!.GetValue<string>());
    }

    [Fact]
    public async Task KdThatCannotStart_IsUnavailableDebuggerFailure()
    {
        var dump = roots.Create("Minidump", "a.dmp");

        var result = await Tool(Installation(dumpChk: false), (_, _, _, _) => Task.FromResult(DebuggerProcessResult.NotStarted)).ExecuteAsync(Arguments(dump));

        var json = Json(result);
        Assert.Equal(("unavailable", "debugger-failure"), (json["status"]!.GetValue<string>(), json["failure"]!.GetValue<string>()));
        Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningKdNotStarted, Warnings(json));
    }

    [Fact]
    public async Task NoDebuggerInstalled_IsUnavailable_AndNothingIsLaunched()
    {
        var dump = roots.Create("Minidump", "a.dmp");

        var result = await Tool(null, Kd("unused")).ExecuteAsync(Arguments(dump));

        Assert.Equal("debugger-unavailable", Json(result)["failure"]!.GetValue<string>());
        Assert.Empty(launches);
    }

    [Fact]
    public async Task MissingDump_IsNotFound_AndNothingIsLaunched()
    {
        var result = await Tool(Installation(dumpChk: true), Kd("unused")).ExecuteAsync(Arguments(Path.Combine(roots.Root, "Minidump", "gone.dmp")));

        var json = Json(result);
        Assert.Equal(("unavailable", "not-found"), (json["status"]!.GetValue<string>(), json["failure"]!.GetValue<string>()));
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
        Assert.Empty(launches);
    }

    [WindowsOnlyFact]
    public async Task UnreadableDump_IsAccessDenied_NeverNotFound()
    {
        var dump = roots.Create("Minidump", "denied.dmp");
        var info = new FileInfo(dump);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            var result = await Tool(Installation(dumpChk: true), Kd("unused")).ExecuteAsync(Arguments(dump));

            var json = Json(result);
            Assert.Equal(("unavailable", "access-denied"), (json["status"]!.GetValue<string>(), json["failure"]!.GetValue<string>()));
            Assert.Contains(WindowsKernelDumpAnalyzeTool.WarningAccessDenied, Warnings(json));
            Assert.Empty(launches);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    [Theory]
    [InlineData(@"relative\a.dmp")]
    [InlineData(@"\\server\share\a.dmp")]
    [InlineData(@"C:\Windows\Minidump\a.dmp"" -c "".shell calc")]
    [InlineData(@"C:\Windows\Minidump\a.dmp;!analyze -v")]
    [InlineData(@"C:\Windows\System32\config\SAM.dmp")]
    public async Task RejectedPaths_AreValidationFailures_AndNothingIsLaunched(string path)
    {
        var result = await Tool(Installation(dumpChk: true), Kd("unused")).ExecuteAsync(Arguments(path));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Contains("approved local kernel-dump location", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task UnknownArguments_AreValidationFailures()
    {
        var result = await Tool(Installation(dumpChk: true), Kd("unused"))
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["path"] = @"C:\Windows\Minidump\a.dmp", ["command"] = "db 0" }));

        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task HugeDebuggerOutput_StaysWithinTheOutputBound()
    {
        var dump = roots.Create("Minidump", "big.dmp");
        var frames = string.Join('\n', Enumerable.Range(0, 500).Select(index =>
            $"ffffb801`2a3f{index:x4} fffff807`4a3c{index:x4}     : 00000000`00000000 00000000`00000000 00000000`00000000 00000000`00000000 : drv{index}!{new string('F', 400)}+0x{index:x}"));
        var pnp = string.Join('\n', Enumerable.Range(0, 500).Select(index => $"DeviceId : USB\\VID_{index:x4}&PID_0000\\{new string('S', 300)}"));
        var output = KernelDumpFixtures.Load("normal-complete")
            .Replace("ffffb801`2a3f6e58 fffff807`4a3c8e2e     : 00000000`0000009f", frames + "\nffffb801`2a3f6e58 fffff807`4a3c8e2e     : 00000000`0000009f", StringComparison.Ordinal)
            .Replace("PnpActivityId", pnp + "\nPnpActivityId", StringComparison.Ordinal);

        var result = await Tool(Installation(dumpChk: false), Kd(output)).ExecuteAsync(Arguments(dump));

        Assert.True(global::System.Text.Encoding.UTF8.GetByteCount(result.Output!) <= DumpAnalysisLimits.OutputBytes);
        var json = Json(result);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Contains(DumpAnalysisFormatting.TruncatedWarning, Warnings(json));
        Assert.True(json["stack"]!.AsArray().Count <= DumpAnalysisLimits.MaximumStackFrames);
        Assert.True(json["modules"]!.AsArray().Count <= DumpAnalysisLimits.MaximumModules);
    }

    [Fact]
    public async Task AuditSummary_IsAggregateOnly()
    {
        var dump = roots.Create("Minidump", "private-name.dmp");
        var tool = Tool(Installation(dumpChk: true), Kd(KernelDumpFixtures.Load("normal-complete")));
        var result = await tool.ExecuteAsync(Arguments(dump));

        var summary = tool.CreateAuditSummary(Arguments(dump), result)!.ToJsonString();

        Assert.Contains("\"bugcheckCode\":\"0x9f\"", summary, StringComparison.Ordinal);
        Assert.Contains("\"symbolsStatus\":\"loaded\"", summary, StringComparison.Ordinal);
        Assert.Contains("\"pnpBlackboxAvailable\":true", summary, StringComparison.Ordinal);
        foreach (var leaked in new[] { "private-name", "Minidump", "USBXHCI", "KeBugCheckEx", "SYNTHETIC0001", "11111111-2222" })
        {
            Assert.DoesNotContain(leaked, summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string DataRoot => Path.Combine(Path.GetDirectoryName(roots.Root)!, "data");

    private WindowsKernelDumpAnalyzeTool Tool(
        DebuggerInstallation? installation,
        Func<ProcessStartInfo, TimeSpan, int, CancellationToken, Task<DebuggerProcessResult>> kd,
        int dumpChkExit = 0) =>
        new(roots.Policy, () => installation, (startInfo, timeout, maximum, ct) =>
        {
            launches.Add(startInfo);
            return startInfo.FileName.EndsWith("dumpchk.exe", StringComparison.Ordinal)
                ? Task.FromResult(new DebuggerProcessResult(true, false, dumpChkExit, "dumpchk output", false, string.Empty))
                : kd(startInfo, timeout, maximum, ct);
        }, () => Nonce, DataRoot);

    private static DebuggerInstallation Installation(bool dumpChk) =>
        new(@"C:\Debuggers\kd.exe", dumpChk ? @"C:\Debuggers\dumpchk.exe" : null, "10.0.99999.1");

    private static Func<ProcessStartInfo, TimeSpan, int, CancellationToken, Task<DebuggerProcessResult>> Kd(string stdout, int exitCode = 0, bool timedOut = false) =>
        (_, _, _, _) => Task.FromResult(new DebuggerProcessResult(true, timedOut, timedOut ? null : exitCode, stdout, false, string.Empty));

    private static ToolArguments Arguments(string path) => ToolArguments.FromJson(new JsonObject { ["path"] = path });

    private static JsonObject Json(ToolCallResult result) => JsonNode.Parse(result.Output!)!.AsObject();

    private static string[] Warnings(JsonObject json) => json["warnings"]!.AsArray().Select(warning => warning!.GetValue<string>()).ToArray();
}

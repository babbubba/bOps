// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>ADR-0048 §2, §7-§8: the shared <c>system.dump_analyze</c> contract, output shape and bounds.</summary>
public sealed class DumpAnalysisContractTests
{
    [Fact]
    public void Manifest_HasExactlyOneBoundedPathParameter_AndTheGivenCapability()
    {
        var manifest = SystemToolManifests.DumpAnalyze("windows", "some.capability", "some.optional");

        Assert.Equal("system.dump_analyze", manifest.Name);
        Assert.Equal(RiskLevel.Read, manifest.Risk);
        Assert.Equal(["windows"], manifest.Platforms);
        Assert.Equal(["some.capability"], manifest.Requires);
        Assert.Equal(["some.optional"], manifest.OptionalRequires);
        var parameter = Assert.Single(manifest.Parameters);
        Assert.Equal(("path", ToolParameterType.Path, true, 7, 260), (parameter.Name, parameter.Type, parameter.Required, parameter.MinLength, parameter.MaxLength));
        foreach (var forbidden in new[] { "command", "symbol", "script", "timeout", "format", "extension" })
        {
            Assert.DoesNotContain(manifest.Parameters, candidate => candidate.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"path":""}""")]
    [InlineData("""{"path":"   "}""")]
    [InlineData("""{"path":42}""")]
    [InlineData("""{"path":"C:\\a.dmp","symbols":"srv*http://evil"}""")]
    [InlineData("""{"path":"C:\\a.dmp","command":"db 0"}""")]
    [InlineData("""{"path":"C:\\x"}""")]
    public void Arguments_RejectAnythingButOnePlausiblePath(string json)
    {
        Assert.False(DumpAnalysisArguments.TryRead(ToolArguments.FromJson(JsonNode.Parse(json)!.AsObject()), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Arguments_RejectAnOverlongPath()
    {
        var path = @"C:\Windows\Minidump\" + new string('a', 300) + ".dmp";

        Assert.False(DumpAnalysisArguments.TryRead(ToolArguments.FromJson(new JsonObject { ["path"] = path }), out _, out _));
    }

    [Fact]
    public void Format_DistinguishesNullFromEmpty_AndLeadsWithTheSchemaVersion()
    {
        var output = DumpAnalysisFormatting.Format(new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Unavailable,
            Failure = DumpAnalysisFailure.AccessDenied,
            Dump = new DumpFileDescription(@"C:\Windows\Minidump\a.dmp", "a.dmp", null, null, "kernel-small"),
            Warnings = ["access-denied-elevation-may-be-required"],
        });

        Assert.StartsWith("{\"schemaVersion\":1,", output, StringComparison.Ordinal);
        var json = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(("unavailable", false, "access-denied"), (json["status"]!.GetValue<string>(), json["complete"]!.GetValue<bool>(), json["failure"]!.GetValue<string>()));
        Assert.Null(json["bugcheck"]);
        Assert.Null(json["analysis"]);
        Assert.Null(json["symbols"]);
        Assert.Null(json["blackbox"]);
        Assert.Null(json["dump"]!["sizeBytes"]);
        Assert.Empty(json["stack"]!.AsArray());
        Assert.Empty(json["modules"]!.AsArray());
        Assert.Equal(DumpAnalysisFormatting.Interpretation, json["interpretation"]!.GetValue<string>());
    }

    [Fact]
    public void Format_KeepsBugcheckParametersPositional()
    {
        var output = DumpAnalysisFormatting.Format(new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Partial,
            Bugcheck = new DumpBugcheck("0x1e", "KMODE_EXCEPTION_NOT_HANDLED", ["0xc0000005", null, "0x0", null]),
        });

        var parameters = JsonNode.Parse(output)!["bugcheck"]!["parameters"]!.AsArray();
        Assert.Equal(4, parameters.Count);
        Assert.Null(parameters[1]);
        Assert.Equal("0x0", parameters[2]!.GetValue<string>());
    }

    [Fact]
    public void Format_CutsListsAndStrings_AndAReportThatWasCompleteBecomesPartial()
    {
        var report = new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Complete,
            Attribution = new DumpDebuggerAttribution(null, new string('m', 10_000), null, null, null, null, null, null),
            Stack = Enumerable.Range(0, 200).Select(index => new DumpStackFrame(index, "nt", "F" + index, "0x1", "0x2")).ToArray(),
            Modules = Enumerable.Range(0, 100).Select(index => new DumpModule("m" + index, null, null, null, index == 0)).ToArray(),
            Warnings = Enumerable.Range(0, 100).Select(index => "w" + index).ToArray(),
            RawAnalysisExcerpt = new string('r', 10_000),
        };

        var json = JsonNode.Parse(DumpAnalysisFormatting.Format(report))!.AsObject();

        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(("partial", false), (json["status"]!.GetValue<string>(), json["complete"]!.GetValue<bool>()));
        Assert.True(json["stack"]!.AsArray().Count <= DumpAnalysisLimits.MaximumStackFrames);
        Assert.True(json["modules"]!.AsArray().Count <= DumpAnalysisLimits.MaximumModules);
        Assert.True(json["warnings"]!.AsArray().Count <= DumpAnalysisLimits.MaximumWarnings + 1);
        Assert.Equal(DumpAnalysisLimits.TextCharacters, json["analysis"]!["moduleName"]!.GetValue<string>().Length);
        Assert.True((json["rawAnalysisExcerpt"]?.GetValue<string>().Length ?? 0) <= DumpAnalysisLimits.RawExcerptCharacters);
        Assert.Contains(DumpAnalysisFormatting.TruncatedWarning, json["warnings"]!.AsArray().Select(warning => warning!.GetValue<string>()));
    }

    [Fact]
    public void Format_DropsDataInTheFixedOrderUntilItFits_AndKeepsTheImplicatedModule()
    {
        var report = new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Complete,
            Stack = Enumerable.Range(0, 64).Select(index => new DumpStackFrame(index, "nt", new string('S', 500), "0x1", "0x2")).ToArray(),
            Modules = Enumerable.Range(0, 32).Select(index => new DumpModule(new string('M', 400) + index, null, null, null, index == 0)).ToArray(),
            PnpBlackbox = new DumpBlackbox(true, ["USB\\VID_0000"], Enumerable.Range(0, 64).Select(index => new string('L', 250)).ToArray()),
            RawAnalysisExcerpt = new string('r', 4_000),
        };

        var output = DumpAnalysisFormatting.Format(report);

        Assert.True(Encoding.UTF8.GetByteCount(output) <= DumpAnalysisLimits.OutputBytes);
        var json = JsonNode.Parse(output)!.AsObject();
        Assert.Null(json["rawAnalysisExcerpt"]);
        Assert.Empty(json["blackbox"]!["pnp"]!["lines"]!.AsArray());
        Assert.True(json["modules"]!.AsArray().Single()!["implicated"]!.GetValue<bool>());
        Assert.True(json["stack"]!.AsArray().Count < 64);
    }

    [Fact]
    public void Format_WithoutBoundsWouldExceedTheLimit()
    {
        // Guards the guard: the oversized report above really is larger than the bound before cutting.
        var report = new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Complete,
            Stack = Enumerable.Range(0, 64).Select(index => new DumpStackFrame(index, "nt", new string('S', 400), "0x1", "0x2")).ToArray(),
        };

        Assert.True(Encoding.UTF8.GetByteCount(DumpAnalysisFormatting.Format(report, int.MaxValue)) > DumpAnalysisLimits.OutputBytes / 2);
    }

    [Theory]
    [InlineData(DumpAnalysisStatus.Complete, "complete")]
    [InlineData(DumpAnalysisStatus.Partial, "partial")]
    [InlineData(DumpAnalysisStatus.Unavailable, "unavailable")]
    [InlineData(DumpAnalysisStatus.Invalid, "invalid")]
    public void StatusNames_AreStable(DumpAnalysisStatus status, string name) => Assert.Equal(name, DumpAnalysisFormatting.StatusName(status));

    [Fact]
    public void FailureNames_DistinguishEveryLimitation()
    {
        var names = Enum.GetValues<DumpAnalysisFailure>().Select(DumpAnalysisFormatting.FailureName).ToArray();

        Assert.Equal(["none", "not-found", "access-denied", "invalid-path", "debugger-unavailable", "invalid-dump", "timeout", "debugger-failure"], names);
    }
}

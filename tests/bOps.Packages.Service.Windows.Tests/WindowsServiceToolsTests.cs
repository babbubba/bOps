// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Service.Conformance;
using bOps.Packages.Service.Windows;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Win32;

namespace bOps.Packages.Service.Windows.Tests;

/// <summary>
/// Integration tests against the real Windows Service Control Manager (never mocked —
/// agentic/04-testing-rules.md, "Never mock the operating system"), run through the shared
/// conformance suite so a difference in output shape from the Linux package would be caught here.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsServiceToolsTests
{
    // The Windows Event Log service: present and installed on every Windows machine this suite
    // runs on, whether or not it happens to be running right now.
    private const string KnownServiceName = "EventLog";

    [WindowsOnlyFact]
    public Task List_Conforms() =>
        ServiceToolConformance.AssertServiceListConformsAsync(new WindowsServiceListTool(), "windows");

    [WindowsOnlyFact]
    public Task Status_ReportsExisting_ForEventLog() =>
        ServiceToolConformance.AssertServiceStatusReportsExistingAsync(new WindowsServiceStatusTool(), "windows", KnownServiceName);

    [WindowsOnlyFact]
    public Task Status_ReportsMissing_ForAnUnlikelyServiceName() =>
        ServiceToolConformance.AssertServiceStatusReportsMissingAsync(
            new WindowsServiceStatusTool(), "windows", "bops-this-service-definitely-does-not-exist");

    [WindowsOnlyFact]
    public async Task Config_ReportsExistingServiceAndNullWorkingDirectory()
    {
        var result = await new WindowsServiceConfigTool().ExecuteAsync(NameArgs(KnownServiceName));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Null(json["workingDirectory"]);
    }

    [WindowsOnlyFact]
    public async Task Config_MatchesWindowsUpdateConfiguration()
    {
        const string name = "wuauserv";
        using var service = new ServiceController(name);
        using var settings = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        Assert.NotNull(settings);
        var commandLine = Assert.IsType<string>(settings.GetValue("ImagePath"));
        var start = Assert.IsType<int>(settings.GetValue("Start"));
        var startupType = start switch
        {
            2 when settings.GetValue("DelayedAutoStart") is 1 => "automatic-delayed",
            2 => "automatic",
            3 => "manual",
            4 => "disabled",
            _ => "unknown",
        };
        var expectedDependencies = (settings.GetValue("DependOnService") as string[] ?? [])
            .OrderBy(value => value, StringComparer.Ordinal);

        var result = await new WindowsServiceConfigTool().ExecuteAsync(NameArgs(name));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        var executable = json["executable"]?.GetValue<string>();
        var arguments = json["arguments"]?.GetValue<string>();
        Assert.Multiple(
            () => Assert.Equal(service.DisplayName, json["displayName"]!.GetValue<string>()),
            () => Assert.Equal(settings.GetValue("ObjectName"), json["user"]?.GetValue<string>()),
            () => Assert.Equal(commandLine, string.Join(' ', new[] { executable, arguments }.OfType<string>()), ignoreCase: true),
            () => Assert.Equal(expectedDependencies, json["dependencies"]!.AsArray().Select(node => node!.GetValue<string>())),
            () => Assert.Equal(startupType, json["startupType"]!.GetValue<string>()),
            () => Assert.Equal(start != 4, json["enabled"]!.GetValue<bool>()),
            () => Assert.True(json["complete"]!.GetValue<bool>()));
    }

    [WindowsOnlyFact]
    public async Task Config_ReportsMissingServiceAsDefiniteAbsence()
    {
        var result = await new WindowsServiceConfigTool().ExecuteAsync(NameArgs("bops-this-service-definitely-does-not-exist"));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.True(json["complete"]!.GetValue<bool>());
    }

    private static ToolArguments NameArgs(string name) => ToolArguments.FromJson(new JsonObject { ["name"] = name });

    [Theory]
    [InlineData("\"\" -arg")]
    [InlineData("\"C:\\Program Files\\Service.exe")]
    public void Config_RejectsIncompleteExecutablePath(string commandLine)
    {
        var parsed = WindowsCommandLine.Parse(commandLine);
        Assert.False(parsed.Complete);
        Assert.Null(parsed.Executable);
    }

    [Theory]
    [InlineData("Dépendance", false)]
    [InlineData("rpcss\0+network\0Spouleur", true)]
    [InlineData("", true)]
    public void Config_CopiesUnicodeAndAllMultiStringEntries(string value, bool multiString)
    {
        ArgumentNullException.ThrowIfNull(value);
        var nativeValue = multiString && value.Length > 0 ? value + '\0' : value;
        var buffer = Marshal.StringToHGlobalUni(nativeValue);
        try
        {
            Assert.Equal(value, WindowsServiceNative.ReadString(buffer, buffer, (nativeValue.Length + 1) * sizeof(char), multiString));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData("rpcss", false, 0)]
    [InlineData("rpcss\0Spooler", true, 1)]
    public void Config_RejectsUnterminatedNativeStrings(string value, bool multiString, int trailingNulls)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = Marshal.StringToHGlobalUni(value);
        try
        {
            var size = (value.Length + trailingNulls) * sizeof(char);
            Assert.Throws<InvalidDataException>(() => WindowsServiceNative.ReadString(buffer, buffer, size, multiString));
            Assert.Throws<InvalidDataException>(() => WindowsServiceNative.ReadString(IntPtr.Add(buffer, size), buffer, size, multiString));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ToolProvider_ContributesExactlyTheNineServiceTools()
    {
        var names = new WindowsServiceToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(["service.list", "service.status", "service.start", "service.stop", "service.restart", "service.config", "service.dependencies", "service.enable", "service.disable"], names);
    }
}

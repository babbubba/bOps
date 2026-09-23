// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

public sealed class WindowsDriverEvidenceTests
{
    [Fact]
    public void Enumeration_UsesPointerSizedByteCountAndBoundedResize()
    {
        var calls = 0;
        var result = WindowsDriverEnumeration.Read(buffer =>
        {
            calls++;
            if (calls == 1) return (true, (uint)(300 * IntPtr.Size));
            buffer[0] = new IntPtr(42);
            return (true, (uint)IntPtr.Size);
        }, 3001);
        Assert.True(result.Success); Assert.Equal(2, calls); Assert.Single(result.Bases); Assert.Equal(new IntPtr(42), result.Bases[0]);
        Assert.False(WindowsDriverEnumeration.Read(_ => (true, (uint)(IntPtr.Size - 1)), 3001).Success);
        Assert.False(WindowsDriverEnumeration.Read(_ => (true, (uint)(3002 * IntPtr.Size)), 3001).Success);
    }

    [Fact]
    public async Task RowMetadataOrderingLimitAndNullAddressEvidenceAreHonest()
    {
        var path = Path.GetTempFileName();
        try
        {
            var tool = new WindowsDriverEvidenceTool(buffer => { buffer[0] = new IntPtr(2); buffer[1] = new IntPtr(1); return (true, (uint)(2 * IntPtr.Size)); },
                address => address == new IntPtr(1) ? "alpha.sys" : "zeta.sys", _ => path, _ => ("1.2", "Vendor"));
            var output = JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }))).Output!)!.AsObject();
            Assert.False(output["complete"]!.GetValue<bool>()); Assert.True(output["truncated"]!.GetValue<bool>());
            Assert.Equal("alpha.sys", output["items"]![0]!["name"]!.GetValue<string>()); Assert.Equal("1.2", output["items"]![0]!["version"]!.GetValue<string>()); Assert.Equal("Vendor", output["items"]![0]!["vendor"]!.GetValue<string>());
        }
        finally { File.Delete(path); }

        var hidden = new WindowsDriverEvidenceTool(buffer => (true, (uint)(2 * IntPtr.Size)), _ => null, _ => null, _ => (null, null));
        var hiddenOutput = JsonNode.Parse((await hidden.ExecuteAsync(ToolArguments.Empty)).Output!)!.AsObject();
        Assert.False(hiddenOutput["complete"]!.GetValue<bool>()); Assert.Empty(hiddenOutput["items"]!.AsArray());
    }

    [Fact]
    public async Task MetadataFailurePreservesOtherRowsAndNativeFailureIsIncomplete()
    {
        var tool = new WindowsDriverEvidenceTool(buffer => { buffer[0] = new IntPtr(1); buffer[1] = new IntPtr(2); return (true, (uint)(2 * IntPtr.Size)); },
            address => "driver" + address + ".sys", _ => "C:\\Windows\\System32\\drivers\\driver.sys", path => path.Contains("driver", StringComparison.Ordinal) ? throw new IOException() : (null, null));
        var rows = JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.Empty)).Output!)!["items"]!.AsArray();
        Assert.Equal(2, rows.Count); Assert.All(rows, row => Assert.Null(row!["version"]));
        var failed = new WindowsDriverEvidenceTool(_ => (false, 0), _ => null, _ => null, _ => (null, null));
        Assert.False(JsonNode.Parse((await failed.ExecuteAsync(ToolArguments.Empty)).Output!)!["complete"]!.GetValue<bool>());
    }

    [WindowsOnlyFact]
    public async Task RealWindowsSmokeReturnsBoundedDriverEvidenceWithoutMutation()
    {
        var result = await new WindowsDriverEvidenceTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 20 }));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["items"]!.AsArray().Count, 0, 20); Assert.NotNull(json["complete"]); Assert.NotNull(json["sources"]);
    }
}

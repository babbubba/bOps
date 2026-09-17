// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class DpkgStatusParserTests
{
    [Fact]
    public async Task ParseAsync_ReturnsOnlyInstalledPackagesAndPreservesNullableFields()
    {
        const string status = """
            Package: alpha
            Status: install ok installed
            Architecture: amd64
            Version: 1.2.3
            Maintainer: Example Maintainer

            Package: removed
            Status: deinstall ok config-files
            Architecture: all

            Package: beta
            Status: install ok installed

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(status));

        var result = await DpkgStatusParser.ParseAsync(stream, collectionLimit: 10, CancellationToken.None);

        Assert.False(result.Truncated);
        Assert.Collection(
            result.Items,
            alpha =>
            {
                Assert.Equal("deb:alpha:amd64", alpha.Identity);
                Assert.Equal("1.2.3", alpha.Version);
                Assert.Equal("Example Maintainer", alpha.Publisher);
            },
            beta =>
            {
                Assert.Equal("deb:beta:unknown", beta.Identity);
                Assert.Null(beta.Version);
                Assert.Null(beta.Publisher);
            });
    }

    [Fact]
    public async Task ParseAsync_StopsAtTheCollectionCeiling()
    {
        const string status = """
            Package: alpha
            Status: install ok installed

            Package: beta
            Status: install ok installed

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(status));

        var result = await DpkgStatusParser.ParseAsync(stream, collectionLimit: 1, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task ApplicationInventory_ReportsAbsentDatabasesAsNotApplicable()
    {
        var unavailableRoot = Path.Combine(Path.GetTempPath(), $"bops-missing-{Guid.NewGuid():N}");
        var tool = new LinuxApplicationInventoryTool(
            Path.Combine(unavailableRoot, "dpkg-status"),
            [Path.Combine(unavailableRoot, "rpm")],
            Path.Combine(unavailableRoot, "apk-installed"));

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        Assert.All(
            json["sources"]!.AsArray(),
            source => Assert.Equal("notApplicable", source!["status"]!.GetValue<string>()));
        Assert.Empty(json["items"]!.AsArray());
    }
}

// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem.Tests;

public sealed class FsTroubleshootingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bops-fs-troubleshooting-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Grep_StreamsTextAndSkipsBinaryFiles()
    {
        var text = Path.Combine(_root.FullName, "app.log");
        await File.WriteAllTextAsync(text, "one\nERROR two\nthree");
        await File.WriteAllBytesAsync(Path.Combine(_root.FullName, "binary.log"), [0, 1, 2]);
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", _root.FullName), ("pattern", "ERROR"), ("recursive", true)));

        Assert.True(result.Succeeded);
        Assert.Contains("app.log", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("binary.log", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_ReturnsTheRequestedFinalLines()
    {
        var path = Path.Combine(_root.FullName, "app.log");
        await File.WriteAllTextAsync(path, "one\r\ntwo\r\nthree\r\n");
        var result = await new FsTailTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path), ("lines", 2)));

        Assert.True(result.Succeeded);
        Assert.Contains("two", result.Output, StringComparison.Ordinal);
        Assert.Contains("three", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_VerifiesMatchingContentAndNeverOverwrites()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "destination.txt");
        await File.WriteAllTextAsync(source, "content");
        var policy = Policy(read: true, write: true);
        var copy = new FsCopyTool(policy, new FilesystemOperationsOptions { MaxCopyBytes = 1024 });

        var copyResult = await copy.ExecuteAsync(Args(("source", source), ("destination", destination)));
        var verifyResult = await new FsCopyVerifyTool(policy).ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.True(copyResult.Succeeded);
        Assert.True(JsonNode.Parse(verifyResult.Output!)!["matching"]!.GetValue<bool>());
        var second = await copy.ExecuteAsync(Args(("source", source), ("destination", destination)));
        Assert.Equal(ToolOutcome.Failure, second.Outcome);
    }

    [Fact]
    public async Task Mkdir_IsIdempotentAndUsesStatForVerification()
    {
        var path = Path.Combine(_root.FullName, "created");
        var tool = new FsMkdirTool(Policy(read: true, write: true));

        Assert.True((await tool.ExecuteAsync(Args(("path", path)))).Succeeded);
        Assert.True((await tool.ExecuteAsync(Args(("path", path)))).Succeeded);
        var stat = await new FsStatTool(Policy(read: true, write: true)).ExecuteAsync(Args(("path", path)));
        var verification = await tool.EvaluateVerificationAsync(Args(("path", path)), stat);

        Assert.Equal(VerificationStatus.Confirmed, verification.Status);
    }

    [Fact]
    public async Task Permissions_ReportsTheContractShape()
    {
        var path = Path.Combine(_root.FullName, "permissions.txt");
        await File.WriteAllTextAsync(path, "x");
        var result = await new FsPermissionsTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal(path, json["path"]!.GetValue<string>());
        Assert.NotNull(json["aclEntries"]);
        Assert.NotNull(json["complete"]);
    }

    private FilesystemPathPolicy Policy(bool read, bool write) => new(
        read ? [Path.Combine(_root.FullName, "**")] : [],
        write ? [Path.Combine(_root.FullName, "**")] : []);

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values) json[name] = JsonValue.Create(value);
        return ToolArguments.FromJson(json);
    }
}

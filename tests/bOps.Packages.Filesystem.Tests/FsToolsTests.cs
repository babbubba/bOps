using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem.Tests;

/// <summary>
/// Integration tests against the real filesystem — a temp directory this process owns, never a
/// mocked one (agentic/04-testing-rules.md: Filesystem is a platform package). Covers the
/// testing-rules "Verification" minimum list at the tool level for <c>fs.write</c>/<c>fs.delete</c>:
/// Confirmed, Refuted, Inconclusive, and a verification call the path policy itself refuses.
/// </summary>
public sealed class FsToolsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bops-fs-tools-");

    public void Dispose() => _root.Delete(recursive: true);

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    [Fact]
    public async Task FsList_ReturnsEntries_WhenReadAllowed()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
        var tool = new FsListTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", _root.FullName)));

        Assert.True(result.Succeeded);
        Assert.Contains("a.txt", result.Output, StringComparison.Ordinal);
        Assert.Contains("sub", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FsList_Fails_WhenReadNotAllowed()
    {
        var tool = new FsListTool(new FilesystemPathPolicy([], []));

        var result = await tool.ExecuteAsync(Args(("path", _root.FullName)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("policy", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FsRead_ReturnsContent_WhenReadAllowed()
    {
        var file = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "hello world");
        var tool = new FsReadTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded);
        Assert.Equal("hello world", result.Output);
    }

    [Fact]
    public async Task FsRead_Truncates_WhenLargerThanMaxBytes()
    {
        var file = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "0123456789");
        var tool = new FsReadTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", file), ("maxBytes", 4)));

        Assert.True(result.Succeeded);
        Assert.StartsWith("0123", result.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FsStat_ReportsExistsTrue_ForAFile()
    {
        var file = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "hi");
        var tool = new FsStatTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal("file", json["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task FsStat_ReportsExistsFalse_ForAMissingPath()
    {
        var missing = Path.Combine(_root.FullName, "nope.txt");
        var tool = new FsStatTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", missing)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.False(json["exists"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FsStat_IsAllowed_WhenOnlyWritePatternCovers_NotRead()
    {
        // fs.write/fs.delete declare fs.stat as their verification target (rule B3) and only
        // require a write pattern to run at all — verification must not silently become
        // Inconclusive for every operator who configured WritePatterns without also duplicating
        // every path into ReadPatterns.
        var file = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "hi");
        var tool = new FsStatTool(new FilesystemPathPolicy(readPatterns: [], writePatterns: [Path.Combine(_root.FullName, "**")]));

        var result = await tool.ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task FsWrite_CreatesFile_WhenWriteAllowed()
    {
        var file = Path.Combine(_root.FullName, "new.txt");
        var tool = new FsWriteTool(new FilesystemPathPolicy([], [Path.Combine(_root.FullName, "**")]));

        var result = await tool.ExecuteAsync(Args(("path", file), ("content", "hello")));

        Assert.True(result.Succeeded);
        Assert.Equal("hello", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task FsWrite_Fails_WhenWriteNotAllowed()
    {
        var file = Path.Combine(_root.FullName, "new.txt");
        var tool = new FsWriteTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", file), ("content", "hello")));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task FsWrite_VerificationIsConfirmed_WhenFileExistsAfterwards()
    {
        var file = Path.Combine(_root.FullName, "new.txt");
        var policy = new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], [Path.Combine(_root.FullName, "**")]);
        var writeTool = new FsWriteTool(policy);
        var statTool = new FsStatTool(policy);

        await writeTool.ExecuteAsync(Args(("path", file), ("content", "hello")));
        var statResult = await statTool.ExecuteAsync(Args(("path", file)));
        var outcome = await writeTool.EvaluateVerificationAsync(Args(("path", file)), statResult);

        Assert.Equal(VerificationStatus.Confirmed, outcome.Status);
    }

    [Fact]
    public async Task FsWrite_VerificationIsRefuted_WhenStatReportsNoFile()
    {
        // The write tool never actually ran here — this simulates fs.stat's own honest
        // observation disagreeing with what the write claimed, the case EvaluateVerificationAsync
        // exists to catch.
        var file = Path.Combine(_root.FullName, "never-written.txt");
        var policy = new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], [Path.Combine(_root.FullName, "**")]);
        var writeTool = new FsWriteTool(policy);
        var statTool = new FsStatTool(policy);

        var statResult = await statTool.ExecuteAsync(Args(("path", file)));
        var outcome = await writeTool.EvaluateVerificationAsync(Args(("path", file)), statResult);

        Assert.Equal(VerificationStatus.Refuted, outcome.Status);
    }

    [Fact]
    public async Task FsWrite_VerificationIsInconclusive_WhenTheVerificationCallItselfFailed()
    {
        var writeTool = new FsWriteTool(new FilesystemPathPolicy([], [Path.Combine(_root.FullName, "**")]));
        var failedStatResult = ToolCallResult.Failure("fs.stat is not registered.");

        var outcome = await writeTool.EvaluateVerificationAsync(Args(("path", "irrelevant")), failedStatResult);

        Assert.Equal(VerificationStatus.Inconclusive, outcome.Status);
    }

    [Fact]
    public async Task FsDelete_RemovesFile_WhenWriteAllowed()
    {
        var file = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "hi");
        var tool = new FsDeleteTool(new FilesystemPathPolicy([], [Path.Combine(_root.FullName, "**")]));

        var result = await tool.ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task FsDelete_Fails_ForADirectory()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
        var tool = new FsDeleteTool(new FilesystemPathPolicy([], [Path.Combine(_root.FullName, "**")]));

        var result = await tool.ExecuteAsync(Args(("path", dir.FullName)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.True(Directory.Exists(dir.FullName));
    }

    [Fact]
    public async Task FsDelete_VerificationIsConfirmed_WhenFileIsGoneAfterwards()
    {
        var file = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "hi");
        var policy = new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], [Path.Combine(_root.FullName, "**")]);
        var deleteTool = new FsDeleteTool(policy);
        var statTool = new FsStatTool(policy);

        await deleteTool.ExecuteAsync(Args(("path", file)));
        var statResult = await statTool.ExecuteAsync(Args(("path", file)));
        var outcome = await deleteTool.EvaluateVerificationAsync(Args(("path", file)), statResult);

        Assert.Equal(VerificationStatus.Confirmed, outcome.Status);
    }

    [Fact]
    public async Task FsDelete_VerificationIsRefuted_WhenStatReportsTheFileStillExists()
    {
        var file = Path.Combine(_root.FullName, "still-here.txt");
        await File.WriteAllTextAsync(file, "hi");
        var policy = new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], [Path.Combine(_root.FullName, "**")]);
        var deleteTool = new FsDeleteTool(policy);
        var statTool = new FsStatTool(policy);

        // The delete never actually ran — fs.stat honestly reports the file is still there.
        var statResult = await statTool.ExecuteAsync(Args(("path", file)));
        var outcome = await deleteTool.EvaluateVerificationAsync(Args(("path", file)), statResult);

        Assert.Equal(VerificationStatus.Refuted, outcome.Status);
    }

    [Fact]
    public void ToolProvider_ContributesExactlyTheFiveFsTools()
    {
        var policy = new FilesystemPathPolicy([], []);
        var names = new FilesystemToolProvider(policy).GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(["fs.list", "fs.read", "fs.stat", "fs.write", "fs.delete"], names);
    }

    [Theory]
    [InlineData("fs.write")]
    [InlineData("fs.delete")]
    public void NonReadTools_DeclareVerificationAgainstFsStat(string toolName)
    {
        var policy = new FilesystemPathPolicy([], []);
        var tool = new FilesystemToolProvider(policy).GetTools().Single(t => t.Manifest.Name == toolName);

        Assert.Equal(RiskLevel.High, tool.Manifest.Risk);
        Assert.NotNull(tool.Manifest.Verification);
        Assert.Equal("fs.stat", tool.Manifest.Verification!.VerifyToolName);
        Assert.IsAssignableFrom<IVerifiableTool>(tool);
    }
}

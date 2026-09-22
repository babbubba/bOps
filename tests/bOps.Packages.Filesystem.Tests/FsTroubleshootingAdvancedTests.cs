// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem.Tests;

/// <summary>
/// V1.3-F coverage beyond the shape-level smoke tests in <see cref="FsTroubleshootingTests"/>:
/// limits, encoding edge cases, real platform ACL/mode/lock evidence, S11 traversal and
/// verification-as-directory. Runs against the real filesystem and, where gated, real OS APIs —
/// never a mocked filesystem or process (agentic/04-testing-rules.md).
/// </summary>
public sealed class FsTroubleshootingAdvancedTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bops-fs-advanced-");

    public void Dispose() => _root.Delete(recursive: true);

    private FilesystemPathPolicy Policy(bool read, bool write) => new(
        read ? [Path.Combine(_root.FullName, "**")] : [],
        write ? [Path.Combine(_root.FullName, "**")] : []);

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values) json[name] = JsonValue.Create(value);
        return ToolArguments.FromJson(json);
    }

    // ---------------------------------------------------------------- fs.grep

    [Fact]
    public async Task Grep_StopsAtMaxMatches()
    {
        var file = Path.Combine(_root.FullName, "many.log");
        await File.WriteAllLinesAsync(file, Enumerable.Range(0, 20).Select(i => $"ERROR {i}"));
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", file), ("pattern", "ERROR"), ("maxMatches", 5)));

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.Output!.Split('\n').Length);
    }

    [Fact]
    public async Task Grep_RejectsMaxMatchesOutsideRange()
    {
        var file = Path.Combine(_root.FullName, "a.log");
        await File.WriteAllTextAsync(file, "x");
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", file), ("pattern", "x"), ("maxMatches", 0)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [Fact]
    public async Task Grep_StopsAtMaxOutputBytes()
    {
        var file = Path.Combine(_root.FullName, "wide.log");
        await File.WriteAllLinesAsync(file, Enumerable.Range(0, 50).Select(i => $"ERROR line number {i} with some padding text"));
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", file), ("pattern", "ERROR"), ("maxOutputBytes", 200)));

        Assert.True(result.Succeeded);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 260);
    }

    [Fact]
    public async Task Grep_RegexTimesOut_OnCatastrophicBacktracking()
    {
        var file = Path.Combine(_root.FullName, "catastrophic.log");
        var payload = new string('a', 40) + "!";
        await File.WriteAllTextAsync(file, payload);
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(
            ("path", file), ("pattern", "^(a+)+$"), ("regex", true)));

        // Either the timeout truncates matches (empty/incomplete) or the call still succeeds
        // having produced no match within the bounded time — it must never hang or throw out.
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Grep_SkipsProbableBinaryFile()
    {
        var file = Path.Combine(_root.FullName, "binary.dat");
        await File.WriteAllBytesAsync(file, [0x41, 0x00, 0x42, 0x00, 0x43]);
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", file), ("pattern", "A")));

        Assert.True(result.Succeeded);
        Assert.Equal("(no matches)", result.Output);
    }

    [Fact]
    public async Task Grep_BoundsAHugeLineTo2048Utf8Bytes()
    {
        var file = Path.Combine(_root.FullName, "huge.log");
        var hugeLine = "ERROR " + new string('x', 5000);
        await File.WriteAllTextAsync(file, hugeLine);
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", file), ("pattern", "ERROR"), ("maxOutputBytes", 131072)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!.Split('\n')[0])!;
        var boundedLine = json["boundedLine"]!.GetValue<string>();
        Assert.True(Encoding.UTF8.GetByteCount(boundedLine) <= 2048);
        Assert.EndsWith("...", boundedLine, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task Grep_DoesNotFollowASymlinkedFile()
    {
        var secretDir = Directory.CreateDirectory(Path.Combine(_root.FullName, "secret-outside"));
        var secretFile = Path.Combine(secretDir.FullName, "secret.log");
        await File.WriteAllTextAsync(secretFile, "ERROR leaked");
        var link = Path.Combine(_root.FullName, "link.log");
        File.CreateSymbolicLink(link, secretFile);
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", link), ("pattern", "ERROR")));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [SymlinkCapableFact]
    public async Task Grep_DoesNotTraverseASymlinkedDirectory()
    {
        var secretDir = Directory.CreateDirectory(Path.Combine(_root.FullName, "secret-outside"));
        await File.WriteAllTextAsync(Path.Combine(secretDir.FullName, "secret.log"), "ERROR leaked");
        var linkedDir = Path.Combine(_root.FullName, "linked");
        Directory.CreateSymbolicLink(linkedDir, secretDir.FullName);
        var tool = new FsGrepTool(Policy(read: true, write: false));

        var result = await tool.ExecuteAsync(Args(("path", _root.FullName), ("pattern", "ERROR"), ("recursive", true)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("leaked", result.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fs.tail

    [Fact]
    public async Task Tail_HandlesLfOnly()
    {
        var path = Path.Combine(_root.FullName, "lf.log");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");
        var result = await new FsTailTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path), ("lines", 2)));

        Assert.True(result.Succeeded);
        Assert.Equal("two\nthree", result.Output);
    }

    [Fact]
    public async Task Tail_HandlesCrLf()
    {
        var path = Path.Combine(_root.FullName, "crlf.log");
        await File.WriteAllTextAsync(path, "one\r\ntwo\r\nthree\r\n");
        var result = await new FsTailTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path), ("lines", 1)));

        Assert.True(result.Succeeded);
        Assert.Equal("three", result.Output);
    }

    [Fact]
    public async Task Tail_StripsAUtf8Bom()
    {
        var path = Path.Combine(_root.FullName, "bom.log");
        await File.WriteAllBytesAsync(path, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("hello\nworld")]);
        var result = await new FsTailTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain('﻿', result.Output!);
        Assert.Equal("hello\nworld", result.Output);
    }

    [Fact]
    public async Task Tail_FailsExplicitly_ForInvalidUtf8Bytes()
    {
        var path = Path.Combine(_root.FullName, "invalid.log");
        // 0xC0 0x80 is an overlong, invalid UTF-8 sequence (never produced by a valid encoder).
        await File.WriteAllBytesAsync(path, [(byte)'a', 0xC0, 0x80, (byte)'b']);
        var result = await new FsTailTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("encoding", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tail_FailsExplicitly_ForABinaryFile()
    {
        var path = Path.Combine(_root.FullName, "binary.bin");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03]);
        var result = await new FsTailTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [Fact]
    public async Task Tail_TruncatesByMaxBytes_WithoutMisreportingASplitMultibyteCharacterAsInvalid()
    {
        var path = Path.Combine(_root.FullName, "multibyte.log");
        // Each line contains multi-byte UTF-8 characters (é = 2 bytes) so a maxBytes cut is very
        // likely to land in the middle of one; the tool must drop that partial first line rather
        // than fail the whole read.
        var lines = Enumerable.Range(0, 200).Select(i => $"line {i} café résumé naïve").ToArray();
        await File.WriteAllLinesAsync(path, lines);

        var result = await new FsTailTool(Policy(read: true, write: false))
            .ExecuteAsync(Args(("path", path), ("lines", 5000), ("maxBytes", 500)));

        Assert.True(result.Succeeded);
        Assert.Contains("truncated", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 19", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_RejectsLinesOrMaxBytesOutsideRange()
    {
        var path = Path.Combine(_root.FullName, "a.log");
        await File.WriteAllTextAsync(path, "x");
        var tool = new FsTailTool(Policy(read: true, write: false));

        Assert.Equal(ToolOutcome.Failure, (await tool.ExecuteAsync(Args(("path", path), ("lines", 0)))).Outcome);
        Assert.Equal(ToolOutcome.Failure, (await tool.ExecuteAsync(Args(("path", path), ("maxBytes", 0)))).Outcome);
    }

    // ---------------------------------------------------------------- fs.permissions

    [WindowsOnlyFact]
    public async Task Permissions_ReportsRealWindowsFileAcl()
    {
        var file = Path.Combine(_root.FullName, "file.txt");
        await File.WriteAllTextAsync(file, "hi");
        var result = await new FsPermissionsTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("file", json["type"]!.GetValue<string>());
        Assert.Equal("windows-acl", json["source"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.NotEmpty(json["aclEntries"]!.AsArray());
        Assert.NotNull(json["owner"]!.GetValue<string>());
    }

    [WindowsOnlyFact]
    public async Task Permissions_ReportsRealWindowsDirectoryAcl_UsingDirectorySecurity()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
        var result = await new FsPermissionsTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", dir.FullName)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("directory", json["type"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.NotEmpty(json["aclEntries"]!.AsArray());
    }

    [LinuxOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Permissions_ReportsRealLinuxModeOwnerGroup()
    {
        var file = Path.Combine(_root.FullName, "file.txt");
        await File.WriteAllTextAsync(file, "hi");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var result = await new FsPermissionsTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("linux-stat", json["source"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.StartsWith("uid:", json["owner"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("gid:", json["group"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("UserRead", json["unixMode"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fs.locks

    [WindowsOnlyFact]
    public async Task Locks_FindsARealProcessHolding_TheFileOpenOnWindows()
    {
        var path = Path.Combine(_root.FullName, "held.txt");
        await File.WriteAllTextAsync(path, "hold me");
        await using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await new FsLocksTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["complete"]!.GetValue<bool>());
        var rows = json["rows"]!.AsArray();
        Assert.Contains(rows, row => row!["pid"]!.GetValue<int>() == Environment.ProcessId);
    }

    [LinuxOnlyFact]
    public async Task Locks_FindsARealProcessHolding_TheFileOpenOnLinux()
    {
        var path = Path.Combine(_root.FullName, "held.txt");
        await File.WriteAllTextAsync(path, "hold me");
        await using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await new FsLocksTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        var rows = json["rows"]!.AsArray();
        Assert.Contains(rows, row => row!["pid"]!.GetValue<int>() == Environment.ProcessId);
    }

    [Fact]
    public async Task Locks_ReportsNoHolders_ForAnUnheldFile()
    {
        var path = Path.Combine(_root.FullName, "free.txt");
        await File.WriteAllTextAsync(path, "free");
        var result = await new FsLocksTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Empty(json["rows"]!.AsArray());
    }

    [Fact]
    public async Task Locks_RejectsLimitOutsideRange()
    {
        var path = Path.Combine(_root.FullName, "a.txt");
        await File.WriteAllTextAsync(path, "x");
        var result = await new FsLocksTool(Policy(read: true, write: false)).ExecuteAsync(Args(("path", path), ("limit", 0)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [LinuxOnlyFact]
    public async Task Locks_ReportsIncomplete_WhenTheRowLimitIsReached()
    {
        // Open enough of our own descriptors on the same file that the tool's own `limit` is hit
        // before the /proc scan finishes; a truncated scan must never claim complete:true.
        var path = Path.Combine(_root.FullName, "many-holders.txt");
        await File.WriteAllTextAsync(path, "x");
        var handles = new List<FileStream>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            }

            var result = await new FsLocksTool(Policy(read: true, write: false))
                .ExecuteAsync(Args(("path", path), ("limit", 1)));

            Assert.True(result.Succeeded);
            var json = JsonNode.Parse(result.Output!)!;
            Assert.False(json["complete"]!.GetValue<bool>());
        }
        finally
        {
            foreach (var handle in handles) await handle.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- fs.copy / fs.copy.verify

    [Fact]
    public async Task Copy_FailsWithoutOverwriting_WhenDestinationExists()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "destination.txt");
        await File.WriteAllTextAsync(source, "new content");
        await File.WriteAllTextAsync(destination, "original content");
        var tool = new FsCopyTool(Policy(read: true, write: true), new FilesystemOperationsOptions());

        var result = await tool.ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Equal("original content", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Copy_Fails_WhenSourceExceedsMaxCopyBytes()
    {
        var source = Path.Combine(_root.FullName, "big.bin");
        var destination = Path.Combine(_root.FullName, "big-copy.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var tool = new FsCopyTool(Policy(read: true, write: true), new FilesystemOperationsOptions { MaxCopyBytes = 100 });

        var result = await tool.ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(_root.FullName, "*.bops-copy-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Copy_CleansUpTheTemporaryFile_WhenCancelledMidCopy()
    {
        var source = Path.Combine(_root.FullName, "large.bin");
        var destination = Path.Combine(_root.FullName, "large-copy.bin");
        await File.WriteAllBytesAsync(source, new byte[8 * 1024 * 1024]);
        var tool = new FsCopyTool(Policy(read: true, write: true), new FilesystemOperationsOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.ExecuteAsync(Args(("source", source), ("destination", destination)), cts.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(_root.FullName, "*.bops-copy-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Copy_Fails_WhenSourceReadNotAllowedByPolicy()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "destination.txt");
        await File.WriteAllTextAsync(source, "x");
        var tool = new FsCopyTool(new FilesystemPathPolicy([], [Path.Combine(_root.FullName, "**")]), new FilesystemOperationsOptions());

        var result = await tool.ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Copy_Fails_WhenDestinationWriteNotAllowedByPolicy()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "destination.txt");
        await File.WriteAllTextAsync(source, "x");
        var tool = new FsCopyTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []), new FilesystemOperationsOptions());

        var result = await tool.ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CopyVerify_ReportsMismatch_WhenContentDiffers()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "destination.txt");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(destination, "different");
        var result = await new FsCopyVerifyTool(Policy(read: true, write: true))
            .ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.False(json["matching"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CopyVerify_ReportsNotMatching_WhenDestinationMissing()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "missing.txt");
        await File.WriteAllTextAsync(source, "x");
        var result = await new FsCopyVerifyTool(Policy(read: true, write: true))
            .ExecuteAsync(Args(("source", source), ("destination", destination)));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.False(json["matching"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Copy_EndToEnd_VerificationOutcomeIsConfirmed()
    {
        var source = Path.Combine(_root.FullName, "source.txt");
        var destination = Path.Combine(_root.FullName, "destination.txt");
        await File.WriteAllTextAsync(source, "content to copy");
        var policy = Policy(read: true, write: true);
        var copyTool = new FsCopyTool(policy, new FilesystemOperationsOptions());
        var verifyTool = new FsCopyVerifyTool(policy);

        var copyResult = await copyTool.ExecuteAsync(Args(("source", source), ("destination", destination)));
        var verifyResult = await verifyTool.ExecuteAsync(Args(("source", source), ("destination", destination)));
        var outcome = await copyTool.EvaluateVerificationAsync(Args(("source", source), ("destination", destination)), verifyResult);

        Assert.True(copyResult.Succeeded);
        Assert.Equal(VerificationStatus.Confirmed, outcome.Status);
    }

    [Fact]
    public async Task Copy_VerificationIsInconclusive_WhenVerificationMismatches()
    {
        var copyTool = new FsCopyTool(Policy(read: true, write: true), new FilesystemOperationsOptions());
        var mismatchResult = ToolCallResult.Success(FsCopyVerificationOutput.Format(false, 1, 2));

        var outcome = await copyTool.EvaluateVerificationAsync(Args(("source", "a"), ("destination", "b")), mismatchResult);

        Assert.Equal(VerificationStatus.Inconclusive, outcome.Status);
    }

    // ---------------------------------------------------------------- fs.mkdir

    [Fact]
    public async Task Mkdir_FailsWhenAFileAlreadyExistsAtThePath()
    {
        var path = Path.Combine(_root.FullName, "already-a-file");
        await File.WriteAllTextAsync(path, "x");
        var tool = new FsMkdirTool(Policy(read: true, write: true));

        var result = await tool.ExecuteAsync(Args(("path", path)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Mkdir_VerificationIsRefuted_WhenStatReportsANonDirectoryAtThePath()
    {
        // Simulates the race the review flagged: fs.mkdir claimed success, but by the time
        // fs.stat ran, a non-directory occupied the path. exists:true alone must not confirm.
        var path = Path.Combine(_root.FullName, "raced");
        var tool = new FsMkdirTool(Policy(read: true, write: true));
        var statAsFile = ToolCallResult.Success(FsStatOutput.ForExisting(path, "file", 0, DateTimeOffset.UtcNow));

        var outcome = await tool.EvaluateVerificationAsync(Args(("path", path)), statAsFile);

        Assert.Equal(VerificationStatus.Refuted, outcome.Status);
    }

    [Fact]
    public async Task Mkdir_VerificationIsInconclusive_WhenTheVerificationCallItselfFailed()
    {
        var tool = new FsMkdirTool(Policy(read: true, write: true));
        var failedStat = ToolCallResult.Failure("fs.stat is not registered.");

        var outcome = await tool.EvaluateVerificationAsync(Args(("path", "irrelevant")), failedStat);

        Assert.Equal(VerificationStatus.Inconclusive, outcome.Status);
    }

    [Fact]
    public async Task Mkdir_Fails_WhenWriteNotAllowedByPolicy()
    {
        var path = Path.Combine(_root.FullName, "denied");
        var tool = new FsMkdirTool(new FilesystemPathPolicy([Path.Combine(_root.FullName, "**")], []));

        var result = await tool.ExecuteAsync(Args(("path", path)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.False(Directory.Exists(path));
    }
}

internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Requires a real Windows ACL/Restart Manager surface.";
        }
    }
}

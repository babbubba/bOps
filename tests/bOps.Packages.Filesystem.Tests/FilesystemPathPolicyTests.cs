// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Filesystem.Tests;

/// <summary>
/// Rule S11: the path policy is checked on the fully resolved path, immediately before the
/// operation, and denies by default. These tests run against the real filesystem — a temp
/// directory this process actually owns — never a mocked one (agentic/04-testing-rules.md:
/// Filesystem is a platform package; never mock the OS).
/// </summary>
public sealed class FilesystemPathPolicyTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bops-fs-policy-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void NoPatternMatches_DeniesByDefault()
    {
        var policy = new FilesystemPathPolicy(readPatterns: [], writePatterns: []);
        var resolved = FilesystemPathPolicy.Resolve(Path.Combine(_root.FullName, "anything.txt"));

        Assert.False(policy.AllowsRead(resolved));
        Assert.False(policy.AllowsWrite(resolved));
    }

    [Fact]
    public void ExactPattern_MatchesOnlyThatPath()
    {
        var file = Path.Combine(_root.FullName, "exact.txt");
        var other = Path.Combine(_root.FullName, "other.txt");
        var policy = new FilesystemPathPolicy(readPatterns: [file], writePatterns: []);

        Assert.True(policy.AllowsRead(FilesystemPathPolicy.Resolve(file)));
        Assert.False(policy.AllowsRead(FilesystemPathPolicy.Resolve(other)));
    }

    [Fact]
    public void RecursivePattern_MatchesDirectoryItselfAndEverythingNested()
    {
        var pattern = Path.Combine(_root.FullName, "**");
        var policy = new FilesystemPathPolicy(readPatterns: [pattern], writePatterns: []);

        Assert.True(policy.AllowsRead(FilesystemPathPolicy.Resolve(_root.FullName)));
        Assert.True(policy.AllowsRead(FilesystemPathPolicy.Resolve(Path.Combine(_root.FullName, "a.txt"))));
        Assert.True(policy.AllowsRead(FilesystemPathPolicy.Resolve(Path.Combine(_root.FullName, "sub", "b.txt"))));
    }

    [Fact]
    public void RecursivePattern_DoesNotMatchSiblingDirectory()
    {
        var allowed = Directory.CreateDirectory(Path.Combine(_root.FullName, "allowed"));
        var sibling = Directory.CreateDirectory(Path.Combine(_root.FullName, "allowed-but-not-really"));
        var policy = new FilesystemPathPolicy(readPatterns: [Path.Combine(allowed.FullName, "**")], writePatterns: []);

        Assert.False(policy.AllowsRead(FilesystemPathPolicy.Resolve(Path.Combine(sibling.FullName, "x.txt"))));
    }

    [Fact]
    public void WildcardSegmentPattern_MatchesOnlyWithinOneSegment()
    {
        var pattern = Path.Combine(_root.FullName, "*.log");
        var policy = new FilesystemPathPolicy(readPatterns: [pattern], writePatterns: []);

        Assert.True(policy.AllowsRead(FilesystemPathPolicy.Resolve(Path.Combine(_root.FullName, "app.log"))));
        Assert.False(policy.AllowsRead(FilesystemPathPolicy.Resolve(Path.Combine(_root.FullName, "app.txt"))));
        Assert.False(policy.AllowsRead(FilesystemPathPolicy.Resolve(Path.Combine(_root.FullName, "sub", "app.log"))));
    }

    [Fact]
    public void DotDotSegments_AreNormalizedBeforeMatching()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
        var viaDotDot = Path.Combine(sub.FullName, "..", "top.txt");
        var direct = Path.Combine(_root.FullName, "top.txt");

        Assert.Equal(FilesystemPathPolicy.Resolve(direct), FilesystemPathPolicy.Resolve(viaDotDot));
    }

    [SymlinkCapableFact]
    public void Resolve_FollowsASymlinkedDirectoryToItsRealTarget()
    {
        var allowed = Directory.CreateDirectory(Path.Combine(_root.FullName, "allowed"));
        var secret = Directory.CreateDirectory(Path.Combine(_root.FullName, "secret"));
        File.WriteAllText(Path.Combine(secret.FullName, "data.txt"), "top secret");

        Directory.CreateSymbolicLink(Path.Combine(allowed.FullName, "link"), secret.FullName);

        var policy = new FilesystemPathPolicy(readPatterns: [Path.Combine(allowed.FullName, "**")], writePatterns: []);
        var requested = Path.Combine(allowed.FullName, "link", "data.txt");
        var resolved = FilesystemPathPolicy.Resolve(requested);

        // The literal request starts with the allowed prefix, but it resolves through the
        // symlink to a path the policy never covered — this is exactly the time-of-check/
        // time-of-use gap rule S11 requires closing. A policy that matched the raw string
        // instead of the resolved path would wrongly allow this.
        Assert.StartsWith(secret.FullName, resolved, StringComparison.Ordinal);
        Assert.False(policy.AllowsRead(resolved));
    }
}

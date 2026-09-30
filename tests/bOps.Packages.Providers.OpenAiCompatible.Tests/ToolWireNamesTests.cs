// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Packages.Providers.Wire;

namespace bOps.Packages.Providers.OpenAiCompatible.Tests;

/// <summary>ADR-0038 decision 1: the closed, deterministic, injective per-request tool-name map.</summary>
public sealed partial class ToolWireNamesTests
{
    // Names in use today, including every one that already contains '_' or '-'.
    private static readonly string[] RealNames =
    [
        "fs.size", "system.crashes", "docker.inspect", "fs.delete_tree", "fs.delete_tree.prepare", "fs.delete_tree.verify",
        "linux.dpkg-status", "network.dns_query", "network.interface_stats", "network.ntp_probe", "network.port_check",
        "network.tls_probe", "system.reboot_pending", "windows.pnp-registry", "system.cpu", "host.info", "process.list",
    ];

    private static readonly string[] NonAsciiNames = ["é.x", "è.x", "é_x"];

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex StrictName();

    [Fact]
    public void H1_01_CanonicalDottedNames_BecomeProviderValidAliases()
    {
        var map = ToolWireNames.Create(RealNames);

        Assert.All(RealNames, name => Assert.Matches(StrictName(), map.ToWire(name)));
        Assert.Equal("fs_size", map.ToWire("fs.size"));
        Assert.Equal("system_crashes", map.ToWire("system.crashes"));
    }

    [Fact]
    public void H1_02_EveryAlias_ReverseMapsExactlyToItsCanonicalName()
    {
        var map = ToolWireNames.Create(RealNames);

        foreach (var name in RealNames)
        {
            Assert.True(map.TryGetCanonical(map.ToWire(name), out var canonical));
            Assert.Equal(name, canonical);
        }

        Assert.Equal(RealNames.Length, RealNames.Select(map.ToWire).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void H1_03_H1_A3_DottedAndUnderscoredNames_StayDistinct()
    {
        var map = ToolWireNames.Create(["a.b", "a_b"]);

        Assert.Equal("a_b", map.ToWire("a_b"));
        Assert.Equal("a_2Eb", map.ToWire("a.b"));
        Assert.True(map.TryGetCanonical("a_b", out var underscored));
        Assert.Equal("a_b", underscored);
        Assert.True(map.TryGetCanonical("a_2Eb", out var dotted));
        Assert.Equal("a.b", dotted);
    }

    [Fact]
    public void ThreeWayCollisionsBetweenEscapedNames_AreAllResolvedWithoutAmbiguity()
    {
        var names = new[] { "a.b", "a:b", "a b", "a_b", "a-b" };

        var map = ToolWireNames.Create(names);

        Assert.All(names, name => Assert.Matches(StrictName(), map.ToWire(name)));
        Assert.Equal(names.Length, names.Select(map.ToWire).Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name =>
        {
            Assert.True(map.TryGetCanonical(map.ToWire(name), out var back));
            Assert.Equal(name, back);
        });
    }

    [Fact]
    public void H1_A2_AWireSafeCanonicalName_IsAnExplicitIdentityMapping()
    {
        var map = ToolWireNames.Create(["docker_inspect", "fs.size"]);

        Assert.Equal("docker_inspect", map.ToWire("docker_inspect"));
        Assert.True(map.TryGetCanonical("docker_inspect", out var canonical));
        Assert.Equal("docker_inspect", canonical);
    }

    [Fact]
    public void H1_A4_TheMapIsStable_WhateverTheOrderOrRepetitionOfTheInput()
    {
        var first = ToolWireNames.Create(RealNames.Append("a_b").Append("a.b"));
        var second = ToolWireNames.Create(RealNames.Append("a.b").Append("a_b").Reverse().Concat(RealNames));

        foreach (var name in RealNames.Append("a_b").Append("a.b"))
        {
            Assert.Equal(first.ToWire(name), second.ToWire(name));
        }
    }

    [Fact]
    public void H1_A5_AliasesRespectTheProviderLengthBound()
    {
        var longest = new string('a', ToolWireNames.MaxLength);
        var map = ToolWireNames.Create([longest, "fs.size"]);
        Assert.Equal(64, map.ToWire(longest).Length);

        var tooLong = new string('a', ToolWireNames.MaxLength + 1);
        var overflow = Assert.Throws<ModelProtocolException>(() => ToolWireNames.Create([tooLong]));
        Assert.Contains("1 to 64", overflow.Message, StringComparison.Ordinal);

        // A collision group escapes to three characters per symbol, which can overflow: refused, never truncated.
        var wide = new string('x', 31) + "." + new string('y', 31);
        var wideOverflow = Assert.Throws<ModelProtocolException>(() => ToolWireNames.Create([wide, wide.Replace('.', '_')]));
        Assert.Contains(wide, wideOverflow.Message, StringComparison.Ordinal);
        Assert.Equal(63, ToolWireNames.Create([wide]).ToWire(wide).Length);
    }

    [Fact]
    public void H1_04_AnAliasThatWasNotMinted_IsNeverResolved_EvenWhenItEqualsARealCanonicalName()
    {
        var map = ToolWireNames.Create(["fs.size", "system.cpu"]);

        Assert.False(map.TryGetCanonical("fs.size", out _));
        Assert.False(map.TryGetCanonical("FS_SIZE", out _));
        Assert.False(map.TryGetCanonical("fs_size ", out _));
        Assert.False(map.TryGetCanonical("fs_sizes", out _));
        Assert.False(map.TryGetCanonical(string.Empty, out _));
    }

    [Fact]
    public void ANameThatCannotBeSentUnambiguously_FailsTheRequestBuild()
    {
        Assert.Throws<ModelProtocolException>(() => ToolWireNames.Create([string.Empty]));
        Assert.Throws<ModelProtocolException>(() => ToolWireNames.Create(["a\uD800b"]));

        // A tool literally named like an escaped alias can never share it with the tool it would shadow.
        var shadow = Assert.Throws<ModelProtocolException>(() => ToolWireNames.Create(["a.b", "a_b", "a_2Eb"]));
        Assert.Contains("a_2Eb", shadow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesOutsideAscii_AreEscapedByTheirUtf8Bytes_AndNeverCollide()
    {
        var map = ToolWireNames.Create(NonAsciiNames);

        Assert.Equal(3, NonAsciiNames.Select(map.ToWire).Distinct(StringComparer.Ordinal).Count());
        Assert.All(NonAsciiNames, name => Assert.Matches(StrictName(), map.ToWire(name)));
    }

    private static ToolManifest Tool(string name) => new()
    {
        Name = name,
        Description = $"{name}.",
        Risk = RiskLevel.Read,
        Platforms = ["linux", "windows"],
        Requires = [],
        Parameters = [],
    };

    private static ModelToolCall Rejected(string id, string rawName) =>
        new(id, rawName, ToolArguments.Empty) { ToolNameError = "rejected" };

    [Theory]
    [InlineData("")]
    [InlineData("way-too-long-a-tool-name-that-exceeds-the-sixty-four-character-provider-limit-by-far")]
    public void M1_ForRequest_ExcludesARejectedRawName_FromAliasConstruction_EvenWhenEmptyOrOverLong(string invalidRawName)
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Rejected("call-0", invalidRawName)]),
            ChatTurn.FromToolResult("call-0", "Unknown tool"),
        };
        var request = new ModelRequest("system", history, [Tool("fs.size")]);

        var map = ToolWireNames.ForRequest(request, includeOfferedTools: true);

        Assert.Equal("fs_size", map.ToWire("fs.size"));
    }

    [Fact]
    public void M1_InvalidToolPlaceholder_IsWireSafeAndNeverResolvesInTheReverseMap()
    {
        var map = ToolWireNames.Create(["fs.size", "system.cpu"]);

        var placeholder = map.InvalidToolPlaceholder;

        Assert.Matches(StrictName(), placeholder);
        Assert.False(map.TryGetCanonical(placeholder, out _));
    }

    [Fact]
    public void M1_InvalidToolPlaceholder_IsDeterministic_ForTheSameRequestNames()
    {
        var first = ToolWireNames.Create(["fs.size", "system.cpu"]);
        var second = ToolWireNames.Create(["system.cpu", "fs.size"]);

        Assert.Equal(first.InvalidToolPlaceholder, second.InvalidToolPlaceholder);
    }

    [Fact]
    public void M1_InvalidToolPlaceholder_AvoidsCollidingWithARealAliasOfThisRequest()
    {
        // A pathological but legal tool name that happens to occupy the reserved placeholder's usual spelling.
        var map = ToolWireNames.Create(["fs.size", "_bops_invalid_tool"]);

        var placeholder = map.InvalidToolPlaceholder;

        Assert.NotEqual("_bops_invalid_tool", placeholder);
        Assert.Matches(StrictName(), placeholder);
        Assert.False(map.TryGetCanonical(placeholder, out _));
    }
}

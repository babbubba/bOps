// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.Loader;
using bOps.Abstractions;

namespace bOps.Architecture.Tests;

/// <summary>
/// The frozen 1.0 public surface of <c>bOps.Abstractions</c> (ADR-0022) as a permanent test. <c>Snapshots/abstractions-1.0.txt</c>
/// was taken from the V1.0 close-out build (<c>891dae2</c>); every line of it, that is every type, base, interface, constructor,
/// method, property, event, field and enum value with its number, must still be true of the current assembly. Adding to the
/// surface is allowed; removing, retyping or renumbering is a failure. The SDK's version can move (V1.2 is
/// <c>1.2.0-preview.N</c>, additive over 1.0), the frozen part cannot.
/// </summary>
public sealed class PublicSurfaceSnapshotTests
{
    private static string[] Baseline() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Snapshots", "abstractions-1.0.txt"))
            .Where(line => line.Length > 0)
            .ToArray();

    [Fact]
    public void TheBaseline_IsTheWholeOf1_0_NotAnEmptyOrTruncatedFile()
    {
        var baseline = Baseline();

        Assert.True(baseline.Length > 500, $"The 1.0 snapshot has only {baseline.Length} lines.");
        Assert.Contains("bOps.Abstractions.RiskLevel | enum-value Critical = 4", baseline);
        Assert.Contains("bOps.Abstractions.RiskLevel | enum-value Read = 0", baseline);
        Assert.Contains(baseline, line => line.StartsWith("bOps.Abstractions.IChatModel | method ", StringComparison.Ordinal));
        Assert.Contains(baseline, line => line.StartsWith("bOps.Abstractions.ToolManifest | property ", StringComparison.Ordinal));
    }

    [Fact]
    public void Abstractions_KeepsEveryLineOfTheFrozen1_0Surface()
    {
        var current = PublicSurface.Describe(typeof(ToolManifest).Assembly).ToHashSet(StringComparer.Ordinal);

        var missing = Baseline().Where(line => !current.Contains(line)).ToList();

        Assert.True(
            missing.Count == 0,
            "The frozen 1.0 surface changed. Removed, retyped or renumbered:\n" + string.Join('\n', missing.Take(40)));
    }

    [Fact]
    public void TheDescription_SeesAnEnumValueChangeAndARemovedMember_ThatANameOnlyDiffWouldMiss()
    {
        var current = PublicSurface.Describe(typeof(ToolManifest).Assembly);

        // The check compares whole lines, so the same enum member with another number, or a method with another parameter, is a different
        // line and the old one is reported missing.
        var renumbered = current.Select(line => line == "bOps.Abstractions.RiskLevel | enum-value High = 3" ? "bOps.Abstractions.RiskLevel | enum-value High = 4" : line).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("bOps.Abstractions.RiskLevel | enum-value High = 3", renumbered);
        Assert.Contains("bOps.Abstractions.RiskLevel | enum-value High = 3", current);

        var retyped = current.Select(line => line.StartsWith("bOps.Abstractions.IChatModel | method ", StringComparison.Ordinal) ? line.Replace("CancellationToken", "String", StringComparison.Ordinal) : line).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Baseline(), line => !retyped.Contains(line));
    }

    [Fact]
    public void TheDescription_ListsOnlyWhatIsPublic()
    {
        var lines = PublicSurface.Describe(typeof(PublicSurfaceSnapshotTests).Assembly);

        Assert.Contains("bOps.Architecture.Tests.PublicSurfaceSnapshotTests | kind sealed class", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("bOps.Architecture.Tests.PublicSurface |", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(" Baseline(", StringComparison.Ordinal));
    }

    /// <summary>
    /// Not a check: writes the description of another build of the assembly, to make or remake a baseline. Set
    /// <c>BOPS_WRITE_SURFACE_FROM</c> to the assembly and <c>BOPS_WRITE_SURFACE_TO</c> to the file. Without them it does nothing.
    /// The 1.0 baseline is made once, from the V1.0 close-out build, and is never remade from a later one.
    /// </summary>
    [Fact]
    public void WriteSnapshot_WhenAsked()
    {
        var from = Environment.GetEnvironmentVariable("BOPS_WRITE_SURFACE_FROM");
        var to = Environment.GetEnvironmentVariable("BOPS_WRITE_SURFACE_TO");
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        {
            return;
        }

        var context = new AssemblyLoadContext("snapshot", isCollectible: true);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(from));
            File.WriteAllLines(to, PublicSurface.Describe(assembly));
        }
        finally
        {
            context.Unload();
        }
    }
}

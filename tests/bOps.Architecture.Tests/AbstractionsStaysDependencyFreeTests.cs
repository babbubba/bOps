// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Architecture.Tests;

/// <summary>
/// <c>bOps.Abstractions</c> is the published SDK and has zero dependencies (its package description and
/// rule A7 say so). V1.2 adds a large contract surface to it; this is the mechanical check that none of
/// it pulled in a package or another bOps assembly — only the .NET base class library.
/// </summary>
public sealed class AbstractionsStaysDependencyFreeTests
{
    [Fact]
    public void Abstractions_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = typeof(NodeId).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !IsBaseClassLibrary(name))
            .ToList();

        Assert.True(references.Count == 0,
            $"bOps.Abstractions must depend on nothing but the .NET base class library, but references: {string.Join(", ", references)}");
    }

    private static bool IsBaseClassLibrary(string name) =>
        name is "netstandard" or "mscorlib" or "System" || name.StartsWith("System.", StringComparison.Ordinal);
}

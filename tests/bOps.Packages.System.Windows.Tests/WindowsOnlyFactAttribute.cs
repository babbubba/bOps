// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process is not
/// running on Windows (agentic/04-testing-rules.md: "Tests requiring a platform are marked
/// [Trait("Platform","Linux")] / "Windows" and are skipped — visibly, never silently — where they
/// cannot run"). Mirrors <c>LinuxOnlyFactAttribute</c> in <c>bOps.Packages.System.Linux.Tests</c>;
/// referenced by name in .github/workflows/ci.yml's comment as "LinuxOnlyFactAttribute and its
/// Windows counterpart" — this is that counterpart.
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Requires a real Windows host (agentic/04-testing-rules.md — never mock the operating system).";
        }
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of <see cref="WindowsOnlyFactAttribute"/>: for data-driven tests whose
/// correctness depends on real Windows path or file-system semantics (<c>System.IO.Path</c> follows the host's rules).
/// </summary>
internal sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Requires a real Windows host (Windows path and file-system semantics — agentic/04-testing-rules.md: never mock the operating system).";
        }
    }
}

/// <summary>
/// Skips visibly on Windows: for the one guarantee that only a non-Windows host can exercise — the production tool refuses to
/// interpret a path when it is not running on Windows.
/// </summary>
internal sealed class NonWindowsHostFactAttribute : FactAttribute
{
    public NonWindowsHostFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Exercises the non-Windows-host refusal; this host is Windows.";
        }
    }
}

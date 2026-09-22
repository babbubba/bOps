// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Network.Native.Windows.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process is not
/// running on Windows (agentic/04-testing-rules.md). Mirrors
/// <c>bOps.Packages.System.Windows.Tests.WindowsOnlyFactAttribute</c>.
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

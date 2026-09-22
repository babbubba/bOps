// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Network.Native.Linux.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process is not
/// running on Linux (agentic/04-testing-rules.md). Mirrors
/// <c>bOps.Packages.System.Linux.Tests.LinuxOnlyFactAttribute</c>.
/// </summary>
internal sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = "Requires a real Linux host (agentic/04-testing-rules.md — never mock the operating system).";
        }
    }
}

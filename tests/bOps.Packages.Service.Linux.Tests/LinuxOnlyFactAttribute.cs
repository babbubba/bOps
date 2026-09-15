// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Service.Linux.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process is not
/// running on Linux. Mirrors <c>bOps.Packages.System.Linux.Tests.LinuxOnlyFactAttribute</c>. There
/// is no Linux host in this dev environment; these tests run wherever one exists (CI's
/// ubuntu-latest matrix, a real systemd machine).
/// </summary>
internal sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = "Requires a real Linux host with systemd (agentic/04-testing-rules.md — never mock the operating system).";
        }
    }
}

// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Principal;

namespace bOps.Packages.Service.Windows.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — unless this process is
/// both on Windows and running elevated (Administrator): creating and deleting a real Windows
/// Service (<c>sc.exe create</c>/<c>delete</c>) requires it. This dev environment's own session is
/// not elevated (agentic/04-testing-rules.md's "skip visibly" applies here exactly as it does for
/// <see cref="WindowsOnlyFactAttribute"/>'s missing-Windows case and Linux's missing-host case) —
/// these tests run on GitHub Actions' <c>windows-latest</c> runner, whose job process runs with
/// administrator rights by default.
/// </summary>
internal sealed class RequiresElevationFactAttribute : FactAttribute
{
    public RequiresElevationFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires a real Windows host (agentic/04-testing-rules.md — never mock the operating system).";
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Requires an elevated (Administrator) process to create/delete a real test Windows service.";
        }
    }
}

using System.Runtime.InteropServices;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process is not
/// running on Linux (agentic/04-testing-rules.md: "Tests requiring a platform are marked
/// [Trait("Platform","Linux")] / "Windows" and are skipped — visibly, never silently — where
/// they cannot run"). There is no Linux host in this dev environment; these tests run wherever
/// one exists (a container, CI's ubuntu-latest matrix from V0.5, or the Aspire AppHost).
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = "Requires a real Linux host with /proc (agentic/04-testing-rules.md — never mock the operating system).";
        }
    }
}

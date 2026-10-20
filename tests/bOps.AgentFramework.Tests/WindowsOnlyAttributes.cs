// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process is not running on Windows
/// (agentic/04-testing-rules.md). For tests that compose the governed diagnostic canary, which registers the real
/// <c>WindowsSystemEventsTool</c> and exists on Windows only (<c>KnowledgeProcess.ComposeCanary</c>).
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Requires a real Windows host: the governed diagnostic canary registers the Windows system.events tool.";
        }
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="WindowsOnlyFactAttribute"/>.</summary>
internal sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Requires a real Windows host: the governed diagnostic canary registers the Windows system.events tool.";
        }
    }
}

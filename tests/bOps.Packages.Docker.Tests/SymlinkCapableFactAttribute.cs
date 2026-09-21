// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly, never silently, when this process cannot create a symbolic link
/// (agentic/04-testing-rules.md: skipped tests must say why). Creating one typically needs an elevated Windows account or
/// Developer Mode. The capability is probed once, at construction, by actually attempting it.
/// </summary>
internal sealed class SymlinkCapableFactAttribute : FactAttribute
{
    public SymlinkCapableFactAttribute()
    {
        var probe = Directory.CreateTempSubdirectory("bops-docker-symlink-probe-");
        try
        {
            var target = Path.Combine(probe.FullName, "target");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(Path.Combine(probe.FullName, "link"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = $"This process cannot create symbolic links: {ex.Message}";
        }
        finally
        {
            probe.Delete(recursive: true);
        }
    }
}

namespace bOps.Packages.Filesystem.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when this process cannot
/// create a symbolic link (agentic/04-testing-rules.md: skipped tests must say why). Creating a
/// symlink typically needs an elevated Windows account or Developer Mode; CI's Linux runner and
/// most developer machines can, but a locked-down Windows session cannot. Mirrors
/// <c>LinuxOnlyFactAttribute</c> in <c>bOps.Packages.System.Linux.Tests</c>: the capability is
/// probed once, at construction, by actually attempting it.
/// </summary>
internal sealed class SymlinkCapableFactAttribute : FactAttribute
{
    public SymlinkCapableFactAttribute()
    {
        var probeDir = Directory.CreateTempSubdirectory("bops-symlink-probe-");
        try
        {
            var target = Path.Combine(probeDir.FullName, "target");
            Directory.CreateDirectory(target);
            var link = Path.Combine(probeDir.FullName, "link");
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = $"This process cannot create symbolic links: {ex.Message}";
        }
        finally
        {
            probeDir.Delete(recursive: true);
        }
    }
}

using System.Runtime.InteropServices;

namespace bOps.Runtime;

/// <summary>
/// The platform identifier used to match <see cref="bOps.Abstractions.ToolManifest.Platforms"/>.
/// bOps supports Windows and Linux (agentic/00-project-spec.md); macOS is a future package, not
/// a case this needs to recognize yet.
/// </summary>
public static class CurrentPlatform
{
    /// <summary>The current platform's id: <c>"windows"</c> or <c>"linux"</c>.</summary>
    public static string Id { get; } = Determine();

    private static string Determine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        throw new PlatformNotSupportedException(
            "bOps supports Windows and Linux only (agentic/00-project-spec.md).");
    }
}

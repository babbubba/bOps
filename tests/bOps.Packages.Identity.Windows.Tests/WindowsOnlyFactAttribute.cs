using System.Runtime.InteropServices;

namespace bOps.Packages.Identity.Windows.Tests;

internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Skip = "Requires a real Windows host.";
    }
}

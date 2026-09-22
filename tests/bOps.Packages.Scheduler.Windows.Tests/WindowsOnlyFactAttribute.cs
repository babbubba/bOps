using System.Runtime.InteropServices;

namespace bOps.Packages.Scheduler.Windows.Tests;

internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Skip = "Requires a real Windows host.";
    }
}

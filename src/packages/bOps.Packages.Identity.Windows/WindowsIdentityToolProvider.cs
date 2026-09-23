using System.Security.Principal;
using bOps.Abstractions;
using bOps.Packages.Identity.Core;

namespace bOps.Packages.Identity.Windows;

public sealed class WindowsIdentityToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new WindowsCurrentIdentityTool(), new WindowsUsersTool(), new WindowsGroupsTool(), new WindowsSessionsTool()];
}

public sealed class WindowsCurrentIdentityTool() : IdentityCurrentToolBase("windows")
{
    protected override Task<IdentityCurrentResult> CollectAsync(CancellationToken ct)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var groups = identity.Groups?.Select(x => x.Translate(typeof(NTAccount)).Value).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var serviceAccount = identity.IsSystem || string.Equals(identity.Name, @"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || string.Equals(identity.Name, @"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new IdentityCurrentResult(identity.Name, identity.User?.Value, principal.IsInRole(WindowsBuiltInRole.Administrator), serviceAccount, groups.Take(100).ToArray(), groups.Length, groups.Length > 100, identity.AuthenticationType));
    }
}

public sealed class WindowsUsersTool() : IdentityUsersToolBase("windows")
{
    protected override Task<IReadOnlyList<IdentityUser>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentityUser>>([]);
}
public sealed class WindowsGroupsTool() : IdentityGroupsToolBase("windows")
{
    protected override Task<IReadOnlyList<IdentityGroup>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentityGroup>>([]);
}
public sealed class WindowsSessionsTool() : IdentitySessionsToolBase("windows")
{
    protected override Task<IReadOnlyList<IdentitySession>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentitySession>>([]);
}

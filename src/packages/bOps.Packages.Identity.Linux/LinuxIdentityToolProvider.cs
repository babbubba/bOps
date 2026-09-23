using bOps.Abstractions;

namespace bOps.Packages.Identity.Linux;

public sealed class LinuxIdentityToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new LinuxCurrentIdentityTool(), new LinuxUsersTool(), new LinuxGroupsTool(), new LinuxSessionsTool()];
}

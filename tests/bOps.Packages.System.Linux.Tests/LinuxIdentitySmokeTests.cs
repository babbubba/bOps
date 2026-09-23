using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Identity.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class LinuxIdentitySmokeTests
{
    [LinuxOnlyFact]
    public async Task LocalIdentitySourcesAreBoundedAndDoNotExposeCredentialFiles()
    {
        var tools = new LinuxIdentityToolProvider().GetTools().ToDictionary(x => x.Manifest.Name, StringComparer.Ordinal);
        var users = await tools["identity.users"].ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 20 }));
        var groups = await tools["identity.groups"].ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 20 }));
        Assert.True(users.Succeeded && groups.Succeeded);
        Assert.DoesNotContain("shadow", users.Output!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", users.Output!, StringComparison.OrdinalIgnoreCase);
    }
}

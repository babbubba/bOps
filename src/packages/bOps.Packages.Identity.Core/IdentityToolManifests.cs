using bOps.Abstractions;

namespace bOps.Packages.Identity.Core;

public static class IdentityToolManifests
{
    public static ToolManifest Current(string platform) => new()
    {
        Name = "identity.current", Description = "Reports the effective local identity without tokens, privileges, environment or credentials.", Risk = RiskLevel.Read,
        Platforms = [platform], Requires = [], Parameters = []
    };
    public static ToolManifest Users(string platform) => new()
    {
        Name = "identity.users", Description = "Lists bounded local account identities with nullable enablement.", Risk = RiskLevel.Read,
        Platforms = [platform], Requires = [], Parameters = [
            new ToolParameter("includeDisabled", ToolParameterType.Boolean, "Include accounts positively known disabled. Defaults true.", false),
            new ToolParameter("limit", ToolParameterType.Integer, "Maximum users, 1-2000, default 200.", false)]
    };
    public static ToolManifest Groups(string platform) => new()
    {
        Name = "identity.groups", Description = "Lists bounded local groups and direct members without recursive expansion.", Risk = RiskLevel.Read,
        Platforms = [platform], Requires = [], Parameters = [new ToolParameter("limit", ToolParameterType.Integer, "Maximum groups, 1-2000, default 200.", false)]
    };
    public static ToolManifest Sessions(string platform) => new()
    {
        Name = "identity.sessions", Description = "Reports bounded interactive sessions.", Risk = RiskLevel.Read,
        Platforms = [platform], Requires = [], Parameters = [new ToolParameter("limit", ToolParameterType.Integer, "Maximum sessions, 1-500, default 100.", false)]
    };
}

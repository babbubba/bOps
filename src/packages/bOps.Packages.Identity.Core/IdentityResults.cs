namespace bOps.Packages.Identity.Core;

public sealed record IdentityCurrentResult(string? User, string? UidOrSid, bool Elevated, bool ServiceAccount,
    IReadOnlyList<string> Groups, int? GroupCount, bool GroupsTruncated, string? AuthenticationType);
public sealed record IdentityUser(string Name, string? Id, bool? Enabled, bool Local, string? Home,
    string? ShellOrProfile, DateTimeOffset? LastLogonUtc, string Source);
public sealed record IdentityGroup(string Name, string? Id, int? MemberCount, IReadOnlyList<string> Members, bool MembersTruncated);
public sealed record IdentitySession(string? SessionId, string? User, string? State, DateTimeOffset? LoginTimeUtc,
    bool? Remote, string? ClientAddress, string? TtyOrStation, string Source);

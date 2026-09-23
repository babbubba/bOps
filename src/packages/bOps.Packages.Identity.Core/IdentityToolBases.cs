using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Identity.Core;

internal static class IdentityJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public abstract class IdentityCurrentToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Current(platform);
    protected abstract Task<IdentityCurrentResult> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(JsonSerializer.Serialize(await CollectAsync(ct), IdentityJson.Options));
}

public abstract class IdentityUsersToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Users(platform);
    protected abstract Task<IReadOnlyList<IdentityUser>> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var includeDisabled = !arguments.TryGet<bool>("includeDisabled", out var include) || include;
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 200;
        if (limit is < 1 or > 2000) return ToolCallResult.Failure("limit must be between 1 and 2000.");
        var rows = (await CollectAsync(ct)).Where(x => includeDisabled || x.Enabled != false).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var truncated = rows.Length > limit;
        return ToolCallResult.Success(JsonSerializer.Serialize(new { users = rows.Take(limit), observedCount = rows.Length, truncated, complete = true }, IdentityJson.Options));
    }
}

public abstract class IdentityGroupsToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Groups(platform);
    protected abstract Task<IReadOnlyList<IdentityGroup>> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 200;
        if (limit is < 1 or > 2000) return ToolCallResult.Failure("limit must be between 1 and 2000.");
        var rows = (await CollectAsync(ct)).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return ToolCallResult.Success(JsonSerializer.Serialize(new { groups = rows.Take(limit), observedCount = rows.Length, truncated = rows.Length > limit, complete = true }, IdentityJson.Options));
    }
}

public abstract class IdentitySessionsToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Sessions(platform);
    protected abstract Task<IReadOnlyList<IdentitySession>> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 100;
        if (limit is < 1 or > 500) return ToolCallResult.Failure("limit must be between 1 and 500.");
        var rows = (await CollectAsync(ct)).OrderBy(x => x.SessionId, StringComparer.Ordinal).ToArray();
        return ToolCallResult.Success(JsonSerializer.Serialize(new { sessions = rows.Take(limit), observedCount = rows.Length, truncated = rows.Length > limit, complete = true }, IdentityJson.Options));
    }
}

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
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 100;
        if (limit is < 1 or > 500) return ToolCallResult.Failure("limit must be between 1 and 500.");
        var result = await CollectAsync(ct);
        var ordered = result.Groups.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var groups = ordered.Take(limit).ToArray();
        var truncated = result.GroupsTruncated || ordered.Length > limit || result.GroupCount.HasValue && result.GroupCount.Value > groups.Length;
        return ToolCallResult.Success(JsonSerializer.Serialize(result with { Groups = groups, GroupsTruncated = truncated }, IdentityJson.Options));
    }
}

public abstract class IdentityUsersToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Users(platform);
    protected virtual Task<IReadOnlyList<IdentityUser>> CollectAsync(CancellationToken ct) => throw new NotSupportedException("Override a collector method.");
    protected virtual bool Complete => true;
    protected virtual async Task<IdentityObservation<IdentityUser>> CollectObservationAsync(CancellationToken ct) => new(await CollectAsync(ct), Complete, platform);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var includeDisabled = !arguments.TryGet<bool>("includeDisabled", out var include) || include;
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 200;
        if (limit is < 1 or > 2000) return ToolCallResult.Failure("limit must be between 1 and 2000.");
        var collection = await CollectObservationAsync(ct);
        var rows = collection.Rows.Where(x => includeDisabled || x.Enabled != false).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var truncated = rows.Length > limit;
        return ToolCallResult.Success(JsonSerializer.Serialize(new { users = rows.Take(limit), observedCount = rows.Length, truncated, complete = collection.Complete, source = collection.Source }, IdentityJson.Options));
    }
}

public abstract class IdentityGroupsToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Groups(platform);
    protected virtual Task<IReadOnlyList<IdentityGroup>> CollectAsync(CancellationToken ct) => throw new NotSupportedException("Override a collector method.");
    protected virtual bool Complete => true;
    protected virtual async Task<IdentityObservation<IdentityGroup>> CollectObservationAsync(CancellationToken ct) => new(await CollectAsync(ct), Complete, platform);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 200;
        if (limit is < 1 or > 2000) return ToolCallResult.Failure("limit must be between 1 and 2000.");
        var collection = await CollectObservationAsync(ct);
        var rows = collection.Rows.Select(x => x with { Members = x.Members.Take(100).ToArray(), MembersTruncated = x.MembersTruncated || x.Members.Count > 100, MemberCount = x.MemberCount ?? x.Members.Count }).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return ToolCallResult.Success(JsonSerializer.Serialize(new { groups = rows.Take(limit), observedCount = rows.Length, truncated = rows.Length > limit, complete = collection.Complete, source = collection.Source }, IdentityJson.Options));
    }
}

public abstract class IdentitySessionsToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = IdentityToolManifests.Sessions(platform);
    protected virtual Task<IReadOnlyList<IdentitySession>> CollectAsync(CancellationToken ct) => throw new NotSupportedException("Override a collector method.");
    protected virtual bool Complete => true;
    protected virtual async Task<IdentityObservation<IdentitySession>> CollectObservationAsync(CancellationToken ct) => new(await CollectAsync(ct), Complete, platform);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : 100;
        if (limit is < 1 or > 500) return ToolCallResult.Failure("limit must be between 1 and 500.");
        var collection = await CollectObservationAsync(ct);
        var rows = collection.Rows.OrderBy(x => x.SessionId, StringComparer.Ordinal).ToArray();
        return ToolCallResult.Success(JsonSerializer.Serialize(new { sessions = rows.Take(limit), observedCount = rows.Length, truncated = rows.Length > limit, complete = collection.Complete, source = collection.Source }, IdentityJson.Options));
    }
}

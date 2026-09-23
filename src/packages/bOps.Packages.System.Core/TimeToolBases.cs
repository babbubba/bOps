using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

internal static class TimeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public abstract class SystemTimeToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = SystemToolManifests.Time(platform);
    protected abstract Task<SystemTimeResult> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) => ToolCallResult.Success(JsonSerializer.Serialize(await CollectAsync(ct), TimeJson.Options));
}
public abstract class RebootPendingToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = SystemToolManifests.RebootPending(platform);
    protected abstract Task<RebootPendingResult> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) => ToolCallResult.Success(JsonSerializer.Serialize(await CollectAsync(ct), TimeJson.Options));
}

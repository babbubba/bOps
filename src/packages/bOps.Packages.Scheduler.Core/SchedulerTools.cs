using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Scheduler.Core;

public abstract class SchedulerListToolBase : ITool
{
    public ToolManifest Manifest { get; } = SchedulerToolManifests.List();
    protected abstract Task<SchedulerCollection<SchedulerListItem>> CollectAsync(string source, int limit, CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    { if (!SchedulerArguments.TryList(arguments, out var source, out var limit, out var error)) return ToolCallResult.Failure(error!); return ToolCallResult.Success(SchedulerFormatting.FormatList(await CollectAsync(source, limit, ct).ConfigureAwait(false), limit)); }
}
public abstract class SchedulerInspectToolBase : ITool
{
    public ToolManifest Manifest { get; } = SchedulerToolManifests.Inspect();
    protected abstract Task<SchedulerInspectResult> CollectAsync(string id, CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    { if (!SchedulerArguments.TryInspect(arguments, out var id, out var error)) return ToolCallResult.Failure(error!); return ToolCallResult.Success(SchedulerFormatting.FormatInspect(await CollectAsync(id, ct).ConfigureAwait(false))); }
}
public abstract class SchedulerHistoryToolBase : ITool
{
    public ToolManifest Manifest { get; } = SchedulerToolManifests.History();
    protected abstract Task<SchedulerHistoryCollection> CollectAsync(string id, int sinceMinutes, int limit, CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    { if (!SchedulerArguments.TryHistory(arguments, out var id, out var since, out var limit, out var error)) return ToolCallResult.Failure(error!); return ToolCallResult.Success(SchedulerFormatting.FormatHistory(await CollectAsync(id, since, limit, ct).ConfigureAwait(false), limit)); }
}
public abstract class SchedulerEnableToolBase : IVerifiableTool
{
    public ToolManifest Manifest { get; } = SchedulerToolManifests.Enable();
    protected abstract Task<ToolCallResult> EnableAsync(string id, CancellationToken ct);
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) { ArgumentNullException.ThrowIfNull(arguments); return EnableAsync(arguments.GetRequired<string>("id"), ct); }
    public Task<VerificationOutcome> EvaluateVerificationAsync(ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) => SchedulerVerification.Evaluate(verificationToolResult, true);
}
public abstract class SchedulerDisableToolBase : IVerifiableTool
{
    public ToolManifest Manifest { get; } = SchedulerToolManifests.Disable();
    protected abstract Task<ToolCallResult> DisableAsync(string id, CancellationToken ct);
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) { ArgumentNullException.ThrowIfNull(arguments); return DisableAsync(arguments.GetRequired<string>("id"), ct); }
    public Task<VerificationOutcome> EvaluateVerificationAsync(ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) => SchedulerVerification.Evaluate(verificationToolResult, false);
}
internal static class SchedulerVerification
{
    public static Task<VerificationOutcome> Evaluate(ToolCallResult result, bool expected)
    {
        if (!result.Succeeded) return Task.FromResult(new VerificationOutcome(VerificationStatus.Inconclusive, "scheduler.inspect failed or was unavailable."));
        try
        {
            var output = JsonNode.Parse(result.Output!)?.AsObject();
            if (output is null || output["complete"]?.GetValue<bool>() != true || output["enabled"] is null) return Task.FromResult(new VerificationOutcome(VerificationStatus.Inconclusive, "scheduler.inspect did not provide complete, definite enablement evidence."));
            var enabled = output["enabled"]!.GetValue<bool>();
            return Task.FromResult(enabled == expected ? new VerificationOutcome(VerificationStatus.Confirmed, null) : new VerificationOutcome(VerificationStatus.Refuted, $"scheduler.inspect reports enabled={enabled}, not {expected}."));
        }
        catch (System.Text.Json.JsonException) { return Task.FromResult(new VerificationOutcome(VerificationStatus.Inconclusive, "scheduler.inspect output was malformed.")); }
    }
}

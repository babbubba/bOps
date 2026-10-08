// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Persists each prerequisite observation and emits a <see cref="SystemMessage"/> only when its <c>state|code</c>
/// fingerprint changes (ADR-0049 section 7) — never one per check, never one per affected component. The state row and the
/// message are written atomically, guarded by a compare-and-set on the previous fingerprint, so concurrent identical
/// observations produce one message.
/// </summary>
public sealed class PrerequisiteTransitionRecorder(IPrerequisiteStateStore store, NodeId node)
{
    /// <summary>The most affected components listed in one message's metadata; the full count is always included.</summary>
    public const int MaxAffectedComponents = 16;

    private const int MaxAttempts = 3;
    private const string DetailPrefix = "detail.";

    /// <summary>
    /// Records <paramref name="result"/> and returns the transition message it produced, or <c>null</c> when nothing
    /// meaningful changed. An <see cref="PrerequisiteState.Unknown"/> result is never recorded.
    /// </summary>
    /// <param name="result">A host-recorded check result.</param>
    /// <param name="usage">Which registered components depend on the prerequisite, deciding the message severity.</param>
    /// <param name="ct">Cancels the store operations.</param>
    public async Task<SystemMessage?> RecordAsync(PrerequisiteCheckResult result, PrerequisiteUsage usage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(usage);

        if (result.State is PrerequisiteState.Unknown)
        {
            return null;
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var previous = await store.LoadStateAsync(node, result.Id, ct);
            var fingerprint = PrerequisiteStateRecord.ComputeFingerprint(result.State, result.Code);
            var changed = previous is null || !string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal);

            var next = new PrerequisiteStateRecord
            {
                Node = node,
                PrerequisiteId = result.Id,
                State = result.State,
                Code = result.Code,
                Message = result.Message,
                CheckedAtUtc = result.CheckedAtUtc,
                ChangedAtUtc = changed ? result.CheckedAtUtc : previous!.ChangedAtUtc,
                Metadata = result.Metadata,
            };
            var message = changed ? CreateMessage(previous, result, usage) : null;

            if (await store.SaveStateAsync(next, previous?.Fingerprint, message, ct))
            {
                return message;
            }
        }

        throw new InvalidOperationException(
            $"Prerequisite '{result.Id}' state kept changing concurrently; the observation was not recorded after {MaxAttempts} attempts.");
    }

    private SystemMessage? CreateMessage(PrerequisiteStateRecord? previous, PrerequisiteCheckResult result, PrerequisiteUsage usage)
    {
        var required = usage.RequiredBy.Count > 0;
        var concern = required ? SystemMessageSeverity.Warning : SystemMessageSeverity.Information;

        (SystemMessageSeverity Severity, string Code, string Text)? kind = result.State switch
        {
            PrerequisiteState.Available when previous is { State: not PrerequisiteState.Available } =>
                (SystemMessageSeverity.Information, PrerequisiteCodes.MessageRecovered, $"Prerequisite '{result.Id}' is available again."),
            PrerequisiteState.Available => null,
            PrerequisiteState.Unavailable => (concern, PrerequisiteCodes.MessageMissing, result.Message),
            PrerequisiteState.Degraded => (concern, PrerequisiteCodes.MessageDegraded, result.Message),
            _ => (SystemMessageSeverity.Error, PrerequisiteCodes.MessageCheckFailed, result.Message),
        };

        if (kind is not { } selected)
        {
            return null;
        }

        return new SystemMessage
        {
            Id = Guid.CreateVersion7(result.CheckedAtUtc),
            TimestampUtc = result.CheckedAtUtc,
            Node = node,
            Source = $"prerequisite/{result.Id.ToLowerInvariant()}",
            Severity = selected.Severity,
            Code = selected.Code,
            Message = selected.Text,
            Metadata = CreateMetadata(previous, result, usage),
            ComponentType = SystemComponentType.Prerequisite,
            ComponentId = result.Id,
        };
    }

    private static OperationalMetadata CreateMetadata(PrerequisiteStateRecord? previous, PrerequisiteCheckResult result, PrerequisiteUsage usage)
    {
        var affected = usage.RequiredBy.Concat(usage.OptionalBy).Select(component => component.ToString()).Distinct(StringComparer.Ordinal).ToArray();
        var fixedEntries = new JsonObject
        {
            ["prerequisiteId"] = result.Id,
            ["state"] = result.State.ToString(),
            ["previousState"] = previous?.State.ToString(),
            ["checkCode"] = result.Code,
            ["requirement"] = usage.RequiredBy.Count > 0 ? "required" : usage.OptionalBy.Count > 0 ? "optional" : "unused",
            ["affectedComponentCount"] = affected.Length,
            ["affectedComponents"] = new JsonArray(affected.Take(MaxAffectedComponents).Select(id => (JsonNode?)Truncate(id)).ToArray()),
        };

        // The check's own detail is carried when it still fits every bound; the fixed entries above always do.
        var withDetail = fixedEntries.DeepClone().AsObject();
        foreach (var (key, value) in result.Metadata.ToJson())
        {
            if ((DetailPrefix + key).Length <= OperationalMetadata.MaxKeyLength)
            {
                withDetail[DetailPrefix + key] = value?.DeepClone();
            }
        }

        return OperationalMetadata.TryFrom(withDetail, out var metadata, out _) ? metadata : OperationalMetadata.From(fixedEntries);
    }

    private static string Truncate(string value) =>
        value.Length <= OperationalMetadata.MaxStringLength ? value : value[..OperationalMetadata.MaxStringLength];
}

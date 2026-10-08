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

    /// <summary>The most remediation characters a message's text carries; the metadata copy is bounded by <see cref="OperationalMetadata.MaxStringLength"/>.</summary>
    public const int MaxRemediationTextLength = 600;

    private const int MaxAttempts = 3;
    private const string DetailPrefix = "detail.";
    private const string DescriptorPrefix = "descriptor.";

    /// <summary>
    /// Records <paramref name="result"/> and returns the transition message it produced, or <c>null</c> when nothing
    /// meaningful changed. An <see cref="PrerequisiteState.Unknown"/> result is never recorded.
    /// </summary>
    /// <param name="result">A host-recorded check result.</param>
    /// <param name="usage">Which registered components depend on the prerequisite, deciding the message severity.</param>
    /// <param name="descriptor">
    /// The registered descriptor of the prerequisite, when known. Its display name, kind and remediation make the message actionable
    /// for an operator; a changed descriptor never changes the transition fingerprint.
    /// </param>
    /// <param name="ct">Cancels the store operations.</param>
    public async Task<SystemMessage?> RecordAsync(
        PrerequisiteCheckResult result, PrerequisiteUsage usage, PrerequisiteDescriptor? descriptor = null, CancellationToken ct = default)
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
            var message = changed ? CreateMessage(previous, result, usage, descriptor) : null;

            if (await store.SaveStateAsync(next, previous?.Fingerprint, message, ct))
            {
                return message;
            }
        }

        throw new InvalidOperationException(
            $"Prerequisite '{result.Id}' state kept changing concurrently; the observation was not recorded after {MaxAttempts} attempts.");
    }

    private SystemMessage? CreateMessage(
        PrerequisiteStateRecord? previous, PrerequisiteCheckResult result, PrerequisiteUsage usage, PrerequisiteDescriptor? descriptor)
    {
        var required = usage.RequiredBy.Count > 0;
        var concern = required ? SystemMessageSeverity.Warning : SystemMessageSeverity.Information;

        (SystemMessageSeverity Severity, string Code, string Text)? kind = result.State switch
        {
            PrerequisiteState.Available when previous is { State: not PrerequisiteState.Available } =>
                (SystemMessageSeverity.Information, PrerequisiteCodes.MessageRecovered, $"{Name(result, descriptor)} is available again."),
            PrerequisiteState.Available => null,
            PrerequisiteState.Unavailable => (concern, PrerequisiteCodes.MessageMissing, Actionable(result, descriptor, "is unavailable")),
            PrerequisiteState.Degraded => (concern, PrerequisiteCodes.MessageDegraded, Actionable(result, descriptor, "is degraded")),
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
            Metadata = CreateMetadata(previous, result, usage, descriptor),
            ComponentType = SystemComponentType.Prerequisite,
            ComponentId = result.Id,
        };
    }

    private static OperationalMetadata CreateMetadata(
        PrerequisiteStateRecord? previous, PrerequisiteCheckResult result, PrerequisiteUsage usage, PrerequisiteDescriptor? descriptor)
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

        if (descriptor is not null)
        {
            fixedEntries["displayName"] = descriptor.DisplayName;
            fixedEntries["kind"] = descriptor.Kind.ToString();
            if (!string.IsNullOrWhiteSpace(descriptor.Remediation))
            {
                fixedEntries["remediation"] = Truncate(descriptor.Remediation.Trim());
            }
        }

        // The descriptor's static facts and the check's own detail are carried when they still fit every bound; the fixed entries
        // above always do (the descriptor strings are bounded by their own contract, remediation is truncated to the metadata limit).
        var withDetail = fixedEntries.DeepClone().AsObject();
        AddPrefixed(withDetail, DescriptorPrefix, descriptor?.Metadata);
        AddPrefixed(withDetail, DetailPrefix, result.Metadata);

        return OperationalMetadata.TryFrom(withDetail, out var metadata, out _) ? metadata : OperationalMetadata.From(fixedEntries);
    }

    private static void AddPrefixed(JsonObject target, string prefix, OperationalMetadata? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source.ToJson())
        {
            if ((prefix + key).Length <= OperationalMetadata.MaxKeyLength)
            {
                target[prefix + key] = value?.DeepClone();
            }
        }
    }

    private static string Name(PrerequisiteCheckResult result, PrerequisiteDescriptor? descriptor) =>
        descriptor is null ? $"Prerequisite '{result.Id}'" : descriptor.DisplayName;

    /// <summary>
    /// The operator-facing text: the display name, what happened, and the descriptor's remediation, within
    /// <see cref="SystemMessage"/>'s bound. Without a descriptor the check's own message is used as before. The check's free-text
    /// message is not repeated here (it stays in the persisted state and the readiness view), so a path or detail a check mentions is
    /// not duplicated into the inbox.
    /// </summary>
    private static string Actionable(PrerequisiteCheckResult result, PrerequisiteDescriptor? descriptor, string whatHappened)
    {
        if (descriptor is null)
        {
            return result.Message;
        }

        var text = $"{descriptor.DisplayName} {whatHappened}.";
        if (!string.IsNullOrWhiteSpace(descriptor.Remediation))
        {
            var remediation = descriptor.Remediation.Trim();
            text += " " + (remediation.Length <= MaxRemediationTextLength ? remediation : remediation[..(MaxRemediationTextLength - 1)] + "…");
        }

        return text.Length <= SystemMessage.MaxMessageLength ? text : text[..(SystemMessage.MaxMessageLength - 1)] + "…";
    }

    private static string Truncate(string value) =>
        value.Length <= OperationalMetadata.MaxStringLength ? value : value[..OperationalMetadata.MaxStringLength];
}

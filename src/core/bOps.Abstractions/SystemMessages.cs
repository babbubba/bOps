// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>How urgently an administrator should look at a <see cref="SystemMessage"/>. There is no Debug or Trace level.</summary>
public enum SystemMessageSeverity
{
    /// <summary>Something worth knowing; no action needed (a prerequisite recovered).</summary>
    Information = 0,

    /// <summary>Something is reduced or missing and may need action (a required prerequisite is unavailable).</summary>
    Warning = 1,

    /// <summary>Something failed (a prerequisite check could not run).</summary>
    Error = 2,

    /// <summary>bOps cannot operate correctly until an administrator acts.</summary>
    Critical = 3,
}

/// <summary>The kind of component a <see cref="SystemMessage"/> is about.</summary>
public enum SystemComponentType
{
    /// <summary>The bOps runtime itself.</summary>
    Runtime = 0,

    /// <summary>A Tool, identified by its manifest name.</summary>
    Tool = 1,

    /// <summary>A Skill Capability, identified as <c>skillId/capabilityName</c>.</summary>
    SkillCapability = 2,

    /// <summary>A Skill, identified by its Skill id.</summary>
    Skill = 3,

    /// <summary>A package or plugin, identified by its host-assigned package id.</summary>
    Package = 4,

    /// <summary>A model provider, identified by its provider id.</summary>
    ModelProvider = 5,

    /// <summary>A prerequisite, identified by its prerequisite id.</summary>
    Prerequisite = 6,
}

/// <summary>
/// An operator-facing operational notice (ADR-0049 section 5) — something a bOps administrator should see. It is not
/// application logging, not audit (it proves nothing and is not hash-chained) and not model evidence (it never enters a
/// model context). Every text and metadata field is shown to administrators and persisted, so none may carry a secret.
/// </summary>
public sealed record SystemMessage
{
    /// <summary>The maximum length of <see cref="Message"/>.</summary>
    public const int MaxMessageLength = 1024;

    /// <summary>The maximum length of <see cref="ComponentId"/>.</summary>
    public const int MaxComponentIdLength = 256;

    /// <summary>A unique id. Producers use <see cref="Guid.CreateVersion7(DateTimeOffset)"/> so ties sort chronologically.</summary>
    public required Guid Id { get; init; }

    /// <summary>When the condition was observed.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The node the condition was observed on.</summary>
    public required NodeId Node { get; init; }

    /// <summary>Who raised it, as <c>&lt;kind&gt;/&lt;name&gt;</c>; see <see cref="OperationalIdentifier.IsValidSource"/>.</summary>
    public required string Source { get; init; }

    /// <summary>How urgent it is.</summary>
    public required SystemMessageSeverity Severity { get; init; }

    /// <summary>A stable machine-readable code, e.g. <c>prerequisite.missing</c>; see <see cref="OperationalIdentifier.IsValidCode"/>.</summary>
    public required string Code { get; init; }

    /// <summary>The operator-facing text, at most <see cref="MaxMessageLength"/> characters.</summary>
    public required string Message { get; init; }

    /// <summary>Bounded, non-secret detail.</summary>
    public OperationalMetadata Metadata { get; init; } = OperationalMetadata.Empty;

    /// <summary>The task the message concerns, if any.</summary>
    public Guid? TaskId { get; init; }

    /// <summary>The kind of component the message concerns, if any.</summary>
    public SystemComponentType? ComponentType { get; init; }

    /// <summary>The component the message concerns, if any. Requires <see cref="ComponentType"/>.</summary>
    public string? ComponentId { get; init; }

    /// <summary>Throws <see cref="ArgumentException"/> when any field violates its format or bound.</summary>
    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("A system message id cannot be empty.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Node.Value))
        {
            throw new ArgumentException("A system message must name its node.", nameof(Node));
        }

        if (!OperationalIdentifier.IsValidSource(Source))
        {
            throw new ArgumentException($"'{Source}' is not a valid system message source.", nameof(Source));
        }

        if (!Enum.IsDefined(Severity))
        {
            throw new ArgumentException($"'{Severity}' is not a defined severity.", nameof(Severity));
        }

        if (!OperationalIdentifier.IsValidCode(Code))
        {
            throw new ArgumentException($"'{Code}' is not a valid system message code.", nameof(Code));
        }

        if (string.IsNullOrWhiteSpace(Message) || Message.Length > MaxMessageLength)
        {
            throw new ArgumentException($"A system message text must be 1–{MaxMessageLength} characters.", nameof(Message));
        }

        ArgumentNullException.ThrowIfNull(Metadata);

        if (ComponentId is not null
            && (ComponentType is null || string.IsNullOrWhiteSpace(ComponentId) || ComponentId.Length > MaxComponentIdLength))
        {
            throw new ArgumentException(
                $"A component id needs a component type and must be 1–{MaxComponentIdLength} characters.", nameof(ComponentId));
        }

        if (ComponentType is { } type && !Enum.IsDefined(type))
        {
            throw new ArgumentException($"'{type}' is not a defined component type.", nameof(ComponentType));
        }
    }
}

/// <summary>
/// A page request over stored system messages. Every supplied filter must match (logical AND). Results are ordered
/// <c>TimestampUtc DESC, Id DESC</c> and paged by an opaque keyset cursor, never by offset.
/// </summary>
public sealed record SystemMessageQuery
{
    /// <summary>The page size used when none is given.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page size a store accepts.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The maximum length of <see cref="Text"/>.</summary>
    public const int MaxTextLength = 256;

    /// <summary>Only messages at or after this instant (inclusive).</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Only messages at or before this instant (inclusive).</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Only messages of exactly this severity.</summary>
    public SystemMessageSeverity? Severity { get; init; }

    /// <summary>Only messages whose <see cref="SystemMessage.Message"/> contains this text, compared case-insensitively.</summary>
    public string? Text { get; init; }

    /// <summary>How many messages to return, 1–<see cref="MaxPageSize"/>.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>The <see cref="SystemMessagePage.NextCursor"/> of the previous page, or <c>null</c> for the first page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Throws <see cref="ArgumentException"/> when the query is out of bounds.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PageSize, 1, nameof(PageSize));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PageSize, MaxPageSize, nameof(PageSize));

        if (FromUtc is { } from && ToUtc is { } to && from > to)
        {
            throw new ArgumentException("FromUtc must not be later than ToUtc.", nameof(FromUtc));
        }

        if (Severity is { } severity && !Enum.IsDefined(severity))
        {
            throw new ArgumentException($"'{severity}' is not a defined severity.", nameof(Severity));
        }

        if (Text is not null && (Text.Length == 0 || Text.Length > MaxTextLength))
        {
            throw new ArgumentException($"Text must be 1–{MaxTextLength} characters.", nameof(Text));
        }
    }
}

/// <summary>One page of system messages, newest first.</summary>
/// <param name="Items">The messages on this page.</param>
/// <param name="NextCursor">The cursor for the next page, or <c>null</c> when this is the last page.</param>
public sealed record SystemMessagePage(IReadOnlyList<SystemMessage> Items, string? NextCursor);

/// <summary>System message retention defaults (ADR-0049 section 8).</summary>
public static class SystemMessageRetention
{
    /// <summary>How long a system message is kept when the operator configures nothing else: 90 days.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromDays(90);
}

/// <summary>Node-local persistent storage and query of system messages (ADR-0049 section 8).</summary>
public interface ISystemMessageStore
{
    /// <summary>Validates and appends one message. An id that already exists is an error.</summary>
    Task AppendAsync(SystemMessage message, CancellationToken ct = default);

    /// <summary>Returns one page matching <paramref name="query"/>. An unreadable cursor is an <see cref="ArgumentException"/>.</summary>
    Task<SystemMessagePage> QueryAsync(SystemMessageQuery query, CancellationToken ct = default);

    /// <summary>Deletes every message strictly older than <paramref name="cutoffUtc"/> and returns how many were deleted.</summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}

// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Operator configuration for governed historical-context summarization (ADR-0052, AF-03).
/// A summary is derived, untrusted, non-authoritative context. When <see cref="Provider"/> and
/// <see cref="Model"/> are set the operator explicitly consents to send historical operational
/// context to that destination; bOps never selects a summarizer by cost, speed or availability.
/// </summary>
public sealed record SummarizationOptions
{
    /// <summary>Opt-in switch. Off by default; with it off no summary inference can occur.</summary>
    public bool Enabled { get; init; }

    /// <summary>Dedicated summarizer provider id; <c>null</c> means the current provider of the task.</summary>
    public string? Provider { get; init; }

    /// <summary>Dedicated summarizer model id; <c>null</c> means the current model of the task.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Summarization is considered only while the estimated history exceeds this fraction of the
    /// candidate history budget after tool-result compaction. Initial tuning default, verified in AF-05.
    /// </summary>
    public double TriggerRatio { get; init; } = 0.75;

    /// <summary>
    /// Most-recent historical groups kept verbatim by the summarizer. Initial tuning default, verified in AF-05.
    /// </summary>
    public int RecentGroupsToPreserve { get; init; } = 4;

    /// <summary>Whether an explicit summarizer destination is configured.</summary>
    public bool IsDedicated => Provider is not null || Model is not null;

    internal void Validate(ModelContextOptions context)
    {
        if (Provider is not null && string.IsNullOrWhiteSpace(Provider))
        {
            throw new InvalidOperationException("'Agent:Summarization:Provider' must be omitted or non-empty.");
        }

        if (Model is not null && string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("'Agent:Summarization:Model' must be omitted or non-empty.");
        }

        if ((Provider is null) != (Model is null))
        {
            throw new InvalidOperationException(
                "'Agent:Summarization:Provider' and 'Agent:Summarization:Model' must be set together or both omitted.");
        }

        if (!(TriggerRatio > 0.0 && TriggerRatio <= 1.0))
        {
            throw new InvalidOperationException("'Agent:Summarization:TriggerRatio' must be greater than 0 and at most 1.");
        }

        if (RecentGroupsToPreserve is < 1 or > 64)
        {
            throw new InvalidOperationException("'Agent:Summarization:RecentGroupsToPreserve' must be from 1 through 64.");
        }

        if (Enabled && IsDedicated
            && !context.Profiles.Any(profile => profile.MaxContextTokens is not null
                && string.Equals(profile.Provider, Provider, StringComparison.Ordinal)
                && string.Equals(profile.Model, Model, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"'Agent:Summarization' names {Provider}/{Model} but 'Agent:Context:Profiles' has no exact profile with a known "
                + "MaxContextTokens for it; a dedicated summarizer needs its own context budget and none is guessed.");
        }
    }
}

/// <summary>
/// The per-logical-call summarization state shared across the projection attempts of one broker call: the
/// summary-only token ledger and a response cache so a retry or fallback re-projection never pays twice for the same input.
/// </summary>
public sealed class ModelSummarizationLedger
{
    private readonly Dictionary<string, CachedSummary> cache = new(StringComparer.Ordinal);

    /// <summary>Summary inference calls actually sent to a model.</summary>
    public int Calls { get; private set; }

    /// <summary>Provider-reported summary prompt tokens.</summary>
    public long PromptTokens { get; private set; }

    /// <summary>Provider-reported summary completion tokens.</summary>
    public long CompletionTokens { get; private set; }

    /// <summary>Wall-clock time spent in summary inference.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>All summary tokens, which count toward the task total.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// The summed provider-reported estimated cost of the summary calls that reported one, or <c>null</c> when none did. A cost is
    /// never invented for a call whose provider reported none, so this is a lower bound when only some calls report.
    /// </summary>
    public decimal? EstimatedCostUsd { get; private set; }

    /// <summary>Records one summary inference.</summary>
    public void Record(ModelUsage? usage, TimeSpan elapsed)
    {
        Calls++;
        PromptTokens += usage?.PromptTokens ?? 0;
        CompletionTokens += usage?.CompletionTokens ?? 0;
        if (usage?.EstimatedCostUsd is { } cost)
        {
            EstimatedCostUsd = (EstimatedCostUsd ?? 0m) + cost;
        }

        Duration += elapsed;
    }

    /// <summary>Stable cache key over the destination and exact input; the input is hashed, never retained.</summary>
    public static string Key(ChatModelDescriptor destination, string system, string transcript)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{destination.ProviderId}\u001f{destination.ModelId}\u001f{system}\u001f{transcript}")));
    }

    /// <summary>Returns a previously produced outcome for the same destination and input.</summary>
    public bool TryGet(string key, out CachedSummary? summary) => cache.TryGetValue(key, out summary);

    /// <summary>Remembers one outcome (text for success, or a safe failure reason).</summary>
    public void Remember(string key, CachedSummary summary) => cache[key] = summary;
}

/// <summary>One memoized summarizer outcome; <see cref="Text"/> is null when it failed.</summary>
public sealed record CachedSummary(string? Text, string? FailureReason);

/// <summary>
/// Everything the isolated MAF integration needs to run one governed summary call. The broker owns it, so the
/// summarizer inherits timeout, retry, audit, recording and accounting from the same governance as task inference.
/// </summary>
public sealed class ModelSummarizationAccess
{
    /// <summary>Creates the access for one projection attempt.</summary>
    public ModelSummarizationAccess(
        IModelInvocationBroker broker,
        ChatModelDescriptor descriptor,
        bool dedicated,
        ModelContextProfile? profile,
        Func<ModelInvocationContext> contextFactory,
        ModelSummarizationLedger ledger,
        SummarizationOptions options)
    {
        Broker = broker ?? throw new ArgumentNullException(nameof(broker));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Dedicated = dedicated;
        Profile = profile;
        ContextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The governed broker every summary inference must use.</summary>
    public IModelInvocationBroker Broker { get; }

    /// <summary>The summarizer destination, for budgeting and non-sensitive telemetry.</summary>
    public ChatModelDescriptor Descriptor { get; }

    /// <summary>True when the operator configured an explicit summarizer destination.</summary>
    public bool Dedicated { get; }

    /// <summary>The summarizer own exact context profile, or <c>null</c> when none is configured (never guessed).</summary>
    public ModelContextProfile? Profile { get; }

    /// <summary>Creates the task-scoped invocation context for a summary call (same task, records, accounting and budgets).</summary>
    public Func<ModelInvocationContext> ContextFactory { get; }

    /// <summary>The shared per-logical-call ledger.</summary>
    public ModelSummarizationLedger Ledger { get; }

    /// <summary>The validated operator options.</summary>
    public SummarizationOptions Options { get; }
}

/// <summary>Non-content summary measurements for one projection.</summary>
public sealed record ModelSummaryStats(
    string Outcome,
    string? Reason,
    int GroupsBefore,
    int GroupsAfter,
    int InputEstimatedTokens,
    int OutputEstimatedTokens);

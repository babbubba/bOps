// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using BopsChatRole = bOps.Abstractions.ChatRole;

namespace bOps.AgentFramework;

/// <summary>Why the final context gate refused a request (ADR-0054 G2-1). The names are the stable codes written to the audit.</summary>
internal enum ContextGateCode
{
    /// <summary>The effective context carries a message replayed from a MAF chat history.</summary>
    HistoryMessage,

    /// <summary>The effective context carries a message a MAF context provider added that is not the one provider the host admitted.</summary>
    UnadmittedProvider,

    /// <summary>A System message is not one of the messages bOps projected.</summary>
    SystemElevation,

    /// <summary>The standing instructions differ from the protected instructions bOps projected.</summary>
    ProtectedInstructionsModified,

    /// <summary>The effective messages are not exactly the bOps-projected context.</summary>
    ContextMismatch,

    /// <summary>A message cannot be represented by the bOps conversation contract.</summary>
    UnrepresentableContent,

    /// <summary>A tool the model could be offered can be invoked by the framework instead of only being declared.</summary>
    ToolInvocable,

    /// <summary>A tool is not a plain declaration (for example a hosted tool).</summary>
    ToolNotDeclaration,

    /// <summary>A declared tool is not on the step's allow-list.</summary>
    ToolOutsideAllowList,

    /// <summary>The request uses a stateful, background or session-bound response.</summary>
    StatefulSession,

    /// <summary>The caller tries to choose the model.</summary>
    ModelOverride,

    /// <summary>The request carries generation options the bOps model contract does not represent.</summary>
    UnsupportedOptions,
}

/// <summary>The outcome of the gate: the request bOps authorised, or the reason it refused.</summary>
internal sealed record ContextGateResult(ModelRequest? Request, ContextGateCode? Code, string? Detail)
{
    public bool Accepted => Request is not null;

    public static ContextGateResult Accept(ModelRequest request) => new(request, null, null);

    public static ContextGateResult Reject(ContextGateCode code, string detail) => new(null, code, detail);
}

/// <summary>
/// The final context check (ADR-0054 G2-1), run in <see cref="GovernedChatClient"/> on the fully merged MAF request, before it is
/// mapped and before the broker. It is deliberately not an inspection of labels. Authority is established only by comparing the
/// effective context against the request bOps itself authorised for this call: a message is admitted when, and only when, it is
/// exactly the next bOps-projected turn, and it then enters the broker as that bOps turn, with the provenance bOps assigned to it.
/// A MAF source attribution, <c>AdditionalProperties</c>, a message id or a provider name is never read to grant anything; the
/// attribution is read only to refuse a message with a more specific reason. Without an admitted provider any message a provider or
/// a chat history adds is refused; with one (AF-TURN-01B) the host names the single bOps-authored provider instance it composed
/// for this call, a message tagged by anything else is refused, and a message the admitted provider tagged still has to equal the
/// next bOps-projected turn: admission lets a message be compared, it never lets one be believed.
/// </summary>
internal static class ContextGate
{
    public static ContextGateResult Evaluate(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        ModelRequest authorized,
        ChatModelDescriptor descriptor,
        string? admittedProviderId = null)
    {
        if (CheckOptions(options, descriptor) is { } optionsRejection)
        {
            return optionsRejection;
        }

        foreach (var message in messages)
        {
            var source = message.GetAgentRequestMessageSourceType();
            if (source == AgentRequestMessageSourceType.ChatHistory)
            {
                return ContextGateResult.Reject(ContextGateCode.HistoryMessage,
                    "A message comes from a MAF chat history; governed turns are stateless.");
            }

            if (source == AgentRequestMessageSourceType.AIContextProvider
                && admittedProviderId is not null
                && string.Equals(message.GetAgentRequestMessageSourceId(), admittedProviderId, StringComparison.Ordinal))
            {
                continue; // the host-composed bOps provider; the exact comparison below still decides
            }

            if (source != AgentRequestMessageSourceType.External)
            {
                return ContextGateResult.Reject(ContextGateCode.UnadmittedProvider,
                    "A message was added by a MAF context provider that the host did not admit for this call.");
            }
        }

        if (!string.Equals(options?.Instructions ?? string.Empty, authorized.SystemPrompt, StringComparison.Ordinal))
        {
            return ContextGateResult.Reject(ContextGateCode.ProtectedInstructionsModified,
                "The standing instructions are not the protected instructions bOps projected.");
        }

        if (CheckTools(options, authorized) is { } toolRejection)
        {
            return toolRejection;
        }

        IReadOnlyList<ChatTurn> effective;
        try
        {
            effective = MafChatTurnMapper.FromMaf(messages);
        }
        catch (NotSupportedException)
        {
            return ContextGateResult.Reject(ContextGateCode.UnrepresentableContent,
                "A message cannot be represented by the bOps conversation contract.");
        }

        var expected = authorized.History;
        for (var index = 0; index < Math.Max(effective.Count, expected.Count); index++)
        {
            if (index < effective.Count && index < expected.Count && SameTurn(effective[index], expected[index]))
            {
                continue;
            }

            var offending = index < effective.Count ? effective[index] : null;
            return offending?.Role == BopsChatRole.System
                ? ContextGateResult.Reject(ContextGateCode.SystemElevation,
                    $"The System message at position {index} is not a bOps-projected message.")
                : ContextGateResult.Reject(ContextGateCode.ContextMismatch,
                    $"The effective context differs from the bOps-projected context at position {index} "
                    + $"({effective.Count} effective, {expected.Count} projected).");
        }

        // The tool surface that reaches the broker is what MAF declared, and the checks above proved it is within the allow-list.
        var declared = options?.Tools?.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var tools = authorized.AvailableTools.Where(tool => declared.Contains(tool.Name)).ToArray();
        return ContextGateResult.Accept(new ModelRequest(authorized.SystemPrompt, expected, tools));
    }

    private static ContextGateResult? CheckOptions(ChatOptions? options, ChatModelDescriptor descriptor)
    {
        if (options is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(options.ModelId)
            && !string.Equals(options.ModelId, descriptor.ModelId, StringComparison.Ordinal))
        {
            return ContextGateResult.Reject(ContextGateCode.ModelOverride,
                "A MAF caller cannot override the provider/model selected and pinned by bOps.");
        }

        if (!string.IsNullOrEmpty(options.ConversationId)
            || options.ContinuationToken is not null
            || options.AllowBackgroundResponses == true)
        {
            return ContextGateResult.Reject(ContextGateCode.StatefulSession,
                "Stateful or background MAF responses are not enabled.");
        }

        if (options.Temperature is not null
            || options.MaxOutputTokens is not null
            || options.TopP is not null
            || options.TopK is not null
            || options.FrequencyPenalty is not null
            || options.PresencePenalty is not null
            || options.Seed is not null
            || options.Reasoning is not null
            || options.ResponseFormat is not null
            || options.StopSequences is { Count: > 0 }
            || options.RawRepresentationFactory is not null
            || options.AdditionalProperties is { Count: > 0 })
        {
            return ContextGateResult.Reject(ContextGateCode.UnsupportedOptions,
                "This MAF request uses generation options that the bOps model contract does not represent.");
        }

        return null;
    }

    private static ContextGateResult? CheckTools(ChatOptions? options, ModelRequest authorized)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return null;
        }

        var allowed = authorized.AvailableTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            // An AIFunction can be invoked by the framework; only a bare declaration is inert. Operational authority stays with bOps.
            if (tool is AIFunction)
            {
                return ContextGateResult.Reject(ContextGateCode.ToolInvocable,
                    "A MAF tool that the framework could invoke is not allowed; tools are declarations only.");
            }

            if (tool is not AIFunctionDeclaration declaration)
            {
                return ContextGateResult.Reject(ContextGateCode.ToolNotDeclaration,
                    "A MAF tool that is not a plain function declaration is not allowed.");
            }

            if (!allowed.Contains(declaration.Name))
            {
                return ContextGateResult.Reject(ContextGateCode.ToolOutsideAllowList,
                    "A declared tool is not on the step's allow-list.");
            }
        }

        return null;
    }

    /// <summary>Content equality of two turns. Provenance is deliberately not compared: the effective turn has none to trust.</summary>
    private static bool SameTurn(ChatTurn effective, ChatTurn expected)
    {
        if (effective.Role != expected.Role
            || !string.Equals(effective.Content, expected.Content, StringComparison.Ordinal)
            || !string.Equals(effective.ToolCallId, expected.ToolCallId, StringComparison.Ordinal))
        {
            return false;
        }

        var left = effective.ToolCalls ?? [];
        var right = expected.ToolCalls ?? [];
        return left.Count == right.Count
            && left.Zip(right).All(pair =>
                string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                && string.Equals(pair.First.ToolName, pair.Second.ToolName, StringComparison.Ordinal)
                && string.Equals(pair.First.Arguments.ToJson().ToJsonString(), pair.Second.Arguments.ToJson().ToJsonString(), StringComparison.Ordinal)
                && string.Equals(pair.First.ToolNameError, pair.Second.ToolNameError, StringComparison.Ordinal)
                && string.Equals(pair.First.ArgumentsError, pair.Second.ArgumentsError, StringComparison.Ordinal));
    }
}

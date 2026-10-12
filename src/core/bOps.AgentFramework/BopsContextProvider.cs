// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace bOps.AgentFramework;

/// <summary>
/// How the model-facing context of a governed MAF turn is handed to the agent (AF-TURN-01B).
/// </summary>
public enum MafContextAssembly
{
    /// <summary>The agent is given every turn as its input and the standing instructions as options. The AF-TURN-01A shape.</summary>
    InputOnly = 0,

    /// <summary>
    /// The runtime-protected instructions and the protected turns before and after the history come from one bOps-authored
    /// context provider, composed by the host for this call; the agent input is the historical turns. The request that reaches
    /// the broker is still the one bOps authorised.
    /// </summary>
    BopsProvider = 1,
}

/// <summary>
/// The one MAF context provider bOps admits (AF-TURN-01B, ADR-0054 section 4): bOps-authored, constructed per call from the
/// protected context the runtime already projected, never from anything external. It generates no prompt of its own: its content is
/// the <see cref="ModelContextRequest"/> the runner built, split exactly where the runner split it, so the effective request is
/// the current projection and not a second rendering of it.
///
/// It overrides <c>InvokingCoreAsync</c> only to keep the runner's order (protected turns, then history, then continuation turns),
/// because the framework appends provider messages after the input. It stamps its messages with the id the host assigned; that stamp
/// is a label the final context gate may use to admit a message for comparison, never a source of authority. Provenance is not
/// carried in any MAF message: it stays on the <see cref="ChatTurn"/>s held here and in the authorised request.
/// </summary>
internal sealed class BopsContextProvider : AIContextProvider
{
    private readonly string protectedInstructions;
    private readonly IReadOnlyList<ChatTurn> before;
    private readonly IReadOnlyList<ChatTurn> after;

    internal BopsContextProvider(
        string providerId, string protectedInstructions, IReadOnlyList<ChatTurn> before, IReadOnlyList<ChatTurn> after)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentNullException.ThrowIfNull(protectedInstructions);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ProviderId = providerId;
        this.protectedInstructions = protectedInstructions;
        this.before = before;
        this.after = after;
    }

    /// <summary>The id the host assigned to this instance for this call.</summary>
    internal string ProviderId { get; }

    /// <summary>How many times the framework asked this provider for context. A composed turn asks exactly once.</summary>
    internal int Invocations { get; private set; }

    /// <summary>The turns this provider supplies, in the order they are sent, each with the provenance bOps assigned.</summary>
    internal IReadOnlyList<ChatTurn> SuppliedTurns => [.. before, .. after];

    /// <inheritdoc />
    protected override ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Invocations++;
        var incoming = context.AIContext;

        // Instructions are merged the way the framework merges them (newline-joined). The composed turn gives the agent none of its
        // own, so the effective instructions are exactly the protected ones; anything else makes the gate refuse the request.
        var instructions = string.IsNullOrEmpty(incoming.Instructions)
            ? protectedInstructions
            : incoming.Instructions + "\n" + protectedInstructions;

        List<ChatMessage> messages = [.. Stamp(before), .. incoming.Messages ?? [], .. Stamp(after)];
        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = instructions,
            Messages = messages,
            Tools = incoming.Tools,
        });
    }

    private IEnumerable<ChatMessage> Stamp(IReadOnlyList<ChatTurn> turns) =>
        MafChatTurnMapper.ToMaf(turns).Select(message =>
            message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, ProviderId));
}

/// <summary>
/// What the host composes for one governed call in <see cref="MafContextAssembly.BopsProvider"/> mode: the admitted provider, its
/// host-assigned id, and the part of the context the agent is given as input.
/// </summary>
internal sealed class MafContextComposition
{
    private MafContextComposition(BopsContextProvider provider, IReadOnlyList<ChatTurn> inputTurns)
    {
        Provider = provider;
        InputTurns = inputTurns;
    }

    /// <summary>The admitted provider.</summary>
    internal BopsContextProvider Provider { get; }

    /// <summary>The id the gate admits for this call.</summary>
    internal string ProviderId => Provider.ProviderId;

    /// <summary>The historical turns, the agent's input.</summary>
    internal IReadOnlyList<ChatTurn> InputTurns { get; }

    /// <summary>
    /// Splits a context request exactly where the runner split it. The id is a fresh value per call, so two compositions never
    /// share one and an identifier seen in an earlier call or an earlier message grants nothing.
    /// </summary>
    internal static MafContextComposition Create(ModelContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = new BopsContextProvider(
            "bops.context/" + Guid.NewGuid().ToString("N"),
            request.SystemPrompt,
            request.ProtectedBeforeHistory,
            request.ProtectedAfterHistory);
        return new MafContextComposition(provider, request.HistoricalTurns);
    }
}

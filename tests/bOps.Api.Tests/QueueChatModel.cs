using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Api.Tests;

/// <summary>Replays a fixed sequence of <see cref="ModelResponse"/> values, deterministically — same technique as <c>bOps.Runtime.Tests.FakeChatModel</c>, reimplemented locally since this project does not reference that test assembly.</summary>
internal sealed class QueueChatModel(params ModelResponse[] responses) : IChatModel
{
    private int _callCount;

    public ChatModelDescriptor Descriptor { get; } = new("test", "test-model");

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        if (_callCount >= responses.Length)
        {
            throw new InvalidOperationException($"QueueChatModel received a {_callCount + 1}th call but only {responses.Length} responses were recorded.");
        }

        return Task.FromResult(responses[_callCount++]);
    }

    /// <summary>The canned plan response every task's initial planning call needs — an empty step list, so the loop never treats any step as plan-exhausted.</summary>
    public static ModelResponse PlanResponse(string rationale = "test plan") =>
        new(new JsonObject { ["rationale"] = rationale, ["steps"] = new JsonArray() }.ToJsonString(), [], false, null);

    public static ModelResponse ToolCall(string toolName, JsonObject? arguments = null) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), toolName, ToolArguments.FromJson(arguments ?? []))], false, null);

    public static ModelResponse Final(string text) => new(text, [], true, null);
}

/// <summary>An <see cref="IPolicyEngine"/> that always returns the same decision, regardless of risk — used to force an approval or a denial deterministically in a test.</summary>
internal sealed class FixedPolicyEngine(PolicyMode mode, string reason = "test") : IPolicyEngine
{
    public PolicyDecision Evaluate(PolicyContext context) => new(mode, reason);
}

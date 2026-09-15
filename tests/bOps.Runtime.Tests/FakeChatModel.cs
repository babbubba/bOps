using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Replays a fixed sequence of <see cref="ModelResponse"/> values, one per call, so the agent
/// loop can be tested deterministically without a live model (agentic/04-testing-rules.md,
/// "Making the planner deterministic").
/// </summary>
internal sealed class FakeChatModel(params ModelResponse[] responses) : IChatModel
{
    private int _callCount;

    public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

    public IReadOnlyList<ModelRequest> Requests => _requests;

    private readonly List<ModelRequest> _requests = [];

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        _requests.Add(request);

        if (_callCount >= responses.Length)
        {
            throw new InvalidOperationException(
                $"FakeChatModel received a {_callCount + 1}th call but only {responses.Length} responses were recorded.");
        }

        return Task.FromResult(responses[_callCount++]);
    }
}

/// <summary>An <see cref="IChatModel"/> that always throws, for exercising provider-failure paths.</summary>
internal sealed class ThrowingChatModel(Exception exception) : IChatModel
{
    public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
        throw exception;
}

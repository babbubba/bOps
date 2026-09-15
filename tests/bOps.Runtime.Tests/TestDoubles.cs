using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>Collects every audit event written to it, in order, for assertion.</summary>
public sealed class RecordingAuditSink : IAuditSink
{
    private readonly List<AuditEvent> _events = [];

    public IReadOnlyList<AuditEvent> Events => _events;

    public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        _events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>A capability probe that reports every capability as available. Nothing under test needs a real probe.</summary>
public sealed class AlwaysAvailableCapabilityProbe : ICapabilityProbe
{
    public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>A minimal, always-succeeding <see cref="RiskLevel.Read"/> tool for exercising the happy path.</summary>
public sealed class FakeReadTool(string name = "test.read", string output = "ok") : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake read-only tool for tests.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success(output));
}

/// <summary>A tool whose <see cref="ExecuteAsync"/> always throws, to exercise rule C1 (nothing thrown escapes an iteration).</summary>
public sealed class ThrowingTool(string name = "test.throws") : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake tool that always throws.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        throw new InvalidOperationException("Simulated tool failure.");
}

/// <summary>A tool that never completes, to exercise rule S7 (every action runs under a timeout).</summary>
public sealed class HangingTool(string name = "test.hangs") : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake tool that never completes.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("Unreachable.");
    }
}

/// <summary>A non-<see cref="RiskLevel.Read"/> tool with a valid <see cref="VerificationSpec"/>, for exercising the policy-absence guard (rule S3).</summary>
public sealed class FakeHighRiskTool(string name = "test.highrisk") : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake high-risk tool for tests.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec("test.read", [], "Checks nothing, for tests."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success("done"));

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, null));
}

/// <summary>A non-<see cref="RiskLevel.Read"/> tool with no <see cref="VerificationSpec"/>, which registration must reject (rule B3).</summary>
public sealed class UnverifiedHighRiskTool(string name = "test.unverified") : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake high-risk tool with no verification, which must be rejected at registration.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success("done"));
}

/// <summary>
/// A non-<see cref="RiskLevel.Read"/> tool that declares a <see cref="VerificationSpec"/> but does
/// not implement <see cref="IVerifiableTool"/>, which registration must also reject (rule B3).
/// </summary>
public sealed class DeclaredButNotVerifiableTool(string name = "test.declared-not-verifiable") : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "Declares verification but does not implement IVerifiableTool.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec("test.read", [], "Checks nothing, for tests."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success("done"));
}

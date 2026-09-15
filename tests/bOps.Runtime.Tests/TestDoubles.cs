using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// The default policy engine for tests that do not exercise policy/approval directly (rule S3):
/// Read is automatic, everything else is forbidden — the same fail-closed shape V0.1/V0.2 had
/// hardcoded before a real <see cref="IPolicyEngine"/> existed.
/// </summary>
internal sealed class DefaultTestPolicyEngine : IPolicyEngine
{
    public PolicyDecision Evaluate(PolicyContext context) =>
        context.Manifest.Risk == RiskLevel.Read
            ? new PolicyDecision(PolicyMode.Automatic, "test default: Read is automatic")
            : new PolicyDecision(PolicyMode.Forbidden, "test default: non-Read is forbidden");
}

/// <summary>An <see cref="IPolicyEngine"/> that always returns the same decision, for tests exercising a specific mode.</summary>
internal sealed class StubPolicyEngine(PolicyMode mode, string reason = "test stub") : IPolicyEngine
{
    public PolicyDecision Evaluate(PolicyContext context) => new(mode, reason);
}

/// <summary>An <see cref="IApprovalProvider"/> that always returns the same decision, for tests exercising a specific approval outcome.</summary>
internal sealed class StubApprovalProvider(bool approved, string? note = null) : IApprovalProvider
{
    public ActorIdentity? LastApprover { get; set; }

    public Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
    {
        var approver = LastApprover ?? ActorIdentity.FromOperatingSystemUser("test-approver");
        return Task.FromResult(new ApprovalDecision(approved, approver, note));
    }
}

/// <summary>An <see cref="IApprovalProvider"/> that fails the test if it is ever called — for tests asserting approval is never requested.</summary>
internal sealed class NeverCalledApprovalProvider : IApprovalProvider
{
    public Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default) =>
        throw new InvalidOperationException("Approval was not expected in this test.");
}

/// <summary>Collects every audit event written to it, in order, for assertion.</summary>
internal sealed class RecordingAuditSink : IAuditSink
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
internal sealed class AlwaysAvailableCapabilityProbe : ICapabilityProbe
{
    public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>A minimal, always-succeeding <see cref="RiskLevel.Read"/> tool for exercising the happy path.</summary>
internal sealed class FakeReadTool(string name = "test.read", string output = "ok", IReadOnlyList<ToolParameter>? parameters = null) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake read-only tool for tests.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = parameters ?? [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success(output));
}

/// <summary>A tool whose <see cref="ExecuteAsync"/> always throws, to exercise rule C1 (nothing thrown escapes an iteration).</summary>
internal sealed class ThrowingTool(string name = "test.throws") : ITool
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
internal sealed class HangingTool(string name = "test.hangs") : ITool
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
internal sealed class FakeHighRiskTool(string name = "test.highrisk") : IVerifiableTool
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
internal sealed class UnverifiedHighRiskTool(string name = "test.unverified") : ITool
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
internal sealed class DeclaredButNotVerifiableTool(string name = "test.declared-not-verifiable") : ITool
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

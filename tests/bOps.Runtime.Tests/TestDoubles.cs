// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

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

/// <summary>
/// An <see cref="ITaskStore"/> held entirely in memory, for tests that do not exercise real
/// persistence directly — the real store is <c>SqliteTaskStore</c> (V0.7, ADR-0017), tested on
/// its own in <c>bOps.Memory.Tests</c>. Also records every save, in order, so a test can assert
/// exactly when the runtime persists (rule V0.7: after every step, and again at the end).
/// </summary>
internal sealed class InMemoryTaskStore : ITaskStore
{
    private readonly Dictionary<Guid, TaskState> _tasks = [];

    public List<TaskState> Saves { get; } = [];

    public Task SaveAsync(TaskState task, CancellationToken ct = default)
    {
        _tasks[task.Id] = task;
        Saves.Add(task);
        return Task.CompletedTask;
    }

    public Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default) =>
        Task.FromResult(_tasks.TryGetValue(taskId, out var task) ? task : null);

    public Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaskState>>(_tasks.Values.Where(t => t.Status == status).ToList());
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

/// <summary>
/// A non-<see cref="RiskLevel.Read"/> tool with a valid <see cref="VerificationSpec"/>, for
/// exercising the policy-absence guard (rule S3) and, from V0.4, verification itself (rule S4).
/// <see cref="EvaluateVerificationAsync"/> ignores whatever the verification tool call actually
/// returned and always answers with <paramref name="verificationOutcome"/> — tests that care what
/// <c>AgentRunner</c> does with the verification tool's own result use
/// <see cref="InspectingVerifiableTool"/> instead.
/// </summary>
internal sealed class FakeHighRiskTool(string name = "test.highrisk", VerificationOutcome? verificationOutcome = null) : IVerifiableTool
{
    private readonly VerificationOutcome _verificationOutcome = verificationOutcome ?? new VerificationOutcome(VerificationStatus.Confirmed, null);

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
        Task.FromResult(_verificationOutcome);
}

/// <summary>
/// A non-<see cref="RiskLevel.Read"/> tool whose <see cref="EvaluateVerificationAsync"/> actually
/// inspects the <see cref="ToolCallResult"/> the runtime got back from calling
/// <see cref="VerificationSpec.VerifyToolName"/> — used to test how <c>AgentRunner</c> resolves
/// and calls that tool, as opposed to <see cref="FakeHighRiskTool"/>, which ignores it.
/// </summary>
internal sealed class InspectingVerifiableTool(string name, string verifyToolName) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake high-risk tool whose verification inspects the verification tool's own result.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec(verifyToolName, [], "Inspects the verification tool's result, for tests."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success("done"));

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(verificationToolResult.Succeeded
            ? new VerificationOutcome(VerificationStatus.Confirmed, null)
            : new VerificationOutcome(VerificationStatus.Inconclusive, $"verification call did not succeed: {verificationToolResult.ErrorMessage}"));
}

/// <summary>A non-<see cref="RiskLevel.Read"/> tool whose <see cref="EvaluateVerificationAsync"/> always throws, to exercise rule C1 applied to verification.</summary>
internal sealed class ThrowingVerificationTool(string name = "test.throwing-verification") : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake high-risk tool whose verification always throws.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec("test.read", [], "Always throws while evaluating, for tests."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Success("done"));

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        throw new InvalidOperationException("Simulated verification failure.");
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

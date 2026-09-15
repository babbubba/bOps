using bOps.Abstractions;

namespace bOps.Cli;

/// <summary>
/// Asks the operator at the console to approve or reject a call policy has placed in
/// <see cref="PolicyMode.Approval"/> — the CLI is the primary interface (agentic/00-project-spec.md,
/// principle 6), so this is where V0.3's approval flow lives first. A future host (the Phase 2
/// API/UI) implements its own <see cref="IApprovalProvider"/> — an approval queue, not a blocking
/// console prompt — without AgentRunner or the policy engine changing at all.
/// </summary>
internal sealed class ConsoleApprovalProvider : IApprovalProvider
{
    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(arguments);

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"Approval required: '{manifest.Name}' ({manifest.Risk}-risk)");
        await Console.Out.WriteLineAsync($"  Reason: {reason}");
        await Console.Out.WriteLineAsync($"  Arguments: {arguments.ToJson().ToJsonString()}");
        await Console.Out.WriteLineAsync(verification is null
            ? "  Verified afterwards: nothing declared."
            : $"  Verified afterwards: {verification.Description}");
        await Console.Out.WriteAsync("Approve? [y/N] ");

        var response = Console.ReadLine();
        var approved = string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

        string? note = null;
        if (approved)
        {
            await Console.Out.WriteAsync("Optional note: ");
            note = Console.ReadLine();
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        }

        var actor = ActorIdentity.FromOperatingSystemUser(Environment.UserName);
        return new ApprovalDecision(approved, actor, note);
    }
}

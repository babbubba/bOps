// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

public sealed class FsDeleteTreeTool(FilesystemDeletionService deletion) :
    IContextualTool,
    IVerifiableTool,
    IApprovalBoundTool,
    IToolAuditSummaryProvider
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.delete_tree",
        Description = "Permanently deletes only the exact entries in a fresh approved deletion manifest. There is no trash, rollback or recursive path argument.",
        Risk = RiskLevel.High,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("manifestId", ToolParameterType.String, "The opaque deletion manifest id."),
            new ToolParameter("approvalHash", ToolParameterType.String, "The exact hash displayed to the approver."),
        ],
        RequiresExplicitApproval = true,
        Verification = new VerificationSpec(
            "fs.delete_tree.verify",
            ["manifestId", "approvalHash"],
            "Checks every approved entry and confirms that all are absent; remaining, changed and inaccessible paths are classified explicitly."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Failure(
            "Permanent recursive deletion requires host-owned node, task and actor context."));

    public async Task<ToolCallResult> BindApprovalAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        ApprovalDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        var result = await deletion.BindDecisionAsync(
            arguments.GetRequired<string>("manifestId"),
            arguments.GetRequired<string>("approvalHash"),
            context,
            decision.Approved,
            ct);
        return result switch
        {
            DeletionApprovalBindingResult.Bound => ToolCallResult.Success("The approval is bound to the exact deletion manifest."),
            DeletionApprovalBindingResult.Rejected => ToolCallResult.Success("The deletion manifest was rejected without execution."),
            DeletionApprovalBindingResult.NotFound => ToolCallResult.Failure("The deletion manifest does not exist in this node/task/actor scope."),
            DeletionApprovalBindingResult.HashMismatch => ToolCallResult.Failure("The approval hash does not match the deletion manifest."),
            DeletionApprovalBindingResult.Expired => ToolCallResult.Failure("The deletion manifest expired before approval."),
            _ => ToolCallResult.Failure("The deletion manifest is not ready for a new approval and may already have been consumed."),
        };
    }

    public async Task<ToolCallResult> ExecuteAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var result = await deletion.ExecuteAsync(
                arguments.GetRequired<string>("manifestId"),
                arguments.GetRequired<string>("approvalHash"),
                context,
                ct);
            return result.FailureCount == 0
                ? ToolCallResult.Success(FilesystemDeletionOutput.FormatExecution(result))
                : ToolCallResult.Failure(FilesystemDeletionOutput.FormatExecution(result));
        }
        catch (FilesystemDeletionException ex)
        {
            return ToolCallResult.Failure(ex.Message);
        }
        catch (SqliteException ex)
        {
            return ToolCallResult.Failure($"Could not execute the deletion manifest: {ex.Message}");
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments,
        ToolCallResult verificationToolResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);
        if (!verificationToolResult.Succeeded || string.IsNullOrEmpty(verificationToolResult.Output))
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive,
                $"Could not reconcile the permanent deletion: {verificationToolResult.ErrorMessage}"));
        }

        try
        {
            var json = JsonNode.Parse(verificationToolResult.Output);
            var status = json?["status"]?.GetValue<string>();
            return Task.FromResult(status switch
            {
                "confirmed" => new VerificationOutcome(VerificationStatus.Confirmed, "Every approved entry is absent."),
                "refuted" => new VerificationOutcome(VerificationStatus.Refuted, "One or more approved paths remain or were recreated."),
                _ => new VerificationOutcome(VerificationStatus.Inconclusive, "One or more approved paths could not be observed."),
            });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive,
                $"Deletion verification output could not be read: {ex.Message}"));
        }
    }

    public JsonObject? CreateAuditSummary(ToolArguments arguments, ToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(result);
        var summary = new JsonObject
        {
            ["manifestId"] = arguments.GetRequired<string>("manifestId"),
            ["approvalHash"] = arguments.GetRequired<string>("approvalHash"),
        };
        var payload = result.Succeeded ? result.Output : result.ErrorMessage;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return summary;
        }

        try
        {
            var execution = JsonNode.Parse(payload)?.AsObject();
            foreach (var name in new[] { "status", "entryCount", "totalBytes", "deletedCount", "failureCount" })
            {
                if (execution?[name] is { } value)
                {
                    summary[name] = value.DeepClone();
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Expected pre-execution failures are plain text; id/hash still identify the refusal.
        }

        return summary;
    }
}

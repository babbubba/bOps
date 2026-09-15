// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Creates or overwrites a text file with the given content. <see cref="RiskLevel.High"/>: it can
/// silently destroy existing content, on any path the write policy allows. Verified via
/// <c>fs.stat</c> on the same path (rule B3/D-006) — <see cref="EvaluateVerificationAsync"/> reads
/// back whether the write left a file where one was claimed.
///
/// <c>fs.stat</c> enforces its own read-or-write path check (rule S11 is a tool-owned invariant,
/// never bypassed by verification — ADR-0016 only bypasses <c>IPolicyEngine</c>/
/// <c>IApprovalProvider</c>), so verification only ever confirms something when the written path
/// is covered by a write pattern — which it must be, since the write itself required that.
/// </summary>
public sealed class FsWriteTool(FilesystemPathPolicy pathPolicy) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.write",
        Description = "Creates or overwrites a text file with the given content.",
        Risk = RiskLevel.High,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The file to create or overwrite."),
            new ToolParameter("content", ToolParameterType.String, "The text content to write."),
        ],
        Verification = new VerificationSpec(
            "fs.stat", ["path"], "Confirms a file exists at the written path afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = arguments.GetRequired<string>("path");
        var content = arguments.GetRequired<string>("content");
        var resolvedPath = FilesystemPathPolicy.Resolve(path);

        if (!pathPolicy.AllowsWrite(resolvedPath))
        {
            return ToolCallResult.Failure($"Path not permitted for write access by filesystem policy: {path}");
        }

        if (Directory.Exists(resolvedPath))
        {
            return ToolCallResult.Failure($"'{resolvedPath}' is a directory; fs.write only writes files.");
        }

        try
        {
            await File.WriteAllTextAsync(resolvedPath, content, ct);
            return ToolCallResult.Success($"Wrote {content.Length} characters to '{resolvedPath}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ToolCallResult.Failure($"Could not write '{resolvedPath}': {ex.Message}");
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the write: {verificationToolResult.ErrorMessage}"));
        }

        return Task.FromResult(FsStatOutput.TryReadExists(verificationToolResult.Output) switch
        {
            true => new VerificationOutcome(VerificationStatus.Confirmed, null),
            false => new VerificationOutcome(VerificationStatus.Refuted, "fs.stat reports the path does not exist after the write."),
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "fs.stat's output could not be read."),
        });
    }
}

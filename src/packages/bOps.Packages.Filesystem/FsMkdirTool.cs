// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsMkdirTool(FilesystemPathPolicy pathPolicy) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.mkdir",
        Description = "Creates a directory if absent; succeeds without changes when it already exists.",
        Risk = RiskLevel.Low,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "The directory path to create.")],
        Verification = new VerificationSpec("fs.stat", ["path"], "Confirms the path exists as a directory afterwards."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();
        var requested = arguments.GetRequired<string>("path");
        var resolved = FilesystemPathPolicy.Resolve(requested);
        if (!pathPolicy.AllowsWrite(resolved))
        {
            return Task.FromResult(ToolCallResult.Failure($"Path not permitted for write access by filesystem policy: {requested}"));
        }

        if (File.Exists(resolved))
        {
            return Task.FromResult(ToolCallResult.Failure($"'{resolved}' is a file; fs.mkdir requires a directory path."));
        }

        try
        {
            resolved = FilesystemPathPolicy.Resolve(requested);
            if (!pathPolicy.AllowsWrite(resolved))
            {
                return Task.FromResult(ToolCallResult.Failure("Path is no longer permitted by filesystem policy."));
            }

            Directory.CreateDirectory(resolved);
            return Task.FromResult(ToolCallResult.Success($"Directory exists: '{resolved}'."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not create directory '{resolved}': {ex.Message}"));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(verificationToolResult is not null && FsStatOutput.TryReadExists(verificationToolResult.Output) is true
            ? new VerificationOutcome(VerificationStatus.Confirmed, null)
            : new VerificationOutcome(VerificationStatus.Refuted, "fs.stat did not confirm the directory exists."));
}

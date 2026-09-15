// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Moves (renames) a single file. <see cref="RiskLevel.High"/>: it can silently destroy the
/// original location, on any path the write policy allows. Never overwrites an existing
/// destination — that would be a second, separate data-loss risk stacked on top of the move
/// itself, and the model can always follow up with a deliberate <c>fs.delete</c> if overwriting
/// is genuinely intended.
///
/// <para><b>Content identity</b> — the plan's own requirement for this tool — is enforced
/// synchronously, inside <see cref="ExecuteAsync"/>, not by the separate post-action
/// <see cref="VerificationSpec"/> step: the source's SHA-256 is computed before the move and the
/// destination's SHA-256 is computed immediately after, and a mismatch is reported as
/// <see cref="ToolOutcome.Failure"/>, not merely an inconclusive verification. This is a
/// deliberate design choice, not an oversight: <see cref="IVerifiableTool.EvaluateVerificationAsync"/>
/// only ever receives the <em>original call's arguments</em> and the <em>separately executed
/// verification tool's</em> result (see <c>bOps.Runtime.AgentRunner.EvaluateVerificationAsync</c>)
/// — never this tool's own <see cref="ToolCallResult"/> from the move itself. By the time a
/// deferred verification call could run, the source is already gone, so there is nothing left to
/// re-hash and compare against; a historical pre-move hash cannot be threaded into a later
/// verification call without changing <see cref="IVerifiableTool"/>'s shape, which would need its
/// own ADR (agentic/05-workflow.md's trigger list: "alters a type in bOps.Abstractions"). Verifying
/// the hash match inside the move itself is strictly stronger than a deferred check could ever be
/// — there is no time-of-check/time-of-use gap — so nothing is lost by not extending the contract
/// for this. The declared <see cref="VerificationSpec"/> below still exists and still runs,
/// confirming the externally observable half of the claim: a file now exists at the destination.
/// </para>
/// </summary>
public sealed class FsMoveTool(FilesystemPathPolicy pathPolicy) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.move",
        Description = "Moves (renames) a single file to a new path. Never overwrites an existing destination.",
        Risk = RiskLevel.High,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("source", ToolParameterType.Path, "The file to move."),
            new ToolParameter("destination", ToolParameterType.Path, "Where to move it to. Must not already exist."),
        ],
        Verification = new VerificationSpec(
            "fs.stat", ["destination"], "Confirms a file exists at the destination path afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var source = arguments.GetRequired<string>("source");
        var destination = arguments.GetRequired<string>("destination");
        var resolvedSource = FilesystemPathPolicy.Resolve(source);
        var resolvedDestination = FilesystemPathPolicy.Resolve(destination);

        if (!pathPolicy.AllowsWrite(resolvedSource))
        {
            return ToolCallResult.Failure($"Source path not permitted for write access by filesystem policy: {source}");
        }

        if (!pathPolicy.AllowsWrite(resolvedDestination))
        {
            return ToolCallResult.Failure($"Destination path not permitted for write access by filesystem policy: {destination}");
        }

        if (Directory.Exists(resolvedSource))
        {
            return ToolCallResult.Failure($"'{resolvedSource}' is a directory; fs.move only moves files.");
        }

        if (!File.Exists(resolvedSource))
        {
            return ToolCallResult.Failure($"'{resolvedSource}' does not exist.");
        }

        if (File.Exists(resolvedDestination) || Directory.Exists(resolvedDestination))
        {
            return ToolCallResult.Failure($"'{resolvedDestination}' already exists; fs.move does not overwrite.");
        }

        try
        {
            var sourceHash = await ComputeHashAsync(resolvedSource, ct);
            File.Move(resolvedSource, resolvedDestination);
            var destinationHash = await ComputeHashAsync(resolvedDestination, ct);

            if (!sourceHash.SequenceEqual(destinationHash))
            {
                return ToolCallResult.Failure(
                    $"Moved '{resolvedSource}' to '{resolvedDestination}', but the content hash changed — treating this as a failed move.");
            }

            return ToolCallResult.Success(FsHashOutput.Format(resolvedDestination, destinationHash, new FileInfo(resolvedDestination).Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolCallResult.Failure($"Could not move '{resolvedSource}' to '{resolvedDestination}': {ex.Message}");
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the move: {verificationToolResult.ErrorMessage}"));
        }

        return Task.FromResult(FsStatOutput.TryReadExists(verificationToolResult.Output) switch
        {
            true => new VerificationOutcome(VerificationStatus.Confirmed, null),
            false => new VerificationOutcome(VerificationStatus.Refuted, "fs.stat reports no file at the destination after the move."),
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "fs.stat's output could not be read."),
        });
    }

    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }
}

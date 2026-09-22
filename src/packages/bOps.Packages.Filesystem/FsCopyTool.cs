// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsCopyTool(FilesystemPathPolicy pathPolicy, FilesystemOperationsOptions options) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.copy",
        Description = "Copies one regular file to a new destination without overwriting an existing path.",
        Risk = RiskLevel.Medium,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("source", ToolParameterType.Path, "The existing regular file to copy."),
            new ToolParameter("destination", ToolParameterType.Path, "The new destination path; it must not exist."),
        ],
        Verification = new VerificationSpec(
            "fs.copy.verify", ["source", "destination"], "Confirms source and destination have the same SHA-256 and length."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var source = arguments.GetRequired<string>("source");
        var destination = arguments.GetRequired<string>("destination");
        var resolvedSource = FilesystemPathPolicy.Resolve(source);
        var resolvedDestination = FilesystemPathPolicy.Resolve(destination);

        if (!pathPolicy.AllowsRead(resolvedSource) || !pathPolicy.AllowsWrite(resolvedDestination))
        {
            return ToolCallResult.Failure("Source is not read-allowed or destination is not write-allowed by filesystem policy.");
        }

        if (!File.Exists(resolvedSource) || Directory.Exists(resolvedSource))
        {
            return ToolCallResult.Failure($"'{resolvedSource}' is not an existing regular file.");
        }

        if (File.Exists(resolvedDestination) || Directory.Exists(resolvedDestination))
        {
            return ToolCallResult.Failure($"'{resolvedDestination}' already exists; fs.copy never overwrites.");
        }

        if (new FileInfo(resolvedSource).Length > options.MaxCopyBytes)
        {
            return ToolCallResult.Failure($"Source exceeds the configured copy limit of {options.MaxCopyBytes} bytes.");
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(resolvedDestination) ?? string.Empty,
            $".{Path.GetFileName(resolvedDestination)}.bops-copy-{Guid.NewGuid():N}.tmp");
        var createdTemporary = false;
        try
        {
            // S11: resolve and authorize the exact endpoints again at the last practical moment.
            resolvedSource = FilesystemPathPolicy.Resolve(source);
            resolvedDestination = FilesystemPathPolicy.Resolve(destination);
            temporary = Path.Combine(
                Path.GetDirectoryName(resolvedDestination) ?? string.Empty,
                $".{Path.GetFileName(resolvedDestination)}.bops-copy-{Guid.NewGuid():N}.tmp");
            if (!pathPolicy.AllowsRead(resolvedSource) || !pathPolicy.AllowsWrite(resolvedDestination)
                || !pathPolicy.AllowsWrite(temporary)
                || File.Exists(resolvedDestination) || Directory.Exists(resolvedDestination))
            {
                return ToolCallResult.Failure("Filesystem paths changed or are no longer permitted before copy execution.");
            }

            await using (var input = new FileStream(resolvedSource, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                createdTemporary = true;
                if (input.Length > options.MaxCopyBytes)
                {
                    return ToolCallResult.Failure($"Source exceeds the configured copy limit of {options.MaxCopyBytes} bytes.");
                }

                await input.CopyToAsync(output, 65536, ct);
                await output.FlushAsync(ct);
            }

            File.Move(temporary, resolvedDestination, overwrite: false);
            createdTemporary = false;
            var hash = await ComputeHashAsync(resolvedDestination, ct);
            return ToolCallResult.Success(FsHashOutput.Format(resolvedDestination, hash, new FileInfo(resolvedDestination).Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ToolCallResult.Failure($"Could not copy '{resolvedSource}' to '{resolvedDestination}': {ex.Message}");
        }
        finally
        {
            if (createdTemporary)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(verificationToolResult is not null && verificationToolResult.Succeeded && FsCopyVerificationOutput.IsMatching(verificationToolResult.Output)
            ? new VerificationOutcome(VerificationStatus.Confirmed, null)
            : new VerificationOutcome(VerificationStatus.Inconclusive, "fs.copy.verify could not confirm identical content."));

    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }
}

// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsCopyVerifyTool(FilesystemPathPolicy pathPolicy) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.copy.verify",
        Description = "Compares two regular files by existence, length and SHA-256.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("source", ToolParameterType.Path, "The original file."),
            new ToolParameter("destination", ToolParameterType.Path, "The copied file."),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var source = FilesystemPathPolicy.Resolve(arguments.GetRequired<string>("source"));
        var destination = FilesystemPathPolicy.Resolve(arguments.GetRequired<string>("destination"));
        if (!pathPolicy.AllowsRead(source) || (!pathPolicy.AllowsRead(destination) && !pathPolicy.AllowsWrite(destination)))
        {
            return ToolCallResult.Failure("Source or destination is not permitted for verification by filesystem policy.");
        }

        if (!File.Exists(source) || !File.Exists(destination))
        {
            return ToolCallResult.Success(FsCopyVerificationOutput.Format(false, null, null));
        }

        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        var sourceHash = await HashAsync(source, ct);
        var destinationHash = await HashAsync(destination, ct);
        var matching = sourceInfo.Length == destinationInfo.Length && sourceHash.SequenceEqual(destinationHash);
        return ToolCallResult.Success(FsCopyVerificationOutput.Format(matching, sourceInfo.Length, destinationInfo.Length));
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }
}

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Deletes a single file. <see cref="RiskLevel.High"/>: hard to reverse. Deliberately does not
/// delete directories (recursive deletion is a materially larger blast radius and out of scope
/// for this version) — a directory path fails with an explicit message rather than silently
/// refusing. Verified via <c>fs.stat</c> on the same path: the mirror image of
/// <see cref="FsWriteTool"/>'s verification, confirming absence instead of presence.
/// </summary>
public sealed class FsDeleteTool(FilesystemPathPolicy pathPolicy) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.delete",
        Description = "Deletes a single file. Does not delete directories.",
        Risk = RiskLevel.High,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "The file to delete.")],
        Verification = new VerificationSpec(
            "fs.stat", ["path"], "Confirms no file remains at the deleted path afterwards."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = arguments.GetRequired<string>("path");
        var resolvedPath = FilesystemPathPolicy.Resolve(path);

        if (!pathPolicy.AllowsWrite(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Failure($"Path not permitted for write access by filesystem policy: {path}"));
        }

        if (Directory.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Failure($"'{resolvedPath}' is a directory; fs.delete only deletes files."));
        }

        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Failure($"'{resolvedPath}' does not exist."));
        }

        try
        {
            File.Delete(resolvedPath);
            return Task.FromResult(ToolCallResult.Success($"Deleted '{resolvedPath}'."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not delete '{resolvedPath}': {ex.Message}"));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the delete: {verificationToolResult.ErrorMessage}"));
        }

        return Task.FromResult(FsStatOutput.TryReadExists(verificationToolResult.Output) switch
        {
            false => new VerificationOutcome(VerificationStatus.Confirmed, null),
            true => new VerificationOutcome(VerificationStatus.Refuted, "fs.stat reports the path still exists after the delete."),
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "fs.stat's output could not be read."),
        });
    }
}

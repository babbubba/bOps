// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsLocksTool(FilesystemPathPolicy pathPolicy) : ITool
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 1000;
    private const int MaximumProcesses = 4096;
    private const int MaximumDescriptorsPerProcess = 4096;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.locks",
        Description = "Reports visible processes holding an exact file path; restricted process visibility is reported as incomplete.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The existing regular file to inspect."),
            new ToolParameter("limit", ToolParameterType.Integer, "Maximum rows, 1 through 1000. Defaults to 100.", Required: false),
        ],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var requested = arguments.GetRequired<string>("path");
        var path = FilesystemPathPolicy.Resolve(requested);
        if (!pathPolicy.AllowsRead(path)) return Task.FromResult(ToolCallResult.Failure($"Path not permitted for read access by filesystem policy: {requested}"));
        if (!File.Exists(path) || Directory.Exists(path)) return Task.FromResult(ToolCallResult.Failure($"'{path}' is not an existing regular file."));
        var limit = !arguments.TryGet<int>("limit", out var requestedLimit) ? DefaultLimit : requestedLimit;
        if (limit is < 1 or > MaximumLimit) return Task.FromResult(ToolCallResult.Failure("limit is outside its supported range."));
        return Task.FromResult(OperatingSystem.IsWindows() ? Windows(path, limit) : Linux(path, limit, ct));
    }

    private static ToolCallResult Linux(string path, int limit, CancellationToken ct)
    {
        var rows = new List<object>();
        var complete = true;
        try
        {
            var processDirectories = Directory.EnumerateDirectories("/proc").ToArray();
            if (processDirectories.Length > MaximumProcesses) complete = false;
            foreach (var processDirectory in processDirectories.Take(MaximumProcesses))
            {
                ct.ThrowIfCancellationRequested();
                if (!int.TryParse(Path.GetFileName(processDirectory), out var pid)) continue;
                var descriptorDirectory = Path.Combine(processDirectory, "fd");
                try
                {
                    var descriptors = Directory.EnumerateFileSystemEntries(descriptorDirectory).ToArray();
                    if (descriptors.Length > MaximumDescriptorsPerProcess) complete = false;
                    foreach (var descriptor in descriptors.Take(MaximumDescriptorsPerProcess))
                    {
                        FileSystemInfo? target;
                        try { target = new FileInfo(descriptor).ResolveLinkTarget(returnFinalTarget: true); }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        if (!string.Equals(target?.FullName, path, StringComparison.Ordinal)) continue;
                        rows.Add(new { pid, processName = ProcessName(pid), user = (string?)null, accessKind = "file-descriptor", source = "procfs" });
                        if (rows.Count >= limit)
                        {
                            // The row limit is the caller's own ceiling, not a restriction on the
                            // scan itself, but stopping early still means the scan was not
                            // exhaustive: report it honestly rather than implying completeness.
                            return ToolCallResult.Success(JsonSerializer.Serialize(new { rows, complete = false }));
                        }
                    }
                }
                catch (UnauthorizedAccessException) { complete = false; }
                catch (IOException) { complete = false; }
            }
        }
        catch (UnauthorizedAccessException) { complete = false; }
        catch (IOException) { complete = false; }
        return ToolCallResult.Success(JsonSerializer.Serialize(new { rows, complete }));
    }

    private static ToolCallResult Windows(string path, int limit)
    {
        var (found, complete) = WindowsRestartManager.Find(path, limit);
        var rows = found
            .Select(row => new { pid = row.ProcessId, processName = row.ProcessName, user = (string?)null, accessKind = "restart-manager", source = "restart-manager" });
        return ToolCallResult.Success(JsonSerializer.Serialize(new { rows, complete }));
    }

    private static string? ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

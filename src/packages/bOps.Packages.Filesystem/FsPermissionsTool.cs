// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsPermissionsTool(FilesystemPathPolicy pathPolicy) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.permissions",
        Description = "Reports ownership, mode or ACL evidence for one filesystem path; it does not calculate effective access.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "The path whose permissions to inspect.")],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();
        var requested = arguments.GetRequired<string>("path");
        var path = FilesystemPathPolicy.Resolve(requested);
        if (!pathPolicy.AllowsRead(path) && !pathPolicy.AllowsWrite(path))
        {
            return Task.FromResult(ToolCallResult.Failure($"Path not permitted by filesystem policy: {requested}"));
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Task.FromResult(ToolCallResult.Success(JsonSerializer.Serialize(new
            {
                path, exists = false, type = (string?)null, owner = (string?)null, group = (string?)null,
                unixMode = (string?)null, aclEntries = Array.Empty<object>(), readOnly = (bool?)null,
                source = "filesystem", complete = true,
            })));
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Task.FromResult(ToolCallResult.Success(WindowsPermissions(path)));
            }

            if (OperatingSystem.IsLinux())
            {
                return Task.FromResult(ToolCallResult.Success(LinuxPermissions(path)));
            }

            return Task.FromResult(ToolCallResult.Failure("fs.permissions supports Windows and Linux only."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not inspect permissions for '{path}': {ex.Message}"));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsPermissions(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var owner = security.GetOwner(typeof(NTAccount))?.Value;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(NTAccount))
            .Cast<FileSystemAccessRule>()
            .Take(200)
            .Select(rule => new
            {
                identity = rule.IdentityReference.Value,
                access = rule.AccessControlType.ToString(),
                rights = rule.FileSystemRights.ToString(),
                inherited = rule.IsInherited,
            });
        return JsonSerializer.Serialize(new
        {
            path, exists = true, type = Directory.Exists(path) ? "directory" : "file", owner, group = (string?)null,
            unixMode = (string?)null, aclEntries = entries, readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0,
            source = "windows-acl", complete = true,
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string LinuxPermissions(string path)
    {
        var mode = File.GetUnixFileMode(path);
        if (LinuxFileStatus.TryRead(path, out var status))
        {
            return JsonSerializer.Serialize(new
            {
                path, exists = true, type = Directory.Exists(path) ? "directory" : "file", owner = $"uid:{status.UserId}", group = $"gid:{status.GroupId}",
                unixMode = mode.ToString(), aclEntries = Array.Empty<object>(), readOnly = (mode & UnixFileMode.UserWrite) == 0,
                source = "linux-stat", complete = true,
            });
        }

        return JsonSerializer.Serialize(new
        {
            path, exists = true, type = Directory.Exists(path) ? "directory" : "file", owner = (string?)null, group = (string?)null,
            unixMode = mode.ToString(), aclEntries = Array.Empty<object>(), readOnly = (mode & UnixFileMode.UserWrite) == 0,
            source = "linux-unix-mode", complete = false,
        });
    }
}

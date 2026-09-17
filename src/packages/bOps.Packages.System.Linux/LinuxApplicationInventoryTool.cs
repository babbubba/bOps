// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects installed applications from supported Linux package databases.</summary>
public sealed class LinuxApplicationInventoryTool : ApplicationInventoryToolBase
{
    private readonly string dpkgStatusPath;
    private readonly IReadOnlyList<string> rpmDatabasePaths;
    private readonly string apkDatabasePath;

    /// <summary>Creates an inventory tool over the standard Linux package database locations.</summary>
    public LinuxApplicationInventoryTool()
        : this(
            "/var/lib/dpkg/status",
            ["/var/lib/rpm", "/usr/lib/sysimage/rpm"],
            "/lib/apk/db/installed")
    {
    }

    internal LinuxApplicationInventoryTool(
        string dpkgStatusPath,
        IReadOnlyList<string> rpmDatabasePaths,
        string apkDatabasePath)
        : base("linux")
    {
        this.dpkgStatusPath = dpkgStatusPath;
        this.rpmDatabasePaths = rpmDatabasePaths;
        this.apkDatabasePath = apkDatabasePath;
    }

    protected override async Task<InventorySnapshot<ApplicationInventoryItem>> CollectAsync(
        int collectionLimit,
        CancellationToken ct)
    {
        var items = new List<ApplicationInventoryItem>();
        var sources = new List<InventorySourceResult>();
        var truncated = false;

        if (!File.Exists(dpkgStatusPath))
        {
            sources.Add(new("linux.dpkg-status", InventorySourceStatus.NotApplicable, "The dpkg status database does not exist."));
        }
        else
        {
            try
            {
                await using var stream = new FileStream(
                    dpkgStatusPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var result = await DpkgStatusParser.ParseAsync(stream, collectionLimit, ct);
                items.AddRange(result.Items);
                truncated = result.Truncated;
                sources.Add(new(
                    "linux.dpkg-status",
                    truncated ? InventorySourceStatus.Partial : InventorySourceStatus.Available,
                    truncated ? "The collection ceiling was reached." : null));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                sources.Add(new("linux.dpkg-status", InventorySourceStatus.Unavailable, exception.GetType().Name));
            }
        }

        sources.Add(DetectedUnsupported(
            "linux.rpm-database",
            rpmDatabasePaths.Any(Directory.Exists)));
        sources.Add(DetectedUnsupported("linux.apk-database", File.Exists(apkDatabasePath)));
        return new(items, sources, truncated);
    }

    private static InventorySourceResult DetectedUnsupported(string name, bool detected) => detected
        ? new(name, InventorySourceStatus.Unsupported, "The package database was detected but is not parsed by this bOps version.")
        : new(name, InventorySourceStatus.NotApplicable, "The package database was not detected.");
}

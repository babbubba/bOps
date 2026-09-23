// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

/// <summary>Stable L0 inventory of the first-party V1.3 host-diagnostic manifests.</summary>
public sealed class V13DiagnosticSurfaceTests
{
    private static readonly string[] Expected =
    [
        "system.info", "system.time", "system.reboot_pending", "system.updates", "system.update_history", "system.crashes", "system.drivers", "system.apps", "system.devices", "system.events", "system.cpu", "system.memory", "system.disk", "system.swap", "system.io",
        "process.list", "process.inspect", "process.metrics", "process.tree", "process.modules", "process.stop", "process.kill",
        "fs.list", "fs.read", "fs.stat", "fs.write", "fs.delete", "fs.search", "fs.hash", "fs.move", "fs.size", "fs.delete_tree.prepare", "fs.delete_tree.verify", "fs.delete_tree", "fs.grep", "fs.tail", "fs.permissions", "fs.locks", "fs.copy.verify", "fs.copy", "fs.mkdir",
        "network.interfaces", "network.dns", "network.ping", "network.connections", "network.port_check", "network.route", "network.sockets", "network.routes", "network.neighbors", "network.interface_stats", "network.dns_query", "network.traceroute", "network.ntp_probe",
        "storage.disks", "storage.partitions", "storage.mounts", "storage.io", "storage.health",
        "service.list", "service.status", "service.start", "service.stop", "service.restart", "service.config", "service.dependencies", "service.enable", "service.disable",
        "scheduler.list", "scheduler.inspect", "scheduler.history", "scheduler.enable", "scheduler.disable",
        "identity.current", "identity.users", "identity.groups", "identity.sessions",
        "firewall.status", "firewall.rules", "firewall.rule.inspect",
        "network.tls_probe", "certificate.list", "certificate.inspect",
        "docker.containers", "docker.images", "docker.networks", "docker.inspect", "docker.logs", "docker.start", "docker.stop", "docker.restart", "docker.image.inspect", "docker.image.pull", "docker.image.tag", "docker.image.remove", "docker.build", "docker.volumes", "docker.volume.inspect", "docker.volume.create", "docker.volume.remove",
    ];

    [Fact]
    public void ExpectedV13DiagnosticManifests_ArePresentExactlyOnceInTheManifestSources()
    {
        var source = string.Join('\n', SourceFiles().Select(File.ReadAllText));

        Assert.Equal(Expected.Length, Expected.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in Expected)
        {
            Assert.Contains($"\"{name}\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DiagnosticSurface_HasNoForbiddenGenericCapability()
    {
        var forbidden = new[] { "shell", "exec", "command", "process.start", "sql", "firewall.raw", "process.environment", "private-key" };
        var offending = Expected.Where(name => forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();

        Assert.Empty(offending);
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "bOps.slnx"))) root = root.Parent;
        return root is null ? [] : Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "packages"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}

// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Hosting;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Web;
using bOps.Runtime;

namespace bOps.Architecture.Tests;

/// <summary>Stable L0 inventory of the raw first-party V1.3 host-diagnostic registrations.</summary>
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
        "web.search", "web.fetch",
    ];

    [Fact]
    public void RawFirstPartyRegistrations_MatchTheExpectedV13DiagnosticSurfaceExactly()
    {
        var registrations = CreateRawRegistrations();
        var actual = registrations.Select(registration => registration.Tool.Manifest.Name).ToArray();
        var comparison = Compare(Expected, actual);

        Assert.Equal(Expected.Length, Expected.Distinct(StringComparer.Ordinal).Count());
        Assert.True(comparison.IsExact, comparison.ToDiagnosticMessage());
        Assert.Equal(Expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RawFirstPartyRegistrations_HaveUniqueToolNamesBeforeSetComparison()
    {
        var duplicates = CreateRawRegistrations()
            .GroupBy(registration => registration.Tool.Manifest.Name, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToArray();

        Assert.True(duplicates.Length == 0, $"Duplicate raw first-party registrations: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void CanonicalComposition_RegistersTheRawSequenceWithoutAvailabilityFiltering()
    {
        var registrations = CreateRawRegistrations();
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());

        FirstPartyToolComposition.Register(registry, registrations);

        foreach (var registration in registrations)
        {
            Assert.Equal(registration.PackageId, registration.Tool.Manifest.Package);
        }
    }

    [Fact]
    public void Comparison_RejectsAnUnexpectedRegistration()
    {
        var comparison = Compare(["diagnostic.expected"], ["diagnostic.expected", "diagnostic.fake"]);

        Assert.False(comparison.IsExact);
        Assert.Equal(["diagnostic.fake"], comparison.Unexpected);
    }

    [Fact]
    public void Comparison_RejectsAMissingRegistration()
    {
        var comparison = Compare(["diagnostic.expected"], []);

        Assert.False(comparison.IsExact);
        Assert.Equal(["diagnostic.expected"], comparison.Missing);
    }

    [Fact]
    public void DiagnosticSurface_HasNoForbiddenGenericCapability()
    {
        var forbidden = new[] { "shell", "exec", "command", "process.start", "sql", "firewall.raw", "process.environment", "private-key" };
        var offending = Expected.Where(name => forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();

        Assert.Empty(offending);
    }

    private static IReadOnlyList<FirstPartyToolRegistration> CreateRawRegistrations()
    {
        var filesystemProvider = new FilesystemToolProvider(new FilesystemPathPolicy([], []));
        using var webProvider = new WebToolProvider(new WebFetchOptions(), new WebSearchOptions());

        return FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
            filesystemProvider,
            webProvider,
            new DockerClientFactory(),
            new DockerBuildOptions(),
            new DockerVolumeOptions()));
    }

    private static SurfaceComparison Compare(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedNames = expected.Order(StringComparer.Ordinal).ToArray();
        var actualNames = actual.Order(StringComparer.Ordinal).ToArray();
        return new SurfaceComparison(
            expectedNames.Except(actualNames, StringComparer.Ordinal).ToArray(),
            actualNames.Except(expectedNames, StringComparer.Ordinal).ToArray());
    }

    private sealed record SurfaceComparison(IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected)
    {
        public bool IsExact => Missing.Count == 0 && Unexpected.Count == 0;

        public string ToDiagnosticMessage() =>
            $"Missing: {string.Join(", ", Missing)}{Environment.NewLine}Unexpected: {string.Join(", ", Unexpected)}";
    }

    private sealed class AlwaysAvailableCapabilityProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }
}

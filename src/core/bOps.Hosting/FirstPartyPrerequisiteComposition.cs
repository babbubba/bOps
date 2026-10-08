// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Docker;
using bOps.Packages.Web;
using bOps.Runtime;

namespace bOps.Hosting;

/// <summary>
/// Registers the first-party prerequisite checks (ADR-0049) against the host's one <see cref="PrerequisiteRegistry"/>. The
/// prerequisite ids are the capability ids the tools already declare in <c>Requires</c>; each descriptor is owned by its package and
/// the existing boolean check is adapted without changing what it observes. The runtime itself never names any of them (rule A1).
/// </summary>
public static class FirstPartyPrerequisiteComposition
{
    /// <summary>Registers the Docker and SearXNG prerequisites under their packages' host-assigned ids.</summary>
    public static void Register(
        PrerequisiteRegistry registry,
        IDockerClientFactory dockerClientFactory,
        DockerBuildOptions dockerBuildOptions,
        WebSearchOptions webSearchOptions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dockerClientFactory);
        ArgumentNullException.ThrowIfNull(dockerBuildOptions);
        ArgumentNullException.ThrowIfNull(webSearchOptions);

        registry.Register(
            new PackageId("bops.packages.docker"),
            new BooleanPrerequisiteCheck(DockerCapability.DaemonDescriptor, ct => DockerCapability.IsAvailableAsync(dockerClientFactory, ct)));
        registry.Register(
            new PackageId("bops.packages.docker"),
            new BooleanPrerequisiteCheck(DockerCapability.BuildContextsDescriptor, _ => DockerCapability.IsBuildConfiguredAsync(dockerBuildOptions)));
        registry.Register(
            new PackageId("bops.packages.web"),
            new BooleanPrerequisiteCheck(WebCapabilities.SearxngDescriptor, ct => WebCapabilities.IsSearxngConfiguredAsync(webSearchOptions, ct)));
    }
}

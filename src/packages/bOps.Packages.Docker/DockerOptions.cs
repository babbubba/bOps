// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Docker;

/// <summary>
/// Where <c>docker.build</c> may build from and how much it may send to the daemon (ADR-0033). Bound from
/// <c>Docker:Build</c>. Deny by default: with no <see cref="Contexts"/> nothing can be built, and the tool is hidden from the
/// planner (<see cref="DockerCapability.BuildContexts"/>). These are the Docker package's own settings; the filesystem
/// package's patterns are never consulted.
/// </summary>
public sealed class DockerBuildOptions
{
    /// <summary>The largest number of files a context may hold, unless configured otherwise.</summary>
    public const int DefaultMaximumContextFiles = 10_000;

    /// <summary>The largest number of bytes a context may hold, unless configured otherwise (64 MiB).</summary>
    public const long DefaultMaximumContextBytes = 64L * 1024 * 1024;

    /// <summary>The directories a build context must be, or be inside of, after every symbolic link is resolved.</summary>
    public IReadOnlyList<string> Contexts { get; init; } = [];

    /// <summary>The most files one context may hold.</summary>
    public int MaximumContextFiles { get; init; } = DefaultMaximumContextFiles;

    /// <summary>The most bytes (of file content) one context may hold.</summary>
    public long MaximumContextBytes { get; init; } = DefaultMaximumContextBytes;

    /// <summary>True when at least one context directory is configured.</summary>
    public bool IsConfigured => Contexts.Any(context => !string.IsNullOrWhiteSpace(context));
}

/// <summary>The volume drivers <c>docker.volume.create</c> accepts (ADR-0033). Bound from <c>Docker:Volumes</c>.</summary>
public sealed class DockerVolumeOptions
{
    /// <summary>The driver used, and the only one allowed, when nothing is configured.</summary>
    public const string DefaultDriver = "local";

    /// <summary>The allowed drivers. Empty means <c>local</c> only.</summary>
    public IReadOnlyList<string> Drivers { get; init; } = [];

    /// <summary>The drivers actually allowed: the configured ones, or <c>local</c>.</summary>
    public IReadOnlyList<string> AllowedDrivers =>
        Drivers.Where(driver => !string.IsNullOrWhiteSpace(driver)).Distinct(StringComparer.Ordinal).ToArray() is { Length: > 0 } configured
            ? configured
            : [DefaultDriver];
}

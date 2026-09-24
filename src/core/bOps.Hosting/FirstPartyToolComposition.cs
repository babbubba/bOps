// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Firewall.Linux;
using bOps.Packages.Firewall.Windows;
using bOps.Packages.Identity.Linux;
using bOps.Packages.Identity.Windows;
using bOps.Packages.Network;
using bOps.Packages.Network.Native.Linux;
using bOps.Packages.Network.Native.Windows;
using bOps.Packages.Security;
using bOps.Packages.Service.Linux;
using bOps.Packages.Service.Windows;
using bOps.Packages.Scheduler.Linux;
using bOps.Packages.Scheduler.Windows;
using bOps.Packages.Storage.Linux;
using bOps.Packages.Storage.Windows;
using bOps.Packages.Sys.Linux;
using bOps.Packages.Sys.Windows;
using bOps.Packages.Web;

namespace bOps.Hosting;

/// <summary>A first-party tool and the host-assigned package identity under which it is registered.</summary>
public sealed record FirstPartyToolRegistration(PackageId PackageId, ITool Tool);

/// <summary>Host-owned dependencies used by the canonical first-party tool composition.</summary>
public sealed record FirstPartyToolCompositionOptions(
    FilesystemToolProvider FilesystemToolProvider,
    WebToolProvider WebToolProvider,
    IDockerClientFactory DockerClientFactory,
    DockerBuildOptions DockerBuildOptions,
    DockerVolumeOptions DockerVolumeOptions);

/// <summary>Builds and registers the complete statically composed first-party operational tool surface.</summary>
public static class FirstPartyToolComposition
{
    /// <summary>Creates the raw registrations before platform/capability availability filtering.</summary>
    public static IReadOnlyList<FirstPartyToolRegistration> Create(FirstPartyToolCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var registrations = new List<FirstPartyToolRegistration>();
        Add(registrations, new PackageId($"bops.packages.system.{CurrentPlatform.Id}"), CreateSystemProvider());
        Add(registrations, new PackageId("bops.packages.filesystem"), options.FilesystemToolProvider);
        Add(registrations, new PackageId("bops.packages.network"), new NetworkToolProvider());
        Add(registrations, new PackageId("bops.packages.security"), new SecurityToolProvider());
        Add(registrations, new PackageId($"bops.packages.network.native.{CurrentPlatform.Id}"), CreateNetworkNativeProvider());
        Add(registrations, new PackageId($"bops.packages.firewall.{CurrentPlatform.Id}"), CreateFirewallProvider());
        Add(registrations, new PackageId($"bops.packages.storage.{CurrentPlatform.Id}"), CreateStorageProvider());
        Add(registrations, new PackageId($"bops.packages.service.{CurrentPlatform.Id}"), CreateServiceProvider());
        Add(registrations, new PackageId($"bops.packages.scheduler.{CurrentPlatform.Id}"), CreateSchedulerProvider());
        Add(registrations, new PackageId($"bops.packages.identity.{CurrentPlatform.Id}"), CreateIdentityProvider());
        Add(registrations, new PackageId("bops.packages.docker"), new DockerToolProvider(
            options.DockerClientFactory, options.DockerBuildOptions, options.DockerVolumeOptions));
        Add(registrations, new PackageId("bops.packages.web"), options.WebToolProvider);

        return registrations;
    }

    /// <summary>Registers the exact raw sequence returned by <see cref="Create"/>.</summary>
    public static void Register(IToolRegistry registry, IReadOnlyList<FirstPartyToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            registry.Register(registration.PackageId, registration.Tool);
        }
    }

    private static IToolProvider CreateSystemProvider() =>
        OperatingSystem.IsWindows() ? new WindowsSystemToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxSystemToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider CreateNetworkNativeProvider() =>
        OperatingSystem.IsWindows() ? new WindowsNetworkNativeToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxNetworkNativeToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider CreateFirewallProvider() =>
        OperatingSystem.IsWindows() ? new WindowsFirewallToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxFirewallToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider CreateStorageProvider() =>
        OperatingSystem.IsWindows() ? new WindowsStorageToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxStorageToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider CreateServiceProvider() =>
        OperatingSystem.IsWindows() ? new WindowsServiceToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxServiceToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider CreateSchedulerProvider() =>
        OperatingSystem.IsWindows() ? new WindowsSchedulerToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxSchedulerToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider CreateIdentityProvider() =>
        OperatingSystem.IsWindows() ? new WindowsIdentityToolProvider() :
        OperatingSystem.IsLinux() ? new LinuxIdentityToolProvider() : ThrowUnsupportedPlatform();

    private static IToolProvider ThrowUnsupportedPlatform() => throw new PlatformNotSupportedException(
        "bOps supports Windows and Linux only (agentic/00-project-spec.md).");

    private static void Add(
        List<FirstPartyToolRegistration> registrations,
        PackageId packageId,
        IToolProvider provider)
    {
        foreach (var tool in provider.GetTools())
        {
            registrations.Add(new FirstPartyToolRegistration(packageId, tool));
        }
    }
}

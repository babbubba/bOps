// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

public sealed class DriverModuleEvidenceSecurityTests
{
    [Fact]
    public void K7ProductionSourcesContainNoDriverOrModuleMutationSurface()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "bOps.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var windows = File.ReadAllText(Path.Combine(root!.FullName, "src", "packages", "bOps.Packages.System.Windows", "WindowsDriverEvidenceTool.cs"));
        var linux = File.ReadAllText(Path.Combine(root.FullName, "src", "packages", "bOps.Packages.System.Linux", "LinuxDriverEvidenceTool.cs"));
        foreach (var forbidden in new[] { "StartService", "ControlService", "CreateService", "DeleteService", "SetupCopyOEMInf", "pnputil", "driverquery", "PowerShell", "AdjustTokenPrivileges", "SeDebugPrivilege" }) Assert.DoesNotContain(forbidden, windows, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "init_module", "finit_module", "delete_module", "insmod", "rmmod", "modprobe", "sh -c", "bash -c", "File.Write", "WriteAllText", "ProcessStartInfo" }) Assert.DoesNotContain(forbidden, linux, StringComparison.OrdinalIgnoreCase);
    }
}

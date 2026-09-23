// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Reads currently loaded Windows drivers through the bounded PSAPI device-driver inventory.</summary>
public sealed class WindowsDriverEvidenceTool : SystemDriversToolBase
{
    private const string SourceName = "windows-psapi-enum-device-drivers";
    private const int NameCapacity = 512;
    private readonly Func<IntPtr[], (bool Success, uint BytesNeeded)> enumerate;
    private readonly Func<IntPtr, string?> baseName;
    private readonly Func<IntPtr, string?> fileName;
    private readonly Func<string, (string? Version, string? Vendor)> version;

    public WindowsDriverEvidenceTool() : this(Enumerate, ReadBaseName, ReadFileName, ReadVersion) { }

    internal WindowsDriverEvidenceTool(Func<IntPtr[], (bool Success, uint BytesNeeded)> enumerate, Func<IntPtr, string?> baseName, Func<IntPtr, string?> fileName, Func<string, (string? Version, string? Vendor)> version) : base("windows")
    { this.enumerate = enumerate; this.baseName = baseName; this.fileName = fileName; this.version = version; }

    protected override Task<MaintenanceSnapshot<DriverRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = WindowsDriverEnumeration.Read(enumerate, SystemMaintenanceLimits.MaximumDrivers + 1);
        if (!result.Success) return Task.FromResult(new MaintenanceSnapshot<DriverRecord>([], [new(SourceName, InventorySourceStatus.Unavailable, result.Detail)], [result.Warning!]));
        if (result.Bases.All(x => x == IntPtr.Zero))
            return Task.FromResult(new MaintenanceSnapshot<DriverRecord>([], [new(SourceName, InventorySourceStatus.Partial, "usable image bases unavailable")], ["Windows enumerated driver slots but did not expose usable image bases or metadata."]));

        var rows = new List<DriverRecord>();
        var warnings = new List<string>();
        foreach (var address in result.Bases.Where(x => x != IntPtr.Zero))
        {
            ct.ThrowIfCancellationRequested();
            var name = baseName(address);
            if (string.IsNullOrWhiteSpace(name)) { warnings.Add("A driver disappeared before its base name could be read."); continue; }
            var nativePath = fileName(address);
            string? filePath = TryLocalDriverPath(nativePath);
            string? rowVersion = null;
            string? vendor = null;
            if (filePath is not null)
            {
                try { (rowVersion, vendor) = version(filePath); }
                catch (IOException) { warnings.Add("Driver version metadata was unavailable for one row."); }
                catch (UnauthorizedAccessException) { warnings.Add("Driver version metadata was unavailable for one row."); }
                catch (Win32Exception) { warnings.Add("Driver version metadata was unavailable for one row."); }
            }
            rows.Add(new DriverRecord(name, nativePath, rowVersion, vendor, "loaded", true,
                "0x" + ((nuint)address).ToString("x", CultureInfo.InvariantCulture), SourceName));
        }
        var partial = result.Truncated || warnings.Count > 0;
        if (result.Truncated) warnings.Add("Driver inventory was truncated by the bounded native collection limit.");
        return Task.FromResult(new MaintenanceSnapshot<DriverRecord>(rows, [new(SourceName, partial ? InventorySourceStatus.Partial : InventorySourceStatus.Available)], warnings, result.Truncated));
    }

    private static (bool Success, uint BytesNeeded) Enumerate(IntPtr[] buffer) => (NativeMethods.EnumDeviceDrivers(buffer, checked((uint)(buffer.Length * IntPtr.Size)), out var needed), needed);
    private static unsafe string? ReadBaseName(IntPtr address) => ReadNativeString(address, &NativeMethods.GetDeviceDriverBaseName);
    private static unsafe string? ReadFileName(IntPtr address) => ReadNativeString(address, &NativeMethods.GetDeviceDriverFileName);
    private static unsafe string? ReadNativeString(IntPtr address, delegate* managed<IntPtr, char*, uint, uint> read)
    {
        var buffer = stackalloc char[NameCapacity];
        var length = read(address, buffer, NameCapacity);
        return length == 0 || length >= NameCapacity ? null : new string(buffer, 0, checked((int)length));
    }
    private static (string? Version, string? Vendor) ReadVersion(string path)
    { var info = FileVersionInfo.GetVersionInfo(path); return (Bound(info.FileVersion), Bound(info.CompanyName)); }
    private static string? Bound(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 256 ? value : value[..256];
    private static string? TryLocalDriverPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > SystemMaintenanceLimits.PathCharacters) return null;
        const string systemRoot = "\\SystemRoot\\";
        if (path.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[systemRoot.Length..]);
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }
}

internal sealed record WindowsDriverEnumeration(bool Success, IReadOnlyList<IntPtr> Bases, bool Truncated, string? Detail, string? Warning)
{
    internal static WindowsDriverEnumeration Read(Func<IntPtr[], (bool Success, uint BytesNeeded)> enumerate, int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        var capacity = Math.Min(256, maximumEntries);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new IntPtr[capacity];
            var call = enumerate(buffer);
            if (!call.Success) return new(false, [], false, "native API failure", "EnumDeviceDrivers failed.");
            if (call.BytesNeeded % IntPtr.Size != 0) return new(false, [], false, "malformed byte count", "EnumDeviceDrivers returned a non-integral pointer byte count.");
            var count = checked((int)(call.BytesNeeded / IntPtr.Size));
            if (count > maximumEntries) return new(false, [], false, "native collection exceeds bounded maximum", "EnumDeviceDrivers requested an oversized buffer.");
            if (count <= buffer.Length) return new(true, buffer.Take(count).ToArray(), false, null, null);
            capacity = count;
        }
        return new(false, [], false, "native inventory changed during bounded retry", "EnumDeviceDrivers did not stabilize within the bounded retry.");
    }
}

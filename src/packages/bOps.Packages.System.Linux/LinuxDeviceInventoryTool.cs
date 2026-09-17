// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects a bounded PCI and USB device inventory from Linux sysfs.</summary>
public sealed class LinuxDeviceInventoryTool() : DeviceInventoryToolBase("linux")
{
    protected override async Task<InventorySnapshot<DeviceInventoryItem>> CollectAsync(
        int collectionLimit,
        CancellationToken ct)
    {
        var items = new List<DeviceInventoryItem>();
        var sources = new List<InventorySourceResult>();
        var truncated = false;

        sources.Add(await ReadPciAsync(items, collectionLimit, value => truncated |= value, ct));
        if (items.Count >= collectionLimit)
        {
            truncated = true;
            sources.Add(new("linux.sysfs-usb", InventorySourceStatus.Partial, "Not inspected after the collection ceiling was reached."));
        }
        else
        {
            sources.Add(await ReadUsbAsync(items, collectionLimit, value => truncated |= value, ct));
        }

        return new(items, sources, truncated);
    }

    private static async Task<InventorySourceResult> ReadPciAsync(
        List<DeviceInventoryItem> items,
        int collectionLimit,
        Action<bool> setTruncated,
        CancellationToken ct)
    {
        const string path = "/sys/bus/pci/devices";
        const string source = "linux.sysfs-pci";
        if (!Directory.Exists(path))
        {
            return new(source, InventorySourceStatus.NotApplicable, "The PCI sysfs bus does not exist.");
        }

        var skipped = 0;
        try
        {
            foreach (var devicePath in Directory.EnumerateDirectories(path))
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count >= collectionLimit)
                {
                    setTruncated(true);
                    return new(source, InventorySourceStatus.Partial, "The collection ceiling was reached.");
                }

                try
                {
                    var identity = Path.GetFileName(devicePath);
                    var vendor = await ReadAttributeAsync(devicePath, "vendor", ct);
                    var device = await ReadAttributeAsync(devicePath, "device", ct);
                    var deviceClass = await ReadAttributeAsync(devicePath, "class", ct);
                    var driver = GetLinkName(Path.Combine(devicePath, "driver"));
                    items.Add(new(
                        $"linux-pci:{identity}",
                        deviceClass ?? "pci",
                        identity,
                        vendor,
                        device,
                        driver is null ? null : $"driver:{driver}",
                        source));
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    skipped++;
                }
            }

            return skipped == 0
                ? new(source, InventorySourceStatus.Available, null)
                : new(source, InventorySourceStatus.Partial, $"{skipped} sysfs entries could not be read.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return new(source, items.Count == 0 ? InventorySourceStatus.Unavailable : InventorySourceStatus.Partial, exception.GetType().Name);
        }
    }

    private static async Task<InventorySourceResult> ReadUsbAsync(
        List<DeviceInventoryItem> items,
        int collectionLimit,
        Action<bool> setTruncated,
        CancellationToken ct)
    {
        const string path = "/sys/bus/usb/devices";
        const string source = "linux.sysfs-usb";
        if (!Directory.Exists(path))
        {
            return new(source, InventorySourceStatus.NotApplicable, "The USB sysfs bus does not exist.");
        }

        var skipped = 0;
        try
        {
            foreach (var devicePath in Directory.EnumerateDirectories(path))
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count >= collectionLimit)
                {
                    setTruncated(true);
                    return new(source, InventorySourceStatus.Partial, "The collection ceiling was reached.");
                }

                try
                {
                    var vendorId = await ReadAttributeAsync(devicePath, "idVendor", ct);
                    var productId = await ReadAttributeAsync(devicePath, "idProduct", ct);
                    if (vendorId is null && productId is null)
                    {
                        continue;
                    }

                    var identity = Path.GetFileName(devicePath);
                    var manufacturer = await ReadAttributeAsync(devicePath, "manufacturer", ct);
                    var product = await ReadAttributeAsync(devicePath, "product", ct);
                    var authorized = await ReadAttributeAsync(devicePath, "authorized", ct);
                    items.Add(new(
                        $"linux-usb:{identity}",
                        "usb",
                        product ?? identity,
                        manufacturer ?? vendorId,
                        productId,
                        authorized switch { "1" => "authorized", "0" => "disabled", _ => null },
                        source));
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    skipped++;
                }
            }

            return skipped == 0
                ? new(source, InventorySourceStatus.Available, null)
                : new(source, InventorySourceStatus.Partial, $"{skipped} sysfs entries could not be read.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return new(source, items.Count == 0 ? InventorySourceStatus.Unavailable : InventorySourceStatus.Partial, exception.GetType().Name);
        }
    }

    private static async Task<string?> ReadAttributeAsync(string directory, string name, CancellationToken ct)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = (await File.ReadAllTextAsync(path, ct)).Trim('\0', ' ', '\r', '\n', '\t');
        return value.Length == 0 ? null : value;
    }

    private static string? GetLinkName(string path)
    {
        var target = new DirectoryInfo(path).LinkTarget;
        return target is null ? null : Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
    }
}

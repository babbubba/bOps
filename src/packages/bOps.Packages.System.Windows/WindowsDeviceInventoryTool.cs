// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security;
using bOps.Packages.Sys.Core;
using Microsoft.Win32;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects a bounded Plug and Play device inventory from the Windows registry.</summary>
public sealed class WindowsDeviceInventoryTool() : DeviceInventoryToolBase("windows")
{
    private const string SourceName = "windows.pnp-registry";
    private const string EnumPath = @"SYSTEM\CurrentControlSet\Enum";

    protected override Task<InventorySnapshot<DeviceInventoryItem>> CollectAsync(
        int collectionLimit,
        CancellationToken ct)
    {
        var items = new List<DeviceInventoryItem>();
        var skippedEntries = 0;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(EnumPath);
            if (root is null)
            {
                return Task.FromResult(new InventorySnapshot<DeviceInventoryItem>(
                    items,
                    [new(SourceName, InventorySourceStatus.NotApplicable, "The Plug and Play registry location does not exist.")]));
            }

            foreach (var enumeratorName in OrderedNames(root))
            {
                ct.ThrowIfCancellationRequested();
                using var enumerator = TryOpen(root, enumeratorName, ref skippedEntries);
                if (enumerator is null)
                {
                    continue;
                }

                foreach (var deviceName in OrderedNames(enumerator))
                {
                    ct.ThrowIfCancellationRequested();
                    using var device = TryOpen(enumerator, deviceName, ref skippedEntries);
                    if (device is null)
                    {
                        continue;
                    }

                    foreach (var instanceName in OrderedNames(device))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (items.Count >= collectionLimit)
                        {
                            return Task.FromResult(Snapshot(items, skippedEntries, collectionTruncated: true));
                        }

                        using var instance = TryOpen(device, instanceName, ref skippedEntries);
                        if (instance is null)
                        {
                            continue;
                        }

                        var identity = $"{enumeratorName}\\{deviceName}\\{instanceName}";
                        var description = ReadString(instance, "FriendlyName") ?? ReadString(instance, "DeviceDesc");
                        items.Add(new DeviceInventoryItem(
                            $"windows-pnp:{identity}",
                            ReadString(instance, "Class") ?? enumeratorName.ToLowerInvariant(),
                            description ?? deviceName,
                            ReadString(instance, "Mfg"),
                            ReadFirstString(instance, "HardwareID"),
                            ReadProblemStatus(instance),
                            SourceName));
                    }
                }
            }

            return Task.FromResult(Snapshot(items, skippedEntries, collectionTruncated: false));
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return Task.FromResult(new InventorySnapshot<DeviceInventoryItem>(
                items,
                [new(SourceName, items.Count == 0 ? InventorySourceStatus.Unavailable : InventorySourceStatus.Partial, exception.GetType().Name)]));
        }
    }

    private static InventorySnapshot<DeviceInventoryItem> Snapshot(
        IReadOnlyList<DeviceInventoryItem> items,
        int skippedEntries,
        bool collectionTruncated)
    {
        var status = collectionTruncated || skippedEntries > 0 ? InventorySourceStatus.Partial : InventorySourceStatus.Available;
        var detail = collectionTruncated
            ? "The collection ceiling was reached."
            : skippedEntries > 0
                ? $"{skippedEntries} registry branches could not be read."
                : null;
        return new(items, [new(SourceName, status, detail)], collectionTruncated);
    }

    private static IEnumerable<string> OrderedNames(RegistryKey key) =>
        key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal);

    private static RegistryKey? TryOpen(RegistryKey parent, string name, ref int skippedEntries)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            skippedEntries++;
            return null;
        }
    }

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string is { } value
            ? value.Trim()
            : null;

    private static string? ReadFirstString(RegistryKey key, string name) => key.GetValue(name) switch
    {
        string value => value.Trim(),
        string[] values => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim(),
        _ => null,
    };

    private static string? ReadProblemStatus(RegistryKey key) => key.GetValue("Problem") switch
    {
        int problem when problem == 0 => "ok",
        int problem => $"problem:{problem.ToString(CultureInfo.InvariantCulture)}",
        long problem when problem == 0 => "ok",
        long problem => $"problem:{problem.ToString(CultureInfo.InvariantCulture)}",
        _ => null,
    };

    private static bool IsExpectedReadFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or IOException;
}

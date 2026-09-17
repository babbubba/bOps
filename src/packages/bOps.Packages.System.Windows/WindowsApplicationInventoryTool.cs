// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security;
using bOps.Packages.Sys.Core;
using Microsoft.Win32;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects installed applications from the Windows Uninstall registry locations.</summary>
public sealed class WindowsApplicationInventoryTool() : ApplicationInventoryToolBase("windows")
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    protected override Task<InventorySnapshot<ApplicationInventoryItem>> CollectAsync(
        int collectionLimit,
        CancellationToken ct)
    {
        var items = new List<ApplicationInventoryItem>();
        var sources = new List<InventorySourceResult>();
        var definitions = new[]
        {
            new SourceDefinition("windows.registry.machine64", RegistryHive.LocalMachine, RegistryView.Registry64),
            new SourceDefinition("windows.registry.machine32", RegistryHive.LocalMachine, RegistryView.Registry32),
            new SourceDefinition("windows.registry.user64", RegistryHive.CurrentUser, RegistryView.Registry64),
            new SourceDefinition("windows.registry.user32", RegistryHive.CurrentUser, RegistryView.Registry32),
        };

        var truncated = false;
        foreach (var definition in definitions)
        {
            ct.ThrowIfCancellationRequested();
            if (definition.View == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem)
            {
                sources.Add(new(definition.Name, InventorySourceStatus.NotApplicable, "The operating system is not 64-bit."));
                continue;
            }

            if (items.Count >= collectionLimit)
            {
                truncated = true;
                sources.Add(new(definition.Name, InventorySourceStatus.Partial, "Not inspected after the collection ceiling was reached."));
                continue;
            }

            sources.Add(ReadSource(definition, items, collectionLimit, ct, out var sourceTruncated));
            truncated |= sourceTruncated;
        }

        return Task.FromResult(new InventorySnapshot<ApplicationInventoryItem>(items, sources, truncated));
    }

    private static InventorySourceResult ReadSource(
        SourceDefinition definition,
        List<ApplicationInventoryItem> items,
        int collectionLimit,
        CancellationToken ct,
        out bool truncated)
    {
        truncated = false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(definition.Hive, definition.View);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey is null)
            {
                return new(definition.Name, InventorySourceStatus.NotApplicable, "The Uninstall registry location does not exist.");
            }

            var skippedEntries = 0;
            foreach (var subkeyName in uninstallKey.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count >= collectionLimit)
                {
                    truncated = true;
                    return new(definition.Name, InventorySourceStatus.Partial, "The collection ceiling was reached.");
                }

                try
                {
                    using var entry = uninstallKey.OpenSubKey(subkeyName);
                    if (entry is null || ReadInteger(entry, "SystemComponent") == 1)
                    {
                        continue;
                    }

                    var displayName = ReadString(entry, "DisplayName");
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    items.Add(new ApplicationInventoryItem(
                        $"windows-registry:{definition.Name}:{subkeyName}",
                        displayName,
                        ReadString(entry, "DisplayVersion"),
                        ReadString(entry, "Publisher"),
                        definition.Name));
                }
                catch (Exception exception) when (IsExpectedReadFailure(exception))
                {
                    skippedEntries++;
                }
            }

            return skippedEntries == 0
                ? new(definition.Name, InventorySourceStatus.Available, null)
                : new(definition.Name, InventorySourceStatus.Partial, $"{skippedEntries} registry entries could not be read.");
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return new(definition.Name, InventorySourceStatus.Unavailable, exception.GetType().Name);
        }
    }

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string is { } value
            ? value.Trim()
            : null;

    private static int? ReadInteger(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int value => value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        _ => null,
    };

    private static bool IsExpectedReadFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or IOException;

    private sealed record SourceDefinition(string Name, RegistryHive Hive, RegistryView View);
}

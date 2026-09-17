// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>Creates the common bounded JSON output for cross-platform system inventories.</summary>
public static class SystemInventoryFormatting
{
    /// <summary>Formats an application inventory with deterministic deduplication and bounds.</summary>
    public static string FormatApplications(
        InventorySnapshot<ApplicationInventoryItem> snapshot,
        int limit,
        int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var items = snapshot.Items
            .Where(IsValid)
            .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();

        return Format(
            items,
            snapshot.Sources,
            snapshot.CollectionTruncated,
            limit,
            maxOutputBytes,
            item => new JsonObject
            {
                ["identity"] = Bounded(item.Identity, 512),
                ["name"] = Bounded(item.Name, 512),
                ["version"] = Bounded(item.Version, 256),
                ["publisher"] = Bounded(item.Publisher, 512),
                ["source"] = Bounded(item.Source, 128),
            });
    }

    /// <summary>Formats a device inventory with deterministic deduplication and bounds.</summary>
    public static string FormatDevices(
        InventorySnapshot<DeviceInventoryItem> snapshot,
        int limit,
        int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var items = snapshot.Items
            .Where(IsValid)
            .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();

        return Format(
            items,
            snapshot.Sources,
            snapshot.CollectionTruncated,
            limit,
            maxOutputBytes,
            item => new JsonObject
            {
                ["identity"] = Bounded(item.Identity, 512),
                ["category"] = Bounded(item.Category, 128),
                ["name"] = Bounded(item.Name, 512),
                ["vendor"] = Bounded(item.Vendor, 256),
                ["model"] = Bounded(item.Model, 256),
                ["status"] = Bounded(item.Status, 128),
                ["source"] = Bounded(item.Source, 128),
            });
    }

    private static string Format<T>(
        IReadOnlyList<T> observedItems,
        IReadOnlyList<InventorySourceResult> sources,
        bool collectionTruncated,
        int limit,
        int maxOutputBytes,
        Func<T, JsonObject> createItem)
    {
        var selectedItems = observedItems.Take(limit).Select(createItem).ToList();
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = GetStatus(sources),
            ["observedItems"] = observedItems.Count,
            ["returnedItems"] = selectedItems.Count,
            ["truncated"] = collectionTruncated || selectedItems.Count < observedItems.Count,
            ["sources"] = new JsonArray(sources
                .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.Name, StringComparer.Ordinal)
                .Select(source => (JsonNode)new JsonObject
                {
                    ["name"] = Bounded(source.Name, 128),
                    ["status"] = ToWireValue(source.Status),
                    ["detail"] = Bounded(source.Detail, 256),
                })
                .ToArray()),
            ["items"] = new JsonArray(selectedItems.Select(item => (JsonNode)item).ToArray()),
        };

        var output = root.ToJsonString();
        var itemArray = root["items"]!.AsArray();
        while (Encoding.UTF8.GetByteCount(output) > maxOutputBytes && itemArray.Count > 0)
        {
            itemArray.RemoveAt(itemArray.Count - 1);
            root["returnedItems"] = itemArray.Count;
            root["truncated"] = true;
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > maxOutputBytes)
        {
            throw new InvalidOperationException(
                $"Inventory metadata exceeds the configured {maxOutputBytes}-byte output limit.");
        }

        return output;
    }

    private static string GetStatus(IReadOnlyList<InventorySourceResult> sources)
    {
        var applicable = sources
            .Where(source => source.Status != InventorySourceStatus.NotApplicable)
            .ToArray();
        if (applicable.Length == 0)
        {
            return "unavailable";
        }

        if (applicable.All(source => source.Status == InventorySourceStatus.Available))
        {
            return "complete";
        }

        return applicable.Any(source => source.Status is InventorySourceStatus.Available or InventorySourceStatus.Partial)
            ? "partial"
            : "unavailable";
    }

    private static string ToWireValue(InventorySourceStatus status) => status switch
    {
        InventorySourceStatus.Available => "available",
        InventorySourceStatus.Partial => "partial",
        InventorySourceStatus.Unavailable => "unavailable",
        InventorySourceStatus.Unsupported => "unsupported",
        InventorySourceStatus.NotApplicable => "notApplicable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown inventory source status."),
    };

    private static bool IsValid(ApplicationInventoryItem item) =>
        !string.IsNullOrWhiteSpace(item.Identity)
        && !string.IsNullOrWhiteSpace(item.Name)
        && !string.IsNullOrWhiteSpace(item.Source);

    private static bool IsValid(DeviceInventoryItem item) =>
        !string.IsNullOrWhiteSpace(item.Identity)
        && !string.IsNullOrWhiteSpace(item.Category)
        && !string.IsNullOrWhiteSpace(item.Name)
        && !string.IsNullOrWhiteSpace(item.Source);

    private static string? Bounded(string? value, int maximumCharacters) =>
        value is null || value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];
}

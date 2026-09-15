// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace bOps.PluginHost;

/// <summary>
/// Tracks installed plugins in a single JSON file (ADR-0020). Every write goes to a temp file
/// first and is atomically renamed into place, so a process killed mid-write leaves the previous,
/// valid state rather than a half-written file the next read would fail to parse. A rejected
/// operation (a duplicate id, an unknown id) never touches disk at all — validation happens
/// entirely in memory before any write is attempted.
/// </summary>
public sealed class PluginStore(string storeFilePath)
{
    /// <summary>Every installed plugin, enabled or not.</summary>
    public IReadOnlyList<PluginRecord> List() => LoadAll();

    /// <summary>The installed plugin with this id, or <c>null</c> if none is installed.</summary>
    public PluginRecord? Find(string id) =>
        LoadAll().FirstOrDefault(record => string.Equals(record.Id, id, StringComparison.Ordinal));

    /// <summary>Records a newly installed plugin. Throws if its id is already installed.</summary>
    public void Add(PluginRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var all = LoadAll();
        if (all.Any(existing => string.Equals(existing.Id, record.Id, StringComparison.Ordinal)))
        {
            throw new PluginOperationException($"A plugin with id '{record.Id}' is already installed.");
        }

        all.Add(record);
        SaveAll(all);
    }

    /// <summary>Flips the enabled flag for an installed plugin. Throws if the id is not installed.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var all = LoadAll();
        var index = all.FindIndex(record => string.Equals(record.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new PluginOperationException($"No installed plugin with id '{id}'.");
        }

        all[index] = all[index] with { Enabled = enabled };
        SaveAll(all);
    }

    /// <summary>Drops an installed plugin's record. Throws if the id is not installed.</summary>
    public void Remove(string id)
    {
        var all = LoadAll();
        if (all.RemoveAll(record => string.Equals(record.Id, id, StringComparison.Ordinal)) == 0)
        {
            throw new PluginOperationException($"No installed plugin with id '{id}'.");
        }

        SaveAll(all);
    }

    private List<PluginRecord> LoadAll()
    {
        if (!File.Exists(storeFilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(storeFilePath);
            return JsonSerializer.Deserialize(json, PluginStoreJsonContext.Default.ListPluginRecord) ?? [];
        }
        catch (JsonException ex)
        {
            throw new PluginOperationException($"'{storeFilePath}' (plugins.json) is corrupted and could not be parsed.", ex);
        }
    }

    private void SaveAll(List<PluginRecord> records)
    {
        var directory = Path.GetDirectoryName(storeFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(records, PluginStoreJsonContext.Default.ListPluginRecord);
        var tempPath = $"{storeFilePath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, storeFilePath, overwrite: true);
    }
}

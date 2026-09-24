// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace bOps.PluginHost;

/// <summary>The sole atomically-written lifecycle authority. Legacy record arrays migrate idempotently on mutation.</summary>
public sealed class PluginStore(string storeFilePath)
{
    private const int CurrentSchemaVersion = 1;

    // Serializes read-modify-write cycles within one process so concurrent lifecycle mutations of
    // different plugins never lose each other's update. Multi-process access is unsupported (ADR-0037).
    private readonly object _gate = new();

    public IReadOnlyList<PluginRecord> List() => Load().Plugins;
    public PluginRecord? Find(string id) => Load().Plugins.FirstOrDefault(record => string.Equals(record.Id, id, StringComparison.Ordinal));

    public void Add(PluginRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Mutate(document =>
        {
            if (document.Plugins.Any(existing => string.Equals(existing.Id, record.Id, StringComparison.Ordinal)))
                throw new PluginOperationException($"A plugin with id '{record.Id}' is already installed.");
            document.Plugins.Add(record);
            document.Lifecycles[record.Id] = CreateInitialLifecycle(record);
        });
    }

    public void SetEnabled(string id, bool enabled)
    {
        Mutate(document =>
        {
            var index = document.Plugins.FindIndex(record => string.Equals(record.Id, id, StringComparison.Ordinal));
            if (index < 0) throw new PluginOperationException($"No installed plugin with id '{id}'.");
            var lifecycle = RequireLifecycle(document, document.Plugins[index]);
            document.Plugins[index] = document.Plugins[index] with { Enabled = enabled };
            document.Lifecycles[id] = lifecycle with
            {
                LifecycleVersion = checked(lifecycle.LifecycleVersion + 1),
                State = enabled ? PluginLifecycleState.Enabled : PluginLifecycleState.InstalledDisabled,
                TransactionRollbackGenerationId = null,
                CandidateGenerationId = null,
                TransactionPhase = PluginTransactionPhase.None,
                SanitizedFailure = null,
            };
            if (enabled)
            {
                document.Lifecycles[id] = PluginLifecycleTransitions.WithActivationLkg(document.Lifecycles[id], DateTimeOffset.UtcNow);
            }
        });
    }

    public void Remove(string id) => Mutate(document =>
    {
        if (document.Plugins.RemoveAll(record => string.Equals(record.Id, id, StringComparison.Ordinal)) == 0)
            throw new PluginOperationException($"No installed plugin with id '{id}'.");
        document.Lifecycles.Remove(id);
        document.Journals.Remove(id);
    });

    internal PluginLifecycleMetadata GetLifecycle(string id)
    {
        var document = Load();
        var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new PluginOperationException($"No installed plugin with id '{id}'.");
        return RequireLifecycle(document, record);
    }

    internal PluginLifecycleJournal? GetJournal(string id) => Load().Journals.GetValueOrDefault(id);
    internal IReadOnlyList<string> GetJournalPluginIds() => Load().Journals.Keys.ToArray();

    internal void SetLifecycle(string id, PluginLifecycleMetadata lifecycle, PluginLifecycleJournal? journal = null) => Mutate(document =>
    {
        if (!document.Plugins.Any(record => string.Equals(record.Id, id, StringComparison.Ordinal)))
            throw new PluginOperationException($"No installed plugin with id '{id}'.");
        document.Lifecycles[id] = lifecycle;
        if (journal is null) document.Journals.Remove(id); else document.Journals[id] = journal;
    });

    /// <summary>Reads the current durable document under the store gate. Callers must treat it as a snapshot.</summary>
    internal PluginLifecycleDocument Read() => Load();

    /// <summary>One atomically persisted read-modify-write. The document write is the authoritative commit.</summary>
    internal void Mutate(Action<PluginLifecycleDocument> mutation)
    {
        lock (_gate)
        {
            var document = Load();
            EnsureLifecycleDefaults(document);
            mutation(document);
            document.SchemaVersion = CurrentSchemaVersion;
            Save(document);
        }
    }

    private PluginLifecycleDocument Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    private PluginLifecycleDocument LoadCore()
    {
        if (!File.Exists(storeFilePath)) return new PluginLifecycleDocument();
        try
        {
            var json = File.ReadAllText(storeFilePath);
            if (json.TrimStart().StartsWith('['))
            {
                var records = JsonSerializer.Deserialize(json, PluginStoreJsonContext.Default.ListPluginRecord) ?? [];
                var migrated = new PluginLifecycleDocument { Plugins = records };
                EnsureLifecycleDefaults(migrated);
                return migrated;
            }

            var document = JsonSerializer.Deserialize(json, PluginStoreJsonContext.Default.PluginLifecycleDocument)
                ?? throw new JsonException("The lifecycle document is empty.");
            EnsureLifecycleDefaults(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new PluginOperationException($"'{storeFilePath}' (plugins.json) is corrupted and could not be parsed.", ex);
        }
    }

    private static void EnsureLifecycleDefaults(PluginLifecycleDocument document)
    {
        document.Plugins ??= [];
        document.Lifecycles ??= new(StringComparer.Ordinal);
        document.Journals ??= new(StringComparer.Ordinal);
        document.Operations ??= [];
        foreach (var record in document.Plugins) document.Lifecycles.TryAdd(record.Id, CreateInitialLifecycle(record));
    }

    private static PluginLifecycleMetadata RequireLifecycle(PluginLifecycleDocument document, PluginRecord record) =>
        document.Lifecycles.TryGetValue(record.Id, out var lifecycle) ? lifecycle : CreateInitialLifecycle(record);

    private static PluginLifecycleMetadata CreateInitialLifecycle(PluginRecord record)
    {
        // Deterministic so a legacy record migrates to the same generation id however many times it is read before persistence.
        var generationId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{record.Id}|{record.InstalledAtUtc.UtcTicks}|{record.Provenance?.PackageDigestSha256}")))[..32];
        var generation = new PluginGeneration(generationId, record.InstallPath, record.Provenance?.PackageDigestSha256 ?? "legacy-unknown", record.InstalledAtUtc, WasActivationLkg: record.Enabled);
        return new PluginLifecycleMetadata(0, record.Enabled ? PluginLifecycleState.Enabled : PluginLifecycleState.InstalledDisabled,
            generationId, record.Enabled ? generationId : null, null, null, PluginTransactionPhase.None, [generation]);
    }

    private void Save(PluginLifecycleDocument document)
    {
        var directory = Path.GetDirectoryName(storeFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = $"{storeFilePath}.tmp-{Guid.NewGuid():N}";
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) { writer.Write(JsonSerializer.Serialize(document, PluginStoreJsonContext.Default.PluginLifecycleDocument)); writer.Flush(); stream.Flush(flushToDisk: true); }
        File.Move(tempPath, storeFilePath, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(storeFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>The single durable document. Internal: only the store and lifecycle service interpret it.</summary>
internal sealed class PluginLifecycleDocument
{
    public int SchemaVersion { get; set; }
    public List<PluginRecord> Plugins { get; set; } = [];
    public Dictionary<string, PluginLifecycleMetadata> Lifecycles { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PluginLifecycleJournal> Journals { get; set; } = new(StringComparer.Ordinal);
    public List<PluginIdempotencyOperation> Operations { get; set; } = [];
}

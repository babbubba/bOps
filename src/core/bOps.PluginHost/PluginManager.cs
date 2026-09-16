// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using bOps.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace bOps.PluginHost;

/// <summary>
/// Installs, lists, enables, disables and removes plugins (ADR-0020). Registers directly into
/// the same <see cref="IToolRegistry"/>/<see cref="IChatModelRegistry"/> the host already uses
/// for its first-party packages — there is no parallel registry and no special case anywhere
/// else for a dynamically loaded one.
/// </summary>
public sealed class PluginManager(
    PluginStore store,
    IToolRegistry toolRegistry,
    IChatModelRegistry chatModelRegistry,
    string pluginsRootDirectory,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ICapabilityProbe capabilityProbe)
{
    private readonly Dictionary<string, PluginLoadContext> _loadContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginKind> _activatedKinds = new(StringComparer.Ordinal);

    /// <summary>Every installed plugin, enabled or not.</summary>
    public IReadOnlyList<PluginRecord> List() => store.List();

    /// <summary>
    /// Installs a plugin from a local directory (no remote sources in V0.10 — ADR-0020).
    /// Validates the manifest against a staging copy first, so a rejected or interrupted install
    /// leaves nothing behind. Recorded disabled: installing is not enabling (rule S8).
    /// </summary>
    public PluginRecord Install(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        if (!Directory.Exists(sourceDirectory))
        {
            throw new PluginOperationException($"Source directory '{sourceDirectory}' does not exist.");
        }

        var manifest = PluginManifestValidator.ReadManifest(sourceDirectory);
        PluginManifestValidator.Validate(manifest, sourceDirectory);

        if (store.Find(manifest.Id) is not null)
        {
            throw new PluginOperationException($"A plugin with id '{manifest.Id}' is already installed.");
        }

        // Resolved to absolute here, once: AssemblyLoadContext.LoadFromAssemblyPath (used later,
        // at Enable) requires an absolute path, and every stored PluginRecord.InstallPath must
        // already be one for that to work regardless of the host process's current directory.
        var absolutePluginsRoot = Path.GetFullPath(pluginsRootDirectory);
        Directory.CreateDirectory(absolutePluginsRoot);
        var stagingPath = Path.Combine(absolutePluginsRoot, $".staging-{Guid.NewGuid():N}");
        var finalPath = Path.Combine(absolutePluginsRoot, manifest.Id);
        var trustStore = new PluginPublisherTrustStore(
            configuration["Plugins:TrustStorePath"] ?? "publisher-trust.json");
        PluginProvenance provenance;

        try
        {
            CopyDirectory(sourceDirectory, stagingPath);
            // Re-validate against the staged copy — the copy that will actually be loaded from.
            var stagedManifest = PluginManifestValidator.ReadManifest(stagingPath);
            if (!string.Equals(stagedManifest.Id, manifest.Id, StringComparison.Ordinal))
            {
                throw new PluginValidationException(
                    "The plugin manifest changed while its package was being staged; retry from a stable source directory.");
            }

            manifest = stagedManifest;
            PluginManifestValidator.Validate(manifest, stagingPath);
            provenance = PluginPackageSignature.Verify(stagingPath, manifest, trustStore);

            if (Directory.Exists(finalPath))
            {
                throw new PluginOperationException($"'{finalPath}' already exists on disk (not tracked in plugins.json) — remove it manually before installing '{manifest.Id}'.");
            }

            Directory.Move(stagingPath, finalPath);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            throw;
        }

        var record = new PluginRecord(manifest.Id, finalPath, manifest, Enabled: false, timeProvider.GetUtcNow(), provenance);
        store.Add(record);
        return record;
    }

    /// <summary>
    /// Loads and activates every installed plugin the store already marks enabled, without
    /// changing that flag. Called once at host start-up (agentic/01-architecture-rules.md, rule
    /// A10) — <see cref="Enable"/> is the operator-facing command that both activates now and
    /// persists the flag for next time.
    /// </summary>
    public void LoadAllEnabled()
    {
        foreach (var record in store.List().Where(r => r.Enabled))
        {
            Activate(record);
        }
    }

    /// <summary>Activates an installed, currently-disabled plugin now, and persists that it should activate on every future start-up.</summary>
    public void Enable(string id)
    {
        var record = FindOrThrow(id);
        if (record.Enabled)
        {
            throw new PluginOperationException($"Plugin '{id}' is already enabled.");
        }

        if (record.Provenance is not { Verified: true } provenance || provenance.Trust == PackageTrustLevel.Unverified)
        {
            throw new PluginOperationException(
                $"Plugin '{id}' cannot be enabled because its installed bytes do not have a signature from a locally trusted publisher key.");
        }

        Activate(record);
        store.SetEnabled(id, true);
    }

    /// <summary>
    /// Stops an enabled plugin: removes its tools from <see cref="IToolRegistry"/> entirely
    /// (rather than only hiding them — <see cref="IToolRegistry.SetEnabled"/> deliberately does
    /// not release its reference, but a dynamically loaded package's
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> cannot actually unload while
    /// anything still references its types) and waits for that context to actually be collected.
    /// A plugin that registered an <see cref="IModelProviderPackage"/> cannot be fully retracted
    /// — nothing in <see cref="IChatModelRegistry"/> today lets a provider be unregistered — so
    /// this refuses rather than silently leaving it reachable while claiming it is disabled.
    /// </summary>
    public void Disable(string id)
    {
        var record = FindOrThrow(id);
        if (!record.Enabled)
        {
            throw new PluginOperationException($"Plugin '{id}' is already disabled.");
        }

        if (_activatedKinds.TryGetValue(id, out var kind) && kind == PluginKind.ModelProvider)
        {
            throw new PluginOperationException(
                $"Plugin '{id}' registered a chat model provider. IChatModelRegistry has no way to unregister " +
                "one, so it cannot be genuinely disabled in this process — restart the host without enabling " +
                "it instead.");
        }

        toolRegistry.Unregister(new PackageId(id));
        _activatedKinds.Remove(id);

        if (_loadContexts.Remove(id, out var context))
        {
            UnloadAndWaitForCollection(context);
        }

        store.SetEnabled(id, false);
    }

    /// <summary>Disables (if enabled) and permanently deletes an installed plugin.</summary>
    public void Remove(string id)
    {
        var record = FindOrThrow(id);
        if (record.Enabled)
        {
            Disable(id);
        }

        Directory.Delete(record.InstallPath, recursive: true);
        store.Remove(id);
    }

    private void Activate(PluginRecord record)
    {
        if (record.Provenance is not { Verified: true } provenance || provenance.Trust == PackageTrustLevel.Unverified)
        {
            throw new PluginOperationException(
                $"Plugin '{record.Id}' cannot be activated because its installed bytes do not have verified trusted provenance.");
        }

        var manifest = PluginManifestValidator.ReadManifest(record.InstallPath);
        if (!string.Equals(manifest.Id, record.Id, StringComparison.Ordinal))
        {
            throw new PluginOperationException(
                $"Plugin store id '{record.Id}' does not match the signed installed manifest id '{manifest.Id}'.");
        }

        PluginManifestValidator.Validate(manifest, record.InstallPath);
        var current = PluginPackageSignature.Verify(
            record.InstallPath,
            manifest,
            new PluginPublisherTrustStore(configuration["Plugins:TrustStorePath"] ?? "publisher-trust.json"));
        if (!current.Verified || current.Trust == PackageTrustLevel.Unverified ||
            !string.Equals(current.PackageDigestSha256, provenance.PackageDigestSha256, StringComparison.Ordinal))
        {
            throw new PluginOperationException(
                $"Plugin '{record.Id}' no longer matches the package bytes whose provenance was recorded at install time.");
        }

        var mainAssemblyPath = Path.Combine(record.InstallPath, manifest.EntryAssembly);
        var loadContext = new PluginLoadContext(mainAssemblyPath);

        object instance;
        Type entryType;
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(mainAssemblyPath);
            entryType = assembly.GetType(manifest.EntryType)
                ?? throw new PluginOperationException($"Entry type '{manifest.EntryType}' was not found in '{manifest.EntryAssembly}'.");

            var restrictedServices = new RestrictedPackageServiceProvider(
                loggerFactory, httpClientFactory, timeProvider, configuration.GetSection($"Plugins:{record.Id}"), capabilityProbe);
            instance = ActivatorUtilities.CreateInstance(restrictedServices, entryType);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        var packageId = new PackageId(record.Id);
        var isToolProvider = instance is IToolProvider;
        var isModelProvider = instance is IModelProviderPackage;

        if (isToolProvider && isModelProvider)
        {
            loadContext.Unload();
            throw new PluginOperationException(
                $"Entry type '{manifest.EntryType}' implements both {nameof(IToolProvider)} and {nameof(IModelProviderPackage)}; a plugin must implement exactly one.");
        }

        if (isToolProvider)
        {
            foreach (var tool in ((IToolProvider)instance).GetTools())
            {
                toolRegistry.Register(packageId, current.Trust, tool);
            }

            _activatedKinds[record.Id] = PluginKind.ToolProvider;
        }
        else if (isModelProvider)
        {
            chatModelRegistry.Register(packageId, (IModelProviderPackage)instance);
            _activatedKinds[record.Id] = PluginKind.ModelProvider;
        }
        else
        {
            loadContext.Unload();
            throw new PluginOperationException(
                $"Entry type '{manifest.EntryType}' implements neither {nameof(IToolProvider)} nor {nameof(IModelProviderPackage)}.");
        }

        _loadContexts[record.Id] = loadContext;
    }

    /// <summary>
    /// Requests the collectible context's unload, then gives the CLR a bounded number of GC
    /// passes to actually finalize it — <see cref="System.Runtime.Loader.AssemblyLoadContext.Unload"/>
    /// only starts the process; nothing else in this method blocks on it. Bounded, not infinite:
    /// if the CLR genuinely cannot collect it within a handful of passes (something outside this
    /// class still holds a reference), the caller's own file-delete retry is what surfaces that,
    /// not a hang here.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnloadAndWaitForCollection(PluginLoadContext context)
    {
        var contextRef = new WeakReference(context);
        context.Unload();

        for (var attempt = 0; attempt < 10 && contextRef.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private PluginRecord FindOrThrow(string id) =>
        store.Find(id) ?? throw new PluginOperationException($"No installed plugin with id '{id}'.");

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(filePath, Path.Combine(destinationDirectory, Path.GetFileName(filePath)));
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(subDirectory, Path.Combine(destinationDirectory, Path.GetFileName(subDirectory)));
        }
    }

    private enum PluginKind
    {
        ToolProvider,
        ModelProvider,
    }
}

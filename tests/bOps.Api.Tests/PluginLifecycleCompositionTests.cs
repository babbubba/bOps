// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.PluginHost;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0037: M6 consumes the M5 backend from the one canonical host composition. These tests boot the real
/// API host (no second plugin stack) over a legacy-format <c>plugins.json</c>.
/// </summary>
public sealed class PluginLifecycleCompositionTests
{
    private const string PluginId = "acme.composed-plugin";

    [Fact]
    public async Task LifecycleService_IsTheSharedSingleton_OverTheHostsPluginManagerStore_AndAudits()
    {
        using var factory = new TestAppFactory();
        Seed(factory, enabled: false, createInstallDirectory: true);
        using var client = factory.CreateClient();

        var lifecycle = factory.Services.GetRequiredService<PluginLifecycleService>();
        var manager = factory.Services.GetRequiredService<PluginManager>();
        var context = new PluginLifecycleRequestContext(NodeId.Local, new ActorIdentity("api-user", "admin", null), "compose-1", "corr-compose");

        Assert.Same(lifecycle, factory.Services.GetRequiredService<PluginLifecycleService>());
        Assert.Same(manager, factory.Services.GetRequiredService<PluginManager>());
        Assert.Equal(PluginId, Assert.Single(manager.List()).Id);

        // A legacy array store migrates non-destructively; the shared service sees the manager's record.
        var status = lifecycle.GetStatus(PluginId);
        Assert.NotNull(status);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, status.State);
        Assert.Equal(0, status.LifecycleVersion);
        Assert.Equal("\"plv-0\"", status.ETag);
        Assert.Null(lifecycle.GetStatus("acme.unknown"));

        var stale = await lifecycle.DisableAsync(context, PluginId, expectedLifecycleVersion: 5);
        Assert.Equal(PluginLifecycleResultCategory.StaleVersion, stale.Category);
        var noConfirmation = await lifecycle.EnableAsync(new PluginLifecycleRequestContext(NodeId.Local, context.Actor), PluginId, 0, confirmedVersion: null);
        Assert.Equal(PluginLifecycleResultCategory.ActivationConfirmationRequired, noConfirmation.Category);
        Assert.False(manager.IsActivated(PluginId));

        // The host-registered audit sink received the lifecycle events, with no local path in them.
        var auditPath = Path.Combine(factory.TempDirectory, "audit.jsonl");
        Assert.True(File.Exists(auditPath));
        var audit = await File.ReadAllTextAsync(auditPath);
        Assert.True(audit.Contains("pluginLifecycle", StringComparison.Ordinal), audit);
        Assert.Contains("StaleVersion", audit, StringComparison.Ordinal);
        Assert.Contains("ActivationConfirmationRequired", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.TempDirectory, audit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupReconciliation_RunsBeforePersistedEnabledPluginsActivate()
    {
        using var factory = new TestAppFactory();
        // Persisted enabled intent whose committed material is gone: reconciliation must run first and
        // register nothing, instead of the loader failing on it.
        Seed(factory, enabled: true, createInstallDirectory: false);
        using var client = factory.CreateClient();

        var lifecycle = factory.Services.GetRequiredService<PluginLifecycleService>();
        var manager = factory.Services.GetRequiredService<PluginManager>();

        var status = lifecycle.GetStatus(PluginId);
        Assert.NotNull(status);
        Assert.Equal(PluginLifecycleState.RecoveryRequired, status.State);
        Assert.Equal(1, status.LifecycleVersion);
        Assert.False(manager.IsActivated(PluginId));
        Assert.Empty(manager.StartupLoadErrors);
        Assert.False(manager.List().Single().Enabled);
    }

    [Fact]
    public void StartupActivationFailure_IsPersistedAsActivationFailed_ByTheCanonicalHostSequence()
    {
        using var factory = new TestAppFactory();
        // Persisted enabled intent over material that exists but can never activate (no valid signature).
        Seed(factory, enabled: true, createInstallDirectory: true);
        using var client = factory.CreateClient();

        var lifecycle = factory.Services.GetRequiredService<PluginLifecycleService>();
        var manager = factory.Services.GetRequiredService<PluginManager>();

        var status = lifecycle.GetStatus(PluginId);
        Assert.NotNull(status);
        Assert.Equal(PluginLifecycleState.ActivationFailed, status.State);
        Assert.Equal(1, status.LifecycleVersion);
        Assert.False(manager.IsActivated(PluginId));
        Assert.True(manager.List().Single().Enabled); // operator intent is kept; the lifecycle state carries the failure
        var error = Assert.Single(manager.StartupLoadErrors);
        Assert.Equal(PluginId, error.Key);
        Assert.DoesNotContain(factory.TempDirectory, error.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static void Seed(TestAppFactory factory, bool enabled, bool createInstallDirectory)
    {
        var installPath = Path.Combine(factory.TempDirectory, "plugins", PluginId);
        if (createInstallDirectory)
        {
            Directory.CreateDirectory(installPath);
            File.WriteAllText(Path.Combine(installPath, "Fake.dll"), string.Empty);
        }

        var manifest = new PluginManifest(
            SchemaVersion: PluginManifestValidator.SupportedSchemaVersion,
            Id: PluginId,
            Publisher: "Acme",
            Version: "1.0.0",
            MinHostAbstractionsVersion: "0.10.0",
            EntryAssembly: "Fake.dll",
            EntryType: "Acme.Fake",
            DeclaredCapabilities: [],
            Dependencies: [],
            MaxDeclaredRisk: RiskLevel.Read);
        var record = new PluginRecord(
            PluginId,
            installPath,
            manifest,
            enabled,
            DateTimeOffset.UtcNow,
            new PluginProvenance(SignaturePresent: true, Verified: true, "Acme", "test-key", "deadbeef", PackageTrustLevel.Verified, FailureReason: null));
        File.WriteAllText(Path.Combine(factory.TempDirectory, "plugins.json"), System.Text.Json.JsonSerializer.Serialize(new[] { record }));
    }
}

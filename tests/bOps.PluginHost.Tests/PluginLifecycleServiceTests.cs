// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.PluginHost.Tests;

/// <summary>
/// ADR-0037 lifecycle backend against the REAL sample plugin, real ZIP archives, real signatures and the
/// real registries. Interruption is emulated deterministically through the internal checkpoint seam — no
/// sleeps and no timing races.
/// </summary>
public sealed class PluginLifecycleServiceTests : IDisposable
{
    private const string Id = "acme.sample-plugin";
    private static readonly ActorIdentity Admin = new("api-user", "admin", null);

    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("bops-plugin-lifecycle-");
    private readonly RSA _key = RSA.Create(2048);
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));

    public PluginLifecycleServiceTests() => WriteTrust(PackageTrustLevel.Community);

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private string PluginsRoot => Path.Combine(_work.FullName, "plugins");
    private string StorePath => Path.Combine(_work.FullName, "plugins.json");
    private string TrustPath => Path.Combine(_work.FullName, "publisher-trust.json");

    // ---- First install -------------------------------------------------------------------------------

    [Fact]
    public async Task FirstInstall_CommitsDisabled_NeverEnables_AndLeavesActivationLkgEmpty()
    {
        var host = NewHost();

        var result = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, result.Category);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, result.State);
        Assert.Equal(1, result.LifecycleVersion);
        Assert.Equal("\"plv-1\"", result.ETag);
        var lifecycle = Lc();
        Assert.NotNull(lifecycle.CurrentGenerationId);
        Assert.Null(lifecycle.ActivationLkgGenerationId);
        Assert.Null(lifecycle.TransactionRollbackGenerationId);
        Assert.Null(lifecycle.CandidateGenerationId);
        Assert.Equal(PluginTransactionPhase.None, lifecycle.TransactionPhase);
        Assert.Null(new PluginStore(StorePath).GetJournal(Id));
        Assert.False(host.Manager.List().Single().Enabled);
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Null(host.Tools.Resolve("sample.echo"));
        Assert.Empty(host.Tools.GetAvailableManifests());
        Assert.Equal("1.0.0", FinalManifestVersion());
        Assert.Empty(Entries("staging"));
        Assert.Empty(Entries("uploads"));
    }

    // ---- A / B / C generation roles --------------------------------------------------------------------

    [Fact]
    public async Task Abc_FailedBToCInstall_RestoresBNotA_AndOnlyActivationAdvancesActivationLkg()
    {
        var host = NewHost();
        var a = await InstallActivateDisableA(host);
        var aId = Lc().CurrentGenerationId;
        Assert.Equal(aId, Lc().ActivationLkgGenerationId);

        // B: installed later, disabled, never activated.
        var b = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), a.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, b.Category);
        var afterB = Lc();
        var bId = afterB.CurrentGenerationId;
        Assert.NotEqual(aId, bId);
        Assert.Equal(aId, afterB.ActivationLkgGenerationId);
        Assert.False(afterB.Generations.Single(g => g.GenerationId == bId).WasActivationLkg);
        Assert.Equal("2.0.0", FinalManifestVersion());
        Assert.True(Directory.Exists(Path.Combine(PluginsRoot, ".lifecycle", "lkg", aId)));

        // B -> C fails (a real IO failure, not a crash) right before candidate promotion.
        var snapshots = new List<PluginLifecycleMetadata>();
        host.Service.Checkpoint = name =>
        {
            if (name == "BeforePromotion")
            {
                snapshots.Add(Lc());
                return Task.FromException(new IOException("disk unavailable"));
            }

            return Task.CompletedTask;
        };
        var failed = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("3.0.0")), b.LifecycleVersion);

        Assert.Equal(PluginLifecycleResultCategory.InternalFailure, failed.Category);
        var mid = Assert.Single(snapshots);
        Assert.Equal(bId, mid.CurrentGenerationId);
        Assert.Equal(aId, mid.ActivationLkgGenerationId);
        Assert.Equal(bId, mid.TransactionRollbackGenerationId);
        Assert.NotNull(mid.CandidateGenerationId);
        Assert.NotEqual(bId, mid.CandidateGenerationId);
        var restored = Lc();
        Assert.Equal(bId, restored.CurrentGenerationId);
        Assert.Equal(aId, restored.ActivationLkgGenerationId);
        Assert.Null(restored.TransactionRollbackGenerationId);
        Assert.Null(restored.CandidateGenerationId);
        Assert.Equal(b.LifecycleVersion, restored.LifecycleVersion);
        Assert.DoesNotContain(restored.Generations, g => g.GenerationId == mid.CandidateGenerationId);
        Assert.Equal("2.0.0", FinalManifestVersion());
        Assert.Empty(Entries("staging"));
        Assert.Equal([aId], Entries("lkg"));

        // B -> C succeeds: Current = C, ActivationLkg is STILL A, B is cleanup-eligible.
        host.Service.Checkpoint = null;
        var c = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("3.0.0")), b.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, c.Category);
        var afterC = Lc();
        var cId = afterC.CurrentGenerationId;
        Assert.NotEqual(bId, cId);
        Assert.NotEqual(aId, cId);
        Assert.Equal(aId, afterC.ActivationLkgGenerationId);
        Assert.Null(afterC.TransactionRollbackGenerationId);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, afterC.State);
        Assert.Equal(b.LifecycleVersion + 1, afterC.LifecycleVersion);
        Assert.Equal([aId], Entries("lkg"));
        Assert.DoesNotContain(afterC.Generations, g => g.GenerationId == bId);

        // Activation of C advances ActivationLkg to C; A starts its 30-day retention clock.
        var enabled = await host.Service.EnableAsync(Ctx(), Id, c.LifecycleVersion, "3.0.0");
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, enabled.Category);
        var afterEnable = Lc();
        Assert.Equal(cId, afterEnable.CurrentGenerationId);
        Assert.Equal(cId, afterEnable.ActivationLkgGenerationId);
        Assert.Equal(_clock.Now, afterEnable.Generations.Single(g => g.GenerationId == aId).RetiredAtUtc);
        Assert.Equal([aId], Entries("lkg"));

        // Retention: 29 days is not enough; 30 days permits cleanup.
        _clock.Now += TimeSpan.FromDays(29);
        await NewHost().Service.RecoverAllAsync();
        Assert.Equal([aId], Entries("lkg"));
        _clock.Now += TimeSpan.FromDays(1);
        await NewHost().Service.RecoverAllAsync();
        Assert.Empty(Entries("lkg"));
        Assert.DoesNotContain(Lc().Generations, g => g.GenerationId == aId);
        await host.Service.DisableAsync(Ctx(), Id, afterEnable.LifecycleVersion);
    }

    // ---- Fault-injection recovery matrix ---------------------------------------------------------------

    public static TheoryData<string> ReplacementFaultPoints => new()
    {
        "BeforeRollbackCapture", "AfterRollbackCapture", "BeforePromotion", "AfterPromotion", "BeforeMetadataCommit", "AfterMetadataCommit",
    };

    [Theory]
    [MemberData(nameof(ReplacementFaultPoints))]
    public async Task ReplacementInterruption_RecoversDeterministicallyAndIdempotently(string point)
    {
        var host = NewHost();
        var a = await InstallActivateDisableA(host);
        var aId = Lc().CurrentGenerationId;
        var b = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), a.LifecycleVersion);
        var bId = Lc().CurrentGenerationId;
        host.Service.Checkpoint = name => name == point ? Task.FromException(new PluginLifecycleInterruptedException()) : Task.CompletedTask;

        await Assert.ThrowsAsync<PluginLifecycleInterruptedException>(
            () => host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("3.0.0")), b.LifecycleVersion));

        var journal = new PluginStore(StorePath).GetJournal(Id);
        Assert.NotNull(journal);
        var cId = journal.CandidateGenerationId;
        Assert.Equal(bId, journal.RollbackGenerationId);
        Assert.Equal(aId, journal.ActivationLkgGenerationId);
        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();

        var after = Lc();
        Assert.Null(after.TransactionRollbackGenerationId);
        Assert.Null(after.CandidateGenerationId);
        Assert.Equal(PluginTransactionPhase.None, after.TransactionPhase);
        Assert.Null(new PluginStore(StorePath).GetJournal(Id));
        Assert.Equal(aId, after.ActivationLkgGenerationId);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, after.State);
        Assert.Empty(Entries("staging"));
        Assert.Equal([aId], Entries("lkg"));
        if (point == "AfterMetadataCommit")
        {
            Assert.Equal(cId, after.CurrentGenerationId);
            Assert.Equal(b.LifecycleVersion + 1, after.LifecycleVersion);
            Assert.Equal("3.0.0", FinalManifestVersion());
        }
        else
        {
            Assert.Equal(bId, after.CurrentGenerationId);
            Assert.Equal(b.LifecycleVersion, after.LifecycleVersion);
            Assert.DoesNotContain(after.Generations, g => g.GenerationId == cId);
            Assert.Equal("2.0.0", FinalManifestVersion());
        }

        // Recovery never registers or activates anything, and repeating it changes nothing.
        Assert.False(rebooted.Manager.IsActivated(Id));
        Assert.Null(rebooted.Tools.Resolve("sample.echo"));
        var settled = await File.ReadAllTextAsync(StorePath);
        await NewHost().Service.RecoverAllAsync();
        Assert.Equal(settled, await File.ReadAllTextAsync(StorePath));
    }

    [Theory]
    [InlineData("BeforePromotion")]
    [InlineData("AfterPromotion")]
    [InlineData("BeforeMetadataCommit")]
    [InlineData("AfterMetadataCommit")]
    public async Task FirstInstallInterruption_NeverLeavesAnAuthoritativeCandidateUnlessMetadataCommitted(string point)
    {
        var host = NewHost();
        host.Service.Checkpoint = name => name == point ? Task.FromException(new PluginLifecycleInterruptedException()) : Task.CompletedTask;
        await Assert.ThrowsAsync<PluginLifecycleInterruptedException>(() => host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0"))));

        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();

        Assert.Null(new PluginStore(StorePath).GetJournal(Id));
        Assert.Empty(Entries("staging"));
        if (point == "AfterMetadataCommit")
        {
            var lifecycle = Lc();
            Assert.Equal(1, lifecycle.LifecycleVersion);
            Assert.Equal(PluginLifecycleState.InstalledDisabled, lifecycle.State);
            Assert.Null(lifecycle.ActivationLkgGenerationId);
            Assert.Null(lifecycle.CandidateGenerationId);
            Assert.True(Directory.Exists(Path.Combine(PluginsRoot, Id)));
        }
        else
        {
            Assert.Empty(rebooted.Manager.List());
            Assert.False(Directory.Exists(Path.Combine(PluginsRoot, Id)));
        }

        Assert.Null(rebooted.Tools.Resolve("sample.echo"));
    }

    [Fact]
    public async Task RestoredCurrentAfterInterruption_ActivatesOnlyOnFreshExplicitEnable()
    {
        var host = NewHost();
        var first = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        host.Service.Checkpoint = name => name == "BeforeMetadataCommit" ? Task.FromException(new PluginLifecycleInterruptedException()) : Task.CompletedTask;
        await Assert.ThrowsAsync<PluginLifecycleInterruptedException>(
            () => host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), first.LifecycleVersion));
        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();
        Assert.Empty(rebooted.Manager.LoadAllEnabled());
        Assert.Null(rebooted.Tools.Resolve("sample.echo"));

        var stale = await rebooted.Service.EnableAsync(Ctx(), Id, first.LifecycleVersion + 5, "1.0.0");
        var enabled = await rebooted.Service.EnableAsync(Ctx(), Id, first.LifecycleVersion, "1.0.0");

        Assert.Equal(PluginLifecycleResultCategory.StaleVersion, stale.Category);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, enabled.Category);
        Assert.NotNull(rebooted.Tools.Resolve("sample.echo"));
        await rebooted.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
    }

    [Fact]
    public async Task InterruptionBetweenActivationAndPersistence_LeavesInstalledDisabled_AndNeverAutoActivates()
    {
        var host = NewHost();
        var installed = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        host.Service.Checkpoint = name => name == "AfterActivation" ? Task.FromException(new PluginLifecycleInterruptedException()) : Task.CompletedTask;
        await Assert.ThrowsAsync<PluginLifecycleInterruptedException>(() => host.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, "1.0.0"));
        Assert.True(host.Manager.IsActivated(Id));

        // "Restart": a new process has no registrations and the store never recorded Enabled.
        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();
        Assert.Empty(rebooted.Manager.LoadAllEnabled());

        var lifecycle = Lc();
        Assert.Equal(PluginLifecycleState.InstalledDisabled, lifecycle.State);
        Assert.Equal(installed.LifecycleVersion, lifecycle.LifecycleVersion);
        Assert.Null(lifecycle.ActivationLkgGenerationId);
        Assert.False(new PluginStore(StorePath).Find(Id)!.Enabled);
        Assert.False(rebooted.Manager.IsActivated(Id));
        Assert.Null(rebooted.Tools.Resolve("sample.echo"));
        var enabled = await rebooted.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, "1.0.0");
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, enabled.Category);
        await rebooted.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
        await host.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
    }

    [Fact]
    public async Task Recovery_IsSerializedWithAMutationOfTheSamePlugin_AndNeverSweepsItsInFlightStaging()
    {
        var host = NewHost();
        var release = NewSignal();
        var reached = NewSignal();
        host.Service.Checkpoint = async name =>
        {
            if (name == "BeforePromotion")
            {
                reached.SetResult();
                await release.Task;
            }
        };
        var install = host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        await reached.Task;
        var stagingBefore = Entries("staging");
        Assert.Single(stagingBefore);

        var recovery = host.Service.RecoverAllAsync();
        Assert.True(SpinWait.SpinUntil(() => host.Service.LockReferenceCount(Id) == 2, TimeSpan.FromSeconds(30)), "recovery did not queue on the plugin lock");
        Assert.False(recovery.IsCompleted);
        Assert.Equal(stagingBefore, Entries("staging"));
        Assert.NotNull(new PluginStore(StorePath).GetJournal(Id));
        release.SetResult();

        var result = await install;
        await recovery;

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, result.Category);
        Assert.Equal(1, Lc().LifecycleVersion);
        Assert.Null(new PluginStore(StorePath).GetJournal(Id));
        Assert.Equal("1.0.0", FinalManifestVersion());
    }

    [Fact]
    public async Task Recovery_DeletesOrphanUploadsAndStaging_ButKeepsReferencedRetainedMaterial()
    {
        var host = NewHost();
        var a = await InstallActivateDisableA(host);
        await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), a.LifecycleVersion);
        var lifecycleRoot = Path.Combine(PluginsRoot, ".lifecycle");
        Directory.CreateDirectory(Path.Combine(lifecycleRoot, "uploads"));
        await File.WriteAllTextAsync(Path.Combine(lifecycleRoot, "uploads", "orphan.zip"), "not loaded");
        Directory.CreateDirectory(Path.Combine(lifecycleRoot, "staging", "orphan"));
        await File.WriteAllTextAsync(Path.Combine(lifecycleRoot, "staging", "orphan", "Acme.SamplePlugin.dll"), "never loaded");
        Directory.CreateDirectory(Path.Combine(lifecycleRoot, "lkg", "orphan"));
        var legacy = Directory.CreateDirectory(Path.Combine(PluginsRoot, ".staging-abc"));
        var referenced = Lc().ActivationLkgGenerationId!;

        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();

        Assert.Empty(Entries("uploads"));
        Assert.Empty(Entries("staging"));
        Assert.Equal([referenced], Entries("lkg"));
        Assert.False(Directory.Exists(legacy.FullName));
        Assert.Null(rebooted.Tools.Resolve("sample.echo"));
    }

    [Fact]
    public async Task Recovery_WhenCommittedMaterialIsMissing_MarksRecoveryRequiredOnce_AndAReplacementCanRepairIt()
    {
        var host = NewHost();
        var first = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        Directory.Delete(Path.Combine(PluginsRoot, Id), recursive: true);

        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();
        var marked = Lc();
        await NewHost().Service.RecoverAllAsync();

        Assert.Equal(PluginLifecycleState.RecoveryRequired, marked.State);
        Assert.Equal(first.LifecycleVersion + 1, marked.LifecycleVersion);
        Assert.Equal(marked.LifecycleVersion, Lc().LifecycleVersion);
        Assert.False(new PluginStore(StorePath).Find(Id)!.Enabled);
        var blocked = await rebooted.Service.EnableAsync(Ctx(), Id, marked.LifecycleVersion, "1.0.0");
        Assert.Equal(PluginLifecycleResultCategory.StateConflict, blocked.Category);
        Assert.Null(rebooted.Tools.Resolve("sample.echo"));

        var repaired = await rebooted.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), marked.LifecycleVersion);

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, repaired.Category);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, repaired.State);
        Assert.Equal("2.0.0", FinalManifestVersion());
    }

    // ---- Validation and replacement eligibility --------------------------------------------------------

    [Fact]
    public async Task InvalidCandidates_AreRejectedWithNeutralCategories_AndMutateNothing()
    {
        var host = NewHost();
        using var otherKey = RSA.Create(2048);
        var cases = new (string Name, byte[] Bytes, PluginLifecycleResultCategory Expected)[]
        {
            ("not a zip", "definitely not a zip"u8.ToArray(), PluginLifecycleResultCategory.ArchiveInvalid),
            ("no manifest", ZipOf(("readme.txt", "x"u8.ToArray())), PluginLifecycleResultCategory.ManifestInvalid),
            ("unsigned", ArchiveBytes("1.0.0", sign: false), PluginLifecycleResultCategory.SignatureInvalid),
            ("unknown key", ArchiveBytes("1.0.0", key: otherKey, keyId: "rogue-key"), PluginLifecycleResultCategory.PublisherUntrusted),
            ("tampered", ArchiveBytes("1.0.0", afterSign: dir => File.AppendAllText(Path.Combine(dir, "bops-plugin.json"), " ")), PluginLifecycleResultCategory.SignatureInvalid),
            ("missing entry type", ArchiveBytes("1.0.0", mutateManifest: m => m["EntryType"] = "Acme.SamplePlugin.Missing"), PluginLifecycleResultCategory.CompatibilityRejected),
            ("not a managed assembly", ArchiveBytes("1.0.0", mutate: dir => File.WriteAllText(Path.Combine(dir, "Acme.SamplePlugin.dll"), "MZ not managed")), PluginLifecycleResultCategory.CompatibilityRejected),
        };

        foreach (var (name, bytes, expected) in cases)
        {
            var result = await host.Service.InstallArchiveAsync(Ctx(), Zip(bytes));
            Assert.True(expected == result.Category, $"{name}: expected {expected} but was {result.Category}");
            Assert.Empty(host.Manager.List());
            Assert.False(Directory.Exists(Path.Combine(PluginsRoot, Id)), name);
            Assert.Empty(Entries("staging"));
            Assert.Empty(Entries("uploads"));
        }

        WriteTrust(PackageTrustLevel.Unverified);
        var revoked = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        Assert.Equal(PluginLifecycleResultCategory.PublisherUntrusted, revoked.Category);
        Assert.Empty(host.Manager.List());
        WriteTrust(PackageTrustLevel.Community);
        var mismatch = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")), routePluginId: "acme.other");
        Assert.Equal(PluginLifecycleResultCategory.IdentityConflict, mismatch.Category);
        Assert.Empty(host.Manager.List());
        Assert.Null(host.Tools.Resolve("sample.echo"));
    }

    [Fact]
    public async Task Replacement_RejectsEnabledTargets_SameVersion_AndPublisherChange()
    {
        var host = NewHost();
        var first = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        var enabled = await host.Service.EnableAsync(Ctx(), Id, first.LifecycleVersion, "1.0.0");

        var whileEnabled = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), enabled.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.StateConflict, whileEnabled.Category);
        Assert.Equal("1.0.0", FinalManifestVersion());
        var disabled = await host.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);

        var sameVersion = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0", extra: "different bytes")), disabled.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.VersionConflict, sameVersion.Category);

        using var rotated = RSA.Create(2048);
        await File.WriteAllTextAsync(TrustPath, JsonSerializer.Serialize(new[]
        {
            new PluginPublisherTrust("Acme", "test-key", _key.ExportSubjectPublicKeyInfoPem(), PackageTrustLevel.Community),
            new PluginPublisherTrust("Other", "other-key", rotated.ExportSubjectPublicKeyInfoPem(), PackageTrustLevel.Community),
        }));
        var otherPublisher = await host.Service.InstallArchiveAsync(
            Ctx(), Zip(ArchiveBytes("2.0.0", publisher: "Other", key: rotated, keyId: "other-key")), disabled.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.IdentityConflict, otherPublisher.Category);
        Assert.Equal("1.0.0", FinalManifestVersion());
        Assert.Equal(disabled.LifecycleVersion, Lc().LifecycleVersion);
    }

    // ---- ETag / revision -------------------------------------------------------------------------------

    [Fact]
    public async Task Revision_ChangesOnlyOnAuthoritativeMutation_AndStalePreconditionsMutateAndExecuteNothing()
    {
        var host = NewHost();
        var created = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        Assert.Equal(1, created.LifecycleVersion);

        var staleCreate = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), expectedLifecycleVersion: 7);
        var missingPrecondition = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), expectedLifecycleVersion: null);
        var staleEnable = await host.Service.EnableAsync(Ctx(), Id, created.LifecycleVersion + 3, "1.0.0");
        var missingEnable = await host.Service.EnableAsync(Ctx(), Id, null, "1.0.0");
        var staleDisable = await host.Service.DisableAsync(Ctx(), Id, 0);

        Assert.All(new[] { staleCreate, missingPrecondition, staleEnable, missingEnable, staleDisable },
            r => Assert.Equal(PluginLifecycleResultCategory.StaleVersion, r.Category));
        Assert.Equal(1, Lc().LifecycleVersion);
        Assert.Equal("1.0.0", FinalManifestVersion());
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Null(host.Tools.Resolve("sample.echo"));
        Assert.Empty(Entries("staging"));

        var noPlugin = await host.Service.InstallArchiveAsync(NewContextFor("second"), Zip(ArchiveBytes("1.0.0", id: "acme.second", extra: "b")), expectedLifecycleVersion: 4);
        Assert.Equal(PluginLifecycleResultCategory.StaleVersion, noPlugin.Category);
        Assert.Single(host.Manager.List());

        var enabled = await host.Service.EnableAsync(Ctx(), Id, 1, "1.0.0");
        var again = await host.Service.EnableAsync(Ctx(), Id, 2, "1.0.0");
        var disabled = await host.Service.DisableAsync(Ctx(), Id, 2);
        var disabledAgain = await host.Service.DisableAsync(Ctx(), Id, 3);
        Assert.Equal([2L, 2L, 3L, 3L], new[] { enabled.LifecycleVersion, again.LifecycleVersion, disabled.LifecycleVersion, disabledAgain.LifecycleVersion });
        Assert.Equal(PluginLifecycleETag.Format(3), disabledAgain.ETag);
        Assert.True(PluginLifecycleETag.TryParse(disabledAgain.ETag, out var parsed));
        Assert.Equal(3, parsed);
        Assert.False(PluginLifecycleETag.TryParse("3", out _));
    }

    // ---- Concurrency -----------------------------------------------------------------------------------

    [Fact]
    public async Task SamePlugin_MutationsAreSerialized_AndTheLockTableDrains()
    {
        var host = NewHost();
        var release = NewSignal();
        var reached = NewSignal();
        int active = 0, max = 0, calls = 0;
        host.Service.Checkpoint = async name =>
        {
            if (name != "BeforePromotion")
            {
                return;
            }

            var now = Interlocked.Increment(ref active);
            lock (release)
            {
                max = Math.Max(max, now);
            }

            if (Interlocked.Increment(ref calls) == 1)
            {
                reached.SetResult();
                await release.Task;
            }

            Interlocked.Decrement(ref active);
        };

        var first = host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        await reached.Task;
        var second = host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")));
        Assert.True(SpinWait.SpinUntil(() => host.Service.LockReferenceCount(Id) == 2, TimeSpan.FromSeconds(30)), "the second request never queued on the plugin lock");
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, firstResult.Category);
        Assert.Equal(PluginLifecycleResultCategory.StaleVersion, secondResult.Category);
        Assert.Equal(1, max);
        Assert.Equal("1.0.0", FinalManifestVersion());
        Assert.Equal(0, host.Service.ActiveLockKeyCount);
    }

    [Fact]
    public async Task DifferentPlugins_AreNotSerializedAgainstEachOther()
    {
        var host = NewHost();
        var release = NewSignal();
        var reached = NewSignal();
        var calls = 0;
        host.Service.Checkpoint = async name =>
        {
            if (name == "BeforePromotion" && Interlocked.Increment(ref calls) == 1)
            {
                reached.SetResult();
                await release.Task;
            }
        };

        var slow = host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        await reached.Task;
        var other = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0", id: "acme.other-plugin")));

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, other.Category);
        Assert.False(slow.IsCompleted);
        Assert.Equal(1, host.Service.LockReferenceCount(Id));
        release.SetResult();
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, (await slow).Category);
        Assert.Equal(2, host.Manager.List().Count);
        Assert.Equal(0, host.Service.ActiveLockKeyCount);
    }

    // ---- Idempotency -----------------------------------------------------------------------------------

    [Fact]
    public async Task SameKeySameIntent_ReplaysOneLogicalOperation_EvenAcrossRestart()
    {
        var host = NewHost();
        var commits = 0;
        host.Service.Checkpoint = name =>
        {
            if (name == "AfterMetadataCommit") Interlocked.Increment(ref commits);
            return Task.CompletedTask;
        };
        var bytes = ArchiveBytes("1.0.0");

        var original = await host.Service.InstallArchiveAsync(Ctx("key-1"), Zip(bytes));
        var replay = await host.Service.InstallArchiveAsync(Ctx("key-1"), Zip(bytes));
        var afterRestart = await NewHost().Service.InstallArchiveAsync(Ctx("key-1"), Zip(bytes));

        Assert.False(original.Replayed);
        Assert.True(replay.Replayed);
        Assert.True(afterRestart.Replayed);
        Assert.Equal(1, commits);
        Assert.All(new[] { replay, afterRestart }, r =>
        {
            Assert.Equal(original.Category, r.Category);
            Assert.Equal(original.LifecycleVersion, r.LifecycleVersion);
            Assert.Equal(original.PluginVersion, r.PluginVersion);
        });
        Assert.Equal(1, Lc().LifecycleVersion);
        Assert.Single(new PluginStore(StorePath).Read().Operations);
        Assert.Equal(0, host.Service.InFlightReservationCount);
    }

    [Fact]
    public async Task SameKey_WithDifferentOperationArchiveOrPreconditionOrPlugin_Conflicts_WithoutASecondMutation()
    {
        var host = NewHost();
        var bytes = ArchiveBytes("1.0.0");
        var created = await host.Service.InstallArchiveAsync(Ctx("shared"), Zip(bytes));

        var differentOperation = await host.Service.EnableAsync(Ctx("shared"), Id, created.LifecycleVersion, "1.0.0");
        var differentArchive = await host.Service.InstallArchiveAsync(Ctx("shared"), Zip(ArchiveBytes("1.0.0", extra: "different bytes")));
        var differentPrecondition = await host.Service.InstallArchiveAsync(Ctx("shared"), Zip(bytes), expectedLifecycleVersion: 1);
        var differentRoute = await host.Service.InstallArchiveAsync(Ctx("shared"), Zip(bytes), routePluginId: Id);

        Assert.All(new[] { differentOperation, differentArchive, differentPrecondition, differentRoute },
            r => Assert.Equal(PluginLifecycleResultCategory.IdempotencyConflict, r.Category));
        Assert.Equal(PluginLifecycleState.InstalledDisabled, Lc().State);
        Assert.Equal(1, Lc().LifecycleVersion);
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Equal("1.0.0", FinalManifestVersion());

        var enabled = await host.Service.EnableAsync(Ctx("enable-key"), Id, created.LifecycleVersion, "1.0.0");
        var differentPlugin = await host.Service.EnableAsync(Ctx("enable-key"), "acme.other", enabled.LifecycleVersion, "1.0.0");
        var differentConfirmation = await host.Service.EnableAsync(Ctx("enable-key"), Id, created.LifecycleVersion, "9.9.9");
        Assert.Equal(PluginLifecycleResultCategory.IdempotencyConflict, differentPlugin.Category);
        Assert.Equal(PluginLifecycleResultCategory.IdempotencyConflict, differentConfirmation.Category);
        Assert.Equal(2, Lc().LifecycleVersion);
        await host.Service.DisableAsync(Ctx(), Id, 2);
    }

    [Fact]
    public async Task IdempotencyScope_IsNodeActorKey_NotPluginOrOperation()
    {
        var host = NewHost();
        var first = await host.Service.InstallArchiveAsync(Ctx("k"), Zip(ArchiveBytes("1.0.0")));
        var otherActor = await host.Service.InstallArchiveAsync(
            Ctx("k", new ActorIdentity("api-user", "someone-else", null)), Zip(ArchiveBytes("1.0.0", id: "acme.second")));
        var otherNode = await host.Service.InstallArchiveAsync(
            Ctx("k", node: "node-b"), Zip(ArchiveBytes("1.0.0", id: "acme.third")));

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, first.Category);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, otherActor.Category);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, otherNode.Category);
        Assert.False(otherActor.Replayed || otherNode.Replayed);
        Assert.Equal(3, host.Manager.List().Count);
        Assert.Equal(3, new PluginStore(StorePath).Read().Operations.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Service.InstallArchiveAsync(Ctx(new string('k', 129)), Zip(ArchiveBytes("1.0.0"))));
    }

    [Theory]
    [InlineData("Reserved")]
    [InlineData("DigestBound")]
    public async Task PreIdentityDuplicate_JoinsTheOwner_AndNeverStartsASecondMutation(string pausePoint)
    {
        var host = NewHost();
        var release = NewSignal();
        var reached = NewSignal();
        int commits = 0, intakes = 0;
        host.Service.Checkpoint = async name =>
        {
            if (name == "DigestBound") Interlocked.Increment(ref intakes);
            if (name == "AfterMetadataCommit") Interlocked.Increment(ref commits);
            if (name == pausePoint)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };
        var bytes = ArchiveBytes("1.0.0");

        var owner = host.Service.InstallArchiveAsync(Ctx("dup"), Zip(bytes));
        await reached.Task;
        Assert.Equal(1, host.Service.InFlightReservationCount);
        var duplicate = host.Service.InstallArchiveAsync(Ctx("dup"), Zip(bytes));
        Assert.False(duplicate.IsCompleted);
        release.SetResult();

        var ownerResult = await owner;
        var duplicateResult = await duplicate;

        Assert.False(ownerResult.Replayed);
        Assert.True(duplicateResult.Replayed);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, duplicateResult.Category);
        Assert.Equal(ownerResult.LifecycleVersion, duplicateResult.LifecycleVersion);
        Assert.Equal(1, intakes);
        Assert.Equal(1, commits);
        Assert.Equal(1, Lc().LifecycleVersion);
        Assert.Single(new PluginStore(StorePath).Read().Operations);
        Assert.Equal(0, host.Service.InFlightReservationCount);
    }

    [Theory]
    [InlineData("Reserved")]
    [InlineData("DigestBound")]
    public async Task PreIdentityDuplicate_WithADifferentEventualIntent_ConflictsWithoutMutating(string pausePoint)
    {
        var host = NewHost();
        var release = NewSignal();
        var reached = NewSignal();
        int commits = 0, intakes = 0;
        host.Service.Checkpoint = async name =>
        {
            if (name == "DigestBound") Interlocked.Increment(ref intakes);
            if (name == "AfterMetadataCommit") Interlocked.Increment(ref commits);
            if (name == pausePoint)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };

        var owner = host.Service.InstallArchiveAsync(Ctx("dup"), Zip(ArchiveBytes("1.0.0")));
        await reached.Task;
        var different = host.Service.InstallArchiveAsync(Ctx("dup"), Zip(ArchiveBytes("2.0.0")));
        if (pausePoint == "Reserved")
        {
            // The owner's digest is unknown: the follower waits, it neither conflicts early nor mutates.
            Assert.False(different.IsCompleted);
        }

        release.SetResult();
        var differentResult = await different;
        var ownerResult = await owner;

        Assert.Equal(PluginLifecycleResultCategory.IdempotencyConflict, differentResult.Category);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, ownerResult.Category);
        Assert.Equal(1, intakes);
        Assert.Equal(1, commits);
        Assert.Equal("1.0.0", FinalManifestVersion());
        Assert.Equal(1, Lc().LifecycleVersion);
    }

    [Fact]
    public async Task InterruptedIdempotencyReservation_IsDroppedAtStartup_SoTheActorMayRetry()
    {
        var host = NewHost();
        host.Service.Checkpoint = name => name == "BeforePromotion" ? Task.FromException(new PluginLifecycleInterruptedException()) : Task.CompletedTask;
        var bytes = ArchiveBytes("1.0.0");
        await Assert.ThrowsAsync<PluginLifecycleInterruptedException>(() => host.Service.InstallArchiveAsync(Ctx("retry"), Zip(bytes)));
        Assert.Single(new PluginStore(StorePath).Read().Operations);

        var rebooted = NewHost();
        await rebooted.Service.RecoverAllAsync();
        var retried = await rebooted.Service.InstallArchiveAsync(Ctx("retry"), Zip(bytes));

        Assert.Equal(PluginLifecycleResultCategory.Succeeded, retried.Category);
        Assert.False(retried.Replayed);
        Assert.Equal(1, retried.LifecycleVersion);
    }

    // ---- Activation, failure and disable ---------------------------------------------------------------

    [Fact]
    public async Task Enable_RequiresExplicitConfirmationOfTheNamedVersion_BeforeAnyPluginCodeLoads()
    {
        var host = NewHost();
        var installed = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));

        var none = await host.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, null);
        var wrong = await host.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, "9.9.9");

        Assert.Equal(PluginLifecycleResultCategory.ActivationConfirmationRequired, none.Category);
        Assert.Equal(PluginLifecycleResultCategory.ActivationConfirmationRequired, wrong.Category);
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Empty(host.Tools.GetAvailableManifests());
        Assert.Equal(installed.LifecycleVersion, Lc().LifecycleVersion);

        var enabled = await host.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, "1.0.0");
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, enabled.Category);
        Assert.Equal(PluginLifecycleState.Enabled, enabled.State);
        Assert.Equal(installed.LifecycleVersion + 1, enabled.LifecycleVersion);
        Assert.True(host.Manager.IsActivated(Id));
        Assert.Equal(1, host.Tools.GetAvailableManifests().Count(m => m.Name == "sample.echo"));
        var lifecycle = Lc();
        Assert.Equal(lifecycle.CurrentGenerationId, lifecycle.ActivationLkgGenerationId);
        Assert.True(new PluginStore(StorePath).Find(Id)!.Enabled);

        // Enabling the already-enabled generation is an idempotent no-op: no reload, no revision change.
        var repeat = await host.Service.EnableAsync(Ctx(), Id, enabled.LifecycleVersion, "1.0.0");
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, repeat.Category);
        Assert.Equal(enabled.LifecycleVersion, repeat.LifecycleVersion);
        Assert.Equal(1, host.Tools.GetAvailableManifests().Count(m => m.Name == "sample.echo"));
        await host.Service.DisableAsync(Ctx(), Id, repeat.LifecycleVersion);
    }

    [Fact]
    public async Task ReplacementInstall_NeverAutoExecutesTheNewGeneration()
    {
        var host = NewHost();
        var first = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        var enabled = await host.Service.EnableAsync(Ctx(), Id, first.LifecycleVersion, "1.0.0");
        var disabled = await host.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
        var lkgBefore = Lc().ActivationLkgGenerationId;

        var replaced = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), disabled.LifecycleVersion);

        Assert.Equal(PluginLifecycleState.InstalledDisabled, replaced.State);
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Null(host.Tools.Resolve("sample.echo"));
        Assert.Equal(lkgBefore, Lc().ActivationLkgGenerationId);
        Assert.NotEqual(lkgBefore, Lc().CurrentGenerationId);
        Assert.False(new PluginStore(StorePath).Find(Id)!.Enabled);
    }

    [Fact]
    public async Task ActivationFailure_AfterPartialRegistration_RemovesEveryRegistration_KeepsLkg_AndLeaksNothing()
    {
        var host = NewHost();
        var a = await InstallActivateDisableA(host);
        var aId = Lc().CurrentGenerationId;
        var b = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), a.LifecycleVersion);
        // The third tool the plugin registers collides with a tool owned by another package: the first two
        // registrations have already happened when activation fails.
        host.Tools.Register(new PackageId("other.package"), new ConflictingTool());

        var failed = await host.Service.EnableAsync(Ctx(), Id, b.LifecycleVersion, "2.0.0");

        Assert.Equal(PluginLifecycleResultCategory.ActivationFailed, failed.Category);
        Assert.Equal(PluginLifecycleState.ActivationFailed, failed.State);
        Assert.Equal(b.LifecycleVersion + 1, failed.LifecycleVersion);
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Null(host.Tools.Resolve("sample.echo"));
        Assert.Null(host.Tools.Resolve("sample.marker.status"));
        Assert.NotNull(host.Tools.Resolve("sample.marker.create"));
        Assert.Equal(["sample.marker.create"], host.Tools.GetAvailableManifests().Select(m => m.Name).ToArray());
        Assert.Null(host.Skills.Resolve("sample.echo-marker-skill", "sample.echo-marker"));
        var lifecycle = Lc();
        Assert.Equal(aId, lifecycle.ActivationLkgGenerationId);
        Assert.NotEqual(aId, lifecycle.CurrentGenerationId);
        Assert.False(new PluginStore(StorePath).Find(Id)!.Enabled);
        Assert.NotNull(lifecycle.SanitizedFailure);
        Assert.DoesNotContain(_work.FullName, lifecycle.SanitizedFailure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", lifecycle.SanitizedFailure, StringComparison.Ordinal);
        Assert.True(lifecycle.SanitizedFailure.Length <= 240);

        // ActivationFailed is not enable-able without an explicit repair, and never auto re-enables the LKG.
        var retry = await host.Service.EnableAsync(Ctx(), Id, failed.LifecycleVersion, "2.0.0");
        Assert.Equal(PluginLifecycleResultCategory.StateConflict, retry.Category);
        Assert.Null(host.Tools.Resolve("sample.echo"));
    }

    [Fact]
    public async Task Disable_UnregistersOnce_PreservesTheInstalledGeneration_AndIsIdempotent()
    {
        var host = NewHost();
        var first = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        var enabled = await host.Service.EnableAsync(Ctx(), Id, first.LifecycleVersion, "1.0.0");
        var generation = Lc().CurrentGenerationId;
        Assert.NotNull(host.Tools.Resolve("sample.echo"));

        var disabled = await host.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
        var again = await host.Service.DisableAsync(Ctx(), Id, disabled.LifecycleVersion);

        Assert.Equal(PluginLifecycleState.InstalledDisabled, disabled.State);
        Assert.Equal(enabled.LifecycleVersion + 1, disabled.LifecycleVersion);
        Assert.Equal(disabled.LifecycleVersion, again.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, again.Category);
        Assert.Null(host.Tools.Resolve("sample.echo"));
        Assert.Null(host.Skills.Resolve("sample.echo-marker-skill", "sample.echo-marker"));
        Assert.False(host.Manager.IsActivated(Id));
        Assert.Equal(generation, Lc().CurrentGenerationId);
        Assert.Equal(generation, Lc().ActivationLkgGenerationId);
        Assert.True(File.Exists(Path.Combine(PluginsRoot, Id, "Acme.SamplePlugin.dll")));
        var unknown = await host.Service.DisableAsync(Ctx(), "acme.unknown", 0);
        Assert.Equal(PluginLifecycleResultCategory.NotFound, unknown.Category);
    }

    // ---- Audit -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_RecordsLifecycleOutcomes_WithoutPathsPayloadsOrStacks()
    {
        var audit = new RecordingAudit();
        var host = NewHost(audit);
        var bytes = ArchiveBytes("1.0.0", extra: "SECRET-ARCHIVE-PAYLOAD-MARKER");
        var installed = await host.Service.InstallArchiveAsync(Ctx("audit-1"), Zip(bytes));
        await host.Service.InstallArchiveAsync(Ctx("audit-1"), Zip(bytes));
        await host.Service.EnableAsync(Ctx("audit-1"), Id, 1, "1.0.0");
        await host.Service.EnableAsync(Ctx(), Id, 99, "1.0.0");
        var enabled = await host.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, "1.0.0");
        var disabled = await host.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
        await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0", sign: false, id: "acme.unsigned")));
        host.Tools.Register(new PackageId("other.package"), new ConflictingTool());
        var second = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), disabled.LifecycleVersion);
        await host.Service.EnableAsync(Ctx(), Id, second.LifecycleVersion, "2.0.0");

        var events = audit.Events.OfType<PluginLifecycleAuditEvent>().ToList();

        Assert.Contains(events, e => e is { Operation: "install", Stage: "received", Outcome: "Attempted" });
        Assert.Contains(events, e => e is { Operation: "install", Stage: "trust", Outcome: "Succeeded", PublisherTrust: "Community", PluginId: Id, PluginVersion: "1.0.0" });
        Assert.Contains(events, e => e is { Operation: "install", Stage: "commit", Outcome: "Succeeded", PriorState: null, NewState: "InstalledDisabled", LifecycleVersion: 1, IdempotencyKeyPresent: true, IdempotentReplay: false });
        Assert.Contains(events, e => e is { Operation: "install", Stage: "idempotency", Outcome: "Succeeded", IdempotentReplay: true });
        Assert.Contains(events, e => e is { Operation: "enable", Stage: "idempotency", Outcome: "IdempotencyConflict" });
        Assert.Contains(events, e => e is { Operation: "enable", Stage: "precondition", Outcome: "StaleVersion" });
        Assert.Contains(events, e => e is { Operation: "enable", Stage: "activation", Outcome: "Succeeded", NewState: "Enabled", PriorState: "InstalledDisabled" });
        Assert.Contains(events, e => e is { Operation: "disable", Stage: "deactivation", Outcome: "Succeeded", NewState: "InstalledDisabled", PriorState: "Enabled" });
        Assert.Contains(events, e => e is { Operation: "install", Stage: "signature", Outcome: "SignatureInvalid", PluginId: "acme.unsigned" });
        Assert.Contains(events, e => e is { Operation: "enable", Stage: "activation", Outcome: "ActivationFailed", NewState: "ActivationFailed" });
        Assert.All(events, e => Assert.Equal(-1, e.StepIndex));
        Assert.All(events, e => Assert.Equal(Guid.Empty, e.TaskId));

        var serialized = string.Join('\n', audit.Events.Select(e => JsonSerializer.Serialize(e)));
        Assert.DoesNotContain(_work.FullName, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-ARCHIVE-PAYLOAD-MARKER", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("audit-1", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_RecordsCrashRecoveryWithTheReconciledOutcome()
    {
        var audit = new RecordingAudit();
        var host = NewHost(audit);
        var first = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        host.Service.Checkpoint = name => name == "AfterPromotion" ? Task.FromException(new PluginLifecycleInterruptedException()) : Task.CompletedTask;
        await Assert.ThrowsAsync<PluginLifecycleInterruptedException>(
            () => host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("2.0.0")), first.LifecycleVersion));

        await NewHost(audit).Service.RecoverAllAsync();

        var recovery = Assert.Single(audit.Events.OfType<PluginLifecycleAuditEvent>(), e => e.Operation == "recover");
        Assert.Equal("RollbackRestored", recovery.Outcome);
        Assert.Equal(Id, recovery.PluginId);
        Assert.Equal("InstalledDisabled", recovery.NewState);
        Assert.Equal(ActorIdentity.RuntimeSystem, recovery.Actor);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private async Task<PluginLifecycleResult> InstallActivateDisableA(Host host)
    {
        var installed = await host.Service.InstallArchiveAsync(Ctx(), Zip(ArchiveBytes("1.0.0")));
        var enabled = await host.Service.EnableAsync(Ctx(), Id, installed.LifecycleVersion, "1.0.0");
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, enabled.Category);
        var disabled = await host.Service.DisableAsync(Ctx(), Id, enabled.LifecycleVersion);
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, disabled.Category);
        return disabled;
    }

    private static PluginLifecycleRequestContext Ctx(string? key = null, ActorIdentity? actor = null, string node = "local") =>
        new(new NodeId(node), actor ?? Admin, key, "corr-1");

    private static PluginLifecycleRequestContext NewContextFor(string key) => Ctx(key);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PluginLifecycleMetadata Lc() => new PluginStore(StorePath).GetLifecycle(Id);

    private string FinalManifestVersion() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(PluginsRoot, Id, "bops-plugin.json")))!["Version"]!.GetValue<string>();

    private string[] Entries(string subdirectory)
    {
        var directory = Path.Combine(PluginsRoot, ".lifecycle", subdirectory);
        return Directory.Exists(directory)
            ? Directory.GetFileSystemEntries(directory).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    private void WriteTrust(PackageTrustLevel level) => File.WriteAllText(TrustPath, JsonSerializer.Serialize(new[]
    {
        new PluginPublisherTrust("Acme", "test-key", _key.ExportSubjectPublicKeyInfoPem(), level),
    }));

    private Host NewHost(IAuditSink? audit = null)
    {
        var tools = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var skills = new SkillRegistry();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:TrustStorePath"] = TrustPath })
            .Build();
        var manager = new PluginManager(
            new PluginStore(StorePath), tools, skills, new ChatModelRegistry(), PluginsRoot, configuration,
            NullLoggerFactory.Instance, new FakeHttpClientFactory(), _clock, new AlwaysAvailableCapabilityProbe());
        return new Host(manager, new PluginLifecycleService(manager, PluginsRoot, auditSink: audit), tools, skills);
    }

    private static MemoryStream Zip(byte[] bytes) => new(bytes);

    private static byte[] ZipOf(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Builds a real signed plugin archive from the compiled sample plugin. Bytes are stable per call so a "same request" can be replayed byte-for-byte.</summary>
    private byte[] ArchiveBytes(
        string version,
        string id = Id,
        string publisher = "Acme",
        bool sign = true,
        string? extra = null,
        RSA? key = null,
        string keyId = "test-key",
        Action<string>? mutate = null,
        Action<JsonObject>? mutateManifest = null,
        Action<string>? afterSign = null)
    {
        var directory = Directory.CreateTempSubdirectory("bops-lifecycle-source-").FullName;
        try
        {
            foreach (var name in new[] { "Acme.SamplePlugin.dll", "bops-plugin.json" })
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(directory, name));
            }

            var manifestPath = Path.Combine(directory, "bops-plugin.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["Id"] = id;
            manifest["Version"] = version;
            manifest["Publisher"] = publisher;
            mutateManifest?.Invoke(manifest);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            if (extra is not null)
            {
                File.WriteAllText(Path.Combine(directory, "notes.txt"), extra);
            }

            mutate?.Invoke(directory);
            if (sign)
            {
                PluginPackageSignature.Sign(directory, publisher, keyId, (key ?? _key).ExportPkcs8PrivateKeyPem());
            }

            afterSign?.Invoke(directory);
            return ZipOf(Directory.GetFiles(directory).Select(path => (Path.GetFileName(path), File.ReadAllBytes(path))).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record Host(PluginManager Manager, PluginLifecycleService Service, ToolRegistry Tools, SkillRegistry Skills);

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AlwaysAvailableCapabilityProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            lock (Events)
            {
                Events.Add(evt);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ConflictingTool : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "sample.marker.create",
            Description = "Owned by another package; forces a registration collision.",
            Risk = RiskLevel.Read,
            Platforms = ["windows", "linux"],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success("conflict"));
    }
}

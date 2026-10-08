// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Hosting;
using bOps.Memory;
using bOps.Packages.Docker;
using bOps.Packages.Web;
using bOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0049 on the real stack with no fakes of the subsystem: <see cref="PrerequisiteRegistry"/> → <see cref="PrerequisiteTransitionRecorder"/> →
/// <see cref="SqliteSystemMessageStore"/>, driven by <see cref="PrerequisiteReadinessService"/> exactly as the API's boot refresh and
/// periodic coordinator drive it, including across a host restart.
/// </summary>
public sealed class PrerequisiteReadinessIntegrationTests : IDisposable
{
    private const string Debugger = "sample.debugger";

    private readonly string _directory = Directory.CreateTempSubdirectory("bops-readiness-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a pooled SQLite connection may still hold the file for a moment.
        }
    }

    [Fact]
    public async Task ARequiredPrerequisiteMissingAtBoot_StartsBOps_RegistersTheTool_KeepsItUnavailable_AndWritesOneActionableWarning()
    {
        var host = NewHost();
        var check = new ToggleCheck(DebuggerDescriptor(), PrerequisiteState.Unavailable);
        host.Registry.Register(new PackageId("package.debugger"), check);
        host.Tools.Register(new PackageId("package.debugger"), new ProbeTool("system.dump_analyze", requires: [Debugger]));

        var report = await host.Service.RefreshAsync();

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.Messages);
        var readiness = Assert.Single(host.Tools.GetReadiness());
        Assert.True(readiness.Registered);
        Assert.False(readiness.Available);
        Assert.Null(host.Tools.Resolve("system.dump_analyze"));
        var message = Assert.Single((await host.Store.QueryAsync(new SystemMessageQuery())).Items);
        Assert.Equal(SystemMessageSeverity.Warning, message.Severity);
        Assert.Equal(PrerequisiteCodes.MessageMissing, message.Code);
        Assert.Equal(
            "Microsoft Debugging Tools for Windows is unavailable. Install the \"Debugging Tools for Windows\" component so kd.exe is available.",
            message.Message);
        Assert.Contains("tool:system.dump_analyze", message.Metadata.ToJson()["affectedComponents"]!.ToJsonString(), StringComparison.Ordinal);
        host.Dispose();
    }

    [Fact]
    public async Task TheBootRefresh_RecordsTheStateAndItsMessage_BeforeAnyToolIsOffered()
    {
        var path = Path.Combine(_directory, "ordering.db");
        var inner = new SqliteSystemMessageStore(path);
        ToolRegistry? tools = null;
        var offeredWhenSaving = new List<bool>();
        var store = new InterceptingStore(inner, () => offeredWhenSaving.Add(tools!.Resolve("sample.tool") is not null));
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        tools = new ToolRegistry(registry);
        var skills = new SkillRegistry(registry);
        registry.Register(new PackageId("package.p"), new ToggleCheck(DebuggerDescriptor(), PrerequisiteState.Available));
        tools.Register(new PackageId("package.p"), new ProbeTool("sample.tool", requires: [Debugger]));
        var service = new PrerequisiteReadinessService(
            registry, new PrerequisiteTransitionRecorder(store, NodeId.Local), tools, skills, NullLogger<PrerequisiteReadinessService>.Instance);

        Assert.Null(tools.Resolve("sample.tool"));
        await service.RefreshAsync();

        Assert.Equal([false], offeredWhenSaving);
        Assert.NotNull(tools.Resolve("sample.tool"));
        Assert.Equal(PrerequisiteState.Available, (await inner.LoadStateAsync(NodeId.Local, Debugger))!.State);
        service.Dispose();
    }

    [Fact]
    public async Task Restart_DoesNotDuplicateTheSameWarning_AndRecoveryIsExactlyOneInformation()
    {
        var first = NewHost();
        Compose(first, PrerequisiteState.Unavailable);
        await first.Service.RefreshAsync();
        await first.Service.RefreshAsync();
        Assert.Equal([SystemMessageSeverity.Warning], await Severities(first));
        first.Dispose();

        // A new process: new registry, new store handle on the same file, same observation.
        var second = NewHost();
        var secondCheck = Compose(second, PrerequisiteState.Unavailable);
        var report = await second.Service.RefreshAsync();
        Assert.Equal(0, report.Messages);
        Assert.Equal([SystemMessageSeverity.Warning], await Severities(second));
        Assert.Null(second.Tools.Resolve("system.dump_analyze"));

        secondCheck.State = PrerequisiteState.Available;
        Assert.Equal(1, (await second.Service.RefreshAsync()).Messages);
        await second.Service.RefreshAsync();

        Assert.Equal([SystemMessageSeverity.Information, SystemMessageSeverity.Warning], await Severities(second));
        var recovery = (await second.Store.QueryAsync(new SystemMessageQuery { Severity = SystemMessageSeverity.Information })).Items.Single();
        Assert.Equal(PrerequisiteCodes.MessageRecovered, recovery.Code);
        Assert.NotNull(second.Tools.Resolve("system.dump_analyze"));
        second.Dispose();
    }

    [Fact]
    public async Task AnOptionalPrerequisiteMissing_LeavesTheComponentExecutable_WithOneInformation()
    {
        var host = NewHost();
        host.Registry.Register(new PackageId("package.debugger"), new ToggleCheck(DebuggerDescriptor(), PrerequisiteState.Unavailable));
        host.Tools.Register(new PackageId("package.debugger"), new ProbeTool("system.dump_analyze", optional: [Debugger]));

        await host.Service.RefreshAsync();

        Assert.NotNull(host.Tools.ResolveForExecution("system.dump_analyze"));
        Assert.True(Assert.Single(host.Tools.GetReadiness()).Degraded);
        var message = Assert.Single((await host.Store.QueryAsync(new SystemMessageQuery())).Items);
        Assert.Equal(SystemMessageSeverity.Information, message.Severity);
        host.Dispose();
    }

    [Fact]
    public async Task ARefresh_MakesAToolDisappearAndReappearWithoutARestart()
    {
        var host = NewHost();
        var check = Compose(host, PrerequisiteState.Available);
        await host.Service.RefreshAsync();
        Assert.NotNull(host.Tools.Resolve("system.dump_analyze"));

        check.State = PrerequisiteState.Unavailable;
        await host.Service.RefreshAsync();
        Assert.Null(host.Tools.Resolve("system.dump_analyze"));
        Assert.Empty(host.Tools.GetAvailableManifests());

        check.State = PrerequisiteState.Available;
        await host.Service.RefreshAsync();
        Assert.NotNull(host.Tools.Resolve("system.dump_analyze"));
        Assert.Equal([SystemMessageSeverity.Information, SystemMessageSeverity.Warning], await Severities(host));
        host.Dispose();
    }

    [Fact]
    public async Task ASkillCapability_IsGatedAndRecovers_AndItsWarningNamesIt()
    {
        var host = NewHost();
        var check = new ToggleCheck(DebuggerDescriptor(), PrerequisiteState.Unavailable);
        host.Registry.Register(new PackageId("package.debugger"), check);
        host.Skills.Register(new PackageId("package.debugger"), new ProbeSkill("sample.skill", new ProbeCapability("sample.diagnose", [Debugger])));

        await host.Service.RefreshAsync();
        Assert.Empty(host.Skills.GetAvailableSkills());
        Assert.Null(host.Skills.Resolve("sample.skill", "sample.diagnose"));
        var warning = Assert.Single((await host.Store.QueryAsync(new SystemMessageQuery())).Items);
        Assert.Equal(SystemMessageSeverity.Warning, warning.Severity);
        Assert.Contains("skill-capability:sample.skill/sample.diagnose", warning.Metadata.ToJson()["affectedComponents"]!.ToJsonString(), StringComparison.Ordinal);

        check.State = PrerequisiteState.Available;
        await host.Service.RefreshAsync();
        Assert.NotNull(host.Skills.Resolve("sample.skill", "sample.diagnose"));
        host.Dispose();
    }

    [Fact]
    public async Task ADegradedRequiredPrerequisite_KeepsTheComponentAvailableButDegraded()
    {
        var host = NewHost();
        Compose(host, PrerequisiteState.Degraded);

        await host.Service.RefreshAsync();

        var readiness = Assert.Single(host.Tools.GetReadiness());
        Assert.True(readiness.Available);
        Assert.True(readiness.Degraded);
        Assert.NotNull(host.Tools.Resolve("system.dump_analyze"));
        host.Dispose();
    }

    [Fact]
    public async Task AThrowingCheck_IsOneErrorMessage_AndDoesNotStopAnUnrelatedPrerequisite()
    {
        var host = NewHost();
        host.Registry.Register(new PackageId("package.a"), new ThrowingCheck());
        host.Registry.Register(new PackageId("package.b"), new ToggleCheck(DebuggerDescriptor(), PrerequisiteState.Unavailable));
        host.Tools.Register(new PackageId("package.b"), new ProbeTool("sample.tool", requires: [Debugger]));

        var report = await host.Service.RefreshAsync();

        Assert.Equal(2, report.Checked);
        Assert.Equal(2, report.Messages);
        var all = (await host.Store.QueryAsync(new SystemMessageQuery())).Items;
        Assert.Contains(all, m => m.Severity == SystemMessageSeverity.Error && m.Code == PrerequisiteCodes.MessageCheckFailed);
        Assert.Contains(all, m => m.Severity == SystemMessageSeverity.Warning && m.Code == PrerequisiteCodes.MessageMissing);
        Assert.DoesNotContain(all, m => m.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
        host.Dispose();
    }

    [Fact]
    public async Task OnlyOneGlobalCycleRunsAtATime()
    {
        var host = NewHost();
        var gate = new GatedCycleCheck();
        host.Registry.Register(new PackageId("package.g"), gate);

        var first = host.Service.RefreshAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = host.Service.RefreshAsync();
        await Task.Delay(150);
        Assert.Equal(1, gate.Calls);
        Assert.False(second.IsCompleted);

        gate.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, gate.Calls);
        Assert.Equal(1, gate.MaxActive);
        host.Dispose();
    }

    [Fact]
    public async Task ARecorderFailure_IsCountedAndLogged_ButTheAvailabilitySnapshotIsStillRefreshed()
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        var tools = new ToolRegistry(registry);
        var skills = new SkillRegistry(registry);
        registry.Register(new PackageId("package.p"), new ToggleCheck(DebuggerDescriptor(), PrerequisiteState.Available));
        tools.Register(new PackageId("package.p"), new ProbeTool("sample.tool", requires: [Debugger]));
        using var service = new PrerequisiteReadinessService(
            registry, new PrerequisiteTransitionRecorder(new BrokenStore(), NodeId.Local), tools, skills, NullLogger<PrerequisiteReadinessService>.Instance);

        var report = await service.RefreshAsync();

        Assert.Equal(1, report.RecordFailures);
        Assert.NotNull(tools.Resolve("sample.tool"));
    }

    [Fact]
    public async Task Docker_AndSearxng_KeepTheirIdsAndMeaning_WithOperatorGuidance()
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        var unreachable = new DockerClientFactory("tcp://127.0.0.1:1");
        FirstPartyPrerequisiteComposition.Register(registry, unreachable, new DockerBuildOptions(), new WebSearchOptions());

        var results = await registry.RefreshAsync();

        Assert.Equal(["docker", "docker.build-contexts", "web.searxng"], results.Select(result => result.Id));
        Assert.All(results, result => Assert.Equal(PrerequisiteState.Unavailable, result.State));
        Assert.All(registry.GetRegistrations(), registration =>
        {
            Assert.False(string.IsNullOrWhiteSpace(registration.Descriptor.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(registration.Descriptor.Remediation));
        });
        Assert.Equal(
            ["bops.packages.docker", "bops.packages.docker", "bops.packages.web"],
            registry.GetRegistrations().Select(registration => registration.Package.Value));

        var configured = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        FirstPartyPrerequisiteComposition.Register(
            configured,
            unreachable,
            new DockerBuildOptions { Contexts = ["/srv/contexts"] },
            new WebSearchOptions { BaseUrl = "http://localhost:8080" });
        Assert.Equal(PrerequisiteState.Available, (await configured.CheckAsync("docker.build-contexts")).State);
        Assert.Equal(PrerequisiteState.Available, (await configured.CheckAsync("web.searxng")).State);
        Assert.Equal(PrerequisiteState.Unavailable, (await configured.CheckAsync("docker")).State);
    }

    [Fact]
    public async Task TheDockerAndWebTools_AreHiddenWhenTheirPrerequisitesAreNot_AsBefore()
    {
        var host = NewHost();
        var docker = new DockerClientFactory("tcp://127.0.0.1:1");
        var build = new DockerBuildOptions();
        FirstPartyPrerequisiteComposition.Register(host.Registry, docker, build, new WebSearchOptions());
        foreach (var tool in new DockerToolProvider(docker, build, new DockerVolumeOptions()).GetTools())
        {
            host.Tools.Register(new PackageId("bops.packages.docker"), tool);
        }

        await host.Service.RefreshAsync();

        Assert.DoesNotContain(host.Tools.GetAvailableManifests(), manifest => manifest.Name.StartsWith("docker.", StringComparison.Ordinal));
        Assert.Contains(host.Tools.GetReadiness(), readiness => readiness.Component.Id.StartsWith("docker.", StringComparison.Ordinal) && !readiness.Available);
        host.Dispose();
    }

    private Host NewHost() => new(_directory);

    private static ToggleCheck Compose(Host host, PrerequisiteState state)
    {
        var check = new ToggleCheck(DebuggerDescriptor(), state);
        host.Registry.Register(new PackageId("package.debugger"), check);
        host.Tools.Register(new PackageId("package.debugger"), new ProbeTool("system.dump_analyze", requires: [Debugger]));
        return check;
    }

    private static async Task<SystemMessageSeverity[]> Severities(Host host) =>
        [.. (await host.Store.QueryAsync(new SystemMessageQuery { PageSize = 200 })).Items.Select(message => message.Severity).Order()];

    private static PrerequisiteDescriptor DebuggerDescriptor() => new(
        Debugger, "Microsoft Debugging Tools for Windows", "kd.exe, used to read kernel dumps.", PrerequisiteKind.Executable)
    {
        Remediation = "Install the \"Debugging Tools for Windows\" component so kd.exe is available.",
    };

    /// <summary>One "process": its own registry, registries and store handle over the shared database file.</summary>
    private sealed class Host : IDisposable
    {
        public Host(string directory)
        {
            Store = new SqliteSystemMessageStore(Path.Combine(directory, "system-messages.db"));
            Registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
            Tools = new ToolRegistry(Registry);
            Skills = new SkillRegistry(Registry);
            Service = new PrerequisiteReadinessService(
                Registry, new PrerequisiteTransitionRecorder(Store, NodeId.Local), Tools, Skills, NullLogger<PrerequisiteReadinessService>.Instance);
        }

        public SqliteSystemMessageStore Store { get; }

        public PrerequisiteRegistry Registry { get; }

        public ToolRegistry Tools { get; }

        public SkillRegistry Skills { get; }

        public PrerequisiteReadinessService Service { get; }

        public void Dispose() => Service.Dispose();
    }

    private sealed class ToggleCheck(PrerequisiteDescriptor descriptor, PrerequisiteState state) : IPrerequisiteCheck
    {
        public PrerequisiteState State { get; set; } = state;

        public PrerequisiteDescriptor Descriptor { get; } = descriptor;

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new PrerequisiteCheckOutcome(State, State is PrerequisiteState.Available ? "available" : State.ToString().ToLowerInvariant(), $"kd.exe is {State}."));
    }

    private sealed class ThrowingCheck : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = new("sample.broken", "Broken", "Always throws.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Server=db;Password=secret");
    }

    private sealed class GatedCycleCheck : IPrerequisiteCheck
    {
        private int _calls;
        private int _active;
        private int _maxActive;
        private readonly object _gate = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public PrerequisiteDescriptor Descriptor { get; } = new("sample.gated", "Gated", "Waits until released.");

        public async Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            lock (_gate)
            {
                _maxActive = Math.Max(_maxActive, active);
            }

            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref _active);
            return new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", "Fine.");
        }
    }

    private sealed class ProbeTool(string name, IReadOnlyList<string>? requires = null, IReadOnlyList<string>? optional = null) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A test tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = requires ?? [],
            OptionalRequires = optional ?? [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }

    private sealed class ProbeCapability(string name, IReadOnlyList<string> requires) : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(name, "1.0.0", "Test capability.", RiskLevel.Read, [], [], [], TimeSpan.FromSeconds(5), SupportsDryRun: true)
        {
            Requires = requires,
        };

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport([], [], null));
    }

    private sealed class ProbeSkill(string skillId, params ICapability[] capabilities) : ISkillProvider
    {
        public string SkillId { get; } = skillId;

        public IReadOnlyList<ICapability> GetCapabilities() => capabilities;

        public IEnumerable<ITool> GetTools() => [];
    }

    private sealed class InterceptingStore(SqliteSystemMessageStore inner, Action onSave) : IPrerequisiteStateStore
    {
        public Task<PrerequisiteStateRecord?> LoadStateAsync(NodeId node, string prerequisiteId, CancellationToken ct = default) =>
            inner.LoadStateAsync(node, prerequisiteId, ct);

        public Task<IReadOnlyList<PrerequisiteStateRecord>> ListStatesAsync(NodeId node, CancellationToken ct = default) =>
            inner.ListStatesAsync(node, ct);

        public Task<bool> SaveStateAsync(PrerequisiteStateRecord state, string? expectedFingerprint, SystemMessage? transitionMessage, CancellationToken ct = default)
        {
            onSave();
            return inner.SaveStateAsync(state, expectedFingerprint, transitionMessage, ct);
        }
    }

    private sealed class BrokenStore : IPrerequisiteStateStore
    {
        public Task<PrerequisiteStateRecord?> LoadStateAsync(NodeId node, string prerequisiteId, CancellationToken ct = default) =>
            throw new IOException("disk full");

        public Task<IReadOnlyList<PrerequisiteStateRecord>> ListStatesAsync(NodeId node, CancellationToken ct = default) =>
            throw new IOException("disk full");

        public Task<bool> SaveStateAsync(PrerequisiteStateRecord state, string? expectedFingerprint, SystemMessage? transitionMessage, CancellationToken ct = default) =>
            throw new IOException("disk full");
    }
}

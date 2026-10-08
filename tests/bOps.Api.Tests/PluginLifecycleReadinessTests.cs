// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using bOps.Abstractions;
using bOps.Memory;
using bOps.PluginHost;
using bOps.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0049: a committed plugin enable or disable updates readiness before the HTTP response, with no wait for the periodic timer; a
/// lifecycle operation that changed nothing (stale, unconfirmed, failed) triggers no refresh. Real host composition, a real signed plugin
/// that contributes a prerequisite check, and a host-owned counting check that proves whether a readiness cycle ran at all.
/// </summary>
public sealed class PluginLifecycleReadinessTests
{
    private const string Id = PluginArchiveFixture.PluginId;
    private const string Contributed = PluginArchiveFixture.ContributedPrerequisite;

    // The periodic coordinator must not be what makes these tests pass.
    private static readonly Dictionary<string, string?> NoTimer = new() { ["Prerequisites:RefreshIntervalSeconds"] = "3600" };

    [Fact]
    public async Task Enable_RefreshesReadinessBeforeTheResponse_SoADependentComponentIsAvailableWithoutWaitingForTheTimer()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: NoTimer);
        var host = new HostView(harness);
        host.RegisterDependentTool();
        var installed = await harness.InstallAsync(withCheck: true);
        await host.Readiness.RefreshAsync();

        // Before enabling: the plugin's check does not exist, the dependent component is registered but unavailable.
        Assert.False(host.Dependent.Available);
        Assert.Equal(PrerequisiteState.Error, host.Registry.GetState(Contributed));
        var calls = host.Counter.Calls;

        using var enabled = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, "1.0.0");

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True(host.Counter.Calls > calls, "enable must run a readiness cycle before it answers");
        Assert.Equal(PrerequisiteState.Available, host.Registry.GetState(Contributed));
        Assert.True(host.Dependent.Available);
        Assert.NotNull(host.Tools.ResolveForExecution(HostView.DependentTool));
        var recovery = (await host.Messages.QueryAsync(new SystemMessageQuery { Severity = SystemMessageSeverity.Information })).Items;
        Assert.Contains(recovery, m => m.Code == PrerequisiteCodes.MessageRecovered && m.ComponentId == Contributed);
    }

    [Fact]
    public async Task Disable_RefreshesReadinessBeforeTheResponse_SoTheDependentComponentStopsBelievingThePrerequisiteIsAvailable()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: NoTimer);
        var host = new HostView(harness);
        host.RegisterDependentTool();
        var installed = await harness.InstallAsync(withCheck: true);
        using var enabled = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, "1.0.0");
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True(host.Dependent.Available);
        var calls = host.Counter.Calls;

        using var disabled = await PluginLifecycleApiHarness.ActionAsync(harness.Client, Id, "disable", enabled.Headers.ETag!.Tag);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.True(host.Counter.Calls > calls, "disable must run a readiness cycle before it answers");
        Assert.False(host.Dependent.Available);
        Assert.Null(host.Tools.ResolveForExecution(HostView.DependentTool));
        Assert.Equal(PrerequisiteState.Error, host.Registry.GetState(Contributed));
        Assert.Equal(PrerequisiteCodes.NotRegistered, host.Registry.GetLastResult(Contributed)!.Code);
        var all = (await host.Messages.QueryAsync(new SystemMessageQuery { PageSize = 200 })).Items;
        Assert.Contains(all, m => m.Code == PrerequisiteCodes.MessageNotRegistered && m.ComponentId == Contributed);
    }

    [Fact]
    public async Task ARejectedOrFailedLifecycleOperation_ThatChangedNothing_RunsNoReadinessCycle()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: NoTimer);
        var host = new HostView(harness);
        host.RegisterDependentTool();
        var installed = await harness.InstallAsync(withCheck: true);
        await host.Readiness.RefreshAsync();
        var calls = host.Counter.Calls;

        using var unconfirmed = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, confirmedVersion: null);
        using var wrongVersion = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, "9.9.9");
        using var stale = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, "\"plv-999\"", "1.0.0");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfirmed.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongVersion.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(calls, host.Counter.Calls);
        Assert.False(host.Dependent.Available);
    }

    [Fact]
    public async Task AnEnableThatFailsActivation_RunsNoReadinessCycle_AndOffersNothing()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: NoTimer);
        var host = new HostView(harness);
        host.RegisterDependentTool();
        var installed = await harness.InstallAsync(throwOnActivate: true, withCheck: true);
        await host.Readiness.RefreshAsync();
        var calls = host.Counter.Calls;

        using var failed = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, "1.0.0");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(calls, host.Counter.Calls);
        Assert.False(host.Dependent.Available);
        Assert.Equal(PrerequisiteState.Error, host.Registry.GetState(Contributed));
    }

    [Fact]
    public async Task ARecoveryThatOnlyRestoresAnInstalledDisabledGeneration_RunsNoReadinessCycle()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: NoTimer);
        var host = new HostView(harness);
        var installed = await harness.InstallAsync(throwOnActivate: true, withCheck: true);
        using var failed = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, "1.0.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        await host.Readiness.RefreshAsync();
        var calls = host.Counter.Calls;

        using var recovered = await PluginLifecycleApiHarness.RecoverAsync(harness.Client, Id, (await harness.StatusAsync()).GetProperty("lifecycleETag").GetString());

        Assert.Equal(calls, host.Counter.Calls);
    }

    [Fact]
    public async Task AFollowUpRefreshThatThrows_DoesNotRollBackOrChangeTheCommittedEnable()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: NoTimer);
        var host = new HostView(harness);
        var installed = await harness.InstallAsync(withCheck: true);
        host.Readiness.Dispose(); // every later RefreshAsync now throws ObjectDisposedException

        using var enabled = await PluginLifecycleApiHarness.EnableAsync(harness.Client, Id, installed, "1.0.0");

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True(harness.Manager.IsActivated(Id));
        Assert.True((await harness.StatusAsync()).GetProperty("enabled").GetBoolean());
    }

    private sealed class HostView
    {
        public const string DependentTool = "test.dependent";

        public HostView(PluginLifecycleApiHarness harness)
        {
            var services = harness.Factory.Services;
            Registry = services.GetRequiredService<PrerequisiteRegistry>();
            Tools = services.GetRequiredService<ToolRegistry>();
            Readiness = services.GetRequiredService<PrerequisiteReadinessService>();
            Messages = services.GetRequiredService<ISystemMessageStore>();
            Registry.Register(new PackageId("test.counter"), Counter);
        }

        public PrerequisiteRegistry Registry { get; }

        public ToolRegistry Tools { get; }

        public PrerequisiteReadinessService Readiness { get; }

        public ISystemMessageStore Messages { get; }

        public CountingCheck Counter { get; } = new();

        public ComponentReadiness Dependent => Tools.GetReadiness().Single(c => c.Component.Id == DependentTool);

        public void RegisterDependentTool() => Tools.Register(new PackageId("test.dependent-package"), new DependentToolImpl());
    }

    private sealed class CountingCheck : IPrerequisiteCheck
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public PrerequisiteDescriptor Descriptor { get; } = new("test.counter", "Counter", "Counts readiness cycles.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", "Counted."));
        }
    }

    private sealed class DependentToolImpl : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = HostView.DependentTool,
            Description = "A host tool that needs the plugin's service.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [Contributed],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }
}

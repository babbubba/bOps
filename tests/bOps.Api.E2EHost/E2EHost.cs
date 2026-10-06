// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.E2EHost;

/// <summary>
/// E2E-8 (ADR-0043 §18.1), E2E-9 and E2E-10 (ADR-0044 §21). Boots the real <c>bOps.Api</c> composition root (<see cref="Program"/>)
/// on Kestrel at <c>http://localhost:5080</c> — the address the repository's <c>proxy.conf.json</c> forwards to — with two
/// substitutions: a fake <see cref="IChatModel"/> and one test Skill package (<see cref="TestCatalog"/>). Authentication, the
/// browser-session store, the CSRF gate, the policy file, the delegation reducer, runner and stores and every endpoint are the
/// production ones. The API key comes from <c>BOPS_E2E_API_KEY</c> (generated per run by the Playwright configuration) and is never
/// printed. A loopback-only line protocol on <c>127.0.0.1:5099</c> lets the test release model calls, count them, revoke sessions
/// and restart the API in this process — the way an operator restarts it after writing <c>policy.yaml</c>, which the API reads
/// only at start; it exists only here.
/// </summary>
internal static class E2EHost
{
    public const int ApiPort = 5080;
    public const int ControlPort = 5099;
    public const string KeyVariable = "BOPS_E2E_API_KEY";

    public static async Task<int> Main()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            await Console.Error.WriteLineAsync($"{KeyVariable} is not set; refusing to start without a per-run API key.");
            return 2;
        }

        var state = Environment.GetEnvironmentVariable("BOPS_E2E_STATE_DIR") is { Length: > 0 } configured
            ? Directory.CreateDirectory(configured).FullName
            : Directory.CreateTempSubdirectory("bops-e2e-8-").FullName;
        var vaultVariable = $"BOPS_E2E_VAULT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(vaultVariable, $"e2e-vault-master-key-{Guid.NewGuid():N}");

        // Outside a test runner WebApplicationFactory cannot find bOps.Api's content root by itself; its appsettings.json is copied
        // next to this host, so the host's own directory is the content root (the factory's documented override variable).
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_BOPS_API", AppContext.BaseDirectory);

        using var model = new GatedChatModel();
        await using var api = new ApiHost(state, vaultVariable, model);
        await api.StartAsync();
        await Console.Out.WriteLineAsync($"E2E host listening on http://localhost:{ApiPort} (control 127.0.0.1:{ControlPort}).");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };

        await new ControlChannel(model, api).RunAsync(stop);
        return 0;
    }
}

/// <summary>The running API; <see cref="RestartAsync"/> stops it and starts a new composition on the same state and port.</summary>
internal sealed class ApiHost(string state, string vaultVariable, GatedChatModel model) : IAsyncDisposable
{
    private E2EFactory? _factory;

    public IServiceProvider Services => _factory?.Services ?? throw new InvalidOperationException("The API is not running.");

    public async Task StartAsync()
    {
#pragma warning disable CA2000 // Owned by this host until the next restart or disposal.
        var factory = new E2EFactory(state, vaultVariable, model);
#pragma warning restore CA2000
        factory.UseKestrel(E2EHost.ApiPort);
        factory.StartServer();
        _factory = factory;
        await TestCatalog.RegisterAsync(factory.Services);
    }

    public async Task RestartAsync()
    {
        await DisposeAsync();
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var factory = _factory;
        _factory = null;
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }
    }
}

/// <summary>The production composition with test-only state paths, one credential read from the environment, and the fake model.</summary>
internal sealed class E2EFactory(string state, string vaultVariable, GatedChatModel model) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:FilePath"] = Path.Combine(state, "audit.jsonl"),
            ["Memory:FilePath"] = Path.Combine(state, "tasks.db"),
            ["Delegation:FilePath"] = Path.Combine(state, "delegations.db"),
            ["Filesystem:Inventory:ManifestStorePath"] = Path.Combine(state, "filesystem-manifests.db"),
            ["Policy:FilePath"] = Path.Combine(state, "policy.yaml"),
            ["Plugins:StorePath"] = Path.Combine(state, "plugins.json"),
            ["Plugins:RootPath"] = Path.Combine(state, "plugins"),
            ["Plugins:TrustStorePath"] = Path.Combine(state, "publisher-trust.json"),
            ["Vault:FilePath"] = Path.Combine(state, "vault.dat"),
            ["Vault:MasterKeySecret:Provider"] = "environment",
            ["Vault:MasterKeySecret:Name"] = vaultVariable,
            ["Settings:FilePath"] = Path.Combine(state, "settings.json"),
            ["BrowserSession:FilePath"] = Path.Combine(state, "sessions.db"),
            ["Authentication:ApiKeys:0:Id"] = "e2e-operator",
            ["Authentication:ApiKeys:0:DisplayName"] = "E2E operator",
            ["Authentication:ApiKeys:0:Secret:Provider"] = "environment",
            ["Authentication:ApiKeys:0:Secret:Name"] = E2EHost.KeyVariable,
            ["Authentication:ApiKeys:0:Roles"] = "viewer,operator,approver,administrator",
            // A released model call never waits long, but a held one may wait for the test: no attempt timeout or retry may fire.
            ["Agent:ModelCallAttemptTimeout"] = "00:10:00",
            ["Agent:ModelCallBudget"] = "00:30:00",
        }));
        builder.ConfigureServices(services => services.AddSingleton<IChatModel>(model));
    }
}

/// <summary>
/// The one test package of E2E-10's readiness-vs-submit variant: Skill <c>e2e.skill</c> with Capability <c>e2e.configure</c>, whose
/// input schema the UI renders as a form. It is never prepared in these scenarios — the run is refused before any model call — and
/// it contributes no tool.
/// </summary>
internal static class TestCatalog
{
    private static readonly PackageId Package = new("bops.e2e.catalog");

    public static async Task RegisterAsync(IServiceProvider services)
    {
        services.GetRequiredService<ISkillRegistry>().Register(Package, new Skill());
        await services.GetRequiredService<IToolRegistry>().RefreshCapabilitiesAsync();
    }

    private sealed class Skill : ISkillProvider
    {
        public string SkillId => "e2e.skill";

        public IReadOnlyList<ICapability> GetCapabilities() => [new Configure()];

        public IEnumerable<ITool> GetTools() => [];
    }

    private sealed class Configure : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            "e2e.configure", "1.0.0", "Changes one setting of a service (E2E test Capability).", RiskLevel.High, ["service.manage"],
            [
                new ToolParameter("service", ToolParameterType.String, "The service to change.", Required: true) { MinLength = 2, MaxLength = 16 },
                new ToolParameter("mode", ToolParameterType.Enum, "How to apply it.", Required: false, AllowedValues: ["fast", "safe"]),
            ],
            [], TimeSpan.FromSeconds(5), SupportsDryRun: true, new VerificationSpec("system.cpu", [], "Reads the CPU after the change."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            throw new InvalidOperationException("E2E: e2e.configure is never prepared; its run is refused before any model call.");
    }
}

/// <summary>
/// A deterministic model. A delegation role task (recognized by the role instructions in its goal) is answered at once by
/// <see cref="DelegationScript"/>. Every other task asks four times, in order: plan (answered at once), a <c>system.cpu</c> step, a
/// second <c>system.cpu</c> step, and the final answer — each of the last three only after a <c>release</c>. Tasks in E2E-8 never
/// overlap, so the cycle position is the count of those calls modulo four. <see cref="Calls"/> counts every call.
/// </summary>
internal sealed class GatedChatModel : IChatModel, IDisposable
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _calls;
    private int _allCalls;
    private int _waiting;

    public ChatModelDescriptor Descriptor { get; } = new("e2e", "gated-model");

    public int Waiting => Volatile.Read(ref _waiting);

    public int Calls => Volatile.Read(ref _allCalls);

    public void Release() => _gate.Release();

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _allCalls);
        if (DelegationScript.Answer(request) is { } scripted)
        {
            return scripted;
        }

        var phase = (Interlocked.Increment(ref _calls) - 1) % 4;
        if (phase == 0)
        {
            return new ModelResponse(new JsonObject { ["rationale"] = "E2E-8", ["steps"] = new JsonArray() }.ToJsonString(), [], false, null);
        }

        Interlocked.Increment(ref _waiting);
        try
        {
            await _gate.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        return phase < 3
            ? new ModelResponse(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), "system.cpu", ToolArguments.Empty)], false, null)
            : new ModelResponse("E2E-8 task finished.", [], true, null);
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// E2E-9's diagnosis, decided only from the request: Discovery plans, reads <c>system.cpu</c> once and summarizes; Diagnostic plans
/// and replies with one finding citing that evidence (<c>discovery-0</c>). It never asks for any other tool.
/// </summary>
internal static class DelegationScript
{
    public static ModelResponse? Answer(ModelRequest request)
    {
        var goal = request.History.FirstOrDefault(turn => turn.Role == ChatRole.User)?.Content ?? string.Empty;
        var diagnostic = goal.StartsWith("You are the Diagnostic role", StringComparison.Ordinal);
        if (!diagnostic && !goal.StartsWith("You are the Discovery role", StringComparison.Ordinal))
        {
            return null;
        }

        if (request.SystemPrompt.Contains("lay out your plan", StringComparison.Ordinal) || request.SystemPrompt.Contains("Revise it.", StringComparison.Ordinal))
        {
            var step = new JsonObject { ["description"] = "Read the CPU utilization.", ["expectedTool"] = diagnostic ? null : "system.cpu" };
            return new ModelResponse(new JsonObject { ["rationale"] = "E2E-9 diagnosis.", ["steps"] = new JsonArray { step } }.ToJsonString(), [], false, null);
        }

        if (diagnostic)
        {
            return new ModelResponse(
                """{"findings":[{"summary":"CPU utilization was sampled once.","evidenceIds":["discovery-0"],"severity":"read"}]}""", [], true, null);
        }

        return request.History.Any(turn => turn.Role == ChatRole.Tool)
            ? new ModelResponse("The CPU utilization was read once with system.cpu.", [], true, null)
            : new ModelResponse(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), "system.cpu", ToolArguments.Empty)], false, null);
    }
}

/// <summary>
/// Loopback-only control: one command per line — <c>release</c> (let one held model call answer), <c>waiting</c> (how many model
/// calls are held), <c>calls</c> (how many model calls were made in all), <c>revoke</c> (delete every browser session through the
/// store's own fixed operation), <c>restart</c> (stop the API and start it again on the same state, so it reads
/// <c>policy.yaml</c> again), <c>stop</c>.
/// </summary>
internal sealed class ControlChannel(GatedChatModel model, ApiHost api)
{
    public async Task RunAsync(CancellationTokenSource stop)
    {
        using var listener = new TcpListener(IPAddress.Loopback, E2EHost.ControlPort);
        listener.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
                var command = (await reader.ReadLineAsync(stop.Token))?.Trim();
                switch (command)
                {
                    case "release":
                        model.Release();
                        await writer.WriteLineAsync("ok");
                        break;
                    case "waiting":
                        await writer.WriteLineAsync($"ok {model.Waiting}");
                        break;
                    case "calls":
                        await writer.WriteLineAsync($"ok {model.Calls}");
                        break;
                    case "revoke":
                        var deleted = await api.Services.GetRequiredService<IBrowserSessionStore>().DeleteUnknownCredentialsAsync([], stop.Token);
                        await writer.WriteLineAsync($"ok {deleted}");
                        break;
                    case "restart":
                        await api.RestartAsync();
                        await writer.WriteLineAsync("ok");
                        break;
                    case "stop":
                        await writer.WriteLineAsync("ok");
                        await stop.CancelAsync();
                        break;
                    default:
                        await writer.WriteLineAsync("error");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }
}

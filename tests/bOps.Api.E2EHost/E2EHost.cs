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
/// E2E-8 (ADR-0043 §18.1). Boots the real <c>bOps.Api</c> composition root (<see cref="Program"/>) on Kestrel at
/// <c>http://localhost:5080</c> — the address the repository's <c>proxy.conf.json</c> forwards to — with one substitution: a gated
/// fake <see cref="IChatModel"/>. Authentication, the browser-session store, the CSRF gate and every endpoint are the production ones.
/// The API key comes from <c>BOPS_E2E_API_KEY</c> (generated per run by the Playwright configuration) and is never printed. A
/// loopback-only line protocol on <c>127.0.0.1:5099</c> lets the test release model calls and revoke sessions; it exists only here.
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
        using var factory = new E2EFactory(state, vaultVariable, model);
        factory.UseKestrel(ApiPort);
        factory.StartServer();
        await Console.Out.WriteLineAsync($"E2E-8 host listening on http://localhost:{ApiPort} (control 127.0.0.1:{ControlPort}).");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };

        await new ControlChannel(model, factory.Services.GetRequiredService<IBrowserSessionStore>()).RunAsync(stop);
        return 0;
    }
}

/// <summary>The production composition with test-only state paths, one credential read from the environment, and the gated model.</summary>
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
/// A deterministic model whose calls wait for the test. Every task asks four times, in order: plan (answered at once), a
/// <c>system.cpu</c> step, a second <c>system.cpu</c> step, and the final answer — each of the last three only after a
/// <c>release</c>. Tasks in E2E-8 never overlap, so the cycle position is the call count modulo four.
/// </summary>
internal sealed class GatedChatModel : IChatModel, IDisposable
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _calls;
    private int _waiting;

    public ChatModelDescriptor Descriptor { get; } = new("e2e", "gated-model");

    public int Waiting => Volatile.Read(ref _waiting);

    public void Release() => _gate.Release();

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
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
/// Loopback-only control: one command per line — <c>release</c> (let one held model call answer), <c>waiting</c> (how many model
/// calls are held), <c>revoke</c> (delete every browser session through the store's own fixed operation), <c>stop</c>.
/// </summary>
internal sealed class ControlChannel(GatedChatModel model, IBrowserSessionStore sessions)
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
                    case "revoke":
                        var deleted = await sessions.DeleteUnknownCredentialsAsync([], stop.Token);
                        await writer.WriteLineAsync($"ok {deleted}");
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

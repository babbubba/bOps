// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Api;
using bOps.Knowledge;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Runtime;
using Xunit.Abstractions;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// HOST-CANARY-01B: one explicitly enabled, local-only proof that the governed host can give Qwen
/// signed Knowledge data.  This is deliberately a test-project composition, never a product host.
/// </summary>
public sealed class KnowledgeHostCanaryLiveTests(ITestOutputHelper output)
{
    private const string RunSwitch = "BOPS_RUN_LIVE_QWEN_KNOWLEDGE_CANARY";
    private const string Checklist = "nginx -t; ss -ltnp :80";
    private const string Goal = "Read the local signed knowledge resource nginx-triage/references/checklist.md through "
        + "runtime.knowledge_read. Then use the existing read-only test step to complete the plan and answer with the exact checklist command. "
        + "Do not guess the command; it is not in this request.";

    [LocalQwenKnowledgeCanaryFact]
    public async Task Qwen_ReadsTheSignedLocalPackThroughTheGovernedKnowledgePath()
    {
        var diagnostics = new KnowledgeCanaryDiagnostics();
        KnowledgeHostCanary? canary = null;
        CappedRecordingChatModel? model = null;
        CanaryRig? rig = null;
        TaskState? state = null;
        try
        {
            diagnostics.Phase = "preflight";
            var options = LocalQwenConfiguration.Read();
            await LocalQwenConfiguration.PreflightAsync(options.BaseUrl);

            diagnostics.Phase = "composition";
            canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
            using var transport = new HttpClientHandler();
            using var handler = new DiagnosticHttpHandler(diagnostics, transport);
            using var client = new HttpClient(handler, disposeHandler: false);
            var provider = new LlamaCppProviderPackage(new SingleHttpClientFactory(client));
            model = new CappedRecordingChatModel(provider.Create(options), maximumCalls: 5, diagnostics);
            rig = canary.Compose(model, diagnostics);

            diagnostics.Phase = "broker";
            state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);
            diagnostics.RunReturned = true;
            diagnostics.Phase = state.TerminalReason?.Kind == TaskTerminalKind.KnowledgeUnavailable ? "knowledge-admission" :
                state.Plans.Count == 0 ? "planning" :
                state.Status == AgentTaskStatus.Completed ? "finalization" : "model";

            diagnostics.CheckingAssertions = true;
            Assert.Equal(AgentTaskStatus.Completed, state.Status);
            Assert.InRange(model.CallCount, 1, 5);
            Assert.Equal(1, rig.Read.ExecutionCount);
            Assert.Equal(0, rig.Other.ExecutionCount);
            Assert.True(state.Steps[^1].Observation?.Contains(Checklist, StringComparison.Ordinal) == true, "Final checklist missing.");
            Assert.True(canary.AuditEvents.OfType<KnowledgeReadAuditEvent>().Any(read =>
                read.ResultCode == KnowledgeReadResultCode.Success && read.Operation == "read_resource" &&
                read.Skill == "nginx-triage" && read.Resource == "references/checklist.md"), "Successful Knowledge read missing.");
            Assert.True(canary.AuditEvents.OfType<ToolCallAuditEvent>().All(call => call.Tool == "test.read"), "Unexpected tool audit.");

            var firstServedKnowledge = model.Requests.FindIndex(request => request.History.Any(turn =>
                turn.Provenance?.Origin == ContextOrigin.Skill && turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true));
            Assert.True(firstServedKnowledge >= 0, "The model never received the checklist as attributed Knowledge data.");
            Assert.All(model.Requests.Take(firstServedKnowledge), request =>
            {
                Assert.False(request.SystemPrompt.Contains(Checklist, StringComparison.Ordinal), "Checklist leaked into system prompt.");
                Assert.False(request.History.Any(turn => turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true), "Checklist served without attribution.");
            });
        }
        catch (Exception ex)
        {
            diagnostics.CaptureException(ex);
            // Do not let xUnit print arbitrary provider/preflight exception messages or payloads.
            throw new Xunit.Sdk.XunitException("Live canary failed; see HOST-CANARY-DIAG output for safe diagnostic facts.");
        }
        finally
        {
            try
            {
                await diagnostics.WriteAsync(output, canary, rig, state, model?.CallCount ?? 0);
            }
            finally
            {
                canary?.Dispose();
            }
        }
    }

    [Fact]
    public async Task OfflineDiagnostics_SurviveDisposalAndExplainPlanningFailureWithoutPayloads()
    {
        const string secret = "sensitive-provider-prompt-pack-content";
        var diagnostics = new KnowledgeCanaryDiagnostics();
        string snapshot;
        using (var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA)))
        {
            var inner = new ScriptedRunnerModel([Script.Final(secret), Script.Final(secret), Script.Final(secret)]);
            var model = new CappedRecordingChatModel(inner, 5, diagnostics);
            var rig = canary.Compose(model, diagnostics);
            var state = await rig.Runner.RunAsync("offline diagnostic probe", KnowledgeRunnerWorld.ActorA);
            Assert.Equal(AgentTaskStatus.Failed, state.Status);
            diagnostics.RunReturned = true;
            snapshot = await diagnostics.SnapshotAsync(canary, rig, state, model.CallCount);
        }

        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;
        Assert.Equal("planning", root.GetProperty("Phase").GetString());
        Assert.Equal("model-provider", root.GetProperty("Category").GetString());
        Assert.Equal("ModelFailure", root.GetProperty("TerminalCause").GetString());
        Assert.Equal("Failed", root.GetProperty("StoreStatus").GetString());
        Assert.Equal(2, root.GetProperty("ActualLlmCalls").GetInt32());
        Assert.Equal("MalformedResponse", root.GetProperty("FailureKind").GetString());
        Assert.NotEmpty(root.GetProperty("Exceptions").EnumerateArray());
        Assert.Contains("The plan reply did not contain a JSON object", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("offline diagnostic probe", snapshot, StringComparison.Ordinal);
        Assert.Equal("<unrecognized-tool>", KnowledgeCanaryDiagnostics.SafeTool(secret));

        // Transport exception chains keep the useful codes, never messages or provider details.
        diagnostics.CaptureException(new ModelProtocolException(secret, new HttpRequestException(secret, null, System.Net.HttpStatusCode.Unauthorized))
        {
            FailureKind = ModelFailureKind.Unknown,
            ProviderStatusCode = 401,
        });
        var transport = await diagnostics.SnapshotAsync(null, null, null, 0);
        Assert.Contains("401", transport, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, transport, StringComparison.Ordinal);
    }

    /// <summary>Visible skip by default: only an operator can send traffic to the local Qwen endpoint.</summary>
    private sealed class LocalQwenKnowledgeCanaryFactAttribute : FactAttribute
    {
        public LocalQwenKnowledgeCanaryFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(RunSwitch), "1", StringComparison.Ordinal))
            {
                Skip = $"Set {RunSwitch}=1 to run this one local-Qwen knowledge canary.";
            }
        }
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DiagnosticHttpHandler(KnowledgeCanaryDiagnostics diagnostics, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            diagnostics.HttpRequests++;
            var response = await base.SendAsync(request, cancellationToken);
            diagnostics.RecordHttpStatus((int)response.StatusCode);
            return response;
        }
    }

    /// <summary>Counts every actual provider invocation and refuses a sixth before it can leave this process.</summary>
    private sealed class CappedRecordingChatModel(IChatModel inner, int maximumCalls, KnowledgeCanaryDiagnostics diagnostics) : IChatModel
    {
        public ChatModelDescriptor Descriptor => inner.Descriptor;

        public int CallCount => Requests.Count;

        public List<ModelRequest> Requests { get; } = [];

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (Requests.Count >= maximumCalls)
            {
                throw new InvalidOperationException($"Live canary call cap ({maximumCalls}) reached; no further LLM request was sent.");
            }

            Requests.Add(request);
            diagnostics.Phase = request.AvailableTools.Count == 0 ? "planning" : "model";
            try
            {
                var response = await inner.CompleteAsync(request, ct);
                diagnostics.RecordResponse(response);
                return response;
            }
            catch (Exception ex)
            {
                diagnostics.CaptureException(ex);
                throw;
            }
        }
    }

    /// <summary>Pins the local llama.cpp profile and resolves its credential through the production services.</summary>
    private static class LocalQwenConfiguration
    {
        private const string SettingsPath = "src/core/bOps.Api/settings.json";

        internal static ChatModelOptions Read()
        {
            var fullPath = FindSettingsPath();
            if (fullPath is null)
            {
                throw new InvalidOperationException("Local llama.cpp settings were not found. Run from this repository's test project.");
            }

            var apiDirectory = Path.GetDirectoryName(fullPath)!;
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(apiDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables();
            var settings = new SettingsStore(fullPath, TimeProvider.System);
            var profile = settings.FindProviderProfile("LlamaCpp")
                ?? throw new InvalidOperationException("The local LlamaCpp profile is missing.");
            var provider = profile.ProviderId;
            var baseUrl = profile.BaseUrl;
            var model = profile.Model;
            var nativeTools = profile.SupportsNativeToolCalling;
            if (!string.Equals(provider, "LlamaCpp", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) ||
                !model.StartsWith("Qwen", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || !IsPrivateOrLoopback(endpoint.Host))
            {
                throw new InvalidOperationException("The committed LlamaCpp profile is not a local Qwen endpoint.");
            }

            var secrets = new EnvironmentSecretProvider();
            var masterReference = configuration.GetSection("Vault:MasterKeySecret").Get<SecretReference>();
            var masterSecret = masterReference is null ? null : secrets.GetSecret(masterReference);
            if (masterReference is not null && (string.IsNullOrEmpty(masterSecret) || masterSecret.Length < 20))
                throw new InvalidOperationException("The configured Vault master key is unavailable or too short.");

            // The master key unlocks the store; only the resolved provider API key reaches the adapter.
            using var vault = masterSecret is null ? null : new VaultStore(
                Path.GetFullPath(configuration["Vault:FilePath"] ?? "vault.dat", apiDirectory),
                VaultCipher.DeriveKey(masterSecret), TimeProvider.System);
            var apiKey = new ProviderCredentialResolver(configuration, secrets, vault).Resolve(provider);
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("The local LlamaCpp provider credential is unavailable.");

            return new ChatModelOptions(provider, baseUrl, null, model, nativeTools)
            {
                ResolvedApiKey = apiKey,
                RequestTimeout = TimeSpan.FromSeconds(120),
            };
        }

        private static string? FindSettingsPath()
        {
            for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, SettingsPath);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        internal static async Task PreflightAsync(string baseUrl)
        {
            var endpoint = new Uri(baseUrl, UriKind.Absolute);
            using var socket = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
        }

        private static bool IsPrivateOrLoopback(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (!System.Net.IPAddress.TryParse(host, out var address)) return false;
            var bytes = address.GetAddressBytes();
            return System.Net.IPAddress.IsLoopback(address) ||
                (bytes.Length == 4 && (bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168)));
        }
    }
}

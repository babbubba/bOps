// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    internal const string Goal = "Plan the existing read-only test.read step to complete the task. "
        + "Knowledge is an auxiliary capability available during execution, not a plannable operational step: "
        + "do not put runtime.knowledge_read in expectedTool or put Knowledge arguments on test.read. "
        + "During execution, read the local signed knowledge resource nginx-triage/references/checklist.md through "
        + "runtime.knowledge_read before completing test.read, then answer with the exact checklist command. "
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
            Assert.True(canary.AuditEvents.OfType<ToolCallAuditEvent>().All(call => call.Tool == "test.read"), "Unexpected tool audit.");
            AssertKnowledgeEvidence(canary, state, model);
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

    private static void AssertKnowledgeEvidence(KnowledgeHostCanary canary, TaskState state, CappedRecordingChatModel model)
    {
        var binding = Assert.IsType<TaskKnowledgeBinding>(state.KnowledgeBinding);
        Assert.Equal("tenant-a", binding.TenantId);
        Assert.Equal(KnowledgeRunnerWorld.ActorA, binding.AuthorizedBy);
        Assert.Equal("acme.nginx-runbooks", binding.PackId);
        Assert.Equal("1.0.0", binding.Version);
        Assert.Equal("acme", binding.Publisher);
        Assert.Equal("acme-2026", binding.KeyId);
        var contentHash = KnowledgePackFormat.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(Checklist));
        Assert.Equal(contentHash, binding.ResourceHashes["nginx-triage/references/checklist.md"]);
        Assert.True(canary.SourceOpens > 0);
        var admission = Assert.Single(canary.AuditEvents.OfType<KnowledgePackAdmissionAuditEvent>(), evt =>
            evt.Code == KnowledgeAdmissionCode.Admitted && evt.ContentHash == binding.ManifestHash);
        Assert.False(string.IsNullOrWhiteSpace(admission.ProviderId));
        Assert.Contains(canary.AuditEvents.OfType<KnowledgeReadAuditEvent>(), read =>
            read.ResultCode == KnowledgeReadResultCode.Success && read.Operation == "read_resource" &&
            read.Skill == "nginx-triage" && read.Resource == "references/checklist.md" &&
            read.PackId == binding.PackId && read.PackVersion == binding.Version &&
            read.PackHash == binding.ManifestHash && read.TenantId == binding.TenantId &&
            read.ProviderId == admission.ProviderId && read.ContentHash == contentHash && read.ReturnedLength >= Checklist.Length);

        var firstServedKnowledge = model.Requests.FindIndex(request => request.History.Any(turn =>
            turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true &&
            turn.Provenance is { Origin: ContextOrigin.Skill, Derived: false } provenance &&
            provenance.Operation == "read_resource" && provenance.Item == "nginx-triage/references/checklist.md" &&
            provenance.PackId == binding.PackId && provenance.PackVersion == binding.Version &&
            provenance.PackHash == binding.ManifestHash && provenance.TenantId == binding.TenantId &&
            provenance.AdmittedProviderId == admission.ProviderId && provenance.ContentHash == contentHash));
        Assert.True(firstServedKnowledge > 0, "The model never received the checklist as attributed Knowledge data.");
        // Check each command independently and all request fields, including tool schemas and call arguments.
        foreach (var command in new[] { "nginx -t", "ss -ltnp :80" })
        {
            Assert.DoesNotContain(command, Goal, StringComparison.Ordinal);
            Assert.All(model.Requests, request => Assert.DoesNotContain(command, request.SystemPrompt, StringComparison.Ordinal));
            Assert.All(model.Requests.Take(firstServedKnowledge), request =>
                Assert.DoesNotContain(command, JsonSerializer.Serialize(request), StringComparison.Ordinal));
        }

        var final = model.Responses[^1];
        Assert.True(final.IsFinal);
        Assert.Empty(final.ToolCalls);
        Assert.Equal("stop", final.Details?.FinishReason);
        Assert.Contains(Checklist, final.TextResponse, StringComparison.Ordinal);
        Assert.Equal(final.TextResponse, state.Steps[^1].Observation);
    }

    [Fact]
    public async Task OfflineKnowledgeAssertions_RejectIndividualCommandLeaksAndMissingFinalChecklist()
    {
        var diagnostics = new KnowledgeCanaryDiagnostics();
        using var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
        var final = Script.Final(Checklist) with { Details = new ModelCallDetails("fake-model", "stop", null, null) };
        var model = new CappedRecordingChatModel(new ScriptedRunnerModel([
            Script.Plan("test.read"), Script.Knowledge("k1", "read_resource", "nginx-triage", "references/checklist.md"),
            Script.Tool("t1", "test.read"), final]), 5, diagnostics);
        var rig = canary.Compose(model, diagnostics);
        var state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);
        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        AssertKnowledgeEvidence(canary, state, model);

        var original = model.Requests[0];
        foreach (var command in new[] { "nginx -t", "ss -ltnp :80" })
        {
            model.Requests[0] = original with { SystemPrompt = original.SystemPrompt + command };
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertKnowledgeEvidence(canary, state, model));
            model.Requests[0] = original with { History = [.. original.History, ChatTurn.FromUser(command)] };
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertKnowledgeEvidence(canary, state, model));
        }
        model.Requests[0] = original;
        model.Responses[^1] = final with { TextResponse = "done" };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertKnowledgeEvidence(canary, state, model));
    }

    [Fact]
    public async Task OfflineDiagnostics_SurviveDisposalAndExplainPlanningFailureWithoutPayloads()
    {
        const string secret = "sensitive-provider-prompt-pack-content";
        var invalidPlan = new JsonObject
        {
            ["rationale"] = secret,
            ["steps"] = new JsonArray(new JsonObject
            {
                ["description"] = secret,
                ["expectedTool"] = "test.read",
                ["expectedArguments"] = new JsonObject
                {
                    ["path"] = secret,
                    [secret] = new JsonObject { [secret] = secret },
                    ["operation"] = 42,
                    ["resource"] = new JsonArray(secret),
                    ["skill"] = null,
                },
            }),
        }.ToJsonString();
        var diagnostics = new KnowledgeCanaryDiagnostics();
        string snapshot;
        using (var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA)))
        {
            var inner = new ScriptedRunnerModel([Script.Final(invalidPlan), Script.Final(invalidPlan)]);
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
        Assert.Contains("undeclared-parameter", snapshot, StringComparison.Ordinal);
        Assert.Contains("has invalid expectedArguments", snapshot, StringComparison.Ordinal);
        var shapes = root.GetProperty("Replies")[0].GetProperty("PlanArguments")[0];
        Assert.Equal("test.read", shapes.GetProperty("Tool").GetString());
        var arguments = shapes.GetProperty("Arguments");
        Assert.Equal("path", arguments[0].GetProperty("Key").GetString());
        Assert.Equal("String", arguments[0].GetProperty("Type").GetString());
        Assert.Equal("<unrecognized-argument>", arguments[1].GetProperty("Key").GetString());
        Assert.Equal("Object", arguments[1].GetProperty("Type").GetString());
        Assert.Equal("Number", arguments[2].GetProperty("Type").GetString());
        Assert.Equal("Array", arguments[3].GetProperty("Type").GetString());
        Assert.Equal("Null", arguments[4].GetProperty("Type").GetString());
        Assert.Empty(root.GetProperty("KnowledgeReads").EnumerateArray());
        Assert.Equal(0, root.GetProperty("Plans").GetInt32());
        Assert.Equal(0, root.GetProperty("Executed").GetProperty("Read").GetInt32());
        Assert.DoesNotContain(secret, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("offline diagnostic probe", snapshot, StringComparison.Ordinal);
        Assert.Equal("<unrecognized-tool>", KnowledgeCanaryDiagnostics.SafeTool(secret));

        diagnostics.RecordResponse(new ModelResponse(null, [], false, new ModelUsage(1, 4096, null))
        {
            Details = new ModelCallDetails(secret, "length", secret, secret),
        });
        diagnostics.RecordResponse(new ModelResponse(null, [], false, null)
        {
            Details = new ModelCallDetails(secret, secret, secret, secret),
        });

        // Transport exception chains keep the useful codes, never messages or provider details.
        diagnostics.CaptureException(new ModelProtocolException(secret, new HttpRequestException(secret, null, System.Net.HttpStatusCode.Unauthorized))
        {
            FailureKind = ModelFailureKind.Unknown,
            ProviderStatusCode = 401,
        });
        var transport = await diagnostics.SnapshotAsync(null, null, null, 0);
        Assert.Contains("401", transport, StringComparison.Ordinal);
        Assert.Contains("\"FinishReason\":\"length\"", transport, StringComparison.Ordinal);
        using var transportDocument = JsonDocument.Parse(transport);
        var transportReplies = transportDocument.RootElement.GetProperty("Replies");
        Assert.Equal("<unrecognized-finish-reason>",
            transportReplies[transportReplies.GetArrayLength() - 1].GetProperty("FinishReason").GetString());
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

        public List<ModelResponse> Responses { get; } = [];

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
                Responses.Add(response);
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
    internal static class LocalQwenConfiguration
    {
        private const string ConfigurationPath = "src/core/bOps.Api/appsettings.json";

        internal static ChatModelOptions Read()
        {
            var explicitPath = Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_CONFIG");
            var fullPath = explicitPath ?? FindConfigurationPath();
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new InvalidOperationException("Local configuration absent: set BOPS_KNOW_BENCH_CONFIG to the existing appsettings.json.");
            return Read(fullPath, new EnvironmentSecretProvider(), includeEnvironment: true);
        }

        // Tests inject throw-away secrets and never consult the operator's environment.
        internal static ChatModelOptions Read(string fullPath, ISecretProvider secrets, bool includeEnvironment = false)
        {
            var failure = "Local appsettings.json configuration absent or invalid.";
            try
            {
                fullPath = Path.GetFullPath(fullPath);
                if (!File.Exists(fullPath)) throw new InvalidOperationException();
                var apiDirectory = Path.GetDirectoryName(fullPath)!;
                using var configuration = new ConfigurationManager();
                configuration.SetBasePath(apiDirectory)
                    .AddJsonFile(fullPath, optional: false, reloadOnChange: false);
                if (includeEnvironment) configuration.AddEnvironmentVariables();
                failure = "Local settings.json provider configuration absent or invalid.";
                var settingsPath = Path.GetFullPath(configuration["Settings:FilePath"] ?? "settings.json", apiDirectory);
                if (!File.Exists(settingsPath)) throw new InvalidOperationException();
                var settings = new SettingsStore(settingsPath, TimeProvider.System);
                var profile = settings.FindProviderProfile("LlamaCpp")
                    ?? throw new InvalidOperationException("The local LlamaCpp profile is missing.");
                var provider = profile.ProviderId;
                var baseUrl = profile.BaseUrl;
                var model = profile.Model;
                var nativeTools = profile.SupportsNativeToolCalling;
                failure = "The local LlamaCpp profile is not a local Qwen HTTP endpoint.";
                if (!string.Equals(provider, "LlamaCpp", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) ||
                    !model.StartsWith("Qwen", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
                    !IsPrivateOrLoopback(endpoint.Host))
                {
                    throw new InvalidOperationException("The committed LlamaCpp profile is not a local Qwen endpoint.");
                }

                failure = "Configured Vault master key unavailable or invalid.";
                var masterReference = configuration.GetSection("Vault:MasterKeySecret").Get<SecretReference>();
                if (configuration.GetSection("Vault").Exists() && masterReference is null)
                    throw new InvalidOperationException();
                var masterSecret = masterReference is null ? null : secrets.GetSecret(masterReference);
                if (masterReference is not null && (string.IsNullOrEmpty(masterSecret) || masterSecret.Length < 20))
                    throw new InvalidOperationException("The configured Vault master key is unavailable or too short.");

                // The master key unlocks the store; only the resolved provider API key reaches the adapter.
                failure = "Configured Vault unavailable or invalid.";
                var vaultPath = Path.GetFullPath(configuration["Vault:FilePath"] ?? "vault.dat", apiDirectory);
                if (masterReference is not null && !File.Exists(vaultPath)) throw new InvalidOperationException();
                using var vault = masterSecret is null ? null : new VaultStore(
                    vaultPath,
                    VaultCipher.DeriveKey(masterSecret), TimeProvider.System);
                if (vault is not null) _ = vault.Version;
                failure = "Local LlamaCpp provider credential unavailable or unresolvable.";
                var apiKey = new ProviderCredentialResolver(configuration, secrets, vault).Resolve(provider);
                if (string.IsNullOrEmpty(apiKey))
                    throw new InvalidOperationException("The local LlamaCpp provider credential is unavailable.");

                return new ChatModelOptions(provider, baseUrl, null, model, nativeTools)
                {
                    ResolvedApiKey = apiKey,
                    RequestTimeout = TimeSpan.FromSeconds(120),
                };
            }
            catch (Exception)
            {
                // No inner exception: parsing, filesystem and crypto errors may contain private data.
                throw new InvalidOperationException(failure);
            }
        }

        private static string? FindConfigurationPath()
        {
            for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, ConfigurationPath);
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

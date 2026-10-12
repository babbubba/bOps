// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// HOST-CANARY-01B: one explicitly enabled, local-only proof that the governed host can give Qwen
/// signed Knowledge data.  This is deliberately a test-project composition, never a product host.
/// </summary>
public sealed class KnowledgeHostCanaryLiveTests
{
    private const string RunSwitch = "BOPS_RUN_LIVE_QWEN_KNOWLEDGE_CANARY";
    private const string Checklist = "nginx -t; ss -ltnp :80";
    private const string Goal = "Read the local signed knowledge resource nginx-triage/references/checklist.md through "
        + "runtime.knowledge_read. Then use the existing read-only test step to complete the plan and answer with the exact checklist command. "
        + "Do not guess the command; it is not in this request.";

    [LocalQwenKnowledgeCanaryFact]
    public async Task Qwen_ReadsTheSignedLocalPackThroughTheGovernedKnowledgePath()
    {
        var options = LocalQwenConfiguration.Read();
        await LocalQwenConfiguration.PreflightAsync(options.BaseUrl);

        using var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
        using var client = new HttpClient();
        var provider = new LlamaCppProviderPackage(new SingleHttpClientFactory(client));
        var model = new CappedRecordingChatModel(provider.Create(options), maximumCalls: 5);
        var rig = canary.Compose(model);

        var state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.InRange(model.CallCount, 1, 5);
        Assert.Equal(1, rig.Read.ExecutionCount);
        Assert.Equal(0, rig.Other.ExecutionCount);
        Assert.Contains(Checklist, state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Contains(canary.AuditEvents.OfType<KnowledgeReadAuditEvent>(), read =>
            read.ResultCode == KnowledgeReadResultCode.Success && read.Operation == "read_resource" &&
            read.Skill == "nginx-triage" && read.Resource == "references/checklist.md");
        Assert.DoesNotContain(canary.AuditEvents.OfType<ToolCallAuditEvent>(), call => call.Tool != "test.read");

        var firstServedKnowledge = model.Requests.FindIndex(request => request.History.Any(turn =>
            turn.Provenance?.Origin == ContextOrigin.Skill && turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true));
        Assert.True(firstServedKnowledge >= 0, "The model never received the checklist as attributed Knowledge data.");
        Assert.All(model.Requests.Take(firstServedKnowledge), request =>
        {
            Assert.DoesNotContain(Checklist, request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(request.History, turn => turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true);
        });
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

    /// <summary>Counts every actual provider invocation and refuses a sixth before it can leave this process.</summary>
    private sealed class CappedRecordingChatModel(IChatModel inner, int maximumCalls) : IChatModel
    {
        public ChatModelDescriptor Descriptor => inner.Descriptor;

        public int CallCount => Requests.Count;

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (Requests.Count >= maximumCalls)
            {
                throw new InvalidOperationException($"Live canary call cap ({maximumCalls}) reached; no further LLM request was sent.");
            }

            Requests.Add(request);
            return inner.CompleteAsync(request, ct);
        }
    }

    /// <summary>Reads only the committed, local llama.cpp profile; environment/provider fallbacks are intentionally ignored.</summary>
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

            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            var profile = document.RootElement.GetProperty("Providers").GetProperty("LlamaCpp");
            var provider = profile.GetProperty("ProviderId").GetString();
            var baseUrl = profile.GetProperty("BaseUrl").GetString();
            var model = profile.GetProperty("Model").GetString();
            var nativeTools = profile.GetProperty("SupportsNativeToolCalling").GetBoolean();
            if (!string.Equals(provider, "LlamaCpp", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) ||
                !model.StartsWith("Qwen", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || !IsPrivateOrLoopback(endpoint.Host))
            {
                throw new InvalidOperationException("The committed LlamaCpp profile is not a local Qwen endpoint.");
            }

            return new ChatModelOptions(provider!, baseUrl!, null, model!, nativeTools)
            {
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

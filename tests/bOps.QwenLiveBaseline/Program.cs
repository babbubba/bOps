// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Audit;
using bOps.Hosting;
using bOps.Memory;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Packages.Web;
using bOps.Policy;
using bOps.QwenLiveBaseline;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// QWEN-LIVE-BASELINE-01. One process = one independent run of one scenario against the REAL model.
//   real:     Qwen over the production OpenAI-compatible adapter, ModelInvocationBroker + MafContextProjector + AgentRunner (the bOps.Cli
//             composition), real planning/replanning/recovery/grounding, real policy engine, real audit sink, real task store, real Knowledge host.
//   replayed: only the OUTPUT of the tools. The tool surface (manifests) is the production one; the model chooses tools and arguments freely and each
//             call is answered from a captured fixture selected by the call actually made (Replay.cs), or is UNSUPPORTED_REPLAY. Nothing is executed
//             on the machine.
HostCulture.PinInvariant();
return await Harness.RunAsync(args);

namespace bOps.QwenLiveBaseline
{
    internal sealed record Options(
        string Scenario, int Run, string Root, string Docs, string Config, int MaxCalls, double MaxMinutes, bool Dry)
    {
        public static Options Parse(string[] args)
        {
            string Get(string name, string? fallback = null)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException($"missing {name}");
            }

            return new Options(
                Get("--scenario"), int.Parse(Get("--run"), CultureInfo.InvariantCulture), Get("--root"), Get("--docs"), Get("--config"),
                int.Parse(Get("--max-calls", "40"), CultureInfo.InvariantCulture),
                double.Parse(Get("--max-minutes", "25"), CultureInfo.InvariantCulture),
                args.Contains("--dry"));
        }
    }

    /// <summary>Saves every HTTP request body and response body byte for byte (never headers, so never a credential) and enforces the call cap.</summary>
    internal sealed class RecordingHandler(string directory, int maxCalls, bool dry) : DelegatingHandler(new HttpClientHandler())
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public bool CapReached { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref count);
            if (n > maxCalls)
            {
                Interlocked.Decrement(ref count);
                CapReached = true;
                throw new HttpRequestException($"QWEN-LIVE-BASELINE-01 LLM call cap ({maxCalls}) reached; no request was sent.");
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{n:D3}.request.json"), body, new UTF8Encoding(false), cancellationToken);
            var startedAt = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            if (dry)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"{n:D3}.meta.json"), "{\"dry\":true}", cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("dry run: no request sent") };
            }

            string? failure = null;
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(directory, $"{n:D3}.response.json"), await response.Content.ReadAsStringAsync(cancellationToken),
                    new UTF8Encoding(false), cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                throw;
            }
            finally
            {
                var meta = new JsonObject
                {
                    ["index"] = n,
                    ["startedAtUtc"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["elapsedMs"] = watch.ElapsedMilliseconds,
                    ["status"] = response is null ? null : (int)response.StatusCode,
                    ["transportFailure"] = failure,
                    ["requestSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
                };
                await File.WriteAllTextAsync(Path.Combine(directory, $"{n:D3}.meta.json"), meta.ToJsonString(), CancellationToken.None);
            }
        }
    }

    /// <summary>The prerequisite states of the earlier campaign, so the same tools are visible; no probe touches the machine.</summary>
    internal sealed class CapturedPrerequisites(IReadOnlyDictionary<string, PrerequisiteState> states) : IPrerequisiteStateSource
    {
        public PrerequisiteState GetState(string prerequisiteId) =>
            states.TryGetValue(prerequisiteId, out var state) ? state : PrerequisiteState.Unknown;
    }

    internal sealed class RefusingApprovalProvider : IApprovalProvider
    {
        public int Requests { get; private set; }

        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult(new ApprovalDecision(false, new ActorIdentity("harness", "qwen-live-baseline", null), "campaign is read-only"));
        }
    }

    internal static class Harness
    {
        public static async Task<int> RunAsync(string[] args)
        {
            Options o;
            try
            {
                o = Options.Parse(args);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync("usage: --scenario T08 --run 1 --root <campaign root> --docs <benchmark docs dir> --config <appsettings.json> [--max-calls 40] [--max-minutes 25] [--dry]: " + ex.Message);
                return 2;
            }

            var runDir = Path.Combine(o.Root, "runs", $"{o.Scenario}-r{o.Run}{(o.Dry ? "-dry" : string.Empty)}");
            if (Directory.Exists(runDir) && Directory.EnumerateFileSystemEntries(runDir).Any())
            {
                await Console.Error.WriteLineAsync($"refusing to reuse a non-empty run directory (isolation): {runDir}");
                return 3;
            }

            var httpDir = Path.Combine(runDir, "http");
            Directory.CreateDirectory(httpDir);
            Directory.SetCurrentDirectory(runDir);

            var scenarios = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(o.Docs, "scenarios.json"), Encoding.UTF8))!["scenarios"]!;
            var goal = scenarios[o.Scenario]!["goal"]!.GetValue<string>();
            var fixtures = FixtureStore.Load(Path.Combine(o.Docs, "fixtures"));
            var shared = Path.Combine(o.Root, "shared");
            var prerequisites = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(shared, "prerequisites.json")))!.AsObject()
                .ToDictionary(p => p.Key, p => Enum.Parse<PrerequisiteState>(p.Value!.GetValue<string>()), StringComparer.OrdinalIgnoreCase);

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(o.Config, optional: false)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Every per-run state lives under the run directory; nothing of the earlier campaigns can be overwritten.
                    ["Audit:FilePath"] = Path.Combine(runDir, "audit.jsonl"),
                    ["Memory:FilePath"] = Path.Combine(runDir, "tasks.db"),
                    ["SystemMessages:FilePath"] = Path.Combine(runDir, "system-messages.db"),
                    ["Knowledge:PacksDirectory"] = Path.Combine(shared, "packs"),
                    ["Knowledge:TrustStorePath"] = Path.Combine(shared, "publisher-trust.json"),
                    ["Knowledge:SnapshotsDirectory"] = Path.Combine(runDir, "snapshots"),
                    ["Policy:FilePath"] = Path.Combine(shared, "policy.yaml"),
                })
                .Build();

            using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true).SetMinimumLevel(LogLevel.Information));
            var logger = loggerFactory.CreateLogger<AgentRunner>();

            var configured = configuration.GetSection("ModelProvider").Get<ChatModelOptions>() ?? throw new InvalidOperationException("ModelProvider missing");
            var secrets = new EnvironmentSecretProvider();
            var modelOptions = new ChatModelOptions(configured.Provider, configured.BaseUrl, configured.ApiKeySecret, configured.Model, configured.SupportsNativeToolCalling)
            {
                ResolvedApiKey = configured.ApiKeySecret is null ? null : secrets.GetSecret(configured.ApiKeySecret),
                RequestTimeout = configured.RequestTimeout,
            };
            var runnerOptions = configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();
            runnerOptions.Validate(modelOptions.EffectiveRequestTimeout);

            using var handler = new RecordingHandler(httpDir, o.MaxCalls, o.Dry);
            using var client = new HttpClient(handler, disposeHandler: false);
            var model = new LlamaCppProviderPackage(new SingleClientFactory(client)).Create(modelOptions);

            // --- tools: the production first-party surface with replayed execution
            var broker = new ReplayBroker(fixtures, o.Scenario);
            var stateSource = new CapturedPrerequisites(prerequisites);
            var registry = new ToolRegistry(stateSource);
            var filesystem = configuration.GetSection("Filesystem");
            var webFetch = configuration.GetSection("Web:Fetch").Get<WebFetchOptions>() ?? new WebFetchOptions();
            var webSearch = configuration.GetSection("Web:Search").Get<WebSearchOptions>() ?? new WebSearchOptions();
            using var webProvider = new WebToolProvider(webFetch, webSearch);
            var registrations = FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
                new FilesystemToolProvider(
                    new FilesystemPathPolicy(filesystem.GetSection("ReadPatterns").Get<string[]>() ?? [], filesystem.GetSection("WritePatterns").Get<string[]>() ?? []),
                    filesystem.GetSection("Inventory").Get<FilesystemInventoryOptions>() ?? new FilesystemInventoryOptions(),
                    filesystem.GetSection("Operations").Get<FilesystemOperationsOptions>() ?? new FilesystemOperationsOptions()),
                webProvider,
                new DockerClientFactory(configuration["Docker:Endpoint"]),
                configuration.GetSection("Docker:Build").Get<DockerBuildOptions>() ?? new DockerBuildOptions(),
                configuration.GetSection("Docker:Volumes").Get<DockerVolumeOptions>() ?? new DockerVolumeOptions()));
            foreach (var registration in registrations)
            {
                ITool wrapped = registration.Tool is IVerifiableTool
                    ? new ReplayVerifiableTool(registration.Tool, broker)
                    : new ReplayTool(registration.Tool, broker);
                registry.Register(registration.PackageId, wrapped);
            }

            await registry.RefreshCapabilitiesAsync();
            var visibleTools = registry.GetAvailableManifests().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            // --- policy (the captured read-only policy), audit, store, knowledge
            var policyConfig = PolicyConfigLoader.Load(await File.ReadAllTextAsync(Path.Combine(shared, "policy.yaml")));
            var audit = new JsonLinesAuditSink(Path.Combine(runDir, "audit.jsonl"));
            var store = new SqliteTaskStore(Path.Combine(runDir, "tasks.db"));
            var systemMessages = new SqliteSystemMessageStore(Path.Combine(runDir, "system-messages.db"));
            var skills = new SkillRegistry(stateSource);
            var time = TimeProvider.System;
            var knowledgeOptions = configuration.GetSection(LocalKnowledgeOptions.SectionName)
                .Get<LocalKnowledgeOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new LocalKnowledgeOptions();
            var knowledge = LocalKnowledgeComposition.Create(knowledgeOptions, audit, time);
            var approvals = new RefusingApprovalProvider();

            var invocationBroker = new ModelInvocationBroker(
                model, audit, time, logger, runnerOptions, contextProjector: new MafContextProjector(runnerOptions, logger), dedicatedSummarizer: null);
            var runner = new AgentRunner(
                model, registry, new PolicyEngine(policyConfig), approvals, audit, store, time, logger, runnerOptions, skills,
                systemMessages: systemMessages, invocationBroker: invocationBroker, knowledgeHost: knowledge);

            var started = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(o.MaxMinutes));
            TaskState? state = null;
            string? exception = null;
            var watchdogFired = false;
            try
            {
                state = await runner.RunAsync(goal, ActorIdentity.FromOperatingSystemUser("qwen-live-baseline"), null, watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                watchdogFired = watchdog.IsCancellationRequested;
                exception = "OperationCanceledException";
            }
            catch (Exception ex)
            {
                exception = ex.GetType().FullName;
            }

            watch.Stop();
            var result = new JsonObject
            {
                ["scenario"] = o.Scenario,
                ["run"] = o.Run,
                ["dry"] = o.Dry,
                ["startedAtUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
                ["elapsedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
                ["taskId"] = state?.Id.ToString(),
                ["status"] = state?.Status.ToString(),
                ["terminalReason"] = state?.TerminalReason is { } reason ? JsonSerializer.SerializeToNode(reason) : null,
                ["watchdogMinutes"] = o.MaxMinutes,
                ["watchdogFired"] = watchdogFired,
                ["llmCallCap"] = o.MaxCalls,
                ["callCapReached"] = handler.CapReached,
                ["httpRequestsSent"] = handler.Count,
                ["harnessException"] = exception,
                ["approvalRequests"] = approvals.Requests,
                ["goalSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goal))),
                ["visibleToolCount"] = visibleTools.Length,
                ["visibleTools"] = JsonSerializer.SerializeToNode(visibleTools),
                ["fixtureCount"] = fixtures.Count,
                ["replayLedger"] = JsonSerializer.SerializeToNode(broker.Ledger),
            };
            await File.WriteAllTextAsync(Path.Combine(runDir, "harness-result.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            Console.WriteLine($"{o.Scenario} r{o.Run}: status={state?.Status} elapsed={watch.Elapsed:c} http={handler.Count} replay={broker.Ledger.Count} unsupported={broker.Ledger.Count(c => c.Verdict != "SERVED")} cap={handler.CapReached} watchdog={watchdogFired}");
            return 0;
        }
    }

    internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

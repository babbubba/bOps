// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Audit;
using bOps.Cli;
using bOps.Memory;
using bOps.Hosting;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Providers.Anthropic;
using bOps.Packages.Providers.DeepSeek;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Packages.Providers.Ollama;
using bOps.Packages.Providers.OpenAi;
using bOps.Packages.Providers.OpenRouter;
using bOps.Packages.Web;
using bOps.PluginHost;
using bOps.Policy;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

const string UsageMessage = """
    Usage: bops "<goal>" | bops resume <task-id> | bops delegate "<objective>" | bops delegate status|resume|cancel <run-id> | bops delegate reconcile <run-id> --accept|--abandon | bops delegate readiness [--remediation] | bops delegate profiles init --read-only [--write [--overwrite]] | bops delegate profiles check | bops audit verify [file] | bops plugin <install|list|enable|disable|remove|validate|sign> ... | bops vault rotate-key <new-master-key-environment-variable>
    """;

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync(UsageMessage);
    return 1;
}

// ADR-0029: like "plugin" and "audit" above, "vault" is its own command family with no goal-
// execution composition — rotation is a maintenance action, deliberately CLI-only (not exposed
// through the API) to keep the highest-privilege vault operation on the surface that already
// requires direct machine access.
if (string.Equals(args[0], "vault", StringComparison.OrdinalIgnoreCase))
{
    return await RunVaultCommandAsync(args[1..]);
}

// V0.10 (ADR-0020): "plugin" is its own command family, handled entirely separately — it needs
// none of the goal-execution composition below (no model provider, no policy engine, no audit
// sink), and none of that should have to be configured correctly just to run `bops plugin list`.
if (string.Equals(args[0], "plugin", StringComparison.OrdinalIgnoreCase))
{
    return await RunPluginCommandAsync(args[1..]);
}

if (string.Equals(args[0], "audit", StringComparison.OrdinalIgnoreCase))
{
    return await RunAuditCommandAsync(args[1..]);
}

// V0.7 (ADR-0017): "resume" is the CLI's first real subcommand — everything else is still read
// as the goal, exactly as before, so `bops "<goal>"` keeps working unchanged.
string? goal = null;
Guid? resumeTaskId = null;
DelegateInvocation? delegateInvocation = null;

// V1.2 (ADR-0030 section 9): "delegate" runs an objective through the fixed Discovery, Diagnostic, Remediation and Verification
// pipeline, or reads, resumes, cancels or reconciles a stored run. It shares the composition below and adds a store, a role-profile
// source over the same loaded policy, and a console approval of the plan.
if (string.Equals(args[0], "delegate", StringComparison.OrdinalIgnoreCase))
{
    var (parsedInvocation, parseError) = DelegateArguments.Parse(args[1..]);
    if (parsedInvocation is null)
    {
        await Console.Error.WriteLineAsync(parseError);
        await Console.Error.WriteLineAsync(DelegateArguments.Usage);
        return 1;
    }

    delegateInvocation = parsedInvocation;
}
else if (string.Equals(args[0], "resume", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2 || !Guid.TryParse(args[1], out var parsedTaskId))
    {
        await Console.Error.WriteLineAsync(UsageMessage);
        return 1;
    }

    resumeTaskId = parsedTaskId;
}
else
{
    goal = string.Join(' ', args);
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
if (delegateInvocation is { NeedsNoModel: true })
{
    // ADR-0044 section 10.1: standard output carries only the generated YAML or the report, so it can be redirected to a file;
    // the host's log lines go to standard error.
    builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISecretProvider, EnvironmentSecretProvider>();

// ADR-0049: the same host-owned prerequisite registry as bOps.Api. Packages and plugins only ever receive the read-only
// ICapabilityProbe view; IPrerequisiteRegistrar is host-only. The CLI refreshes once per run (no timer): see the refresh below.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(PrerequisiteOptions.SectionName).Get<PrerequisiteOptions>()?.Validate()
    ?? new PrerequisiteOptions());
builder.Services.AddSingleton(sp => new PrerequisiteRegistry(
    sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30), sp.GetRequiredService<PrerequisiteOptions>().MaxConcurrency));
builder.Services.AddSingleton<IPrerequisiteStateSource>(sp => sp.GetRequiredService<PrerequisiteRegistry>());
builder.Services.AddSingleton<IPrerequisiteRegistrar>(sp => sp.GetRequiredService<PrerequisiteRegistry>());
builder.Services.AddSingleton<ICapabilityProbe>(sp => sp.GetRequiredService<PrerequisiteRegistry>().AsCapabilityProbe());
builder.Services.AddSingleton(sp => new ToolRegistry(sp.GetRequiredService<IPrerequisiteStateSource>()));
builder.Services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<ToolRegistry>());
builder.Services.AddSingleton(sp => new SkillRegistry(sp.GetRequiredService<IPrerequisiteStateSource>()));
builder.Services.AddSingleton<ISkillRegistry>(sp => sp.GetRequiredService<SkillRegistry>());
builder.Services.AddSingleton(sp => new SqliteSystemMessageStore(
    sp.GetRequiredService<IConfiguration>()["SystemMessages:FilePath"] ?? "system-messages.db"));
builder.Services.AddSingleton<IPrerequisiteStateStore>(sp => sp.GetRequiredService<SqliteSystemMessageStore>());
builder.Services.AddSingleton(sp => new PrerequisiteTransitionRecorder(sp.GetRequiredService<IPrerequisiteStateStore>(), NodeId.Local));
builder.Services.AddSingleton<PrerequisiteReadinessService>();
builder.Services.AddSingleton<IChatModelRegistry, ChatModelRegistry>();
builder.Services.AddSingleton<IAuditSink>(
    _ => new JsonLinesAuditSink(builder.Configuration["Audit:FilePath"] ?? "audit.jsonl"));

// Phase 1 provider packages are project-referenced and registered directly — the dynamic
// plugin loader arrives at V0.10 (agentic/06-decisions.md, D-003).
builder.Services.AddSingleton<OpenRouterProviderPackage>();
builder.Services.AddSingleton<OllamaProviderPackage>();
builder.Services.AddSingleton<LlamaCppProviderPackage>();
// V0.8: OpenAI and DeepSeek reuse the shared OpenAI-compatible adapter (ADR-0005); Anthropic is
// the one native adapter.
builder.Services.AddSingleton<OpenAiProviderPackage>();
builder.Services.AddSingleton<DeepSeekProviderPackage>();
builder.Services.AddSingleton<AnthropicProviderPackage>();

// Registered here (rather than constructed inline below, like FilesystemToolProvider) because
// WebToolProvider is IDisposable — the DI container disposes it when `host` disposes at the end
// of this process, closing the HttpClient/SocketsHttpHandler it owns.
builder.Services.AddSingleton(_ =>
    builder.Configuration.GetSection("Web:Fetch").Get<WebFetchOptions>() ?? new WebFetchOptions());
builder.Services.AddSingleton(_ =>
    builder.Configuration.GetSection("Web:Search").Get<WebSearchOptions>() ?? new WebSearchOptions());
builder.Services.AddSingleton<WebToolProvider>();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(BOpsTelemetry.ActivitySourceName).AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter(BOpsTelemetry.MeterName).AddOtlpExporter());

using var host = builder.Build();

var toolRegistry = host.Services.GetRequiredService<IToolRegistry>();
var filesystemSection = builder.Configuration.GetSection("Filesystem");
var pathPolicy = new FilesystemPathPolicy(
    filesystemSection.GetSection("ReadPatterns").Get<string[]>() ?? [],
    filesystemSection.GetSection("WritePatterns").Get<string[]>() ?? []);
var filesystemInventoryOptions = filesystemSection.GetSection("Inventory").Get<FilesystemInventoryOptions>()
    ?? new FilesystemInventoryOptions();
var filesystemOperationsOptions = filesystemSection.GetSection("Operations").Get<FilesystemOperationsOptions>()
    ?? new FilesystemOperationsOptions();

// V0.6: docker.* declares Requires: ["docker"] on every tool (rule B4) — registering the check
// here, before the first RefreshCapabilitiesAsync, is what makes an absent daemon remove every
// docker.* tool from what the planner sees instead of failing only once one is called. The
// registry itself never learns the word "docker" (rule A1); only this composition root does.
var dockerClientFactory = new DockerClientFactory(builder.Configuration["Docker:Endpoint"]);
// V1.3-B (ADR-0033): where docker.build may build from (empty by default: nothing can be built and the tool stays hidden) and
// which volume drivers docker.volume.create accepts. Both are the Docker package's own settings.
var dockerBuildOptions = builder.Configuration.GetSection("Docker:Build").Get<DockerBuildOptions>() ?? new DockerBuildOptions();
var dockerVolumeOptions = builder.Configuration.GetSection("Docker:Volumes").Get<DockerVolumeOptions>() ?? new DockerVolumeOptions();

// web.search declares Requires: ["web.searxng"] (rule A8) — an unconfigured instance removes it
// from what the planner sees, the same pattern docker.* uses for an absent daemon.
var webSearchOptions = host.Services.GetRequiredService<WebSearchOptions>();

// ADR-0049: first-party checks register on the host-owned registry; enabled plugins' providers follow at activation, below.
FirstPartyPrerequisiteComposition.Register(
    host.Services.GetRequiredService<PrerequisiteRegistry>(), dockerClientFactory, dockerBuildOptions, webSearchOptions);

var firstPartyRegistrations = FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
    new FilesystemToolProvider(pathPolicy, filesystemInventoryOptions, filesystemOperationsOptions),
    host.Services.GetRequiredService<WebToolProvider>(),
    dockerClientFactory,
    dockerBuildOptions,
    dockerVolumeOptions));
FirstPartyToolComposition.Register(toolRegistry, firstPartyRegistrations);

var chatModelRegistry = host.Services.GetRequiredService<IChatModelRegistry>();
var skillRegistry = host.Services.GetRequiredService<ISkillRegistry>();

// V0.10 (ADR-0020): every plugin the operator has already enabled (via `bops plugin enable`)
// activates on every run, exactly like a first-party package — there is no separate "plugin
// mode." Before RefreshCapabilitiesAsync, so a plugin tool's own Requires is captured too.
// ADR-0037: the same lifecycle backend as the API reconciles first, then activates; a failed startup activation is persisted.
var pluginManager = CreatePluginManager(builder.Configuration, host.Services, toolRegistry, chatModelRegistry);
var pluginLifecycle = new PluginLifecycleService(
    pluginManager, builder.Configuration["Plugins:RootPath"] ?? "plugins", auditSink: host.Services.GetRequiredService<IAuditSink>());
var pluginStartupErrors = await pluginLifecycle.ActivateEnabledAsync();
if (pluginStartupErrors.Count > 0)
{
    var pluginLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Cli.Plugins");
    foreach (var (pluginId, reason) in pluginStartupErrors)
    {
        pluginLogger.LogWarning("Plugin '{PluginId}' did not activate: {Reason}", pluginId, reason);
    }
}

// ADR-0049: the CLI's one-shot readiness refresh, with the same ordering as the API's boot refresh (checks, atomically recorded
// transitions and messages, then Tool and Skill availability). No periodic timer: a CLI run is short-lived.
await host.Services.GetRequiredService<PrerequisiteReadinessService>().RefreshAsync();
chatModelRegistry.Register(new PackageId("bops.packages.providers.openrouter"), host.Services.GetRequiredService<OpenRouterProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.ollama"), host.Services.GetRequiredService<OllamaProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.llamacpp"), host.Services.GetRequiredService<LlamaCppProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.openai"), host.Services.GetRequiredService<OpenAiProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.deepseek"), host.Services.GetRequiredService<DeepSeekProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.anthropic"), host.Services.GetRequiredService<AnthropicProviderPackage>());

// ADR-0044 section 4: the policy is loaded once per process, and the host keeps why it is what it is and where it came from.
var policyLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Cli.Policy");
var cliPolicy = await LoadPolicyAsync(builder.Configuration["Policy:FilePath"] ?? "policy.yaml", policyLogger);

// ADR-0044 sections 10–12: readiness and the profile commands read configuration only. They compose the tool registry, the
// enabled plugins, the capability probe and the policy loader above, and never create a chat model, so a missing
// 'ModelProvider' section does not affect them.
if (delegateInvocation is { NeedsNoModel: true })
{
    return await new DelegateSetupCommand(
            cliPolicy, toolRegistry.GetAvailableManifests(), host.Services.GetRequiredService<TimeProvider>(), Console.Out, Console.Error)
        .RunAsync(delegateInvocation);
}

var configuredModelOptions = builder.Configuration.GetSection("ModelProvider").Get<ChatModelOptions>()
    ?? throw new InvalidOperationException("Missing 'ModelProvider' configuration section.");
var modelOptions = new ChatModelOptions(
    configuredModelOptions.Provider,
    configuredModelOptions.BaseUrl,
    configuredModelOptions.ApiKeySecret,
    configuredModelOptions.Model,
    configuredModelOptions.SupportsNativeToolCalling)
{
    ResolvedApiKey = configuredModelOptions.ApiKeySecret is null
        ? null
        : host.Services.GetRequiredService<ISecretProvider>().GetSecret(configuredModelOptions.ApiKeySecret),
    RequestTimeout = configuredModelOptions.RequestTimeout,
};

var model = chatModelRegistry.Create(modelOptions);
var runnerOptions = builder.Configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();
// ADR-0039: the runtime's model-call attempt timeout must fire before the provider's outer transport timeout.
runnerOptions.Validate(modelOptions.EffectiveRequestTimeout);

var policyEngine = new PolicyEngine(cliPolicy.Config);
var policyConfig = cliPolicy.Config;
var approvalProvider = new ConsoleApprovalProvider();

// V0.7 (ADR-0017): a plain SQLite file next to the audit log — every task is persisted as it
// runs, so `bops resume <task-id>` can continue a task that failed, was cancelled or ran out of a
// per-attempt budget, under the ADR-0040 resumability rule (never a task still stored Running).
var taskStore = new SqliteTaskStore(builder.Configuration["Memory:FilePath"] ?? "tasks.db");

var runner = new AgentRunner(
    model,
    toolRegistry,
    policyEngine,
    approvalProvider,
    host.Services.GetRequiredService<IAuditSink>(),
    taskStore,
    host.Services.GetRequiredService<TimeProvider>(),
    host.Services.GetRequiredService<ILogger<AgentRunner>>(),
    runnerOptions,
    skillRegistry);

var actor = ActorIdentity.FromOperatingSystemUser(Environment.UserName);

if (delegateInvocation is not null)
{
    // The role profiles come from the same loaded policy the engine does, so a policy.yaml that failed to load (AllForbidden) has
    // none and a start ends Denied on the Profile dimension, before any model call.
    var delegationStore = new SqliteDelegationStore(builder.Configuration["Delegation:FilePath"] ?? "delegations.db");
    var delegationRunner = new DelegationRunner(
        runner,
        new PolicyRoleProfileSource(policyConfig),
        new ConsolePlanApprovalProvider(Console.In, Console.Out, actor, delegationStore),
        host.Services.GetRequiredService<IAuditSink>(),
        host.Services.GetRequiredService<TimeProvider>(),
        host.Services.GetRequiredService<ILogger<DelegationRunner>>(),
        delegationStore);

    // Ctrl+C cancels the run under way, which then ends as Cancelled and is reported like any other end.
    using var interrupt = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        interrupt.Cancel();
    };

    return await new DelegateCommand(delegationRunner, delegationStore, skillRegistry, actor, Console.Out, Console.Error)
        .RunAsync(delegateInvocation, interrupt.Token);
}

TaskState result;
if (resumeTaskId is { } taskIdToResume)
{
    var existing = await taskStore.LoadAsync(taskIdToResume);
    if (existing is null)
    {
        await Console.Error.WriteLineAsync($"No stored task with id '{taskIdToResume}'.");
        return 1;
    }

    // ADR-0040: the same resumability rule and atomic transition as the API; a refusal changes nothing.
    try
    {
        result = await runner.ResumeAsync(existing, actor);
    }
    catch (TaskResumeRefusedException refused)
    {
        await Console.Error.WriteLineAsync($"Task {taskIdToResume} cannot be resumed ({refused.Refusal.Code}): {refused.Refusal.Message}");
        return 1;
    }
}
else
{
    result = await runner.RunAsync(goal!, actor);
}

PrintTranscript(result);
return result.Status == AgentTaskStatus.Completed ? 0 : 1;

static void PrintTranscript(TaskState task)
{
    Console.WriteLine($"Task {task.Id} — {task.Status}");
    foreach (var step in task.Steps)
    {
        Console.WriteLine($"[{step.Index}] {step.Description}");
        if (step.Observation is not null)
        {
            Console.WriteLine(step.Observation);
        }
    }
}

// Rule S3: a missing policy.yaml is not the same as a broken one. No file at all is a normal,
// unconfigured starting point — the built-in safe default applies. A file that exists but fails
// to load means the operator tried to configure something and got it wrong; falling back to the
// safe default there could silently be *more* permissive than what they thought they had
// configured, so everything above Read is forbidden instead, until the file is fixed.
static async Task<CliPolicy> LoadPolicyAsync(string filePath, ILogger logger)
{
    // ADR-0044 section 4: the absolute resolved path and the load state are logged, never the file's contents.
    var path = Path.GetFullPath(filePath);
    if (!File.Exists(path))
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Policy file '{Path}' ({State}): no file; using the built-in default (Read/Low automatic, Medium/High approval, Critical forbidden).",
                path, PolicyLoadState.NoFile);
        }

        return new CliPolicy(PolicyConfig.SafeDefault, PolicyLoadState.NoFile, path, null);
    }

    try
    {
        var yaml = await File.ReadAllTextAsync(path);
        var config = PolicyConfigLoader.Load(yaml);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Policy file '{Path}' ({State}).", path, PolicyLoadState.Loaded);
        }

        return new CliPolicy(config, PolicyLoadState.Loaded, path, null);
    }
    catch (PolicyConfigurationException ex)
    {
        logger.LogError(ex, "Policy file '{Path}' ({State}): it could not be loaded; every tool above Read is forbidden until it is fixed.", path, PolicyLoadState.LoadFailed);
        return new CliPolicy(PolicyConfig.AllForbidden, PolicyLoadState.LoadFailed, path, ex.Message);
    }
}

// V0.10 (ADR-0020): builds the same restricted-container ingredients (rule A10) whichever path
// constructs a PluginManager — the goal-execution composition above and the plugin command
// below share this instead of assembling it twice, differently.
static PluginManager CreatePluginManager(
    IConfiguration configuration, IServiceProvider services, IToolRegistry toolRegistry, IChatModelRegistry chatModelRegistry) =>
    new(
        new PluginStore(configuration["Plugins:StorePath"] ?? "plugins.json"),
        toolRegistry,
        services.GetRequiredService<ISkillRegistry>(),
        chatModelRegistry,
        configuration["Plugins:RootPath"] ?? "plugins",
        configuration,
        services.GetRequiredService<ILoggerFactory>(),
        services.GetRequiredService<IHttpClientFactory>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ICapabilityProbe>(),
        services.GetService<IPrerequisiteRegistrar>());

static async Task<int> RunPluginCommandAsync(string[] pluginArgs)
{
    if (pluginArgs.Length == 0)
    {
        await Console.Error.WriteLineAsync(PluginCommand.Usage);
        return 1;
    }

    var pluginBuilder = Host.CreateApplicationBuilder();
    pluginBuilder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    pluginBuilder.Services.AddHttpClient();
    pluginBuilder.Services.AddSingleton(TimeProvider.System);
    // The plugin-management commands activate nothing for execution, so they register no prerequisite providers (no registrar).
    pluginBuilder.Services.AddSingleton<ICapabilityProbe>(services =>
        new PrerequisiteRegistry(services.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30)).AsCapabilityProbe());
    pluginBuilder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
    pluginBuilder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();
    pluginBuilder.Services.AddSingleton<IChatModelRegistry, ChatModelRegistry>();

    using var pluginHost = pluginBuilder.Build();

    var manager = CreatePluginManager(
        pluginBuilder.Configuration,
        pluginHost.Services,
        pluginHost.Services.GetRequiredService<IToolRegistry>(),
        pluginHost.Services.GetRequiredService<IChatModelRegistry>());

    // ADR-0037: every lifecycle mutation goes through the one backend the API also uses; PluginManager stays its low-level loader.
    using var pluginAudit = new JsonLinesAuditSink(pluginBuilder.Configuration["Audit:FilePath"] ?? "audit.jsonl");
    var lifecycle = new PluginLifecycleService(manager, pluginBuilder.Configuration["Plugins:RootPath"] ?? "plugins", auditSink: pluginAudit);
    var command = new PluginCommand(
        lifecycle, manager, pluginBuilder.Configuration["Plugins:TrustStorePath"] ?? "publisher-trust.json",
        Console.Out, Console.Error, ActorIdentity.FromOperatingSystemUser(Environment.UserName));
    return await command.RunAsync(pluginArgs);
}

static async Task<int> RunVaultCommandAsync(string[] vaultArgs)
{
    const string VaultUsageMessage = """
        Usage: bops vault rotate-key <new-master-key-environment-variable>
        """;

    if (vaultArgs.Length != 2 || !string.Equals(vaultArgs[0], "rotate-key", StringComparison.OrdinalIgnoreCase))
    {
        await Console.Error.WriteLineAsync(VaultUsageMessage);
        return 1;
    }

    var vaultBuilder = Host.CreateApplicationBuilder();
    var masterKeySecretSection = vaultBuilder.Configuration.GetSection("Vault:MasterKeySecret");
    if (!masterKeySecretSection.Exists())
    {
        await Console.Error.WriteLineAsync("'Vault:MasterKeySecret' is not configured; there is nothing to rotate.");
        return 1;
    }

    var reference = masterKeySecretSection.Get<SecretReference>()
        ?? throw new InvalidOperationException("'Vault:MasterKeySecret' is configured but has no 'Provider'/'Name'.");
    var environmentSecretProvider = new EnvironmentSecretProvider();
    var currentMasterSecret = environmentSecretProvider.GetSecret(reference);
    if (string.IsNullOrEmpty(currentMasterSecret))
    {
        await Console.Error.WriteLineAsync($"'Vault:MasterKeySecret' ({reference.Provider}/{reference.Name}) did not resolve to a value.");
        return 1;
    }

    var newMasterSecret = Environment.GetEnvironmentVariable(vaultArgs[1]);
    if (string.IsNullOrEmpty(newMasterSecret))
    {
        await Console.Error.WriteLineAsync($"Environment variable '{vaultArgs[1]}' is not set.");
        return 1;
    }

    var vaultFilePath = vaultBuilder.Configuration["Vault:FilePath"] ?? "vault.dat";
    try
    {
        using var oldStore = new VaultStore(vaultFilePath, VaultCipher.DeriveKey(currentMasterSecret), TimeProvider.System);
        var plaintextByProviderId = oldStore.List().ToDictionary(entry => entry.ProviderId, entry => oldStore.Resolve(entry.ProviderId)!);

        using var newStore = new VaultStore(vaultFilePath, VaultCipher.DeriveKey(newMasterSecret), TimeProvider.System);
        foreach (var (providerId, plaintext) in plaintextByProviderId)
        {
            newStore.Set(providerId, plaintext, newStore.Version);
        }

        Console.WriteLine($"Rotated the master key for {plaintextByProviderId.Count} stored provider key(s) in '{vaultFilePath}'.");
        return 0;
    }
    catch (VaultCorruptedException ex)
    {
        await Console.Error.WriteLineAsync($"Rotation failed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunAuditCommandAsync(string[] auditArgs)
{
    if (auditArgs.Length is < 1 or > 2 || !string.Equals(auditArgs[0], "verify", StringComparison.OrdinalIgnoreCase))
    {
        await Console.Error.WriteLineAsync("Usage: bops audit verify [file]");
        return 1;
    }

    var filePath = auditArgs.Length == 2 ? auditArgs[1] : "audit.jsonl";
    var result = AuditChainVerifier.VerifyFile(filePath);
    if (result.IsValid)
    {
        Console.WriteLine($"Audit chain '{filePath}' is valid.");
        return 0;
    }

    await Console.Error.WriteLineAsync(
        $"Audit chain '{filePath}' is invalid" +
        (result.BrokenAtSequence is { } sequence ? $" at sequence {sequence}" : string.Empty) +
        $": {result.Reason}");
    return 2;
}

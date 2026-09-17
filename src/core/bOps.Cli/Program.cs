// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Audit;
using bOps.Cli;
using bOps.Memory;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Network;
using bOps.Packages.Providers.Anthropic;
using bOps.Packages.Providers.DeepSeek;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Packages.Providers.Ollama;
using bOps.Packages.Providers.OpenAi;
using bOps.Packages.Providers.OpenRouter;
using bOps.Packages.Service.Linux;
using bOps.Packages.Service.Windows;
using bOps.Packages.Sys.Linux;
using bOps.Packages.Sys.Windows;
using bOps.Packages.Web;
using bOps.PluginHost;
using bOps.Policy;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

const string UsageMessage = """
    Usage: bops "<goal>" | bops resume <task-id> | bops audit verify [file] | bops plugin <install|list|enable|disable|remove|validate|sign> ... | bops vault rotate-key <new-master-key-environment-variable>
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

if (string.Equals(args[0], "resume", StringComparison.OrdinalIgnoreCase))
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

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISecretProvider, EnvironmentSecretProvider>();
builder.Services.AddSingleton<ICapabilityProbe>(services =>
    new CachingCapabilityProbe(services.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();
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

// Rule A8: each OS package contributes its own complete tools; the registry's platform filter
// (not this code) is what actually decides visibility — this just picks which package to load.
// OperatingSystem.IsWindows()/IsLinux() (not CurrentPlatform.Id) because the platform-compat
// analyzer (CA1416) only recognizes these specific guards for a [SupportedOSPlatform] type.
IToolProvider platformToolProvider = OperatingSystem.IsWindows()
    ? new WindowsSystemToolProvider()
    : OperatingSystem.IsLinux()
        ? new LinuxSystemToolProvider()
        : throw new PlatformNotSupportedException(
            "bOps supports Windows and Linux only (agentic/00-project-spec.md).");

var systemPackageId = new PackageId($"bops.packages.system.{CurrentPlatform.Id}");
foreach (var tool in platformToolProvider.GetTools())
{
    toolRegistry.Register(systemPackageId, tool);
}

// V0.5: fs.* and network.* are cross-platform via System.IO / System.Net.NetworkInformation, so
// unlike the System family there is no per-OS package to pick between (agentic/01-architecture-
// rules.md, rule A8 does not require an OS split when the BCL already abstracts the difference).
// fs.write and fs.delete are the first non-Read tools this repository ships for real — everything
// V0.3 (policy/approval) and V0.4 (verification) built now has a real tool to exercise it.
var filesystemSection = builder.Configuration.GetSection("Filesystem");
var pathPolicy = new FilesystemPathPolicy(
    filesystemSection.GetSection("ReadPatterns").Get<string[]>() ?? [],
    filesystemSection.GetSection("WritePatterns").Get<string[]>() ?? []);
var filesystemInventoryOptions = filesystemSection.GetSection("Inventory").Get<FilesystemInventoryOptions>()
    ?? new FilesystemInventoryOptions();

var filesystemPackageId = new PackageId("bops.packages.filesystem");
foreach (var tool in new FilesystemToolProvider(pathPolicy, filesystemInventoryOptions).GetTools())
{
    toolRegistry.Register(filesystemPackageId, tool);
}

var networkPackageId = new PackageId("bops.packages.network");
foreach (var tool in new NetworkToolProvider().GetTools())
{
    toolRegistry.Register(networkPackageId, tool);
}

// V0.11 (ADR-0021): mirrors the System family's own OS split above — Windows via
// ServiceController, Linux via a fixed, non-composable systemctl invocation.
IToolProvider serviceToolProvider = OperatingSystem.IsWindows()
    ? new WindowsServiceToolProvider()
    : OperatingSystem.IsLinux()
        ? new LinuxServiceToolProvider()
        : throw new PlatformNotSupportedException(
            "bOps supports Windows and Linux only (agentic/00-project-spec.md).");

var servicePackageId = new PackageId($"bops.packages.service.{CurrentPlatform.Id}");
foreach (var tool in serviceToolProvider.GetTools())
{
    toolRegistry.Register(servicePackageId, tool);
}

// V0.6: docker.* declares Requires: ["docker"] on every tool (rule B4) — registering the check
// here, before the first RefreshCapabilitiesAsync, is what makes an absent daemon remove every
// docker.* tool from what the planner sees instead of failing only once one is called. The
// registry itself never learns the word "docker" (rule A1); only this composition root does.
var dockerClientFactory = new DockerClientFactory(builder.Configuration["Docker:Endpoint"]);
if (host.Services.GetRequiredService<ICapabilityProbe>() is CachingCapabilityProbe cachingCapabilityProbe)
{
    cachingCapabilityProbe.RegisterCheck(DockerCapability.Name, ct => DockerCapability.IsAvailableAsync(dockerClientFactory, ct));
}

var dockerPackageId = new PackageId("bops.packages.docker");
foreach (var tool in new DockerToolProvider(dockerClientFactory).GetTools())
{
    toolRegistry.Register(dockerPackageId, tool);
}

// web.search declares Requires: ["web.searxng"] (rule A8) — an unconfigured instance removes it
// from what the planner sees, the same pattern docker.* uses for an absent daemon.
var webSearchOptions = host.Services.GetRequiredService<WebSearchOptions>();
if (host.Services.GetRequiredService<ICapabilityProbe>() is CachingCapabilityProbe webCapabilityProbe)
{
    webCapabilityProbe.RegisterCheck(WebCapabilities.Searxng, ct => WebCapabilities.IsSearxngConfiguredAsync(webSearchOptions, ct));
}

var webPackageId = new PackageId("bops.packages.web");
foreach (var tool in host.Services.GetRequiredService<WebToolProvider>().GetTools())
{
    toolRegistry.Register(webPackageId, tool);
}

var chatModelRegistry = host.Services.GetRequiredService<IChatModelRegistry>();
var skillRegistry = host.Services.GetRequiredService<ISkillRegistry>();

// V0.10 (ADR-0020): every plugin the operator has already enabled (via `bops plugin enable`)
// activates on every run, exactly like a first-party package — there is no separate "plugin
// mode." Before RefreshCapabilitiesAsync, so a plugin tool's own Requires is captured too.
var pluginManager = CreatePluginManager(builder.Configuration, host.Services, toolRegistry, chatModelRegistry);
var pluginStartupErrors = pluginManager.LoadAllEnabled();
if (pluginStartupErrors.Count > 0)
{
    var pluginLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Cli.Plugins");
    foreach (var (pluginId, reason) in pluginStartupErrors)
    {
        pluginLogger.LogWarning("Plugin '{PluginId}' did not activate: {Reason}", pluginId, reason);
    }
}

await toolRegistry.RefreshCapabilitiesAsync();
chatModelRegistry.Register(new PackageId("bops.packages.providers.openrouter"), host.Services.GetRequiredService<OpenRouterProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.ollama"), host.Services.GetRequiredService<OllamaProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.llamacpp"), host.Services.GetRequiredService<LlamaCppProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.openai"), host.Services.GetRequiredService<OpenAiProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.deepseek"), host.Services.GetRequiredService<DeepSeekProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.anthropic"), host.Services.GetRequiredService<AnthropicProviderPackage>());

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
};

var model = chatModelRegistry.Create(modelOptions);
var runnerOptions = builder.Configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();

var policyLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Cli.Policy");
var policyEngine = await LoadPolicyEngineAsync(builder.Configuration["Policy:FilePath"] ?? "policy.yaml", policyLogger);
var approvalProvider = new ConsoleApprovalProvider();

// V0.7 (ADR-0017): a plain SQLite file next to the audit log — every task is persisted as it
// runs, so `bops resume <task-id>` can pick a `Running` task back up after a crash, a restart,
// or an operator's own interruption.
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

TaskState result;
if (resumeTaskId is { } taskIdToResume)
{
    var existing = await taskStore.LoadAsync(taskIdToResume);
    if (existing is null)
    {
        await Console.Error.WriteLineAsync($"No stored task with id '{taskIdToResume}'.");
        return 1;
    }

    result = await runner.ResumeAsync(existing, actor);
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
static async Task<IPolicyEngine> LoadPolicyEngineAsync(string filePath, ILogger logger)
{
    if (!File.Exists(filePath))
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "No policy file at '{Path}'; using the built-in default (Read/Low automatic, Medium/High approval, Critical forbidden).",
                filePath);
        }

        return new PolicyEngine(PolicyConfig.SafeDefault);
    }

    try
    {
        var yaml = await File.ReadAllTextAsync(filePath);
        var config = PolicyConfigLoader.Load(yaml);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Loaded policy from '{Path}'.", filePath);
        }

        return new PolicyEngine(config);
    }
    catch (PolicyConfigurationException ex)
    {
        logger.LogError(ex, "'{Path}' could not be loaded; every tool above Read is forbidden until it is fixed.", filePath);
        return new PolicyEngine(PolicyConfig.AllForbidden);
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
        services.GetRequiredService<ICapabilityProbe>());

static async Task<int> RunPluginCommandAsync(string[] pluginArgs)
{
    const string PluginUsageMessage = """
        Usage: bops plugin install <directory>
               bops plugin list
               bops plugin enable <id>
               bops plugin disable <id>
               bops plugin remove <id>
               bops plugin validate <directory>
               bops plugin sign <directory> <publisher> <key-id> <private-key-pem-file>
        """;

    if (pluginArgs.Length == 0)
    {
        await Console.Error.WriteLineAsync(PluginUsageMessage);
        return 1;
    }

    var pluginBuilder = Host.CreateApplicationBuilder();
    pluginBuilder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    pluginBuilder.Services.AddHttpClient();
    pluginBuilder.Services.AddSingleton(TimeProvider.System);
    pluginBuilder.Services.AddSingleton<ICapabilityProbe>(services =>
        new CachingCapabilityProbe(services.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30)));
    pluginBuilder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
    pluginBuilder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();
    pluginBuilder.Services.AddSingleton<IChatModelRegistry, ChatModelRegistry>();

    using var pluginHost = pluginBuilder.Build();

    var manager = CreatePluginManager(
        pluginBuilder.Configuration,
        pluginHost.Services,
        pluginHost.Services.GetRequiredService<IToolRegistry>(),
        pluginHost.Services.GetRequiredService<IChatModelRegistry>());

    var command = pluginArgs[0];
    var rest = pluginArgs[1..];

    try
    {
        switch (command.ToLowerInvariant())
        {
            case "install" when rest.Length == 1:
                var installed = manager.Install(rest[0]);
                var provenance = installed.Provenance?.Verified == true
                    ? $"verified ({installed.Provenance.Publisher}/{installed.Provenance.KeyId}, {installed.Provenance.Trust})"
                    : $"unverified ({installed.Provenance?.FailureReason ?? "no provenance"})";
                Console.WriteLine($"Installed '{installed.Id}' v{installed.Manifest.Version} (disabled, {provenance}).");
                return 0;

            case "list":
                foreach (var record in manager.List())
                {
                    Console.WriteLine($"{record.Id}\t{(record.Enabled ? "enabled" : "disabled")}\tv{record.Manifest.Version}\t{record.Manifest.Publisher}\t{record.Provenance?.Trust ?? PackageTrustLevel.Unverified}");
                }

                return 0;

            case "enable" when rest.Length == 1:
                manager.Enable(rest[0]);
                Console.WriteLine($"Enabled '{rest[0]}'.");
                return 0;

            case "disable" when rest.Length == 1:
                manager.Disable(rest[0]);
                Console.WriteLine($"Disabled '{rest[0]}'.");
                return 0;

            case "remove" when rest.Length == 1:
                manager.Remove(rest[0]);
                Console.WriteLine($"Removed '{rest[0]}'.");
                return 0;

            case "validate" when rest.Length == 1:
                var manifest = PluginManifestValidator.ReadManifest(rest[0]);
                PluginManifestValidator.Validate(manifest, rest[0]);
                var verified = PluginPackageSignature.Verify(
                    rest[0], manifest,
                    new PluginPublisherTrustStore(pluginBuilder.Configuration["Plugins:TrustStorePath"] ?? "publisher-trust.json"));
                Console.WriteLine($"'{rest[0]}' is a valid manifest for '{manifest.Id}' v{manifest.Version}; provenance: " +
                    $"{(verified.Verified ? $"verified ({verified.Trust})" : $"unverified ({verified.FailureReason})")}.");
                return 0;

            case "sign" when rest.Length == 4:
                var privateKeyPem = await File.ReadAllTextAsync(rest[3]);
                PluginPackageSignature.Sign(rest[0], rest[1], rest[2], privateKeyPem);
                Console.WriteLine($"Signed plugin package '{rest[0]}' as publisher '{rest[1]}' with key '{rest[2]}'.");
                return 0;

            default:
                await Console.Error.WriteLineAsync(PluginUsageMessage);
                return 1;
        }
    }
    catch (PluginValidationException ex)
    {
        await Console.Error.WriteLineAsync($"Validation failed: {ex.Message}");
        return 1;
    }
    catch (PluginOperationException ex)
    {
        await Console.Error.WriteLineAsync($"Operation failed: {ex.Message}");
        return 1;
    }
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

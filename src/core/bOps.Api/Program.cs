// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Threading.RateLimiting;
using bOps.Abstractions;
using bOps.Api;
using bOps.Audit;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISecretProvider, EnvironmentSecretProvider>();
builder.Services.Configure<ApiAuthenticationOptions>(builder.Configuration.GetSection("Authentication"));
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ApiAuthorization.ViewerPolicy, policy => policy.RequireRole(ApiAuthorization.ViewerRole));
    options.AddPolicy(ApiAuthorization.OperatorPolicy, policy => policy.RequireRole(ApiAuthorization.OperatorRole));
    options.AddPolicy(ApiAuthorization.ApproverPolicy, policy => policy.RequireRole(ApiAuthorization.ApproverRole));
    options.AddPolicy(ApiAuthorization.AdministratorPolicy, policy => policy.RequireRole(ApiAuthorization.AdministratorRole));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Two buckets per caller. Mutating requests (POST/PUT/DELETE…) and anything unauthenticated keep the strict
    // bucket. Authenticated safe reads (GET/HEAD) get their own, larger one: the UI legitimately polls several
    // read-only endpoints (task, task list, approvals, delegations) and must not starve — or be starved by — the
    // writes that matter. Reads stay bounded per caller, and the authentication scheme runs before this middleware.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var isRead = userId is not null
            && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method));
        var key = userId ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return isRead
            ? RateLimitPartition.GetTokenBucketLimiter($"read:{key}", _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 600,
                TokensPerPeriod = 300,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            })
            : RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 120,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
});
builder.Services.AddSingleton<ICapabilityProbe>(services =>
    new CachingCapabilityProbe(services.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();
builder.Services.AddSingleton<IChatModelRegistry, ChatModelRegistry>();
builder.Services.AddSingleton<IAuditSink>(
    _ => new JsonLinesAuditSink(builder.Configuration["Audit:FilePath"] ?? "audit.jsonl"));

// V0.7 (ADR-0017): the same durable task store the CLI's `bops resume` uses — a task started
// through the API is exactly as resumable as one started from the console.
builder.Services.AddSingleton<ITaskStore>(
    _ => new SqliteTaskStore(builder.Configuration["Memory:FilePath"] ?? "tasks.db"));

// Phase 1 provider packages are project-referenced and registered directly — the dynamic plugin
// loader arrives at V0.10 (agentic/06-decisions.md, D-003) — same composition as bOps.Cli.
builder.Services.AddSingleton<OpenRouterProviderPackage>();
builder.Services.AddSingleton<OllamaProviderPackage>();
builder.Services.AddSingleton<LlamaCppProviderPackage>();
builder.Services.AddSingleton<OpenAiProviderPackage>();
builder.Services.AddSingleton<DeepSeekProviderPackage>();
builder.Services.AddSingleton<AnthropicProviderPackage>();

// V0.9 (ADR-0018): the HTTP channel's IApprovalProvider — an approval queue, not a blocking
// console prompt. Registered under its concrete type too, so ApprovalsEndpoints can call
// ListPending()/TryRespond(), which are not part of the IApprovalProvider contract.
builder.Services.AddSingleton<ApiApprovalProvider>();
builder.Services.AddSingleton<IApprovalProvider>(sp => sp.GetRequiredService<ApiApprovalProvider>());

// The policy file is read once: the engine and the delegation role profiles (V1.2, ADR-0030) come from the same PolicyConfig, so a
// policy.yaml that failed to load leaves every tool above Read forbidden and gives no role a profile, and a delegation started
// against it ends Denied on the Profile dimension before any model call.
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Api.Policy");
    return LoadPolicy(builder.Configuration["Policy:FilePath"] ?? "policy.yaml", logger);
});
builder.Services.AddSingleton<IPolicyEngine>(sp => sp.GetRequiredService<LoadedPolicy>().Engine);
builder.Services.AddSingleton<IRoleProfileSource>(sp => new PolicyRoleProfileSource(sp.GetRequiredService<LoadedPolicy>().Config));

// Resolved lazily, on the first request that needs it — by then every provider package below has
// already registered with IChatModelRegistry. Lazy resolution also means a test can replace this
// registration outright (ConfigureTestServices) without ever needing a real 'ModelProvider'
// configuration section or a live provider.
builder.Services.AddSingleton<IChatModel>(sp =>
{
    var modelOptions = ProviderResolution.ResolveEffectiveModelOptions(
        builder.Configuration,
        sp.GetRequiredService<SettingsStore>(),
        sp.GetRequiredService<ISecretProvider>(),
        sp.GetService<VaultSecretProvider>());
    return sp.GetRequiredService<IChatModelRegistry>().Create(modelOptions);
});

builder.Services.AddSingleton(sp =>
{
    var runnerOptions = builder.Configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();
    return new AgentRunner(
        sp.GetRequiredService<IChatModel>(),
        sp.GetRequiredService<IToolRegistry>(),
        sp.GetRequiredService<IPolicyEngine>(),
        sp.GetRequiredService<IApprovalProvider>(),
        sp.GetRequiredService<IAuditSink>(),
        sp.GetRequiredService<ITaskStore>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<AgentRunner>>(),
        runnerOptions,
        sp.GetRequiredService<ISkillRegistry>());
});
builder.Services.AddSingleton<AgentTaskLauncher>();

// V1.2 (ADR-0030 section 9): delegated runs. The store is one more SQLite file next to the task store; the plan approval is a queue
// answered by a separate request from an approver, beside ApiApprovalProvider (which still answers the approval of each step).
builder.Services.AddSingleton<IDelegationStore>(
    sp => new SqliteDelegationStore(sp.GetRequiredService<IConfiguration>()["Delegation:FilePath"] ?? "delegations.db"));
builder.Services.AddSingleton<ApiPlanApprovalProvider>();
builder.Services.AddSingleton<IPlanApprovalProvider>(sp => sp.GetRequiredService<ApiPlanApprovalProvider>());
builder.Services.AddSingleton(sp => new DelegationRunner(
    sp.GetRequiredService<AgentRunner>(),
    sp.GetRequiredService<IRoleProfileSource>(),
    sp.GetRequiredService<IPlanApprovalProvider>(),
    sp.GetRequiredService<IAuditSink>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<DelegationRunner>>(),
    sp.GetRequiredService<IDelegationStore>()));
builder.Services.AddSingleton<DelegationLauncher>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Api:Tasks").Get<AgentTaskLauncherOptions>() ?? new AgentTaskLauncherOptions());
builder.Services.AddSingleton<TaskIdempotencyStore>();

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Filesystem:Inventory").Get<FilesystemInventoryOptions>()
        ?? new FilesystemInventoryOptions());
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Filesystem:Operations").Get<FilesystemOperationsOptions>()
        ?? new FilesystemOperationsOptions());
builder.Services.AddSingleton(sp =>
{
    var section = sp.GetRequiredService<IConfiguration>().GetSection("Filesystem");
    return new FilesystemPathPolicy(
        section.GetSection("ReadPatterns").Get<string[]>() ?? [],
        section.GetSection("WritePatterns").Get<string[]>() ?? []);
});
builder.Services.AddSingleton<FilesystemToolProvider>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<FilesystemToolProvider>().DeletionService);

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Web:Fetch").Get<WebFetchOptions>() ?? new WebFetchOptions());
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Web:Search").Get<WebSearchOptions>() ?? new WebSearchOptions());
builder.Services.AddSingleton<WebToolProvider>();

// V0.10 (ADR-0020): same composition as bOps.Cli's CreatePluginManager — a PluginStore over the
// configured store/root paths, wired to this process's own IToolRegistry/ISkillRegistry/
// IChatModelRegistry (rule A4: each host is its own node with its own in-memory registries).
builder.Services.AddSingleton(sp => new PluginManager(
    new PluginStore(sp.GetRequiredService<IConfiguration>()["Plugins:StorePath"] ?? "plugins.json"),
    sp.GetRequiredService<IToolRegistry>(),
    sp.GetRequiredService<ISkillRegistry>(),
    sp.GetRequiredService<IChatModelRegistry>(),
    sp.GetRequiredService<IConfiguration>()["Plugins:RootPath"] ?? "plugins",
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ICapabilityProbe>()));
// ADR-0037: M6 consumes this same backend; it does not compose a second plugin authority.
// V1.3-M6: the archive limits are configuration (ADR-0037: defaults, with hard ceilings enforced by the backend). The HTTP upload
// bound is derived from the same compressed-archive limit, so the two can never be configured apart.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Plugins:Archive").Get<PluginArchiveLimits>() ?? new PluginArchiveLimits());
builder.Services.AddSingleton(sp => PluginUploadOptions.For(sp.GetRequiredService<PluginArchiveLimits>()));
builder.Services.AddSingleton(sp => new PluginLifecycleService(
    sp.GetRequiredService<PluginManager>(),
    sp.GetRequiredService<IConfiguration>()["Plugins:RootPath"] ?? "plugins",
    sp.GetRequiredService<PluginArchiveLimits>(),
    sp.GetRequiredService<IAuditSink>()));

// ADR-0029: non-secret provider profiles and the active-provider selection are always available;
// the encrypted vault (provider API keys) only activates when a master key is actually configured
// — an unconfigured 'Vault' section means the Settings feature's key-management surface is simply
// absent, not silently running with protection disabled.
//
// Both factories below read configuration through the DI-resolved IConfiguration, not the
// pre-Build() 'builder.Configuration' reference — under WebApplicationFactory (bOps.Api.Tests),
// a test's ConfigureAppConfiguration override is only guaranteed applied by the time Build()
// returns, not while these top-level statements are still running. Registered as 'VaultStore?'
// so the factory can return null (vault absent) without a nullable-return warning; nullable
// annotations on a reference type are erased at runtime, so sp.GetService<VaultStore>() resolves
// the same registration.
builder.Services.AddSingleton(sp =>
    new SettingsStore(sp.GetRequiredService<IConfiguration>()["Settings:FilePath"] ?? "settings.json", sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var masterKeySecretSection = configuration.GetSection("Vault:MasterKeySecret");
    if (!masterKeySecretSection.Exists())
    {
        // Registered as non-nullable VaultStore so sp.GetService<VaultStore>() resolves it
        // normally; null here (vault simply absent) is intentional, not an oversight.
        return null!;
    }

    var reference = masterKeySecretSection.Get<SecretReference>()
        ?? throw new InvalidOperationException("'Vault:MasterKeySecret' is configured but has no 'Provider'/'Name'.");
    var masterSecret = sp.GetRequiredService<ISecretProvider>().GetSecret(reference);
    if (string.IsNullOrEmpty(masterSecret))
    {
        throw new InvalidOperationException(
            $"'Vault:MasterKeySecret' ({reference.Provider}/{reference.Name}) did not resolve to a value. " +
            "Refusing to start with the vault silently unprotected — fix the master key or remove 'Vault' from configuration.");
    }

    if (masterSecret.Length < 20)
    {
        throw new InvalidOperationException(
            "The vault master key is shorter than 20 characters, which is not enough entropy to protect stored secrets.");
    }

    return new VaultStore(
        configuration["Vault:FilePath"] ?? "vault.dat", VaultCipher.DeriveKey(masterSecret), sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton(sp =>
    sp.GetService<VaultStore>() is { } store ? new VaultSecretProvider(store) : null!);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(BOpsTelemetry.ActivitySourceName).AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter(BOpsTelemetry.MeterName).AddOtlpExporter());

var app = builder.Build();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var toolRegistry = app.Services.GetRequiredService<IToolRegistry>();

// V0.6: docker.* declares Requires: ["docker"] on every tool (rule B4) — registering the check
// here, before the first RefreshCapabilitiesAsync, is what makes an absent daemon remove every
// docker.* tool from what the planner sees instead of failing only once one is called.
var dockerClientFactory = new DockerClientFactory(builder.Configuration["Docker:Endpoint"]);
// V1.3-B (ADR-0033): where docker.build may build from (empty by default: nothing can be built and the tool stays hidden) and
// which volume drivers docker.volume.create accepts. Both are the Docker package's own settings.
var dockerBuildOptions = builder.Configuration.GetSection("Docker:Build").Get<DockerBuildOptions>() ?? new DockerBuildOptions();
var dockerVolumeOptions = builder.Configuration.GetSection("Docker:Volumes").Get<DockerVolumeOptions>() ?? new DockerVolumeOptions();
if (app.Services.GetRequiredService<ICapabilityProbe>() is CachingCapabilityProbe cachingCapabilityProbe)
{
    cachingCapabilityProbe.RegisterCheck(DockerCapability.Name, ct => DockerCapability.IsAvailableAsync(dockerClientFactory, ct));
    cachingCapabilityProbe.RegisterCheck(DockerCapability.BuildContexts, _ => DockerCapability.IsBuildConfiguredAsync(dockerBuildOptions));
}

// web.search declares Requires: ["web.searxng"] (rule A8) — an unconfigured instance removes it
// from what the planner sees, the same pattern docker.* uses for an absent daemon.
var webSearchOptions = app.Services.GetRequiredService<WebSearchOptions>();
if (app.Services.GetRequiredService<ICapabilityProbe>() is CachingCapabilityProbe webCapabilityProbe)
{
    webCapabilityProbe.RegisterCheck(WebCapabilities.Searxng, ct => WebCapabilities.IsSearxngConfiguredAsync(webSearchOptions, ct));
}

var firstPartyRegistrations = FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
    app.Services.GetRequiredService<FilesystemToolProvider>(),
    app.Services.GetRequiredService<WebToolProvider>(),
    dockerClientFactory,
    dockerBuildOptions,
    dockerVolumeOptions));
FirstPartyToolComposition.Register(toolRegistry, firstPartyRegistrations);

var chatModelRegistry = app.Services.GetRequiredService<IChatModelRegistry>();

// V0.10 (ADR-0020): every plugin the operator has already enabled (via `bops plugin enable`, the
// only management path today — V1.1-F's catalog is read-only) activates here too, exactly like
// bOps.Cli already does — this host runs its own AgentRunner (AgentsEndpoints/AgentTaskLauncher)
// and needs the same plugin-contributed tools/Skills visible to it. Before
// RefreshCapabilitiesAsync, so a plugin tool's own Requires is captured by the same refresh.
// ADR-0037: reconcile first, then activate enabled plugins; a failed startup activation is persisted as ActivationFailed.
var pluginStartupErrors = await app.Services.GetRequiredService<PluginLifecycleService>().ActivateEnabledAsync();
if (pluginStartupErrors.Count > 0)
{
    var pluginLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Api.Plugins");
    foreach (var (pluginId, reason) in pluginStartupErrors)
    {
        pluginLogger.LogWarning("Plugin '{PluginId}' did not activate: {Reason}", pluginId, reason);
    }
}

await toolRegistry.RefreshCapabilitiesAsync();

chatModelRegistry.Register(new PackageId("bops.packages.providers.openrouter"), app.Services.GetRequiredService<OpenRouterProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.ollama"), app.Services.GetRequiredService<OllamaProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.llamacpp"), app.Services.GetRequiredService<LlamaCppProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.openai"), app.Services.GetRequiredService<OpenAiProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.deepseek"), app.Services.GetRequiredService<DeepSeekProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.anthropic"), app.Services.GetRequiredService<AnthropicProviderPackage>());

app.MapAgentsEndpoints();
app.MapApprovalsEndpoints();
app.MapDelegationsEndpoints();
app.MapToolsEndpoints();
app.MapProvidersEndpoints();
app.MapIdentityEndpoints();
app.MapFilesystemDeletionEndpoints();
app.MapPluginCatalogEndpoints();
app.MapPluginLifecycleEndpoints();
if (app.Services.GetService<VaultStore>() is not null)
{
    app.MapSettingsEndpoints();
}

await app.RunAsync();

// Rule S3: a missing policy.yaml is not the same as a broken one — see bOps.Cli/Program.cs for
// the identical async version; this one is synchronous because it runs inside a synchronous DI
// factory delegate (agentic/02-coding-standards.md forbids blocking on async here, so this uses
// genuinely synchronous file I/O rather than blocking on the async version).
static LoadedPolicy LoadPolicy(string filePath, ILogger logger)
{
    if (!File.Exists(filePath))
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "No policy file at '{Path}'; using the built-in default (Read/Low automatic, Medium/High approval, Critical forbidden).",
                filePath);
        }

        return new LoadedPolicy(new PolicyEngine(PolicyConfig.SafeDefault), PolicyConfig.SafeDefault);
    }

    try
    {
        var yaml = File.ReadAllText(filePath);
        var config = PolicyConfigLoader.Load(yaml);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Loaded policy from '{Path}'.", filePath);
        }

        return new LoadedPolicy(new PolicyEngine(config), config);
    }
    catch (PolicyConfigurationException ex)
    {
        logger.LogError(ex, "'{Path}' could not be loaded; every tool above Read is forbidden until it is fixed.", filePath);
        return new LoadedPolicy(new PolicyEngine(PolicyConfig.AllForbidden), PolicyConfig.AllForbidden);
    }
}

/// <summary>The policy the host loaded, as the engine that evaluates it and the config it came from.</summary>
internal sealed record LoadedPolicy(IPolicyEngine Engine, PolicyConfig Config);

/// <summary>Marker partial class so <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> can target this top-level-statements entry point. Internal, like every other type in this application (CA1515) — visible to the test project via <c>InternalsVisibleTo</c>.</summary>
internal partial class Program;

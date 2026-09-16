// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Api;
using bOps.Audit;
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
using bOps.Packages.Sys.Linux;
using bOps.Packages.Sys.Windows;
using bOps.Policy;
using bOps.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Security.Claims;
using System.Threading.RateLimiting;

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
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetTokenBucketLimiter(
            http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 120,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
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

builder.Services.AddSingleton<IPolicyEngine>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Api.Policy");
    return LoadPolicyEngine(builder.Configuration["Policy:FilePath"] ?? "policy.yaml", logger);
});

// Resolved lazily, on the first request that needs it — by then every provider package below has
// already registered with IChatModelRegistry. Lazy resolution also means a test can replace this
// registration outright (ConfigureTestServices) without ever needing a real 'ModelProvider'
// configuration section or a live provider.
builder.Services.AddSingleton<IChatModel>(sp =>
{
    var configured = builder.Configuration.GetSection("ModelProvider").Get<ChatModelOptions>()
        ?? throw new InvalidOperationException("Missing 'ModelProvider' configuration section.");
    var modelOptions = ResolveModelSecret(configured, sp.GetRequiredService<ISecretProvider>());
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
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Api:Tasks").Get<AgentTaskLauncherOptions>() ?? new AgentTaskLauncherOptions());
builder.Services.AddSingleton<TaskIdempotencyStore>();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(BOpsTelemetry.ActivitySourceName).AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter(BOpsTelemetry.MeterName).AddOtlpExporter());

var app = builder.Build();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var toolRegistry = app.Services.GetRequiredService<IToolRegistry>();

// Rule A8: each OS package contributes its own complete tools; the registry's platform filter
// (not this code) is what actually decides visibility — this just picks which package to load.
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

var filesystemSection = builder.Configuration.GetSection("Filesystem");
var pathPolicy = new FilesystemPathPolicy(
    filesystemSection.GetSection("ReadPatterns").Get<string[]>() ?? [],
    filesystemSection.GetSection("WritePatterns").Get<string[]>() ?? []);

var filesystemPackageId = new PackageId("bops.packages.filesystem");
foreach (var tool in new FilesystemToolProvider(pathPolicy).GetTools())
{
    toolRegistry.Register(filesystemPackageId, tool);
}

var networkPackageId = new PackageId("bops.packages.network");
foreach (var tool in new NetworkToolProvider().GetTools())
{
    toolRegistry.Register(networkPackageId, tool);
}

// V0.6: docker.* declares Requires: ["docker"] on every tool (rule B4) — registering the check
// here, before the first RefreshCapabilitiesAsync, is what makes an absent daemon remove every
// docker.* tool from what the planner sees instead of failing only once one is called.
var dockerClientFactory = new DockerClientFactory(builder.Configuration["Docker:Endpoint"]);
if (app.Services.GetRequiredService<ICapabilityProbe>() is CachingCapabilityProbe cachingCapabilityProbe)
{
    cachingCapabilityProbe.RegisterCheck(DockerCapability.Name, ct => DockerCapability.IsAvailableAsync(dockerClientFactory, ct));
}

var dockerPackageId = new PackageId("bops.packages.docker");
foreach (var tool in new DockerToolProvider(dockerClientFactory).GetTools())
{
    toolRegistry.Register(dockerPackageId, tool);
}

await toolRegistry.RefreshCapabilitiesAsync();

var chatModelRegistry = app.Services.GetRequiredService<IChatModelRegistry>();
chatModelRegistry.Register(new PackageId("bops.packages.providers.openrouter"), app.Services.GetRequiredService<OpenRouterProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.ollama"), app.Services.GetRequiredService<OllamaProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.llamacpp"), app.Services.GetRequiredService<LlamaCppProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.openai"), app.Services.GetRequiredService<OpenAiProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.deepseek"), app.Services.GetRequiredService<DeepSeekProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.anthropic"), app.Services.GetRequiredService<AnthropicProviderPackage>());

app.MapAgentsEndpoints();
app.MapApprovalsEndpoints();
app.MapToolsEndpoints();
app.MapProvidersEndpoints();
app.MapIdentityEndpoints();

await app.RunAsync();

// Rule S3: a missing policy.yaml is not the same as a broken one — see bOps.Cli/Program.cs for
// the identical async version; this one is synchronous because it runs inside a synchronous DI
// factory delegate (agentic/02-coding-standards.md forbids blocking on async here, so this uses
// genuinely synchronous file I/O rather than blocking on the async version).
static IPolicyEngine LoadPolicyEngine(string filePath, ILogger logger)
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
        var yaml = File.ReadAllText(filePath);
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

static ChatModelOptions ResolveModelSecret(ChatModelOptions configured, ISecretProvider secretProvider) =>
    new(configured.Provider, configured.BaseUrl, configured.ApiKeySecret, configured.Model, configured.SupportsNativeToolCalling)
    {
        ResolvedApiKey = configured.ApiKeySecret is null ? null : secretProvider.GetSecret(configured.ApiKeySecret),
    };

/// <summary>Marker partial class so <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> can target this top-level-statements entry point. Internal, like every other type in this application (CA1515) — visible to the test project via <c>InternalsVisibleTo</c>.</summary>
internal partial class Program;

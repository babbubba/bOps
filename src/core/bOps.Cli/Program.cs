using bOps.Abstractions;
using bOps.Audit;
using bOps.Cli;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Packages.Providers.Ollama;
using bOps.Packages.Providers.OpenRouter;
using bOps.Packages.Sys.Linux;
using bOps.Packages.Sys.Windows;
using bOps.Policy;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync("""Usage: bops "<goal>" """);
    return 1;
}

var goal = string.Join(' ', args);

var builder = Host.CreateApplicationBuilder();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICapabilityProbe>(services =>
    new CachingCapabilityProbe(services.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<IChatModelRegistry, ChatModelRegistry>();
builder.Services.AddSingleton<IAuditSink>(
    _ => new JsonLinesAuditSink(builder.Configuration["Audit:FilePath"] ?? "audit.jsonl"));

// Phase 1 provider packages are project-referenced and registered directly — the dynamic
// plugin loader arrives at V0.10 (agentic/06-decisions.md, D-003).
builder.Services.AddSingleton<OpenRouterProviderPackage>();
builder.Services.AddSingleton<OllamaProviderPackage>();
builder.Services.AddSingleton<LlamaCppProviderPackage>();

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

await toolRegistry.RefreshCapabilitiesAsync();

var chatModelRegistry = host.Services.GetRequiredService<IChatModelRegistry>();
chatModelRegistry.Register(new PackageId("bops.packages.providers.openrouter"), host.Services.GetRequiredService<OpenRouterProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.ollama"), host.Services.GetRequiredService<OllamaProviderPackage>());
chatModelRegistry.Register(new PackageId("bops.packages.providers.llamacpp"), host.Services.GetRequiredService<LlamaCppProviderPackage>());

var modelOptions = builder.Configuration.GetSection("ModelProvider").Get<ChatModelOptions>()
    ?? throw new InvalidOperationException("Missing 'ModelProvider' configuration section.");

var model = chatModelRegistry.Create(modelOptions);
var runnerOptions = builder.Configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();

var policyLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bOps.Cli.Policy");
var policyEngine = await LoadPolicyEngineAsync(builder.Configuration["Policy:FilePath"] ?? "policy.yaml", policyLogger);
var approvalProvider = new ConsoleApprovalProvider();

var runner = new AgentRunner(
    model,
    toolRegistry,
    policyEngine,
    approvalProvider,
    host.Services.GetRequiredService<IAuditSink>(),
    host.Services.GetRequiredService<TimeProvider>(),
    host.Services.GetRequiredService<ILogger<AgentRunner>>(),
    runnerOptions);

var actor = ActorIdentity.FromOperatingSystemUser(Environment.UserName);
var result = await runner.RunAsync(goal, actor);

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

// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.PluginHost;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Cli.Tests;

/// <summary>
/// <c>bops plugin</c> must be a client of the ADR-0037 lifecycle backend, not a second state machine. The
/// plugin is a real signed package whose entry assembly is this test assembly: installing only inspects
/// its metadata, and an entry constructor that runs writes a marker file, so "no candidate code executes"
/// is asserted on the marker rather than inferred from call names.
/// </summary>
public sealed class PluginCommandTests : IDisposable
{
    private const string Id = "acme.cli-plugin";
    private const string MarkerVariable = "BOPS_CLI_TEST_PLUGIN_MARKER";
    private static readonly ActorIdentity Operator = new("os-user", "operator", null);

    private readonly string _work = Path.Combine(Path.GetTempPath(), $"bops-cli-plugin-{Guid.NewGuid():N}");
    private readonly RSA _key = RSA.Create(2048);
    private readonly string _marker;

    public PluginCommandTests()
    {
        Directory.CreateDirectory(_work);
        _marker = Path.Combine(_work, "entry-ran.marker");
        Environment.SetEnvironmentVariable(MarkerVariable, _marker);
        File.WriteAllText(TrustPath, JsonSerializer.Serialize(new[]
        {
            new PluginPublisherTrust("Acme", "test-key", _key.ExportSubjectPublicKeyInfoPem(), PackageTrustLevel.Community),
        }));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MarkerVariable, null);
        _key.Dispose();
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder that cannot be removed is not a test failure.
        }
    }

    private string StorePath => Path.Combine(_work, "plugins.json");
    private string TrustPath => Path.Combine(_work, "publisher-trust.json");
    private string PluginsRoot => Path.Combine(_work, "plugins");

    // ---- Install ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Install_GoesThroughTheLifecycleBackend_SoAnUnsignedPackageIsRefusedAndASignedOneCommitsRevisionOne()
    {
        var unsigned = PluginDirectory("1.0.0", sign: false);
        var refused = await RunAsync("install", unsigned);

        Assert.Equal(1, refused.Code);
        Assert.Contains("SignatureInvalid", refused.Error, StringComparison.Ordinal);
        Assert.Empty(NewProcess().Manager.List());
        Assert.False(Directory.Exists(Path.Combine(PluginsRoot, Id)));

        var installed = await RunAsync("install", PluginDirectory("1.0.0", sign: true));

        Assert.Equal(0, installed.Code);
        Assert.Contains("InstalledDisabled", installed.Output, StringComparison.Ordinal);
        var status = NewProcess().Lifecycle.GetStatus(Id)!;
        Assert.Equal(PluginLifecycleState.InstalledDisabled, status.State);
        Assert.Equal(1, status.LifecycleVersion); // the legacy install path leaves revision 0
        Assert.False(File.Exists(_marker));

        var list = await RunAsync("list");
        Assert.Equal(0, list.Code);
        Assert.Contains($"{Id}\tdisabled\tv1.0.0", list.Output, StringComparison.Ordinal);
        Assert.Contains("InstalledDisabled\trevision 1", list.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_SameIdempotencyKeyAndPackage_ReplaysOneLogicalOperation()
    {
        var directory = PluginDirectory("1.0.0", sign: true);
        var first = await RunAsync("install", directory, "--idempotency-key", "cli-key-1");
        var second = await RunAsync("install", directory, "--idempotency-key", "cli-key-1");

        Assert.Equal(0, first.Code);
        Assert.Equal(0, second.Code);
        Assert.Contains("replayed", second.Output, StringComparison.Ordinal);
        Assert.Equal(1, NewProcess().Lifecycle.GetStatus(Id)!.LifecycleVersion);
    }

    // ---- Enable / disable ------------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RequiresTheExplicitVersionBoundConfirmation_AndRunsNoPluginCodeWithoutIt()
    {
        await RunAsync("install", PluginDirectory("1.0.0", sign: true));

        var none = await RunAsync("enable", Id);
        var wrong = await RunAsync("enable", Id, "--confirm-version", "9.9.9");

        Assert.Equal(1, none.Code);
        Assert.Equal(1, wrong.Code);
        Assert.Contains("ActivationConfirmationRequired", none.Error, StringComparison.Ordinal);
        Assert.Contains("--confirm-version 1.0.0", none.Error, StringComparison.Ordinal);
        Assert.Contains("ActivationConfirmationRequired", wrong.Error, StringComparison.Ordinal);
        var status = NewProcess().Lifecycle.GetStatus(Id)!;
        Assert.Equal(PluginLifecycleState.InstalledDisabled, status.State);
        Assert.Equal(1, status.LifecycleVersion);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public async Task Enable_WithAStaleExpectedVersion_IsRejectedBeforeAnyPluginCodeRuns()
    {
        await RunAsync("install", PluginDirectory("1.0.0", sign: true));

        var stale = await RunAsync("enable", Id, "--confirm-version", "1.0.0", "--expected-version", "99");
        var staleDisable = await RunAsync("disable", Id, "--expected-version", "99");

        Assert.Equal(1, stale.Code);
        Assert.Contains("StaleVersion", stale.Error, StringComparison.Ordinal);
        Assert.Equal(1, staleDisable.Code);
        Assert.Contains("StaleVersion", staleDisable.Error, StringComparison.Ordinal);
        Assert.Equal(1, NewProcess().Lifecycle.GetStatus(Id)!.LifecycleVersion);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public async Task Enable_ConfirmedButNotActivatable_PersistsActivationFailedThroughTheLifecycle()
    {
        await RunAsync("install", PluginDirectory("1.0.0", sign: true));

        // The entry type is not a tool/Skill/model package, so activation fails only after the explicit confirmation.
        var result = await RunAsync("enable", Id, "--confirm-version", "1.0.0");

        Assert.Equal(1, result.Code);
        Assert.Contains("ActivationFailed", result.Error, StringComparison.Ordinal);
        var status = NewProcess().Lifecycle.GetStatus(Id)!;
        Assert.Equal(PluginLifecycleState.ActivationFailed, status.State); // the legacy path leaves the store untouched
        Assert.Equal(2, status.LifecycleVersion);
        Assert.False(NewProcess().Manager.List().Single().Enabled);
    }

    [Fact]
    public async Task Enable_OfARecoveryRequiredPlugin_RecoversFirstAndNeverActivatesIt()
    {
        await RunAsync("install", PluginDirectory("1.0.0", sign: true));
        Directory.Delete(Path.Combine(PluginsRoot, Id), recursive: true);

        var result = await RunAsync("enable", Id, "--confirm-version", "1.0.0");

        Assert.Equal(1, result.Code);
        Assert.Contains("RecoveryRequired", result.Error + result.Output, StringComparison.Ordinal);
        Assert.Contains("administrator recovery", result.Error, StringComparison.Ordinal);
        var status = NewProcess().Lifecycle.GetStatus(Id)!;
        Assert.Equal(PluginLifecycleState.RecoveryRequired, status.State);
        Assert.Equal(2, status.LifecycleVersion); // recovery ran (and persisted) before the refused action
        Assert.False(NewProcess().Manager.IsActivated(Id));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public async Task AnInterruptedTransaction_IsRecoveredBeforeAnyMutation_SoItsUncommittedBytesCanNeverBeEnabled()
    {
        var installedDirectory = PluginDirectory("1.0.0", sign: true);
        await RunAsync("install", installedDirectory);
        var final = Path.Combine(PluginsRoot, Id);
        Directory.Delete(final, recursive: true);
        await RunAsync("list"); // recovery marks the missing committed material RecoveryRequired

        // A replacement candidate was promoted into the final path and the process died before the metadata commit.
        CopyDirectory(PluginDirectory("2.0.0", sign: true), final);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(StorePath))!.AsObject();
        document["Journals"]![Id] = JsonNode.Parse(
            $$"""{"OperationId":"op-1","PluginId":"{{Id}}","Phase":1,"CurrentGenerationId":null,"ActivationLkgGenerationId":null,"RollbackGenerationId":null,"CandidateGenerationId":"uncommitted","CandidateFromRetained":false}""");
        await File.WriteAllTextAsync(StorePath, document.ToJsonString());

        var result = await RunAsync("enable", Id, "--confirm-version", "2.0.0");

        Assert.Equal(1, result.Code);
        var after = JsonNode.Parse(await File.ReadAllTextAsync(StorePath))!.AsObject();
        Assert.Empty(after["Journals"]!.AsObject());
        Assert.False(Directory.Exists(final));
        Assert.Equal(PluginLifecycleState.RecoveryRequired, NewProcess().Lifecycle.GetStatus(Id)!.State);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public async Task Disable_GoesThroughTheLifecycle_AndIsAnIdempotentNoOpWhenNothingIsEnabled()
    {
        await RunAsync("install", PluginDirectory("1.0.0", sign: true));

        var result = await RunAsync("disable", Id);

        Assert.Equal(0, result.Code);
        Assert.Contains("InstalledDisabled, revision 1", result.Output, StringComparison.Ordinal);
        var missing = await RunAsync("disable", "acme.unknown");
        Assert.Equal(1, missing.Code);
        Assert.Contains("NotFound", missing.Error, StringComparison.Ordinal);
    }

    // ---- Remove ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_FailsClosed_BecauseTheLifecycleDefinesNoRemovalTransaction()
    {
        await RunAsync("install", PluginDirectory("1.0.0", sign: true));
        var before = await File.ReadAllTextAsync(StorePath);

        var result = await RunAsync("remove", Id);

        Assert.Equal(1, result.Code);
        Assert.Contains("Remove refused", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
        Assert.True(Directory.Exists(Path.Combine(PluginsRoot, Id)));
    }

    [Fact]
    public async Task UnknownOrMalformedArguments_PrintTheUsage_AndChangeNothing()
    {
        var none = await RunAsync();
        var bad = await RunAsync("enable");
        var badOption = await RunAsync("enable", Id, "--confirm");
        var badRevision = await RunAsync("disable", Id, "--expected-version", "-1");

        Assert.All(new[] { none, bad, badOption, badRevision }, r => Assert.Equal(1, r.Code));
        Assert.Contains("Usage: bops plugin install", none.Error, StringComparison.Ordinal);
        Assert.Contains("needs a target", bad.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(StorePath));
    }

    // ---- Harness ---------------------------------------------------------------------------------------

    private sealed record Process(PluginManager Manager, PluginLifecycleService Lifecycle);

    private sealed record Outcome(int Code, string Output, string Error);

    /// <summary>A fresh composition over the same store and plugin root, like a new <c>bops</c> process.</summary>
    private Process NewProcess()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:TrustStorePath"] = TrustPath })
            .Build();
        var manager = new PluginManager(
            new PluginStore(StorePath), new ToolRegistry(new AlwaysAvailableProbe()), new SkillRegistry(), new ChatModelRegistry(), PluginsRoot,
            configuration, NullLoggerFactory.Instance, new NoHttpClientFactory(), TimeProvider.System, new AlwaysAvailableProbe());
        return new Process(manager, new PluginLifecycleService(manager, PluginsRoot));
    }

    private async Task<Outcome> RunAsync(params string[] args)
    {
        var process = NewProcess();
        var output = new StringWriter();
        var error = new StringWriter();
        var command = new PluginCommand(process.Lifecycle, process.Manager, TrustPath, output, error, Operator);
        var code = await command.RunAsync(args);
        return new Outcome(code, output.ToString(), error.ToString());
    }

    private string PluginDirectory(string version, bool sign)
    {
        var directory = Path.Combine(_work, "source", $"{version}-{(sign ? "signed" : "unsigned")}");
        if (Directory.Exists(directory))
        {
            return directory;
        }

        Directory.CreateDirectory(directory);
        var entry = typeof(PluginCommandTests).Assembly;
        File.Copy(entry.Location, Path.Combine(directory, Path.GetFileName(entry.Location)));
        File.WriteAllText(Path.Combine(directory, "bops-plugin.json"), new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Id"] = Id,
            ["Publisher"] = "Acme",
            ["Version"] = version,
            ["MinHostAbstractionsVersion"] = "0.10.0",
            ["EntryAssembly"] = Path.GetFileName(entry.Location),
            ["EntryType"] = typeof(FakePluginEntry).FullName,
            ["DeclaredCapabilities"] = new JsonArray(),
            ["Dependencies"] = new JsonArray(),
        }.ToJsonString());
        if (sign)
        {
            PluginPackageSignature.Sign(directory, "Acme", "test-key", _key.ExportPkcs8PrivateKeyPem());
        }

        return directory;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private sealed class AlwaysAvailableProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>The plugin entry type of <see cref="PluginCommandTests"/>. It is never a tool/Skill/model package; if its constructor runs, the marker proves it.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the plugin loader through reflection.")]
internal sealed class FakePluginEntry
{
    public FakePluginEntry()
    {
        var marker = Environment.GetEnvironmentVariable("BOPS_CLI_TEST_PLUGIN_MARKER");
        if (marker is not null)
        {
            File.WriteAllText(marker, "ran");
        }
    }
}

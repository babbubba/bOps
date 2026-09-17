// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;

namespace bOps.Api.Tests;

/// <summary>
/// Boots the real <c>bOps.Api</c> composition root (<see cref="Program"/>) against an isolated
/// temp directory for its audit log/task store/policy file, with an optional test-double
/// <see cref="IChatModel"/> and <see cref="IPolicyEngine"/> substituted in place of the real ones
/// (set before the first request — <see cref="WebApplicationFactory{TEntryPoint}"/> builds the
/// host lazily). "Test the composition, not the logic" (agentic/04-testing-rules.md) — this drives
/// the actual HTTP surface, not a reimplementation of it.
/// </summary>
internal sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private const string TestApiKey = "test-api-key";
    private readonly string _secretVariableName = $"BOPS_TEST_API_KEY_{Guid.NewGuid():N}";
    private readonly string _modelProviderSecretVariableName = $"BOPS_TEST_MODEL_PROVIDER_API_KEY_{Guid.NewGuid():N}";
    private readonly string _vaultMasterKeyVariableName = $"BOPS_TEST_VAULT_MASTER_KEY_{Guid.NewGuid():N}";

    public string TempDirectory { get; } = Directory.CreateTempSubdirectory("bops-api-tests-").FullName;

    public IChatModel? ChatModel { get; set; }

    public IPolicyEngine? PolicyEngine { get; set; }

    public IReadOnlyList<string> Roles { get; init; } = ["viewer", "operator", "approver"];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            Environment.SetEnvironmentVariable(_secretVariableName, TestApiKey);
            Environment.SetEnvironmentVariable(_vaultMasterKeyVariableName, $"test-vault-master-key-{Guid.NewGuid():N}");
            var settings = new Dictionary<string, string?>
            {
                ["Audit:FilePath"] = Path.Combine(TempDirectory, "audit.jsonl"),
                ["Memory:FilePath"] = Path.Combine(TempDirectory, "tasks.db"),
                ["Filesystem:ReadPatterns:0"] = Path.Combine(TempDirectory, "**"),
                ["Filesystem:WritePatterns:0"] = Path.Combine(TempDirectory, "**"),
                ["Filesystem:Inventory:ManifestStorePath"] = Path.Combine(TempDirectory, "filesystem-manifests.db"),
                ["Policy:FilePath"] = Path.Combine(TempDirectory, "policy.yaml"),
                ["Plugins:StorePath"] = Path.Combine(TempDirectory, "plugins.json"),
                ["Plugins:RootPath"] = Path.Combine(TempDirectory, "plugins"),
                ["Plugins:TrustStorePath"] = Path.Combine(TempDirectory, "publisher-trust.json"),
                ["Vault:FilePath"] = Path.Combine(TempDirectory, "vault.dat"),
                ["Vault:MasterKeySecret:Provider"] = "environment",
                ["Vault:MasterKeySecret:Name"] = _vaultMasterKeyVariableName,
                ["Settings:FilePath"] = Path.Combine(TempDirectory, "settings.json"),
                ["Authentication:ApiKeys:0:Id"] = "test-user",
                ["Authentication:ApiKeys:0:DisplayName"] = "Test User",
                ["Authentication:ApiKeys:0:Secret:Provider"] = "environment",
                ["Authentication:ApiKeys:0:Secret:Name"] = _secretVariableName,
                ["Authentication:ApiKeys:0:Roles"] = string.Join(',', Roles),
                ["ModelProvider:ApiKeySecret:Provider"] = "environment",
                ["ModelProvider:ApiKeySecret:Name"] = _modelProviderSecretVariableName,
            };

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            if (ChatModel is { } chatModel)
            {
                services.AddSingleton(chatModel);
            }

            if (PolicyEngine is { } policyEngine)
            {
                services.AddSingleton(policyEngine);
            }
        });
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    public HttpClient CreateAnonymousClient() => base.CreateClient();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(TempDirectory))
        {
            Environment.SetEnvironmentVariable(_secretVariableName, null);
            Environment.SetEnvironmentVariable(_vaultMasterKeyVariableName, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only — a file still briefly locked by SQLite's pooled
                // connection teardown must never fail the test that already passed.
            }
        }
    }
}
